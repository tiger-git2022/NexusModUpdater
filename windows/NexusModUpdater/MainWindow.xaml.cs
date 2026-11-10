using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxImage = System.Windows.MessageBoxImage;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace NexusModUpdater;

public partial class MainWindow : FluentWindow
{
	private readonly FrameworkElement[] _pages;

	private readonly AppView _app;

	private readonly SiteView _site;

	private bool _ready;

	private int _page;

	private UpdateInfo _appUpdate;

	private readonly DispatcherTimer _tintSave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

	public MainWindow()
	{
		InitializeComponent();
		_pages = new FrameworkElement[] { PageApp, PageSite, PageSettings };
		_app = new AppView(this);
		_site = new SiteView(this);
		PageApp.Child = _app;
		PageSite.Child = _site;
		foreach ((string code, string name) in Translator.Languages)
		{
			Lang.Items.Add(new ComboBoxItem { Content = name, Tag = code });
		}
		LoadSettings();
		_tintSave.Tick += delegate
		{
			_tintSave.Stop();
			Store.SaveConfig();
		};
		ApplicationThemeManager.Changed += OnThemeChanged;
		Closed += delegate { ApplicationThemeManager.Changed -= OnThemeChanged; };
		ApplyAppearance();
		_ready = true;
		SelectPage(0);

		Engine.AskFolder = domain => Dispatcher.InvokeAsync(() => ChooseFolder(domain)).Task;
		Engine.StateChanged += () => Dispatcher.BeginInvoke((Action)UpdateSide);
		Downloader.NeedsBrowser += job => Dispatcher.BeginInvoke((Action)delegate
		{
			SelectPage(1);
			_site.OpenDownload(job);
		});
		SourceInitialized += delegate { WebEnv.HostHwnd = new WindowInteropHelper(this).Handle; };
		Loaded += async delegate
		{
			_app.Start();
			UpdateSide();
			bool sync, check;
			lock (Store.Lock)
			{
				sync = Store.Cfg.SyncOnStart;
				check = Store.Cfg.CheckAppUpdate;
			}
			if (sync)
			{
				_ = Engine.SyncAsync();
			}
			if (check)
			{
				await CheckAppUpdateAsync(false);
			}
		};
		Closing += (s, e) =>
		{
			if (Downloader.All().Any(j => !j.Finished && j.Status != "browser"))
			{
				if (MessageBox.Show("还有下载没完成，确定要退出吗？", "N网 Mod 更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
				{
					e.Cancel = true;
				}
			}
		};
	}

	public void BringToFront()
	{
		if (WindowState == WindowState.Minimized)
		{
			WindowState = WindowState.Normal;
		}
		Show();
		Activate();
		Topmost = true;
		Topmost = false;
	}

	// ---- 页面 ----

	private void Nav_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int page))
		{
			SelectPage(page);
		}
	}

	public void SelectPage(int page)
	{
		_page = page;
		for (int i = 0; i < _pages.Length; i++)
		{
			_pages[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
		}
		for (int i = 0; i < Nav.Children.Count; i++)
		{
			if (Nav.Children[i] is Button b)
			{
				b.Appearance = i == page ? ControlAppearance.Secondary : ControlAppearance.Transparent;
			}
		}
		if (page == 1)
		{
			_site.Start();
			NavSiteDot.Visibility = Visibility.Collapsed;
		}
		if (page == 2)
		{
			BuildGames();
			UpdateKeyState();
		}
	}

	/// <summary>在「N网」页打开一个地址。</summary>
	public void OpenSite(string url, bool? translate = null)
	{
		SelectPage(1);
		_site.Navigate(url, translate);
	}

	public void SetUpdateCount(int n)
	{
		NavBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
		NavCount.Text = n > 99 ? "99+" : n.ToString();
	}

	public void FlagSite()
	{
		if (_page != 1)
		{
			NavSiteDot.Visibility = Visibility.Visible;
		}
	}

	private void UpdateSide()
	{
		lock (Store.Lock)
		{
			SideUser.Text = Store.St.KeyValid && Store.St.UserName != "" ? Store.St.UserName + (Store.St.Premium ? " · 会员" : " · 普通用户") : (string.IsNullOrWhiteSpace(Store.Cfg.ApiKey) ? "未填写 API Key" : "");
		}
		SideLimit.Text = NexusApi.DailyRemaining >= 0 ? "接口剩余：今天 " + NexusApi.DailyRemaining + " / 本小时 " + Math.Max(0, NexusApi.HourlyRemaining) : "";
	}

	// ---- 选文件夹 ----

	public bool ChooseFolder(string domain)
	{
		string name = Engine.GameName(domain);
		using var dlg = new System.Windows.Forms.FolderBrowserDialog
		{
			Description = "选择《" + name + "》的 mod 文件夹（下载的 mod 会放进这里）",
			ShowNewFolderButton = true
		};
		lock (Store.Lock)
		{
			string cur = Store.Cfg.Game(domain).Folder;
			if (!string.IsNullOrEmpty(cur) && Directory.Exists(cur))
			{
				dlg.SelectedPath = cur;
			}
		}
		var owner = new System.Windows.Forms.NativeWindow();
		owner.AssignHandle(new WindowInteropHelper(this).Handle);
		try
		{
			if (dlg.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK)
			{
				return false;
			}
		}
		finally
		{
			owner.ReleaseHandle();
		}
		lock (Store.Lock)
		{
			GameSetting g = Store.Cfg.Game(domain);
			g.Folder = dlg.SelectedPath;
			if (string.IsNullOrEmpty(g.Name))
			{
				g.Name = name;
			}
		}
		Store.SaveConfig();
		if (_page == 2)
		{
			BuildGames();
		}
		Engine.Changed();
		return true;
	}

	// ---- 设置 ----

	private bool _loading;

	private void LoadSettings()
	{
		_loading = true;
		Config c = Store.Cfg;
		ApiKey.Password = c.ApiKey;
		UseHistory.IsChecked = c.UseHistory;
		UseTracked.IsChecked = c.UseTracked;
		ScanFolders.IsChecked = c.ScanFolders;
		SyncOnStart.IsChecked = c.SyncOnStart;
		CheckAppUpdate.IsChecked = c.CheckAppUpdate;
		EngineBox.SelectedItem = EngineBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == c.TranslateEngine) ?? EngineBox.Items[0];
		Lang.SelectedItem = Lang.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == c.TargetLang) ?? Lang.Items[0];
		AutoTranslate.IsChecked = c.AutoTranslate;
		TranslateSite.IsChecked = c.TranslateSite;
		OpenAiBase.Text = c.OpenAiBase;
		OpenAiKey.Password = c.OpenAiKey;
		OpenAiModel.Text = c.OpenAiModel;
		AzureRegion.Text = c.AzureRegion;
		UpdateTranslatePanels();
		ThemeBox.SelectedIndex = c.Theme == "light" ? 1 : (c.Theme == "dark" ? 2 : 0);
		BackdropBox.SelectedIndex = c.Backdrop == "mica" ? 1 : (c.Backdrop == "none" ? 2 : 0);
		TintSlider.Value = c.TintPercent;
		VersionText.Text = "N网 Mod 更新 v" + Updater.CurrentVersion;
		_loading = false;
	}

	private void Option_Changed(object sender, RoutedEventArgs e)
	{
		if (_loading)
		{
			return;
		}
		lock (Store.Lock)
		{
			Config c = Store.Cfg;
			c.UseHistory = UseHistory.IsChecked == true;
			c.UseTracked = UseTracked.IsChecked == true;
			c.ScanFolders = ScanFolders.IsChecked == true;
			c.SyncOnStart = SyncOnStart.IsChecked == true;
			c.CheckAppUpdate = CheckAppUpdate.IsChecked == true;
		}
		Store.SaveConfig();
	}

	private void Translate_Changed(object sender, RoutedEventArgs e)
	{
		if (_loading || !_ready)
		{
			return;
		}
		SaveTranslateKeys();
		lock (Store.Lock)
		{
			Config c = Store.Cfg;
			c.TranslateEngine = (string)((ComboBoxItem)EngineBox.SelectedItem).Tag;
			c.TargetLang = (string)((ComboBoxItem)Lang.SelectedItem).Tag;
			c.AutoTranslate = AutoTranslate.IsChecked == true;
			c.TranslateSite = TranslateSite.IsChecked == true;
		}
		UpdateTranslatePanels();
		Store.SaveConfig();
		_app.PostSettings();
	}

	/// <summary>按选的翻译服务显示要填的 Key / 区域 / 接口地址。</summary>
	private void UpdateTranslatePanels()
	{
		Config c = Store.Cfg;
		string engine = c.TranslateEngine;
		OpenAiPanel.Visibility = engine == "openai" ? Visibility.Visible : Visibility.Collapsed;
		KeyCard.Visibility = engine == "azure" || engine == "deepl" || engine == "google" ? Visibility.Visible : Visibility.Collapsed;
		RegionCard.Visibility = engine == "azure" ? Visibility.Visible : Visibility.Collapsed;
		_keyEngine = engine;
		switch (engine)
		{
		case "azure":
			KeyTitle.Text = "微软 Azure 翻译 Key";
			KeyHelp.Text = "在 Azure 门户创建「翻译器」资源（定价层选 F0 免费），在「密钥和终结点」里复制密钥 1";
			TranslateKey.Password = c.AzureKey;
			break;
		case "deepl":
			KeyTitle.Text = "DeepL API Key";
			KeyHelp.Text = "注册 DeepL API Free（免费），在账户的 API Keys 里复制（以 :fx 结尾）";
			TranslateKey.Password = c.DeepLKey;
			break;
		case "google":
			KeyTitle.Text = "谷歌云 API Key";
			KeyHelp.Text = "在 Google Cloud 控制台启用 Cloud Translation API，创建 API 密钥";
			TranslateKey.Password = c.GoogleKey;
			break;
		}
	}

	private string _keyEngine = "";

	private void SaveTranslateKeys()
	{
		lock (Store.Lock)
		{
			Config c = Store.Cfg;
			c.OpenAiBase = OpenAiBase.Text.Trim();
			c.OpenAiKey = OpenAiKey.Password.Trim();
			c.OpenAiModel = OpenAiModel.Text.Trim();
			c.AzureRegion = AzureRegion.Text.Trim();
			string key = TranslateKey.Password.Trim();
			switch (_keyEngine)
			{
			case "azure":
				c.AzureKey = key;
				break;
			case "deepl":
				c.DeepLKey = key;
				break;
			case "google":
				c.GoogleKey = key;
				break;
			}
		}
	}

	private void OpenAi_LostFocus(object sender, RoutedEventArgs e)
	{
		if (_loading)
		{
			return;
		}
		SaveTranslateKeys();
		Store.SaveConfig();
		_app.PostSettings();
	}

	private void KeyHelp_Click(object sender, RoutedEventArgs e)
	{
		string url = Store.Cfg.TranslateEngine switch
		{
			"azure" => "https://learn.microsoft.com/zh-cn/azure/ai-services/translator/create-translator-resource",
			"deepl" => "https://www.deepl.com/pro-api",
			"google" => "https://console.cloud.google.com/apis/library/translate.googleapis.com",
			_ => null
		};
		if (url != null)
		{
			AppView.OpenExternal(url);
		}
	}

	private async void TestTranslate_Click(object sender, RoutedEventArgs e)
	{
		OpenAi_LostFocus(null, null);
		if (!Translator.Ready)
		{
			TestResult.Text = "还没有设置好：先选翻译服务并填写 Key";
			return;
		}
		TestResult.Text = "翻译中…";
		try
		{
			string r = await Translator.TranslateOneAsync("This mod adds a new armor set and fixes the crash when loading a save.");
			TestResult.Text = r;
		}
		catch (Exception ex)
		{
			TestResult.Text = ex.Message;
		}
	}

	private void ClearTranslate_Click(object sender, RoutedEventArgs e)
	{
		Translator.ClearCache();
		TestResult.Text = "已清空";
	}

	private void UpdateKeyState()
	{
		lock (Store.Lock)
		{
			State st = Store.St;
			KeyState.Text = string.IsNullOrWhiteSpace(Store.Cfg.ApiKey) ? "还没有填写" : (st.KeyValid ? "已验证：" + st.UserName + (st.Premium ? "（会员，可以一键下载）" : "（普通用户：下载时需要在网页上点一下 Slow download）") : "未验证");
		}
	}

	private async void ValidateKey_Click(object sender, RoutedEventArgs e)
	{
		string key = ApiKey.Password.Trim();
		KeyState.Text = "正在验证…";
		try
		{
			var u = await NexusApi.ValidateAsync(key);
			lock (Store.Lock)
			{
				Store.Cfg.ApiKey = key;
				Store.St.KeyValid = true;
				Store.St.UserName = u.Value<string>("name") ?? "";
				Store.St.Premium = u.Value<bool?>("is_premium") ?? false;
				Store.St.Avatar = u.Value<string>("profile_url") ?? "";
			}
			Store.SaveConfig();
			Store.SaveSoon();
			UpdateKeyState();
			UpdateSide();
			Engine.Changed();
			_ = Engine.SyncAsync();
		}
		catch (Exception ex)
		{
			KeyState.Text = "验证失败：" + ex.Message;
		}
	}

	private void GetKey_Click(object sender, RoutedEventArgs e)
	{
		OpenSite("https://next.nexusmods.com/settings/api-keys", false);
	}

	private void Login_Click(object sender, RoutedEventArgs e)
	{
		OpenSite("https://www.nexusmods.com/users/myaccount?tab=download+history", false);
	}

	// ---- 游戏 ----

	private void BuildGames()
	{
		GamesPanel.Children.Clear();
		GameSetting[] games;
		lock (Store.Lock)
		{
			var counts = Store.St.Mods.Values.GroupBy(m => m.Domain).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
			games = Store.Cfg.Games.Values.OrderByDescending(g => counts.TryGetValue(g.Domain, out int n) ? n : 0).ThenBy(g => g.Name).ToArray();
		}
		if (games.Length == 0)
		{
			GamesPanel.Children.Add(new TextBlock { Text = "还没有游戏。先到「Mod 更新」页同步一次，或者在上面输入游戏名添加。", Style = (Style)FindResource("Muted"), Margin = new Thickness(2, 0, 0, 0) });
			return;
		}
		foreach (GameSetting g in games)
		{
			GamesPanel.Children.Add(GameCard(g));
		}
	}

	private FrameworkElement GameCard(GameSetting g)
	{
		var card = new Card { Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(16, 12, 16, 12) };
		var grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
		title.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(g.Name) ? g.Domain : g.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
		title.Children.Add(new TextBlock { Text = g.Domain, Style = (Style)FindResource("Muted"), FontSize = 12 });
		grid.Children.Add(title);

		var folder = new TextBox { PlaceholderText = "还没有设置 mod 文件夹", Text = g.Folder, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
		folder.LostFocus += delegate
		{
			lock (Store.Lock)
			{
				g.Folder = folder.Text.Trim();
			}
			Store.SaveConfig();
		};
		Grid.SetColumn(folder, 1);
		grid.Children.Add(folder);

		var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
		var browse = new Button { Content = "浏览…", Margin = new Thickness(0, 0, 8, 0) };
		browse.Click += delegate { ChooseFolder(g.Domain); };
		var open = new Button { Icon = new SymbolIcon(SymbolRegular.FolderOpen24), Margin = new Thickness(0, 0, 8, 0), ToolTip = "打开文件夹" };
		open.Click += delegate
		{
			if (Directory.Exists(g.Folder))
			{
				Process.Start("explorer.exe", "\"" + g.Folder + "\"");
			}
		};
		var mode = new ComboBox { Width = 210, Margin = new Thickness(0, 0, 8, 0), ToolTip = "下载后怎么放进 mod 文件夹" };
		mode.Items.Add(new ComboBoxItem { Content = "解压到以 mod 命名的子文件夹", Tag = "subfolder" });
		mode.Items.Add(new ComboBoxItem { Content = "解压到 mod 文件夹（覆盖）", Tag = "root" });
		mode.Items.Add(new ComboBoxItem { Content = "只保存压缩包", Tag = "archive" });
		mode.SelectedItem = mode.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == g.Mode) ?? mode.Items[0];
		mode.SelectionChanged += delegate
		{
			lock (Store.Lock)
			{
				g.Mode = (string)((ComboBoxItem)mode.SelectedItem).Tag;
			}
			Store.SaveConfig();
		};
		var removeOld = new CheckBox { Content = "旧版本移到回收站", IsChecked = g.RemoveOld, Margin = new Thickness(0, 0, 8, 0) };
		removeOld.Click += delegate
		{
			lock (Store.Lock)
			{
				g.RemoveOld = removeOld.IsChecked == true;
			}
			Store.SaveConfig();
		};
		var del = new Button { Icon = new SymbolIcon(SymbolRegular.Delete24), Appearance = ControlAppearance.Transparent, ToolTip = "从列表里去掉这个游戏的设置" };
		del.Click += delegate
		{
			lock (Store.Lock)
			{
				Store.Cfg.Games.Remove(g.Domain);
			}
			Store.SaveConfig();
			BuildGames();
		};
		right.Children.Add(browse);
		right.Children.Add(open);
		right.Children.Add(mode);
		right.Children.Add(removeOld);
		right.Children.Add(del);
		Grid.SetColumn(right, 2);
		grid.Children.Add(right);
		card.Content = grid;
		return card;
	}

	private void AddGame_Click(object sender, RoutedEventArgs e)
	{
		string d = NewGame.Text.Trim().ToLowerInvariant();
		// 也接受粘贴的网址：https://www.nexusmods.com/skyrimspecialedition/mods/123
		var m = System.Text.RegularExpressions.Regex.Match(d, @"nexusmods\.com/(?:games/)?([a-z0-9_\-]+)");
		if (m.Success)
		{
			d = m.Groups[1].Value;
		}
		if (!System.Text.RegularExpressions.Regex.IsMatch(d, "^[a-z0-9_\\-]+$"))
		{
			return;
		}
		lock (Store.Lock)
		{
			GameSetting g = Store.Cfg.Game(d);
			if (string.IsNullOrEmpty(g.Name))
			{
				g.Name = Engine.GameName(d);
			}
		}
		Store.SaveConfig();
		NewGame.Text = "";
		BuildGames();
	}

	// ---- 外观（和 WE 加权轮播一样）----

	private void Appearance_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (!_ready || _loading)
		{
			return;
		}
		lock (Store.Lock)
		{
			Store.Cfg.Theme = ThemeBox.SelectedIndex == 1 ? "light" : (ThemeBox.SelectedIndex == 2 ? "dark" : "system");
			Store.Cfg.Backdrop = BackdropBox.SelectedIndex == 1 ? "mica" : (BackdropBox.SelectedIndex == 2 ? "none" : "acrylic");
		}
		ApplyAppearance();
		Store.SaveConfig();
	}

	private void ApplyAppearance()
	{
		Config cfg = Store.Cfg;
		WindowBackdropType backdrop = cfg.Backdrop == "mica" ? WindowBackdropType.Mica : (cfg.Backdrop == "none" ? WindowBackdropType.None : WindowBackdropType.Acrylic);
		try
		{
			SystemThemeWatcher.UnWatch(this);
		}
		catch
		{
		}
		if (cfg.Theme == "light" || cfg.Theme == "dark")
		{
			ApplicationThemeManager.Apply(cfg.Theme == "dark" ? ApplicationTheme.Dark : ApplicationTheme.Light, backdrop, updateAccent: true);
		}
		else
		{
			ApplicationThemeManager.ApplySystemTheme(updateAccent: true);
			SystemThemeWatcher.Watch(this, backdrop, updateAccents: true);
		}
		WindowBackdropType = backdrop;
		UpdateTint();
	}

	private void UpdateTint()
	{
		Config cfg = Store.Cfg;
		bool acrylic = cfg.Backdrop == "acrylic";
		bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
		Tint.Fill = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
		Tint.Opacity = acrylic ? cfg.TintPercent / 100.0 : 0.0;
		PageLayer.Background = acrylic ? Brushes.Transparent : (Brush)FindResource("LayerFillColorDefaultBrush");
		TintCard.Visibility = acrylic ? Visibility.Visible : Visibility.Collapsed;
		TintText.Text = cfg.TintPercent + "%";
	}

	private void OnThemeChanged(ApplicationTheme theme, Color accent)
	{
		Dispatcher.BeginInvoke((Action)delegate
		{
			UpdateTint();
			_app.PostTheme();
			_site.PostTheme();
		});
	}

	private void Tint_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_ready)
		{
			return;
		}
		Store.Cfg.TintPercent = (int)Math.Round(TintSlider.Value);
		UpdateTint();
		_tintSave.Stop();
		_tintSave.Start();
	}

	// ---- 程序更新 ----

	private async Task CheckAppUpdateAsync(bool manual)
	{
		UpdateState.Text = "正在检查…";
		try
		{
			_appUpdate = await Updater.CheckAsync();
			if (_appUpdate == null)
			{
				UpdateState.Text = "已经是最新版本";
				AppUpdateBox.Visibility = Visibility.Collapsed;
				return;
			}
			UpdateState.Text = "有新版本 v" + _appUpdate.Version;
			AppUpdateText.Text = "新版本 v" + _appUpdate.Version + " 可以更新";
			AppUpdateBox.Visibility = Visibility.Visible;
			if (manual)
			{
				AppUpdate_Click(null, null);
			}
		}
		catch (Exception ex)
		{
			UpdateState.Text = "检查更新失败：" + ex.Message;
			Logger.Log("检查更新失败：" + ex.Message);
		}
	}

	private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
	{
		await CheckAppUpdateAsync(true);
	}

	private async void AppUpdate_Click(object sender, RoutedEventArgs e)
	{
		if (_appUpdate == null)
		{
			return;
		}
		string notes = _appUpdate.Notes.Length > 800 ? _appUpdate.Notes.Substring(0, 800) + "…" : _appUpdate.Notes;
		if (MessageBox.Show("更新到 v" + _appUpdate.Version + "？\n\n" + notes + "\n\n设置和记录不会丢失，更新完成后会自动重新打开。", "N网 Mod 更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			return;
		}
		if (!Updater.CanWriteAppDir())
		{
			MessageBox.Show("程序文件夹不能写入（可能放在了 Program Files 里），请手动下载新版本。", "N网 Mod 更新");
			Process.Start(_appUpdate.PageUrl);
			return;
		}
		AppUpdateButton.IsEnabled = false;
		try
		{
			string zip = await Updater.DownloadAsync(_appUpdate, new Progress<int>(p =>
			{
				AppUpdateText.Text = "正在下载 v" + _appUpdate.Version + "… " + p + "%";
				UpdateState.Text = AppUpdateText.Text;
			}));
			Store.SaveNow();
			Updater.StartInstall(zip);
			Application.Current.Shutdown();
		}
		catch (Exception ex)
		{
			AppUpdateButton.IsEnabled = true;
			AppUpdateText.Text = "更新失败：" + ex.Message;
			Logger.Log("更新失败：" + ex);
		}
	}

	// ---- 其他 ----

	private void OpenLog_Click(object sender, RoutedEventArgs e)
	{
		if (File.Exists(Paths.Log))
		{
			Process.Start("notepad.exe", "\"" + Paths.Log + "\"");
		}
	}

	private void OpenFolder_Click(object sender, RoutedEventArgs e)
	{
		Process.Start("explorer.exe", "\"" + Paths.AppDir + "\"");
	}

	private void OpenRepo_Click(object sender, RoutedEventArgs e)
	{
		Process.Start("https://github.com/" + Updater.Repo);
	}
}
