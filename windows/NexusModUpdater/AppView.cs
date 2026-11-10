using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wpf.Ui.Appearance;

namespace NexusModUpdater;

/// <summary>
/// 「Mod 更新」页：一个透明的 WebView2，显示内嵌的 app.html。页面用 postMessage 调用 Dispatch 里的方法。
/// </summary>
public sealed class AppView : Grid
{
	public const string Host = "https://nmu.local/";

	private readonly MainWindow _main;

	private readonly WebView2 _web;

	private readonly TextBlock _message;

	private bool _started;

	private bool _ready;

	private readonly Queue<string> _outbox = new Queue<string>();

	private readonly DispatcherTimer _stateTimer;

	private readonly Dictionary<string, DateTime> _jobPosted = new Dictionary<string, DateTime>();

	public AppView(MainWindow main)
	{
		_main = main;
		_web = new WebView2 { Visibility = Visibility.Hidden };
		_message = new TextBlock
		{
			Text = "正在加载…",
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			TextWrapping = TextWrapping.Wrap,
			MaxWidth = 460,
			TextAlignment = TextAlignment.Center
		};
		_message.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
		Children.Add(_message);
		Children.Add(_web);
		_stateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
		_stateTimer.Tick += delegate
		{
			_stateTimer.Stop();
			Post(new JObject { ["event"] = "state" });
		};
		Engine.StateChanged += () => Dispatcher.BeginInvoke((Action)delegate
		{
			_stateTimer.Stop();
			_stateTimer.Start();
		});
		GameCatalog.Changed += () => Dispatcher.BeginInvoke((Action)delegate
		{
			Post(new JObject { ["event"] = "games" });
		});
		Engine.SyncProgress += (text, pct) => Dispatcher.BeginInvoke((Action)delegate
		{
			Post(new JObject { ["event"] = "sync", ["text"] = text, ["pct"] = pct, ["syncing"] = Engine.Syncing });
		});
		Downloader.Changed += job => Dispatcher.BeginInvoke((Action)delegate
		{
			// 进度最多每 0.4 秒发一次
			DateTime now = DateTime.UtcNow;
			if (job.Status == "downloading" && _jobPosted.TryGetValue(job.Id, out DateTime last) && (now - last).TotalMilliseconds < 400)
			{
				return;
			}
			_jobPosted[job.Id] = now;
			Post(new JObject { ["event"] = "job", ["job"] = job.ToJson() });
			if (job.Status == "done" || job.Status == "failed")
			{
				Post(new JObject { ["event"] = "toast", ["kind"] = job.Status == "done" ? "ok" : "error", ["text"] = (job.Status == "done" ? "已更新：" : "下载失败：") + (job.ModName != "" ? job.ModName : job.FileName) + (job.Status == "failed" ? "（" + job.Message + "）" : "") });
			}
		});
	}

	public async void Start()
	{
		if (_started)
		{
			return;
		}
		_started = true;
		try
		{
			CoreWebView2Environment env = await WebEnv.GetAsync();
			await _web.EnsureCoreWebView2Async(env);
			CoreWebView2 core = _web.CoreWebView2;
			core.Settings.IsStatusBarEnabled = false;
			core.Settings.IsZoomControlEnabled = false;
			core.Settings.AreDefaultContextMenusEnabled = true;
			// 透明：窗口的亚克力背景透出来
			_web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
			core.AddWebResourceRequestedFilter(Host + "*", CoreWebView2WebResourceContext.All);
			core.WebResourceRequested += OnResource;
			core.WebMessageReceived += OnMessage;
			core.NewWindowRequested += (s, e) =>
			{
				e.Handled = true;
				OpenLink(e.Uri);
			};
			core.NavigationStarting += (s, e) =>
			{
				if (!e.Uri.StartsWith(Host, StringComparison.OrdinalIgnoreCase))
				{
					e.Cancel = true;
					OpenLink(e.Uri);
				}
			};
			core.NavigationCompleted += (s, e) =>
			{
				_web.Visibility = Visibility.Visible;
				_message.Visibility = Visibility.Collapsed;
			};
			core.Navigate(Host + "app.html?theme=" + (Dark() ? "dark" : "light") + "&accent=" + Uri.EscapeDataString(AccentHex()));
		}
		catch (Exception ex)
		{
			Logger.Log("页面初始化失败：" + ex);
			_message.Text = "页面打不开：" + ex.Message + "\n\n需要 Microsoft Edge WebView2 运行时（Windows 11 自带；Windows 10 可到微软官网下载安装）。";
		}
	}

