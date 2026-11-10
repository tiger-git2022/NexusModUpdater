using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;

namespace NexusModUpdater;

public sealed class DownloadJob
{
	public string Id = Guid.NewGuid().ToString("N").Substring(0, 12);

	public string Domain = "";

	public int ModId;

	public int FileId;

	public string FileName = "";

	public string FileTitle = "";

	public string Version = "";

	public long Uploaded;

	public string ModName = "";

	/// <summary>waiting / link / browser / downloading / installing / done / failed / canceled</summary>
	public string Status = "waiting";

	public string Message = "";

	public long Received;

	public long Total;

	public double Speed;

	public long CreatedAt = Store.Now;

	public string Path = "";

	internal string NxmKey;

	internal string NxmExpires;

	internal TaskCompletionSource<bool> BrowserLink;

	internal string BrowserFile;

	internal readonly CancellationTokenSource Cts = new CancellationTokenSource();

	public string Key => Store.Key(Domain, ModId);

	public bool Finished => Status == "done" || Status == "failed" || Status == "canceled";

	public JObject ToJson() => new JObject
	{
		["id"] = Id,
		["key"] = Key,
		["domain"] = Domain,
		["modId"] = ModId,
		["fileId"] = FileId,
		["fileName"] = FileName,
		["fileTitle"] = FileTitle,
		["version"] = Version,
		["modName"] = ModName,
		["game"] = Engine.GameName(Domain),
		["status"] = Status,
		["message"] = Message,
		["received"] = Received,
		["total"] = Total,
		["speed"] = Speed,
		["createdAt"] = CreatedAt,
		["path"] = Path
	};
}

/// <summary>
/// 下载队列。会员：直接用接口拿下载地址。普通用户：在「N网」页打开这个文件的下载页，用户点「Slow download」后，
/// 网页生成的 nxm:// 链接（或普通下载）会被这里截住，下载到对应游戏的 mod 文件夹。
/// </summary>
public static class Downloader
{
	private static readonly List<DownloadJob> Jobs = new List<DownloadJob>();

	private static readonly SemaphoreSlim DownloadGate = new SemaphoreSlim(2);

	private static readonly SemaphoreSlim BrowserGate = new SemaphoreSlim(1);

	private static readonly HttpClient Http = CreateClient();

	/// <summary>任务有变化（在任意线程触发）。</summary>
	public static event Action<DownloadJob> Changed;

	/// <summary>普通用户：需要在「N网」页打开这个文件的下载页（在任意线程触发）。</summary>
	public static event Action<DownloadJob> NeedsBrowser;

	/// <summary>当前在等网页下载的任务。</summary>
	public static DownloadJob BrowserJob { get; private set; }

	private static HttpClient CreateClient()
	{
		ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
		var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		c.DefaultRequestHeaders.UserAgent.ParseAdd("NexusModUpdater/" + Updater.CurrentVersion);
		return c;
	}

	public static List<DownloadJob> All()
	{
		lock (Jobs)
		{
			return Jobs.ToList();
		}
	}

	public static bool Busy(string key)
	{
		lock (Jobs)
		{
			return Jobs.Any(j => j.Key == key && !j.Finished);
		}
	}

	public static void ClearFinished()
	{
		lock (Jobs)
		{
			Jobs.RemoveAll(j => j.Finished);
		}
	}

	private static void Raise(DownloadJob job)
	{
		try
		{
			Changed?.Invoke(job);
		}
		catch
		{
		}
	}

	public static DownloadJob Enqueue(string domain, int modId, NexusFile file, string modName, string nxmKey = null, string nxmExpires = null)
	{
		var job = new DownloadJob
		{
			Domain = domain,
			ModId = modId,
			FileId = file.FileId,
			FileName = file.FileName,
			FileTitle = file.Name,
			Version = file.Version,
			Uploaded = file.Uploaded,
			ModName = modName,
			NxmKey = nxmKey,
			NxmExpires = nxmExpires,
			Total = file.SizeKb * 1024
		};
		lock (Jobs)
		{
			// 同一个文件已经在队列里就不再加
			DownloadJob same = Jobs.FirstOrDefault(j => j.FileId == job.FileId && j.Domain == job.Domain && !j.Finished);
			if (same != null)
			{
				if (nxmKey != null && same.Status == "browser")
				{
					same.NxmKey = nxmKey;
					same.NxmExpires = nxmExpires;
					same.BrowserLink?.TrySetResult(true);
				}
				return same;
			}
			Jobs.Add(job);
		}
		Raise(job);
		_ = Task.Run(() => RunAsync(job));
		return job;
	}

