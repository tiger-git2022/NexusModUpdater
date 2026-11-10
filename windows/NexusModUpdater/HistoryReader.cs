using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;

namespace NexusModUpdater;

/// <summary>所有 WebView2 共用一个浏览器环境（同一份登录状态，存在 exe 旁边的 webview2 文件夹）。</summary>
public static class WebEnv
{
	private static Task<CoreWebView2Environment> _env;

	/// <summary>主窗口的句柄，后台读下载记录用的隐藏浏览器挂在它下面。</summary>
	public static IntPtr HostHwnd;

	public static Task<CoreWebView2Environment> GetAsync()
	{
		return _env ??= CoreWebView2Environment.CreateAsync(null, Paths.WebData);
	}
}

public sealed class HistoryRow
{
	public string Domain = "";

	public int ModId;

	public string Name = "";

	public string Game = "";

	public long LastDownload;

	public long LastUpdate;

	public string Category = "";
}

public sealed class HistoryResult
{
	public bool LoggedIn = true;

	public bool Ok;

	public string Note = "";

	public List<HistoryRow> Rows = new List<HistoryRow>();
}

/// <summary>
/// 读 N 网网页上的「下载记录」（https://www.nexusmods.com/users/myaccount?tab=download+history）。
/// 用一个看不见的 WebView2 打开这个页面（和「N网」页共用登录状态），等表格加载完后读出 DataTables 里的数据。
/// 列：2 = 最后下载时间，5 = 最后更新时间，9 = 游戏（与网页上常用的脚本一致）。
/// </summary>
public static class HistoryReader
{
	public const string Url = "https://www.nexusmods.com/users/myaccount?tab=download+history";

	private static readonly Regex ModLink = new Regex(@"(?:https?://(?:www\.)?nexusmods\.com)?/([A-Za-z0-9_\-]+)/mods/(\d+)", RegexOptions.IgnoreCase);

