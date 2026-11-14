using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace NexusModUpdater;

/// <summary>
/// 亚克力 / 云母背景的自我修复。
/// 背景是 DWM 画的，WPF-UI 只在窗口创建和切换背景时设一次。显卡驱动重置（玩游戏、切全屏、更新驱动）、
/// 睡眠唤醒、锁屏、远程桌面、换显示器时 DWM 会重启或重建窗口，之前设的背景就丢了，窗口只剩一层黑底
/// 加上面的深色遮罩，要重启程序才恢复。这里在这些时候和窗口从托盘重新显示时，把背景重新设一遍。
/// </summary>
public partial class MainWindow
{
	private const int WM_DISPLAYCHANGE = 0x007E;
	private const int WM_POWERBROADCAST = 0x0218;
	private const int WM_DWMCOMPOSITIONCHANGED = 0x031E;
	private const int WM_THEMECHANGED = 0x031A;
	private const int PBT_APMRESUMESUSPEND = 0x7;
	private const int PBT_APMRESUMEAUTOMATIC = 0x12;

	private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
	private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
	private const int DWMSBT_DISABLE = 1;
	private const int DWMWCP_ROUND = 2;

	[StructLayout(LayoutKind.Sequential)]
	private struct Margins
	{
		public int Left, Right, Top, Bottom;
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

	[DllImport("dwmapi.dll")]
	private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

	private DispatcherTimer _backdropTimer;

	/// <summary>窗口藏在托盘时发生的事，等窗口再显示时补上。</summary>
	private bool _backdropDirty;

	private string _backdropReason;

	private void InitBackdropRepair()
	{
		_backdropTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
		_backdropTimer.Tick += delegate
		{
			_backdropTimer.Stop();
			RepairBackdrop();
		};
		SourceInitialized += delegate
		{
			HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(BackdropHook);
		};
		IsVisibleChanged += (s, e) =>
		{
			if (IsVisible && _backdropDirty)
			{
				QueueBackdropRepair("窗口重新显示");
			}
		};
		StateChanged += delegate
		{
			if (WindowState != WindowState.Minimized && _backdropDirty)
			{
				QueueBackdropRepair("窗口还原");
			}
		};
		SystemEvents.PowerModeChanged += OnPowerModeChanged;
		SystemEvents.SessionSwitch += OnSessionSwitch;
		SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
		RenderCapability.TierChanged += OnRenderTierChanged;
		Closed += delegate
		{
			SystemEvents.PowerModeChanged -= OnPowerModeChanged;
			SystemEvents.SessionSwitch -= OnSessionSwitch;
			SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
			RenderCapability.TierChanged -= OnRenderTierChanged;
		};
	}

	private IntPtr BackdropHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		switch (msg)
		{
			case WM_DWMCOMPOSITIONCHANGED:
				QueueBackdropRepair("DWM 重启");
				break;
			case WM_DISPLAYCHANGE:
				QueueBackdropRepair("显示器变化");
				break;
			case WM_THEMECHANGED:
				QueueBackdropRepair("系统主题变化");
				break;
			case WM_POWERBROADCAST:
				int e = wParam.ToInt32();
				if (e == PBT_APMRESUMESUSPEND || e == PBT_APMRESUMEAUTOMATIC)
				{
					QueueBackdropRepair("睡眠唤醒");
				}
				break;
		}
		return IntPtr.Zero;
	}

	private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
	{
		if (e.Mode == PowerModes.Resume)
		{
			QueueBackdropRepair("睡眠唤醒");
		}
	}

	private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
	{
		if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
		{
			QueueBackdropRepair("解锁 / 切换会话");
		}
	}

	private void OnDisplaySettingsChanged(object sender, EventArgs e) => QueueBackdropRepair("显示设置变化");

	private void OnRenderTierChanged(object sender, EventArgs e) => QueueBackdropRepair("显卡渲染能力变化");

	/// <summary>等事件停一会儿再修（唤醒、驱动重置时会连着来好几条，DWM 也要一点时间才就绪）。系统事件可能在别的线程上来。</summary>
	private void QueueBackdropRepair(string reason)
	{
		Dispatcher.BeginInvoke((Action)delegate
		{
			_backdropReason = reason;
			_backdropDirty = true;
			_backdropTimer.Stop();
			_backdropTimer.Start();
		});
	}

	private void RepairBackdrop()
	{
		IntPtr h = new WindowInteropHelper(this).Handle;
		if (h == IntPtr.Zero || !IsVisible || WindowState == WindowState.Minimized)
		{
			// 藏在托盘或最小化时 DWM 不画它，等显示出来再修
			return;
		}
		_backdropDirty = false;
		WindowBackdropType backdrop = WindowBackdropType;
		Logger.Log("重新套用窗口背景（" + _backdropReason + "）：" + backdrop);
		try
		{
			// DWM 重启后要重新把框架扩展到整个窗口，背景才能透出来
			var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
			DwmExtendFrameIntoClientArea(h, ref margins);
			int round = DWMWCP_ROUND;
			DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
			if (backdrop == WindowBackdropType.None)
			{
				return;
			}
			// 先关掉再打开：值没变时 DWM 不会重建背景
			int off = DWMSBT_DISABLE;
			DwmSetWindowAttribute(h, DWMWA_SYSTEMBACKDROP_TYPE, ref off, sizeof(int));
			// 内容层要透明（显卡重置后 WPF 可能把合成背景重设成不透明），亚克力才看得见
			WindowBackdrop.RemoveBackground(this);
			WindowBackdrop.ApplyBackdrop(this, backdrop);
			WindowBackdrop.RemoveTitlebarBackground(this);
		}
		catch (Exception ex)
		{
			Logger.Log("重新套用窗口背景失败：" + ex.Message);
		}
	}
}
