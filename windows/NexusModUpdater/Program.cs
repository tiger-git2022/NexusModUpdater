using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace NexusModUpdater;

public static class Program
{
	private const string Name = "NexusModUpdater_" + "SingleInstance";

	[STAThread]
	public static void Main(string[] args)
	{
		string user = Environment.UserName;
		using var mutex = new Mutex(true, Name + "_" + user, out bool first);
		using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, Name + "_Show_" + user);
		if (!first)
		{
			// 已经开着：让它把窗口拿到前面
			signal.Set();
			return;
		}
		// 以前写在 exe.config 里：WPF 跟随每个显示器的缩放重新排版
		AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
		SingleFile.Prepare();
		Run(signal);
		GC.KeepAlive(mutex);
	}

	/// <summary>按设置应该用深色吗：跟随系统时读 Windows 的「应用使用浅色主题」开关。</summary>
	public static bool WantDark()
	{
		string theme = Store.Cfg?.Theme;
		if (theme == "dark" || theme == "light")
		{
			return theme == "dark";
		}
		try
		{
			object v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
			return v is int i && i == 0;
		}
		catch (Exception)
		{
			return false;
		}
	}

	// 单独一个方法：用到 WPF-UI 等依赖的代码放在清理旧文件之后才编译、加载
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void Run(EventWaitHandle signal)
	{
		Store.Load();
		Logger.Log("启动 v" + Updater.CurrentVersion);
		var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
		bool dark = WantDark();
		app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light });
		app.Resources.MergedDictionaries.Add(new ControlsDictionary());
		app.DispatcherUnhandledException += (s, e) =>
		{
			Logger.Log("未处理异常：" + e.Exception);
			e.Handled = true;
		};
		TaskScheduler.UnobservedTaskException += (s, e) =>
		{
			Logger.Log("未处理的后台异常：" + e.Exception);
			e.SetObserved();
		};
		var window = new MainWindow();
		var waiter = new Thread(() =>
		{
			while (signal.WaitOne())
			{
				app.Dispatcher.BeginInvoke((Action)window.BringToFront);
			}
		}) { IsBackground = true };
		waiter.Start();
		app.Exit += (s, e) => { Store.SaveNow(); Translator.SaveNow(); };
		app.Run(window);
	}
}
