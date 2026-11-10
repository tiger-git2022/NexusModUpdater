using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Wpf.Ui.Controls;
using Forms = System.Windows.Forms;

namespace NexusModUpdater;

/// <summary>托盘图标：点关闭时缩到右下角的通知区域，程序（下载、检查）在后台继续；单击图标打开，右键可以退出。</summary>
public partial class MainWindow
{
	private Forms.NotifyIcon _tray;

	/// <summary>真的要退出（托盘菜单的「退出」、程序更新），不再缩到托盘。</summary>
	private bool _exiting;

	private const string TrayTitle = "N网 Mod 更新";

	private readonly TrayMenu _trayMenu = new TrayMenu();

	private void InitTray()
	{
		_tray = new Forms.NotifyIcon { Text = TrayTitle, Icon = TrayIcon() };
		_tray.MouseClick += (s, e) =>
		{
			if (e.Button == Forms.MouseButtons.Left)
			{
				BringToFront();
			}
		};
		// 右键菜单和 WE 加权轮播一样：亚克力背景、圆角、Fluent 图标、跟随深浅色
		_tray.MouseUp += (s, e) =>
		{
			if (e.Button == Forms.MouseButtons.Right)
			{
				ShowTrayMenu();
			}
		};
		_tray.BalloonTipClicked += delegate { BringToFront(); };
		UpdateTrayVisible();
		Closed += delegate
		{
			_tray.Visible = false;
			_tray.Dispose();
		};
		Downloader.Changed += job => Dispatcher.BeginInvoke((Action)delegate
		{
			// 窗口缩在托盘里时，下载完成或失败用通知告诉你
			if (IsVisible || !_tray.Visible || (job.Status != "done" && job.Status != "failed"))
			{
				return;
			}
			string name = job.ModName != "" ? job.ModName : job.FileName;
			_tray.ShowBalloonTip(4000, job.Status == "done" ? "已更新" : "下载失败", name + (job.Status == "failed" && job.Message != "" ? "：" + job.Message : ""), job.Status == "done" ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
		});
	}

	private void ShowTrayMenu()
	{
		try
		{
			int downloading = Downloader.All().Count(j => !j.Finished);
			var entries = new List<TrayMenu.Entry>
			{
				new TrayMenu.Entry { Text = downloading > 0 ? downloading + " 个下载中" : "没有正在下载的文件", Enabled = false, Icon = SymbolRegular.ArrowDownload24 },
				new TrayMenu.Entry { Separator = true },
				new TrayMenu.Entry { Text = "打开主界面", Bold = true, Icon = SymbolRegular.Home24, Click = BringToFront },
				new TrayMenu.Entry { Text = "下载", Icon = SymbolRegular.ArrowDownload24, Click = () => { BringToFront(); ShowDownloads(); } },
				new TrayMenu.Entry
				{
					Text = Engine.Syncing ? "正在检查更新…" : "检查更新",
					Enabled = !Engine.Syncing,
					Icon = SymbolRegular.ArrowSync24,
					Click = () => _ = Engine.SyncAsync(Store.Cfg.LastGame),
				},
				new TrayMenu.Entry { Text = "N网", Icon = SymbolRegular.Globe24, Click = () => { BringToFront(); SelectPage(1); } },
				new TrayMenu.Entry { Text = "设置…", Icon = SymbolRegular.Settings24, Click = () => { BringToFront(); SelectPage(2); } },
				new TrayMenu.Entry { Separator = true },
				new TrayMenu.Entry { Text = "退出", Icon = SymbolRegular.Power24, Click = ExitApp },
			};
			_trayMenu.Show(entries);
		}
		catch (Exception ex)
		{
			Logger.Log("托盘菜单打开失败：" + ex);
		}
	}

	private static System.Drawing.Icon TrayIcon()
	{
		using Stream s = typeof(MainWindow).Assembly.GetManifestResourceStream("NexusModUpdater.app.ico");
		return new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
	}

	/// <summary>开了「关闭时最小化到托盘」才常驻托盘图标。</summary>
	private void UpdateTrayVisible()
	{
		if (_tray != null)
		{
			_tray.Visible = Store.Cfg.CloseToTray;
		}
	}

	/// <summary>托盘图标上的提示：有下载时显示数量。</summary>
	private void UpdateTrayText(int downloading)
	{
		if (_tray != null)
		{
			_tray.Text = downloading > 0 ? TrayTitle + " · " + downloading + " 个下载中" : TrayTitle;
		}
	}

	/// <summary>点关闭：缩到托盘。返回 true 表示已经缩到托盘（关闭要取消）。</summary>
	private bool CloseToTray()
	{
		if (_exiting || !Store.Cfg.CloseToTray || _tray == null)
		{
			return false;
		}
		Hide();
		if (!Store.Cfg.TrayHinted)
		{
			lock (Store.Lock)
			{
				Store.Cfg.TrayHinted = true;
			}
			Store.SaveConfig();
			_tray.ShowBalloonTip(5000, TrayTitle + " 还在后台运行", "下载和检查会继续。单击这里的图标重新打开，右键可以退出；在设置里可以改成点关闭直接退出。", Forms.ToolTipIcon.Info);
		}
		return true;
	}

	/// <summary>真的退出（还有下载没完成时会问一下）。</summary>
	public void ExitApp()
	{
		_exiting = true;
		Close();
	}

	/// <summary>切到第一页的「下载」。</summary>
	private void ShowDownloads()
	{
		_appView = "downloads";
		_app.ShowView("downloads");
		SelectPage(0);
	}
}