	private static readonly Regex Anchor = new Regex(@"<a[^>]*href=""[^""]*/mods/\d+[^""]*""[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

	private static readonly Regex Tags = new Regex("<[^>]+>");

	private const string Script = @"(function(){
 try {
  var u = location.href;
  if (/users\.nexusmods\.com|\/login|oauth|sign[-_]?in/i.test(u) || document.querySelector('input[type=password]')) return JSON.stringify({state:'login', url:u});
  var h1 = document.querySelector('h1');
  if (h1 && /please log ?in|access denied/i.test(h1.textContent) && !document.querySelector('table')) return JSON.stringify({state:'login', url:u});
  var $ = window.jQuery;
  var t = document.querySelector('table.datatable') || document.querySelector('table.dataTable');
  if ($ && $.fn && $.fn.dataTable && t && $.fn.dataTable.isDataTable(t)) {
    var dt = $(t).DataTable();
    var data = dt.rows().data().toArray();
    var rows = data.map(function(r){ if (Array.isArray(r)) return r.map(function(c){return c==null?'':String(c)}); var o=[]; for (var k in r) o.push(r[k]==null?'':String(r[k])); return o; });
    var head = Array.prototype.map.call(t.querySelectorAll('thead th'), function(th){return th.innerText.trim()});
    var p = document.querySelector('.dataTables_processing');
    var processing = !!(p && getComputedStyle(p).display !== 'none' && p.offsetParent);
    return JSON.stringify({state:'ok', url:u, rows:rows, head:head, empty:!!t.querySelector('.dataTables_empty'), processing:processing});
  }
  var links = [];
  document.querySelectorAll('tr').forEach(function(tr){
    var a = tr.querySelector('a[href*=""/mods/""]'); if (!a) return;
    var cells = Array.prototype.map.call(tr.querySelectorAll('td'), function(td){ var tm = td.querySelector('time'); return { text: td.innerText.trim(), order: td.getAttribute('data-order') || '', time: tm ? (tm.getAttribute('datetime') || '') : '' }; });
    links.push({ href: a.href, name: a.innerText.trim(), cells: cells });
  });
  return JSON.stringify({state: t ? 'table' : 'notable', url:u, links:links, title: document.title});
 } catch (e) { return JSON.stringify({state:'error', message:String(e)}); }
})()";

	public static Task<HistoryResult> ReadAsync(IProgress<string> progress)
	{
		return Application.Current.Dispatcher.InvokeAsync(() => ReadOnUiAsync(progress)).Task.Unwrap();
	}

	private static async Task<HistoryResult> ReadOnUiAsync(IProgress<string> progress)
	{
		var result = new HistoryResult();
		if (WebEnv.HostHwnd == IntPtr.Zero)
		{
			result.Note = "窗口还没准备好";
			return result;
		}
		CoreWebView2Environment env = await WebEnv.GetAsync();
		CoreWebView2Controller ctl = await env.CreateCoreWebView2ControllerAsync(WebEnv.HostHwnd);
		try
		{
			ctl.IsVisible = false;
			ctl.Bounds = new Rectangle(0, 0, 1280, 900);
			CoreWebView2 core = ctl.CoreWebView2;
			core.Settings.AreDefaultScriptDialogsEnabled = false;
			// 图片、字体、视频不需要，挡掉加快加载
			core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
			core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);
			core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Font);
			core.WebResourceRequested += (s, e) => e.Response = env.CreateWebResourceResponse(null, 404, "Blocked", "");
			bool loggedAjax = false;
			core.WebResourceResponseReceived += (s, e) =>
			{
				string u = e.Request.Uri;
				if (!loggedAjax && u.IndexOf("istory", StringComparison.OrdinalIgnoreCase) >= 0 && u.IndexOf("myaccount", StringComparison.OrdinalIgnoreCase) < 0)
				{
					loggedAjax = true;
					Logger.Log("下载记录的数据地址：" + u);
				}
			};
			var nav = new TaskCompletionSource<bool>();
			int status = 0;
			core.NavigationCompleted += (s, e) =>
			{
				status = e.HttpStatusCode;
				nav.TrySetResult(e.IsSuccess);
			};
			progress?.Report("正在打开 N 网下载记录…");
			core.Navigate(Url);
			await Task.WhenAny(nav.Task, Task.Delay(60000));
			if (status == 401 || status == 403)
			{
				result.LoggedIn = false;
				result.Note = "N 网网页没有登录，无法读取下载记录：请到「N网」页登录";
				return result;
			}
			int prev = -1;
			int stable = 0;
			JObject last = null;
			for (int i = 0; i < 75; i++)
			{
				await Task.Delay(1000);
				string raw;
				try
				{
					raw = await core.ExecuteScriptAsync(Script);
				}
				catch (Exception ex)
				{
					Logger.Log("读下载记录脚本出错：" + ex.Message);
					continue;
				}
				JObject j;
				try
				{
					j = JObject.Parse(JToken.Parse(raw).Value<string>() ?? "{}");
				}
				catch
				{
					continue;
				}
				last = j;
				string state = j.Value<string>("state");
				if (state == "login")
				{
					result.LoggedIn = false;
					result.Note = "N 网网页没有登录，无法读取下载记录：请到「N网」页登录";
					return result;
				}
				if (state == "ok")
				{
					var rows = (JArray)j["rows"];
					bool processing = j.Value<bool?>("processing") ?? false;
					progress?.Report("正在读取下载记录…（" + rows.Count + " 条）");
					if (rows.Count > 0 && rows.Count == prev && !processing)
					{
						stable++;
					}
					else
					{
						stable = 0;
					}
					prev = rows.Count;
					bool empty = j.Value<bool?>("empty") ?? false;
					if (stable >= 2 || (rows.Count == 0 && empty && !processing && i >= 6))
					{
						ParseDataTable(rows, j["head"] as JArray, result);
						result.Ok = true;
						return result;
					}
				}
				else if ((state == "table" || state == "notable") && i >= 20)
				{
					var links = j["links"] as JArray;
					if (links != null && links.Count > 0)
					{
						ParseLinks(links, result);
						result.Ok = true;
						result.Note = "下载记录页面和预期的不一样，只读到了 mod 列表（没有下载时间）";
						return result;
					}
				}
			}
			Logger.Log("读下载记录超时，最后一次结果：" + Short(last?.ToString() ?? ""));
			result.Note = "读取下载记录超时（网页可能需要人机验证：到「N网」页打开一次下载记录）";
			return result;
		}
		finally
		{
			try
			{
				ctl.Close();
			}
			catch
			{
			}
		}
	}

	private static void ParseDataTable(JArray rows, JArray head, HistoryResult result)
	{
		if (rows.Count > 0)
		{
			Logger.Log("下载记录：" + rows.Count + " 条；表头 " + (head == null ? "" : string.Join(" | ", head.Select(h => h.ToString()))) + "；第一条 " + Short(rows[0].ToString(Newtonsoft.Json.Formatting.None)));
		}
		foreach (JArray r in rows.OfType<JArray>())
		{
			string[] cells = r.Select(c => c.ToString()).ToArray();
			string all = string.Join(" ", cells);
			Match m = ModLink.Match(all);
			if (!m.Success || m.Groups[1].Value.Equals("users", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			var row = new HistoryRow { Domain = m.Groups[1].Value.ToLowerInvariant(), ModId = int.Parse(m.Groups[2].Value) };
			Match a = Anchor.Match(all);
			row.Name = a.Success ? Text(a.Groups[1].Value) : "";
			long dl = Cell(cells, 2);
			long up = Cell(cells, 5);
			if (dl == 0 || up == 0)
			{
				// 列的位置变了：按出现顺序找时间
				var times = cells.Select(c => Time(c)).Where(t => t > 0).ToList();
				if (dl == 0 && times.Count > 0)
				{
					dl = times[0];
				}
				if (up == 0 && times.Count > 1)
				{
					up = times[1];
				}
			}
			row.LastDownload = dl;
			row.LastUpdate = up;
			row.Game = cells.Length > 9 ? Text(cells[9]) : "";
			row.Category = cells.Length > 4 ? Text(cells[4]) : "";
			if (row.Name == "" && cells.Length > 0)
			{
				row.Name = Text(cells[0]);
			}
			result.Rows.Add(row);
		}
	}

	private static void ParseLinks(JArray links, HistoryResult result)
	{
		Logger.Log("下载记录（无 DataTable）：" + links.Count + " 条；第一条 " + Short(links[0].ToString(Newtonsoft.Json.Formatting.None)));
		foreach (JObject l in links.OfType<JObject>())
		{
			Match m = ModLink.Match(l.Value<string>("href") ?? "");
			if (!m.Success)
			{
				continue;
			}
			var row = new HistoryRow { Domain = m.Groups[1].Value.ToLowerInvariant(), ModId = int.Parse(m.Groups[2].Value), Name = l.Value<string>("name") ?? "" };
			var times = new List<long>();
			foreach (JObject c in (l["cells"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
			{
				long t = Time(c.Value<string>("order"));
				if (t == 0)
				{
					t = Time(c.Value<string>("time"));
				}
				if (t == 0)
				{
					t = Time(c.Value<string>("text"));
				}
				if (t > 0)
				{
					times.Add(t);
				}
			}
			row.LastDownload = times.Count > 0 ? times[0] : 0;
			row.LastUpdate = times.Count > 1 ? times[1] : 0;
			result.Rows.Add(row);
		}
	}

	private static long Cell(string[] cells, int i) => i < cells.Length ? Time(cells[i]) : 0;

	/// <summary>Unix 时间戳（秒或毫秒）、或日期文字 → Unix 秒。</summary>
	private static long Time(string s)
	{
		s = Text(s ?? "");
		if (s == "")
		{
			return 0;
		}
		if (Regex.IsMatch(s, @"^\d{9,13}$"))
		{
			long v = long.Parse(s);
			if (v > 100000000000)
			{
				v /= 1000;
			}
			return v > 946684800 ? v : 0;
		}
		string[] formats = { "dd MMM yyyy, h:mmtt", "dd MMM yyyy, h:mm tt", "dd MMM yyyy", "d MMM yyyy", "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fffZ", "MMM d, yyyy" };
		if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime d)
			|| (s.Length <= 40 && Regex.IsMatch(s, @"\d{4}") && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d)))
		{
			return new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds();
		}
		return 0;
	}

	private static string Text(string html) => WebUtility.HtmlDecode(Tags.Replace(html ?? "", " ")).Trim().Replace("  ", " ");

	private static string Short(string s) => s.Length > 1500 ? s.Substring(0, 1500) + "…" : s;
}
