using System;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace NexusModUpdater;

/// <summary>托盘图标：点关闭时缩到右下角的通知区域，程序（下载、检查）在后台继续；单击图标打开，右键可以退出。</summary>
public partial class MainWindow
{
	private Forms.NotifyIcon _tray;

	/// <summary>真的要退出（托盘菜单的「退出」、程序更新），不再缩到托盘。</summary>
	private bool _exiting;

	private const string TrayTitle = "N网 Mod 更新";

	private void InitTray()
	{
		var menu = new Forms.ContextMenuStrip();
		var open = new Forms.ToolStripMenuItem("打开", null, delegate { BringToFront(); });
		open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
		menu.Items.Add(open);
		menu.Items.Add(new Forms.ToolStripMenuItem("下载", null, delegate
		{
			BringToFront();
			ShowDownloads();
		}));
		menu.Items.Add(new Forms.ToolStripSeparator());
		menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, delegate { ExitApp(); }));
		_tray = new Forms.NotifyIcon { Text = TrayTitle, Icon = TrayIcon(), ContextMenuStrip = menu };
		_tray.MouseClick += (s, e) =>
		{
			if (e.Button == Forms.MouseButtons.Left)
			{
				BringToFront();
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
