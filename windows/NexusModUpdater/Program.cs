using System;
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
		Store.Load();
		Logger.Log("启动 v" + Updater.CurrentVersion);
		var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
		bool dark = Store.Cfg.Theme == "dark" || (Store.Cfg.Theme != "light" && ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark);
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
		GC.KeepAlive(mutex);
	}
}