	private void OpenLink(string uri)
	{
		if (uri.IndexOf("nexusmods.com", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			_main.OpenSite(uri);
		}
		else
		{
			OpenExternal(uri);
		}
	}

	public static void OpenExternal(string uri)
	{
		try
		{
			if (Uri.TryCreate(uri, UriKind.Absolute, out Uri u) && (u.Scheme == "https" || u.Scheme == "http"))
			{
				Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
			}
		}
		catch (Exception ex)
		{
			Logger.Log("打开链接失败：" + ex.Message);
		}
	}

	private static bool Dark() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

	public static string AccentHex()
	{
		try
		{
			System.Windows.Media.Color c = ApplicationAccentColorManager.PrimaryAccent;
			return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2");
		}
		catch
		{
			return "#60cdff";
		}
	}

	public void PostTheme()
	{
		Post(new JObject { ["event"] = "theme", ["theme"] = Dark() ? "dark" : "light", ["accent"] = AccentHex() });
	}

	public void PostSettings()
	{
		Post(new JObject { ["event"] = "status", ["status"] = Engine.StatusJson() });
	}

	private void Post(JObject o)
	{
		string json = o.ToString(Formatting.None);
		if (!_ready || _web.CoreWebView2 == null)
		{
			_outbox.Enqueue(json);
			return;
		}
		try
		{
			_web.CoreWebView2.PostWebMessageAsJson(json);
		}
		catch
		{
		}
	}

	private void OnResource(object sender, CoreWebView2WebResourceRequestedEventArgs e)
	{
		string path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
		string type = path.EndsWith(".js") ? "application/javascript" : "text/html";
		Stream s = path == "app.html" || path == "translate.js" ? typeof(AppView).Assembly.GetManifestResourceStream("NexusModUpdater." + path) : null;
		CoreWebView2Environment env = _web.CoreWebView2.Environment;
		e.Response = s == null
			? env.CreateWebResourceResponse(null, 404, "Not Found", "")
			: env.CreateWebResourceResponse(s, 200, "OK", "Content-Type: " + type + "; charset=utf-8\r\nCache-Control: no-store");
	}

	private async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
	{
		if (!e.Source.StartsWith(Host, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		JObject msg;
		try
		{
			msg = JObject.Parse(e.WebMessageAsJson);
		}
		catch
		{
			return;
		}
		// 翻译脚本（translate.js）的请求
		if (msg.Value<string>("nmu") == "tr")
		{
			await TranslateForScript(_web.CoreWebView2, msg);
			return;
		}
		string method = msg.Value<string>("method");
		if (method == "ready")
		{
			_ready = true;
			while (_outbox.Count > 0)
			{
				try
				{
					_web.CoreWebView2.PostWebMessageAsJson(_outbox.Dequeue());
				}
				catch
				{
				}
			}
			return;
		}
		if (method == "log")
		{
			Logger.Log("页面：" + msg["args"]?.Value<string>("message"));
			return;
		}
		JToken id = msg["id"];
		var reply = new JObject { ["id"] = id };
		try
		{
			reply["result"] = await Dispatch(method, msg["args"] as JObject ?? new JObject());
			reply["ok"] = true;
		}
		catch (Exception ex)
		{
			if (!(ex is NexusException))
			{
				if (method != "translate")
				{
					// 翻译失败已经在 Translator 里记过了
					Logger.Log("页面调用 " + method + " 出错：" + ex);
				}
			}
			reply["ok"] = false;
			reply["error"] = ex.Message;
		}
		try
		{
			_web.CoreWebView2?.PostWebMessageAsJson(reply.ToString(Formatting.None));
		}
		catch
		{
		}
	}

	/// <summary>translate.js 发来的 {nmu:'tr', id, texts} → 回 {nmu:'tr', id, result}。SiteView 也用。</summary>
	public static async Task TranslateForScript(CoreWebView2 core, JObject msg)
	{
		var texts = (msg["texts"] as JArray)?.Select(t => t.ToString()).ToList() ?? new List<string>();
		var reply = new JObject { ["nmu"] = "tr", ["id"] = msg["id"] };
		try
		{
			reply["result"] = new JArray(await Translator.TranslateAsync(texts));
		}
		catch (Exception ex)
		{
			reply["error"] = ex.Message;
		}
		try
		{
			core?.PostWebMessageAsJson(reply.ToString(Formatting.None));
		}
		catch
		{
		}
	}

	private static List<string> Keys(JObject a) => (a["keys"] as JArray)?.Select(k => k.ToString()).ToList() ?? new List<string>();

	private async Task<JToken> Dispatch(string method, JObject a)
	{
		switch (method)
		{
		case "status":
			return Engine.StatusJson();
		case "list":
			return Engine.ListJson();
		case "sync":
			_ = Engine.SyncAsync(a.Value<string>("domain"));
			return true;
		case "games":
			return GameCatalog.Json();
		case "setGame":
			GameCatalog.SetLast(a.Value<string>("domain"));
			return true;
		case "pinGame":
			GameCatalog.SetPinned(a.Value<string>("domain"), a.Value<bool>("on"));
			return GameCatalog.Json();
		case "renameGame":
			GameCatalog.SetAlias(a.Value<string>("domain"), a.Value<string>("alias"));
			return GameCatalog.Json();
		case "detail":
			return await Task.Run(() => Engine.DetailAsync(a.Value<string>("domain"), a.Value<int>("modId"), a.Value<bool?>("force") ?? false));
		case "translate":
		{
			var texts = (a["texts"] as JArray)?.Select(t => t.ToString()).ToList() ?? new List<string>();
			return new JArray(await Task.Run(() => Translator.TranslateAsync(texts, a.Value<string>("target"))));
		}
		case "ignore":
			Engine.SetIgnore(Keys(a), a.Value<string>("kind") ?? "temp");
			return true;
		case "markUpToDate":
			Engine.MarkUpToDate(Keys(a));
			return true;
		case "remove":
			Engine.Remove(Keys(a));
			return true;
		case "update":
			return await Task.Run(() => Engine.UpdateAsync(Keys(a)));
		case "download":
			return await Task.Run(() => Engine.DownloadFileAsync(a.Value<string>("domain"), a.Value<int>("modId"), a.Value<int>("fileId")));
		case "jobs":
			return new JArray(Downloader.All().Select(j => j.ToJson()));
		case "cancelJob":
			Downloader.Cancel(a.Value<string>("id"));
			return true;
		case "retryJob":
			Downloader.Retry(a.Value<string>("id"));
			return true;
		case "clearJobs":
			Downloader.ClearFinished();
			return true;
		case "openSite":
			_main.OpenSite(a.Value<string>("url"), a.Value<bool?>("translate"));
			return true;
		case "openExternal":
			OpenExternal(a.Value<string>("url"));
			return true;
		case "openFolder":
		{
			string p = a.Value<string>("path");
			if (string.IsNullOrEmpty(p) && a.Value<string>("domain") != null)
			{
				lock (Store.Lock)
				{
					p = Store.Cfg.Game(a.Value<string>("domain")).Folder;
				}
			}
			if (!string.IsNullOrEmpty(p))
			{
				if (File.Exists(p))
				{
					Process.Start("explorer.exe", "/select,\"" + p + "\"");
				}
				else if (Directory.Exists(p))
				{
					Process.Start("explorer.exe", "\"" + p + "\"");
				}
				else
				{
					throw new IOException("文件夹不存在：" + p);
				}
			}
			return true;
		}
		case "chooseFolder":
			return _main.ChooseFolder(a.Value<string>("domain"));
		case "settings":
			_main.SelectPage(2);
			return true;
		case "login":
			_main.OpenSite(HistoryReader.Url, false);
			return true;
		case "setAutoTranslate":
			lock (Store.Lock)
			{
				Store.Cfg.AutoTranslate = a.Value<bool>("on");
			}
			Store.SaveConfig();
			return Engine.StatusJson();
		case "counts":
			_main.SetUpdateCount(a.Value<int>("updates"));
			return true;
		default:
			throw new InvalidOperationException("未知方法 " + method);
		}
	}
}
