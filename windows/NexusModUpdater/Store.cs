using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace NexusModUpdater;

/// <summary>一个“我下载过 / 关注的”mod。来源：网页下载记录、追踪列表、本地文件夹、本程序的下载。</summary>
public sealed class ModEntry
{
	public string Domain = "";

	public int ModId;

	public string Name = "";

	public string Game = "";

	/// <summary>最后一次下载时间（Unix 秒）。0 = 不知道。</summary>
	public long LastDownload;

	/// <summary>下载记录里写的 mod 最后更新时间。</summary>
	public long HistoryUpdate;

	/// <summary>程序第一次看到它的时间；不知道下载时间时，用它当基准。</summary>
	public long FirstSeen;

	public bool InHistory;

	public bool Tracked;

	public bool Local;

	/// <summary>「标记为已是最新」时最新文件的上传时间：这之前的新文件都不算更新。</summary>
	public long MarkedLatest;

	public string Key => Store.Key(Domain, ModId);
}

/// <summary>N 网接口返回的 mod 信息（缓存）。</summary>
public sealed class ModInfo
{
	public string Name = "";

	public string Summary = "";

	public string Description = "";

	public string Picture = "";

	public string Version = "";

	public string Author = "";

	public string Uploader = "";

	public long Created;

	public long Updated;

	public int Endorsements;

	public int Downloads;

	public string Status = "";

	public bool Available = true;

	public bool Adult;

	public int CategoryId;

	public long FetchedAt;
}

public sealed class NexusFile
{
	public int FileId;

	public string Name = "";

	public string FileName = "";

	public string Version = "";

	/// <summary>1 MAIN, 2 UPDATE, 3 OPTIONAL, 4 OLD_VERSION, 5 MISCELLANEOUS, 6 DELETED, 7 ARCHIVED</summary>
	public int CategoryId;

	public string Category = "";

	public long Uploaded;

	public long SizeKb;

	public string Description = "";

	public bool Primary;

	/// <summary>N 网的压缩包内容清单地址（里面有每个文件的路径、大小、哈希）。</summary>
	public string Manifest = "";

	public bool Current => CategoryId == 1 || CategoryId == 2 || CategoryId == 3 || CategoryId == 5;
}

public sealed class FileUpdateLink
{
	public int OldId;

	public int NewId;

	public string OldName = "";

	public string NewName = "";

	public long Uploaded;
}

public sealed class ModFiles
{
	public List<NexusFile> Files = new List<NexusFile>();

	public List<FileUpdateLink> Updates = new List<FileUpdateLink>();

	public long FetchedAt;

	/// <summary>1.0.8 起记了内容清单地址；以前缓存的要重新取。</summary>
	public bool HasManifests;
}

/// <summary>压缩包内容清单（只留核对本地文件要用的）。</summary>
public sealed class Manifest
{
	public long ArchiveSize;

	public string ArchiveMd5 = "";

	/// <summary>压缩包里的文件：路径（/ 分隔）、大小、MD5。</summary>
	public List<ManifestEntry> Files = new List<ManifestEntry>();
}

public sealed class ManifestEntry
{
	public string P = "";

	public long S;

	public string H = "";
}

/// <summary>在 mod 文件夹里找到的、认出来的 mod。</summary>
public sealed class LocalFound
{
	public string Path = "";

	/// <summary>认出来是哪个文件（版本）；0 = 只认出了 mod，不知道版本。</summary>
	public int FileId;

	public string FileName = "";

	public long Uploaded;

	/// <summary>filename = N 网文件名；hash = 文件内容对上了；size = 文件大小对上了；name = 只有文件夹名对上了。</summary>
	public string How = "";
}

/// <summary>本程序装好的一个文件。</summary>
public sealed class InstallRecord
{
	public string Domain = "";

	public int ModId;

	public int FileId;

	public string FileName = "";

	public string FileTitle = "";

	public string Version = "";

	public long Uploaded;

	public long InstalledAt;

	public string Mode = "";

	/// <summary>子文件夹 / 压缩包的路径。</summary>
	public string Path = "";

	/// <summary>root 模式下解压出来的文件（相对 mod 文件夹），更新时用来删除旧文件。</summary>
	public List<string> Files = new List<string>();
}

/// <summary>temp = 暂时忽略（有更新的更新时再出现）；perm = 永久忽略。</summary>
public sealed class IgnoreEntry
{
	public string Kind = "temp";

	public long UntilUpdate;

	public long At;
}

public sealed class GameInfo
{
	public int Id;

	public string Domain = "";

	public string Name = "";
}

public sealed class State
{
	public Dictionary<string, ModEntry> Mods = new Dictionary<string, ModEntry>();

	public Dictionary<string, IgnoreEntry> Ignores = new Dictionary<string, IgnoreEntry>();

	public Dictionary<string, InstallRecord> Installs = new Dictionary<string, InstallRecord>();