	public static void Cancel(string id)
	{
		DownloadJob job;
		lock (Jobs)
		{
			job = Jobs.FirstOrDefault(j => j.Id == id);
		}
		if (job == null || job.Finished)
		{
			return;
		}
		job.Cts.Cancel();
		job.BrowserLink?.TrySetCanceled();
	}

	public static void Retry(string id)
	{
		DownloadJob job;
		lock (Jobs)
		{
			job = Jobs.FirstOrDefault(j => j.Id == id);
			if (job == null || !job.Finished || job.Status == "done")
			{
				return;
			}
			Jobs.Remove(job);
		}
		Enqueue(job.Domain, job.ModId, new NexusFile { FileId = job.FileId, FileName = job.FileName, Name = job.FileTitle, Version = job.Version, Uploaded = job.Uploaded, SizeKb = job.Total / 1024 }, job.ModName);
	}

	private static async Task RunAsync(DownloadJob job)
	{
		CancellationToken ct = job.Cts.Token;
		string tmp = null;
		try
		{
			GameSetting game;
			lock (Store.Lock)
			{
				game = Store.Cfg.Game(job.Domain);
			}
			if (string.IsNullOrEmpty(game.Folder))
			{
				throw new InvalidOperationException("还没有设置《" + Engine.GameName(job.Domain) + "》的 mod 文件夹");
			}
			string url = null;
			bool premium;
			lock (Store.Lock)
			{
				premium = Store.St.Premium;
			}
			if (job.NxmKey == null && premium)
			{
				job.Status = "link";
				job.Message = "正在获取下载地址…";
				Raise(job);
				try
				{
					url = await NexusApi.DownloadUrlAsync(job.Domain, job.ModId, job.FileId).ConfigureAwait(false);
				}
				catch (NexusException ex) when (ex.Status == 403)
				{
					lock (Store.Lock)
					{
						Store.St.Premium = false;
					}
				}
			}
			if (url == null && job.NxmKey == null)
			{
				// 普通用户：一次只让一个任务占用「N网」页
				job.Status = "browser";
				job.Message = "排队等待网页下载…";
				Raise(job);
				await BrowserGate.WaitAsync(ct).ConfigureAwait(false);
				try
				{
					if (job.NxmKey == null)
					{
						job.BrowserLink = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
						BrowserJob = job;
						job.Message = "请在「N网」页点击「Slow download」（慢速下载）";
						Raise(job);
						NeedsBrowser?.Invoke(job);
						using (ct.Register(() => job.BrowserLink.TrySetCanceled()))
						{
							Task done = await Task.WhenAny(job.BrowserLink.Task, Task.Delay(TimeSpan.FromMinutes(15), ct)).ConfigureAwait(false);
							if (done != job.BrowserLink.Task)
							{
								throw new TimeoutException("等了 15 分钟没有在网页上点下载");
							}
							await job.BrowserLink.Task.ConfigureAwait(false);
						}
					}
				}
				finally
				{
					if (BrowserJob == job)
					{
						BrowserJob = null;
					}
					BrowserGate.Release();
				}
			}
			if (job.BrowserFile != null)
			{
				// 网页自己下载好了
				tmp = job.BrowserFile;
			}
			else
			{
				if (url == null)
				{
					job.Status = "link";
					job.Message = "正在获取下载地址…";
					Raise(job);
					url = await NexusApi.DownloadUrlAsync(job.Domain, job.ModId, job.FileId, job.NxmKey, job.NxmExpires).ConfigureAwait(false);
				}
				await DownloadGate.WaitAsync(ct).ConfigureAwait(false);
				try
				{
					tmp = await FetchAsync(job, url, ct).ConfigureAwait(false);
				}
				finally
				{
					DownloadGate.Release();
				}
			}
			ct.ThrowIfCancellationRequested();
			job.Status = "installing";
			job.Message = "正在安装…";
			Raise(job);
			string name = string.IsNullOrEmpty(job.FileName) ? System.IO.Path.GetFileName(tmp) : job.FileName;
			InstallRecord old = Engine.OldFor(job.Domain, job.ModId, job.FileId);
			string modName = string.IsNullOrEmpty(job.FileTitle) ? job.ModName : job.FileTitle;
			InstallRecord rec = Installer.Install(tmp, name, game, modName, old, s =>
			{
				job.Message = s;
				Raise(job);
			});
			tmp = null;
			rec.Domain = job.Domain;
			rec.ModId = job.ModId;
			rec.FileId = job.FileId;
			rec.FileTitle = job.FileTitle;
			rec.Version = job.Version;
			rec.Uploaded = job.Uploaded;
			Engine.OnInstalled(rec, old);
			job.Path = rec.Path;
			job.Status = "done";
			job.Message = "已安装到 " + rec.Path;
			Logger.Log("下载完成：" + job.Key + " " + job.FileName + " → " + rec.Path);
		}
		catch (OperationCanceledException)
		{
			job.Status = "canceled";
			job.Message = "已取消";
		}
		catch (Exception ex)
		{
			job.Status = "failed";
			job.Message = ex.Message;
			Logger.Log("下载失败：" + job.Key + " " + job.FileName + "：" + ex);
		}
		finally
		{
			if (tmp != null)
			{
				try
				{
					File.Delete(tmp);
				}
				catch
				{
				}
			}
			Raise(job);
		}
	}

