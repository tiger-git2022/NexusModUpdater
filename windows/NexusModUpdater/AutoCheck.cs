using System;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace NexusModUpdater;

/// <summary>
/// 自动检查：打开程序时、从托盘打开窗口时（距上次有一阵了）、程序在后台时定时，
/// 检查上次看的游戏有没有 mod 更新，以及程序本身有没有新版本。
/// 窗口缩在托盘里时查到新东西，弹通知。
/// </summary>
public partial class MainWindow
{
	/// <summary>后台多久查一次 mod 更新 / 程序新版本。</summary>
	private static readonly TimeSpan ModEvery = TimeSpan.FromHours(1), AppEvery = TimeSpan.FromHours(3);

	/// <summary>从托盘打开窗口时，距上次超过多久就再查一次。</summary>
	private static readonly TimeSpan ModOnShow = TimeSpan.FromMinutes(30), AppOnShow = TimeSpan.FromHours(1);

	private DateTime _lastModCheck = DateTime.MinValue, _lastAppCheck = DateTime.MinValue;

	private readonly DispatcherTimer _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };

	/// <summary>上次知道的「有更新」数量，后台查完变多了才弹通知。</summary>
	private int _knownUpdates = -1;

	/// <summary>已经弹过通知的程序新版本，同一个版本只提醒一次。</summary>
	private string _notifiedVersion;

	private void InitAutoCheck()
	{
		_autoTimer.Tick += delegate { AutoCheck(false); };
		_autoTimer.Start();
	}

	/// <summary>到时间了就检查。shown：窗口刚打开（启动或从托盘打开），间隔短一些。</summary>
	private void AutoCheck(bool shown)
	{
		bool sync, check;
		string game;
		lock (Store.Lock)
		{
			sync = Store.Cfg.SyncOnStart;
			check = Store.Cfg.CheckAppUpdate;
			game = Store.Cfg.LastGame;
		}
		DateTime now = DateTime.UtcNow;
		if (sync && !Engine.Syncing && now - _lastModCheck >= (shown ? ModOnShow : ModEvery))
		{
			_lastModCheck = now;
			// 只检查上次看的那个游戏；没选过游戏时只读下载记录
			_ = Engine.SyncAsync(game);
		}
		if (check && now - _lastAppCheck >= (shown ? AppOnShow : AppEvery))
		{
			_lastAppCheck = now;
			_ = CheckAppUpdateAsync(false);
		}
	}

	/// <summary>窗口缩在托盘里时，「有更新」的 mod 变多了就弹通知。</summary>
	private void NotifyNewUpdates(int n)
	{
		int before = _knownUpdates;
		_knownUpdates = n;
		if (before < 0 || n <= before || IsVisible || _tray == null || !_tray.Visible)
		{
			return;
		}
		_tray.ShowBalloonTip(5000, TrayTitle, "有 " + n + " 个 mod 可以更新（新增 " + (n - before) + " 个）", Forms.ToolTipIcon.Info);
	}

	/// <summary>窗口缩在托盘里时查到程序新版本，弹一次通知。</summary>
	private void NotifyAppUpdate(string version)
	{
		if (IsVisible || _tray == null || !_tray.Visible || version == _notifiedVersion)
		{
			return;
		}
		_notifiedVersion = version;
		_tray.ShowBalloonTip(5000, TrayTitle, "新版本 v" + version + " 可以更新，点这里打开主界面更新", Forms.ToolTipIcon.Info);
	}
}