	/// <summary>本程序装的每一个文件：「mod key#文件 ID」→ 记录（同一个 mod 可能装了主文件和附加文件）。</summary>
	public Dictionary<string, InstallRecord> InstallsByFile = new Dictionary<string, InstallRecord>();

	/// <summary>文件夹里扫描到的 N 网压缩包：key → 时间戳（文件上传时间）。</summary>
	public Dictionary<string, long> LocalFiles = new Dictionary<string, long>();

	public Dictionary<string, string> LocalFileNames = new Dictionary<string, string>();

	/// <summary>mod 文件夹里认出来的 mod：key → 位置和版本。</summary>
	public Dictionary<string, LocalFound> Found = new Dictionary<string, LocalFound>();

	public long LastSync;

	public string LastSyncNote = "";

	public long LastHistory;

	public string UserName = "";

	public string Avatar = "";

	public bool Premium;

	public bool KeyValid;
}

public sealed class Cache
{
	public Dictionary<string, ModInfo> Mods = new Dictionary<string, ModInfo>();

	public Dictionary<string, ModFiles> Files = new Dictionary<string, ModFiles>();

	public Dictionary<string, GameInfo> Games = new Dictionary<string, GameInfo>(StringComparer.OrdinalIgnoreCase);

	public long GamesFetched;

	/// <summary>updated.json 里的最新文件更新时间：key → 时间。</summary>
	public Dictionary<string, long> FileActivity = new Dictionary<string, long>();

	/// <summary>每个游戏上次取 updated.json 的时间。</summary>
	public Dictionary<string, long> UpdatedFetched = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	/// <summary>压缩包内容清单：清单地址 → 内容（不会变，存下来就不用再取）。</summary>
	public Dictionary<string, Manifest> Manifests = new Dictionary<string, Manifest>();

	/// <summary>游戏的中文名（从 Steam 查到的）。</summary>
	public Dictionary<string, GameMeta> GameMeta = new Dictionary<string, GameMeta>(StringComparer.OrdinalIgnoreCase);
}

public sealed class GameMeta
{
	/// <summary>Steam 上的简体中文名；没有中文名时为空。</summary>
	public string Zh = "";

	public int SteamId;

	public long FetchedAt;
}

/// <summary>设置、状态、缓存的读写。所有读写都要在 lock (Store.Lock) 里。</summary>
public static class Store
{
	public static readonly object Lock = new object();

	public static Config Cfg;

	public static State St;

	public static Cache Ca;

	private static Timer _saveTimer;

	public static string Key(string domain, int modId) => (domain ?? "").ToLowerInvariant() + "/" + modId;

	public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

	public static void Load()
	{
		Cfg = Config.Load(Paths.Config);
		St = Read<State>(Paths.State) ?? new State();
		Ca = Read<Cache>(Paths.Cache) ?? new Cache();
		Ca.Games = new Dictionary<string, GameInfo>(Ca.Games ?? new Dictionary<string, GameInfo>(), StringComparer.OrdinalIgnoreCase);
		Ca.UpdatedFetched = new Dictionary<string, long>(Ca.UpdatedFetched ?? new Dictionary<string, long>(), StringComparer.OrdinalIgnoreCase);
	}

	private static T Read<T>(string path) where T : class
	{
		try
		{
			if (File.Exists(path))
			{
				return JsonConvert.DeserializeObject<T>(File.ReadAllText(path, Encoding.UTF8));
			}
		}
		catch (Exception ex)
		{
			Logger.Log("读取 " + Path.GetFileName(path) + " 失败：" + ex.Message);
			try
			{
				File.Copy(path, path + ".bad", true);
			}
			catch
			{
			}
		}
		return null;
	}

	public static void SaveConfig()
	{
		lock (Lock)
		{
			try
			{
				Cfg.Save(Paths.Config);
			}
			catch (Exception ex)
			{
				Logger.Log("保存设置失败：" + ex.Message);
			}
		}
	}

	/// <summary>一秒后保存状态和缓存（连续多次调用只写一次）。</summary>
	public static void SaveSoon()
	{
		lock (Lock)
		{
			_saveTimer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
			_saveTimer.Change(1000, Timeout.Infinite);
		}
	}

	public static void SaveNow()
	{
		lock (Lock)
		{
			try
			{
				WriteAtomic(Paths.State, JsonConvert.SerializeObject(St));
				WriteAtomic(Paths.Cache, JsonConvert.SerializeObject(Ca));
			}
			catch (Exception ex)
			{
				Logger.Log("保存状态失败：" + ex.Message);
			}
		}
	}

	public static void WriteAtomic(string path, string text)
	{
		string tmp = path + ".tmp";
		File.WriteAllText(tmp, text, new UTF8Encoding(false));
		if (File.Exists(path))
		{
			File.Replace(tmp, path, null);
		}
		else
		{
			File.Move(tmp, path);
		}
	}
}