	private static async Task<string> FetchAsync(DownloadJob job, string url, CancellationToken ct)
	{
		job.Status = "downloading";
		job.Message = "";
		Raise(job);
		string ext = System.IO.Path.GetExtension(job.FileName);
		if (string.IsNullOrEmpty(ext))
		{
			ext = System.IO.Path.GetExtension(new Uri(url).AbsolutePath);
		}
		string tmp = System.IO.Path.Combine(Paths.Temp, "dl-" + job.Id + ext);
		for (int attempt = 0; ; attempt++)
		{
			try
			{
				using HttpResponseMessage resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
				resp.EnsureSuccessStatusCode();
				if (string.IsNullOrEmpty(job.FileName))
				{
					job.FileName = resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? System.IO.Path.GetFileName(HttpUtility.UrlDecode(new Uri(url).AbsolutePath));
				}
				job.Total = resp.Content.Headers.ContentLength ?? job.Total;
				using Stream input = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
				using FileStream output = File.Create(tmp);
				var buffer = new byte[1 << 17];
				long read = 0;
				DateTime last = DateTime.UtcNow;
				long lastRead = 0;
				int n;
				while ((n = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
				{
					await output.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
					read += n;
					job.Received = read;
					double secs = (DateTime.UtcNow - last).TotalSeconds;
					if (secs >= 0.5)
					{
						job.Speed = (read - lastRead) / secs;
						last = DateTime.UtcNow;
						lastRead = read;
						Raise(job);
					}
				}
				return tmp;
			}
			catch (Exception ex) when (!(ex is OperationCanceledException) && attempt < 1)
			{
				Logger.Log("下载出错，重试：" + ex.Message);
				await Task.Delay(1500, ct).ConfigureAwait(false);
			}
		}
	}

	// ---- 网页 ----

	private static readonly Regex NxmRe = new Regex(@"^nxm://([^/]+)/mods/(\d+)/files/(\d+)", RegexOptions.IgnoreCase);

	/// <summary>网页里点「Slow download」（mod 管理器下载）生成的 nxm 链接。返回 true = 已处理。</summary>
	public static bool HandleNxm(string uri)
	{
		Match m = NxmRe.Match(uri ?? "");
		if (!m.Success)
		{
			return false;
		}
		string domain = m.Groups[1].Value.ToLowerInvariant();
		int modId = int.Parse(m.Groups[2].Value);
		int fileId = int.Parse(m.Groups[3].Value);
		var q = HttpUtility.ParseQueryString(new Uri(uri.Replace("nxm://", "https://")).Query);
		string key = q["key"];
		string expires = q["expires"];
		Logger.Log("截获 nxm 链接：" + domain + "/" + modId + " 文件 " + fileId);
		DownloadJob job;
		lock (Jobs)
		{
			job = Jobs.FirstOrDefault(j => j.Domain == domain && j.FileId == fileId && !j.Finished);
		}
		if (job != null)
		{
			job.NxmKey = key;
			job.NxmExpires = expires;
			job.BrowserLink?.TrySetResult(true);
			return true;
		}
		// 用户自己在网页上点的下载：查一下文件信息再加入队列
		_ = Task.Run(async () =>
		{
			try
			{
				NexusFile file = await Engine.FindFileAsync(domain, modId, fileId).ConfigureAwait(false) ?? new NexusFile { FileId = fileId };
				if (!await Engine.EnsureFolderAsync(domain).ConfigureAwait(false))
				{
					return;
				}
				Enqueue(domain, modId, file, Engine.ModName(domain, modId), key, expires);
			}
			catch (Exception ex)
			{
				Logger.Log("处理 nxm 链接失败：" + ex.Message);
			}
		});
		return true;
	}

	private static readonly Regex PageMod = new Regex(@"nexusmods\.com/([A-Za-z0-9_\-]+)/mods/(\d+)", RegexOptions.IgnoreCase);

	/// <summary>网页里的普通下载（Manual download → Slow download）：存到临时文件夹，下完后安装。在 UI 线程上调用。</summary>
	public static void HandleBrowserDownload(CoreWebView2DownloadStartingEventArgs e, string pageUrl)
	{
		string suggested = System.IO.Path.GetFileName(e.ResultFilePath);
		DownloadJob job = BrowserJob;
		string domain = null;
		int modId = 0;
		Match pm = PageMod.Match(pageUrl ?? "");
		if (pm.Success)
		{
			domain = pm.Groups[1].Value.ToLowerInvariant();
			modId = int.Parse(pm.Groups[2].Value);
		}
		bool named = Installer.ParseName(suggested, out int fileModId, out _);
		if (job != null && !(job.Domain == domain && job.ModId == modId) && !(named && fileModId == job.ModId))
		{
			job = null;
		}
		if (job == null)
		{
			if (domain == null)
			{
				// 不是 mod 页面上的下载：交给浏览器默认处理
				return;
			}
			if (named)
			{
				modId = fileModId;
			}
			bool hasFolder;
			lock (Store.Lock)
			{
				hasFolder = !string.IsNullOrEmpty(Store.Cfg.Game(domain).Folder);
			}
			if (!hasFolder)
			{
				return;
			}
			job = new DownloadJob { Domain = domain, ModId = modId, FileName = suggested, FileTitle = suggested, ModName = Engine.ModName(domain, modId) };
			lock (Jobs)
			{
				Jobs.Add(job);
			}
		}
		else
		{
			if (!string.IsNullOrEmpty(suggested))
			{
				job.FileName = suggested;
			}
		}
		string tmp = System.IO.Path.Combine(Paths.Temp, "dl-" + job.Id + System.IO.Path.GetExtension(suggested));
		e.ResultFilePath = tmp;
		e.Handled = true;
		CoreWebView2DownloadOperation op = e.DownloadOperation;
		job.Status = "downloading";
		job.Message = "网页下载中…";
		job.Total = (long)(op.TotalBytesToReceive ?? 0);
		Raise(job);
		DateTime last = DateTime.UtcNow;
		long lastRead = 0;
		op.BytesReceivedChanged += (s, a) =>
		{
			job.Received = op.BytesReceived;
			job.Total = (long)(op.TotalBytesToReceive ?? (ulong)job.Total);
			double secs = (DateTime.UtcNow - last).TotalSeconds;
			if (secs >= 0.5)
			{
				job.Speed = (job.Received - lastRead) / secs;
				last = DateTime.UtcNow;
				lastRead = job.Received;
				Raise(job);
			}
		};
		DownloadJob captured = job;
		bool fromQueue = captured.BrowserLink != null;
		op.StateChanged += (s, a) =>
		{
			if (op.State == CoreWebView2DownloadState.Completed)
			{
				captured.BrowserFile = op.ResultFilePath;
				if (fromQueue)
				{
					captured.BrowserLink.TrySetResult(true);
				}
				else
				{
					_ = Task.Run(() => InstallBrowserFileAsync(captured));
				}
			}
			else if (op.State == CoreWebView2DownloadState.Interrupted)
			{
				captured.Status = "failed";
				captured.Message = "网页下载中断：" + op.InterruptReason;
				if (fromQueue)
				{
					captured.BrowserLink.TrySetException(new IOException(captured.Message));
				}
				Raise(captured);
			}
		};
	}

	private static async Task InstallBrowserFileAsync(DownloadJob job)
	{
		try
		{
			GameSetting game;
			lock (Store.Lock)
			{
				game = Store.Cfg.Game(job.Domain);
			}
			if (job.ModId > 0)
			{
				NexusFile f = await Engine.FindFileByNameAsync(job.Domain, job.ModId, job.FileName).ConfigureAwait(false);
				if (f != null)
				{
					job.FileId = f.FileId;
					job.FileTitle = f.Name;
					job.Version = f.Version;
					job.Uploaded = f.Uploaded;
				}
			}
			job.Status = "installing";
			Raise(job);
			InstallRecord old = job.ModId > 0 ? Engine.OldFor(job.Domain, job.ModId, job.FileId) : null;
			InstallRecord rec = Installer.Install(job.BrowserFile, job.FileName, game, string.IsNullOrEmpty(job.FileTitle) ? job.ModName : job.FileTitle, old, null);
			rec.Domain = job.Domain;
			rec.ModId = job.ModId;
			rec.FileId = job.FileId;
			rec.FileTitle = job.FileTitle;
			rec.Version = job.Version;
			rec.Uploaded = job.Uploaded;
			if (job.ModId > 0)
			{
				Engine.OnInstalled(rec, old);
			}
			job.Path = rec.Path;
			job.Status = "done";
			job.Message = "已安装到 " + rec.Path;
		}
		catch (Exception ex)
		{
			job.Status = "failed";
			job.Message = ex.Message;
			Logger.Log("安装网页下载的文件失败：" + ex);
		}
		Raise(job);
	}
}
