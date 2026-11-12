using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;

namespace NexusModUpdater;

/// <summary>
/// 读 mod 的评论：N 网接口没有评论，只能从网页上拿。和读下载记录一样，用一个隐藏的内嵌浏览器
/// （同一份登录状态、人机验证记录）打开 mod 的评论页，把每条评论和回复取成数据。
/// 打开过的页面留两分钟，翻页、再看同一个 mod 不用重新打开。
/// </summary>
public static class CommentReader
{
	private static CoreWebView2Controller _ctl;

	/// <summary>隐藏浏览器当前打开的评论页地址。</summary>
	private static string _loaded;

	private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

	private static DispatcherTimer _idle;

	/// <summary>页面状态：人机验证 / 还在加载 / 评论区已出来 / 这个 mod 没有评论区。</summary>
	private const string StateScript = @"(function(){
 var t = document.title || '';
 if (/just a moment|attention required|请稍候|checking your browser/i.test(t) || document.querySelector('#challenge-form, .cf-turnstile, #cf-wrapper')) return 'cf';
 if (document.querySelector('#comment-container-wrapper')) return 'ready';
 var tab = document.querySelector('.tabcontent');
 if (tab && tab.innerText.trim().length > 0 && !tab.querySelector('.loading, .spinner')) return 'none:' + tab.innerText.trim().slice(0, 300);
 return 'loading';
})()";

	/// <summary>取第 page 页的评论，结果放到 window.__nmuComments（ExecuteScriptAsync 不等 Promise，所以轮询它）。</summary>
	private const string FetchScript = @"(function(page){
 window.__nmuComments = null;
 function abs(u){ try { return u ? new URL(u, location.href).href : ''; } catch(e){ return ''; } }
 function one(li){
  var head = li.querySelector(':scope > .comment-head');
  var content = li.querySelector(':scope > .comment-content');
  var name = head && head.querySelector('.comment-name');
  var img = head && head.querySelector('.comment-user img');
  var t = content && content.querySelector(':scope > time');
  var body = content && content.querySelector(':scope > .comment-content-text');
  var kids = li.querySelector(':scope > .comment-kids');
  return {
   id: li.id.replace(/^comment-/, ''),
   name: name ? name.textContent.trim() : '',
   avatar: img ? abs(img.getAttribute('src')) : '',
   date: t ? +(t.getAttribute('data-date') || 0) : 0,
   html: body ? body.innerHTML : '',
   sticky: li.classList.contains('comment-sticky'),
   author: li.classList.contains('comment-author'),
   replies: kids ? Array.prototype.map.call(kids.querySelectorAll(':scope > li.comment'), one) : []
  };
 }
 function extract(root){
  var wrap = root.querySelector('#comment-container-wrapper') || root;
  var tops = Array.prototype.filter.call(wrap.querySelectorAll('li.comment'), function(li){ return !li.parentElement.closest('li.comment'); });
  var nums = Array.prototype.map.call(wrap.querySelectorAll('.comment-nav .pagination li a'), function(a){ return parseInt(a.textContent.trim(), 10); }).filter(function(n){ return n > 0; });
  var cur = wrap.querySelector('#current-page-number');
  var p = cur ? (+cur.value || 1) : 1;
  return { state: 'ok', page: p, pages: Math.max.apply(null, [p].concat(nums)), comments: Array.prototype.map.call(tops, one) };
 }
 (async function(){
  try {
   var cur = document.querySelector('#current-page-number');
   if (!cur || +cur.value === page) { window.__nmuComments = JSON.stringify(extract(document)); return; }
   var url = document.querySelector('#reload-tab-url').value.replace(/([?&])page=\d+/, '$1page=' + page);
   var r = await fetch(url, { credentials: 'include', headers: { 'X-Requested-With': 'XMLHttpRequest' } });
   if (!r.ok) throw new Error('HTTP ' + r.status);
   var doc = new DOMParser().parseFromString(await r.text(), 'text/html');
   var res = extract(doc);
   res.page = page;
   window.__nmuComments = JSON.stringify(res);
  } catch (e) { window.__nmuComments = JSON.stringify({ state: 'error', message: String(e && e.message || e) }); }
 })();
 return 'started';
})";

	/// <summary>读一个 mod 第 page 页的评论。force：重新打开评论页（刷新）。</summary>
	public static Task<JObject> ReadAsync(string domain, int modId, int page, bool force)
	{
		return Application.Current.Dispatcher.InvokeAsync(() => ReadOnUiAsync(domain, modId, Math.Max(1, page), force)).Task.Unwrap();
	}

	private static async Task<JObject> ReadOnUiAsync(string domain, int modId, int page, bool force)
	{
		await Gate.WaitAsync();
		try
		{
			_idle?.Stop();
			string url = "https://www.nexusmods.com/" + domain + "/mods/" + modId + "?tab=posts";
			CoreWebView2 core = await EnsureAsync();
			if (force || _loaded != url)
			{
				_loaded = null;
				JObject none = await OpenAsync(core, url);
				if (none != null)
				{
					return none;
				}
				_loaded = url;
			}
			await core.ExecuteScriptAsync(FetchScript + "(" + page + ")");
			for (int i = 0; i < 60; i++)
			{
				await Task.Delay(250);
				string raw = await core.ExecuteScriptAsync("window.__nmuComments");
				string json = JToken.Parse(raw).Type == JTokenType.String ? JToken.Parse(raw).Value<string>() : null;
				if (json == null)
				{
					continue;
				}
				var j = JObject.Parse(json);
				if (j.Value<string>("state") == "error")
				{
					_loaded = null;
					throw new InvalidOperationException("读取评论失败：" + j.Value<string>("message"));
				}
				return j;
			}
			_loaded = null;
			throw new TimeoutException("读取评论超时");
		}
		finally
		{
			Gate.Release();
			StartIdle();
		}
	}

	/// <summary>打开评论页并等评论区出来。返回非空表示这个 mod 没有评论区（结果里带网页上的说明）。</summary>
	private static async Task<JObject> OpenAsync(CoreWebView2 core, string url)
	{
		var nav = new TaskCompletionSource<int>();
		void Done(object s, CoreWebView2NavigationCompletedEventArgs e) => nav.TrySetResult(e.HttpStatusCode);
		core.NavigationCompleted += Done;
		try
		{
			core.Navigate(url);
			await Task.WhenAny(nav.Task, Task.Delay(30000));
		}
		finally
		{
			core.NavigationCompleted -= Done;
		}
		int status = nav.Task.IsCompleted ? nav.Task.Result : 0;
		if (status == 404)
		{
			throw new InvalidOperationException("N 网上找不到这个 mod 的页面（可能已删除或隐藏）");
		}
		for (int i = 0; i < 120; i++)
		{
			string state = JToken.Parse(await core.ExecuteScriptAsync(StateScript)).Value<string>() ?? "";
			if (state == "ready")
			{
				return null;
			}
			// 人机验证有时几秒后自己过，等一会儿再报
			if (state == "cf" && i >= 24)
			{
				throw new InvalidOperationException("N 网要求人机验证：请到「N网」页打开任意页面完成验证，再点「刷新」");
			}
			if (state.StartsWith("none:") && i >= 40)
			{
				return new JObject { ["state"] = "ok", ["page"] = 1, ["pages"] = 1, ["comments"] = new JArray(), ["note"] = state.Substring(5) };
			}
			await Task.Delay(250);
		}
		Logger.Log("读评论超时：" + url);
		throw new TimeoutException("打开评论页超时");
	}

	private static async Task<CoreWebView2> EnsureAsync()
	{
		if (_ctl != null)
		{
			return _ctl.CoreWebView2;
		}
		if (WebEnv.HostHwnd == IntPtr.Zero)
		{
			throw new InvalidOperationException("窗口还没准备好");
		}
		CoreWebView2Environment env = await WebEnv.GetAsync();
		CoreWebView2Controller ctl = await env.CreateCoreWebView2ControllerAsync(WebEnv.HostHwnd);
		ctl.IsVisible = false;
		ctl.Bounds = new Rectangle(0, 0, 1280, 900);
		CoreWebView2 core = ctl.CoreWebView2;
		core.Settings.AreDefaultScriptDialogsEnabled = false;
		// 图片、字体、视频不需要（头像地址照样能读到），挡掉加快加载
		core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
		core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);
		core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Font);
		core.WebResourceRequested += (s, e) => e.Response = env.CreateWebResourceResponse(null, 404, "Blocked", "");
		_ctl = ctl;
		return core;
	}

	/// <summary>两分钟没再读评论就关掉隐藏浏览器。</summary>
	private static void StartIdle()
	{
		if (_idle == null)
		{
			_idle = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
			_idle.Tick += delegate
			{
				_idle.Stop();
				if (Gate.CurrentCount == 0)
				{
					return;
				}
				try
				{
					_ctl?.Close();
				}
				catch
				{
				}
				_ctl = null;
				_loaded = null;
			};
		}
		_idle.Stop();
		_idle.Start();
	}
}
