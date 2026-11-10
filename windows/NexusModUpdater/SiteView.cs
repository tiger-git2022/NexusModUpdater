using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace NexusModUpdater;

/// <summary>
/// 「N网」页：内嵌的 nexusmods.com 浏览器。用来登录、看评论（自动翻译）、发评论（中文写好翻成英文插进评论框），
/// 以及非会员下载时点「Slow download」（生成的 nxm 链接 / 下载会被截住，放进对应游戏的 mod 文件夹）。
/// </summary>
public sealed class SiteView : Grid
{
	public const string Home = "https://www.nexusmods.com/";

	private readonly MainWindow _main;

	private readonly WebView2 _web;

	private readonly TextBox _address;

	private readonly ToggleSwitch _translate;

	private readonly Border _jobBar;

	private readonly TextBlock _jobText;

	private readonly Border _composer;

	private readonly TextBox _compose;

	private readonly TextBox _composed;

	private readonly TextBlock _composeState;

	private bool _started;

	private string _pending;

	private string _lastNxm;

	private DateTime _lastNxmAt;

	public SiteView(MainWindow main)
	{
		_main = main;
		RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

		// 工具栏
		var bar = new DockPanel { Margin = new Thickness(10, 8, 10, 8), LastChildFill = true };
		Button Tool(SymbolRegular icon, string tip, RoutedEventHandler click)
		{
			var b = new Button { Icon = new SymbolIcon(icon), ToolTip = tip, Appearance = ControlAppearance.Transparent, Margin = new Thickness(0, 0, 2, 0), Padding = new Thickness(8, 6, 8, 6) };
			b.Click += click;
			return b;
		}
		var left = new StackPanel { Orientation = Orientation.Horizontal };
		left.Children.Add(Tool(SymbolRegular.ArrowLeft24, "后退", (s, e) => { if (_web.CanGoBack) _web.GoBack(); }));
		left.Children.Add(Tool(SymbolRegular.ArrowRight24, "前进", (s, e) => { if (_web.CanGoForward) _web.GoForward(); }));
		left.Children.Add(Tool(SymbolRegular.ArrowClockwise24, "刷新", (s, e) => _web.Reload()));
		left.Children.Add(Tool(SymbolRegular.Home24, "N 网首页", (s, e) => Navigate(Home)));
		left.Children.Add(Tool(SymbolRegular.History24, "我的下载记录", (s, e) => Navigate(HistoryReader.Url)));
		DockPanel.SetDock(left, Dock.Left);
		bar.Children.Add(left);

		var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
		_translate = new ToggleSwitch { Content = "翻译", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0), ToolTip = "把网页上的英文（评论、简介…）翻译后显示在原文下面" };
		lock (Store.Lock)
		{
			_translate.IsChecked = Store.Cfg.TranslateSite && Translator.Ready;
		}
		_translate.Click += delegate
		{
			if (_translate.IsChecked == true && !Translator.Ready)
			{
				_translate.IsChecked = false;
				if (System.Windows.MessageBox.Show("还没有设置翻译服务（需要填一个翻译接口的 Key）。现在去设置吗？", "N网 Mod 更新", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes)
				{
					_main.SelectPage(2);
				}
				return;
			}
			lock (Store.Lock)
			{
				Store.Cfg.TranslateSite = _translate.IsChecked == true;
			}
			Store.SaveConfig();
			PostTranslateState();
		};
		right.Children.Add(_translate);
		var write = new Button { Content = "写评论（中→英）", Icon = new SymbolIcon(SymbolRegular.CommentEdit24), Margin = new Thickness(0, 0, 6, 0) };
		write.Click += delegate { _composer.Visibility = _composer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; };
		right.Children.Add(write);
		right.Children.Add(Tool(SymbolRegular.Open24, "用系统浏览器打开", (s, e) => AppView.OpenExternal(_web.Source?.AbsoluteUri)));
		DockPanel.SetDock(right, Dock.Right);
		bar.Children.Add(right);

		_address = new TextBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), ClearButtonEnabled = false };
		_address.KeyDown += (s, e) =>
		{
			if (e.Key == Key.Enter)
			{
				string t = _address.Text.Trim();
				if (t != "")
				{
					Navigate(t.Contains("://") ? t : "https://" + t);
				}
			}
		};
		bar.Children.Add(_address);
		Children.Add(bar);

		// 正在等网页下载的任务
		_jobText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
		var cancel = new Button { Content = "取消这个下载", Margin = new Thickness(12, 0, 0, 0) };
		cancel.Click += delegate
		{
			DownloadJob j = Downloader.BrowserJob;
			if (j != null)
			{
				Downloader.Cancel(j.Id);
			}
		};
		var jobDock = new DockPanel();
		DockPanel.SetDock(cancel, Dock.Right);
		jobDock.Children.Add(cancel);
		jobDock.Children.Add(_jobText);
		_jobBar = new Border
		{
			Child = jobDock,
			Padding = new Thickness(14, 10, 14, 10),
			Margin = new Thickness(10, 0, 10, 8),
			CornerRadius = new CornerRadius(6),
			BorderThickness = new Thickness(1),
			Visibility = Visibility.Collapsed
		};
		_jobBar.SetResourceReference(Border.BackgroundProperty, "SystemFillColorCautionBackgroundBrush");
		_jobBar.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
		SetRow(_jobBar, 1);
		Children.Add(_jobBar);

		// 写评论：中文 → 英文
		var cgrid = new Grid();
		cgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		cgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		cgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		cgrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		cgrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		_compose = new TextBox { PlaceholderText = "用中文写评论…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
		_composed = new TextBox { PlaceholderText = "英文译文（可以修改）", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
		var go = new Button { Icon = new SymbolIcon(SymbolRegular.ArrowRight24), Appearance = ControlAppearance.Primary, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "翻译成英文" };
		go.Click += async delegate { await ComposeAsync(); };
		Grid.SetColumn(go, 1);
		Grid.SetColumn(_composed, 2);
		cgrid.Children.Add(_compose);
		cgrid.Children.Add(go);
		cgrid.Children.Add(_composed);
		var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
		var insert = new Button { Content = "插入到评论框", Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0) };
		insert.Click += async delegate { await InsertAsync(); };
		var copy = new Button { Content = "复制", Margin = new Thickness(0, 0, 8, 0) };
		copy.Click += delegate
		{
			try
			{
				Clipboard.SetText(_composed.Text ?? "");
				_composeState.Text = "已复制";
			}
			catch
			{
			}
		};
		var posts = new Button { Content = "打开当前 mod 的评论区", Margin = new Thickness(0, 0, 12, 0) };
		posts.Click += delegate
		{
			Match m = Regex.Match(_web.Source?.AbsoluteUri ?? "", @"nexusmods\.com/([^/]+)/mods/(\d+)");
			if (m.Success)
			{
				Navigate("https://www.nexusmods.com/" + m.Groups[1].Value + "/mods/" + m.Groups[2].Value + "?tab=posts");
			}
		};
		_composeState = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
		_composeState.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
		actions.Children.Add(insert);
		actions.Children.Add(copy);
		actions.Children.Add(posts);
		actions.Children.Add(_composeState);
		Grid.SetRow(actions, 1);
		Grid.SetColumnSpan(actions, 3);
		cgrid.Children.Add(actions);
		_composer = new Border { Child = cgrid, Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(10, 0, 10, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
		_composer.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
		_composer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
		SetRow(_composer, 2);
		Children.Add(_composer);

		_web = new WebView2();
		SetRow(_web, 3);
		Children.Add(_web);

		Downloader.Changed += job => Dispatcher.BeginInvoke((Action)UpdateJobBar);
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
			core.Settings.IsStatusBarEnabled = true;
			string script;
			using (Stream s = typeof(SiteView).Assembly.GetManifestResourceStream("NexusModUpdater.translate.js"))
			using (var r = new StreamReader(s))
			{
				script = r.ReadToEnd();
			}
			await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
			core.SourceChanged += (s, e) => _address.Text = core.Source;
			core.NewWindowRequested += (s, e) =>
			{
				e.Handled = true;
				if (e.Uri.StartsWith("nxm:", StringComparison.OrdinalIgnoreCase))
				{
					OnNxm(e.Uri);
				}
				else
				{
					core.Navigate(e.Uri);
				}
			};
			core.NavigationStarting += (s, e) =>
			{
				if (e.Uri.StartsWith("nxm:", StringComparison.OrdinalIgnoreCase))
				{
					e.Cancel = true;
					OnNxm(e.Uri);
				}
			};
			core.LaunchingExternalUriScheme += (s, e) =>
			{
				if (e.Uri.StartsWith("nxm:", StringComparison.OrdinalIgnoreCase))
				{
					e.Cancel = true;
					OnNxm(e.Uri);
				}
			};
			core.DownloadStarting += OnDownloadStarting;
			core.WebMessageReceived += OnMessage;
			core.DocumentTitleChanged += delegate { _main.FlagSite(); };
			core.Navigate(_pending ?? Home);
			_pending = null;
		}
		catch (Exception ex)
		{
			Logger.Log("N网页面初始化失败：" + ex);
		}
	}

	public void Navigate(string url, bool? translate = null)
	{
		// 指定了就只在这次改开关（不写设置）；没指定按设置
		lock (Store.Lock)
		{
			_translate.IsChecked = (translate ?? Store.Cfg.TranslateSite) && Translator.Ready;
		}
		if (_web.CoreWebView2 == null)
		{
			_pending = url;
			Start();
			return;
		}
		_web.CoreWebView2.Navigate(url);
	}

	public void PostTheme()
	{
	}

	private void PostTranslateState()
	{
		try
		{
			_web.CoreWebView2?.PostWebMessageAsJson(new JObject { ["nmu"] = _translate.IsChecked == true ? "trOn" : "trOff", ["target"] = Store.Cfg.TargetLang }.ToString(Formatting.None));
		}
		catch
		{
		}
	}

	private async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
	{
		if (!Uri.TryCreate(e.Source, UriKind.Absolute, out Uri src) || !(src.Host == "nexusmods.com" || src.Host.EndsWith(".nexusmods.com")))
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
		string kind = msg.Value<string>("nmu");
		if (kind == "trState")
		{
			PostTranslateState();
		}
		else if (kind == "tr" && _translate.IsChecked == true)
		{
			await AppView.TranslateForScript(_web.CoreWebView2, msg);
		}
	}

	private void OnNxm(string uri)
	{
		// 同一个链接可能从两个事件各来一次
		if (uri == _lastNxm && (DateTime.UtcNow - _lastNxmAt).TotalSeconds < 5)
		{
			return;
		}
		_lastNxm = uri;
		_lastNxmAt = DateTime.UtcNow;
		if (!Downloader.HandleNxm(uri))
		{
			Logger.Log("看不懂的 nxm 链接：" + uri);
		}
	}

	private void OnDownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
	{
		string page = _web.CoreWebView2.Source;
		Match m = Regex.Match(page ?? "", @"nexusmods\.com/([A-Za-z0-9_\-]+)/mods/\d+", RegexOptions.IgnoreCase);
		bool needFolder = false;
		if (m.Success)
		{
			lock (Store.Lock)
			{
				needFolder = string.IsNullOrEmpty(Store.Cfg.Game(m.Groups[1].Value.ToLowerInvariant()).Folder);
			}
		}
		if (!needFolder)
		{
			Downloader.HandleBrowserDownload(e, page);
			return;
		}
		CoreWebView2Deferral deferral = e.GetDeferral();
		Dispatcher.BeginInvoke((Action)delegate
		{
			try
			{
				_main.ChooseFolder(m.Groups[1].Value.ToLowerInvariant());
				Downloader.HandleBrowserDownload(e, page);
			}
			finally
			{
				deferral.Complete();
			}
		});
	}

	/// <summary>非会员下载：打开这个文件的下载页，提示用户点 Slow download。</summary>
	public void OpenDownload(DownloadJob job)
	{
		Navigate("https://www.nexusmods.com/" + job.Domain + "/mods/" + job.ModId + "?tab=files&file_id=" + job.FileId + "&nmm=1", false);
		UpdateJobBar();
	}

	private void UpdateJobBar()
	{
		DownloadJob j = Downloader.BrowserJob;
		if (j == null || j.Status != "browser")
		{
			_jobBar.Visibility = Visibility.Collapsed;
			return;
		}
		_jobBar.Visibility = Visibility.Visible;
		_jobText.Text = "正在更新「" + (j.ModName != "" ? j.ModName : j.FileTitle) + "」的文件「" + j.FileTitle + " " + j.Version + "」：请点击下面页面里的「Slow download」（慢速下载），程序会自动接手下载并放进 mod 文件夹。";
	}

	// ---- 写评论 ----

	private async Task ComposeAsync()
	{
		string text = _compose.Text?.Trim() ?? "";
		if (text == "")
		{
			return;
		}
		if (!Translator.Ready)
		{
			_composeState.Text = "还没有设置翻译服务（设置 → 翻译）";
			return;
		}
		_composeState.Text = "翻译中…";
		try
		{
			// 流式时一边收一边显示
			string en = await Translator.TranslateOneAsync(text, "en", partial => Dispatcher.BeginInvoke((Action)(() => _composed.Text = partial)));
			_composed.Text = en ?? "";
			_composeState.Text = "";
		}
		catch (Exception ex)
		{
			_composeState.Text = ex.Message;
		}
	}

	private async Task InsertAsync()
	{
		string text = _composed.Text ?? "";
		if (text == "" || _web.CoreWebView2 == null)
		{
			return;
		}
		try
		{
			Clipboard.SetText(text);
		}
		catch
		{
		}
		const string js = @"(function(t){
 function ok(e){ return e && (e.tagName === 'TEXTAREA' || e.isContentEditable) && e.offsetParent !== null; }
 var el = document.activeElement;
 if (!ok(el)) { var all = Array.prototype.filter.call(document.querySelectorAll('textarea, [contenteditable=""true""], [contenteditable=""""]'), ok); el = all[all.length - 1]; }
 if (!el) return 'none';
 el.focus();
 if (el.tagName === 'TEXTAREA') {
   var s = el.selectionStart == null ? el.value.length : el.selectionStart, en = el.selectionEnd == null ? s : el.selectionEnd;
   el.value = el.value.slice(0, s) + t + el.value.slice(en);
   el.dispatchEvent(new Event('input', { bubbles: true }));
 } else { document.execCommand('insertText', false, t); }
 return 'ok';
})(";
		string r = await _web.CoreWebView2.ExecuteScriptAsync(js + JsonConvert.SerializeObject(text) + ")");
		_composeState.Text = r.Contains("ok") ? "已插入评论框（也复制到了剪贴板），确认后在网页上点发送" : "没找到评论框：先在网页上点开评论输入框，或者直接粘贴（已复制到剪贴板）";
	}
}
