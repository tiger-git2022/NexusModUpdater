using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace NexusModUpdater;

/// <summary>
/// 核心逻辑：同步（下载记录 + 追踪 + 本地文件夹 → 接口查 mod 信息和文件）、判断有没有更新、忽略、选文件、下载更新。
/// </summary>
public static class Engine
{
	/// <summary>列表数据变了（任意线程）。</summary>
	public static event Action StateChanged;

	/// <summary>同步进度：文字、百分比（-1 = 不知道）。</summary>
	public static event Action<string, int> SyncProgress;

	/// <summary>需要用户选某个游戏的 mod 文件夹：返回 true = 选好了。由界面设置。</summary>
	public static Func<string, Task<bool>> AskFolder;

	public static bool Syncing { get; private set; }

	private static readonly Dictionary<string, JToken> Changelogs = new Dictionary<string, JToken>();

	// 文件更新时间比下载时间晚这么多秒以上才算有更新（时钟误差）
	private const long Slack = 120;

	public static void Changed()
	{
		try
		{
			StateChanged?.Invoke();
		}
		catch
		{
		}
	}

	private static void Progress(string text, int pct = -1)
	{
		try
		{
			SyncProgress?.Invoke(text, pct);
		}
		catch
		{
		}
	}

	public static string GameName(string domain) => GameCatalog.Display(domain);

	/// <summary>N 网上的英文名（设置里记的名字用这个）。</summary>
	private static string EnglishName(string domain)
	{
		lock (Store.Lock)
		{
			if (Store.Ca.Games.TryGetValue(domain ?? "", out GameInfo g) && !string.IsNullOrEmpty(g.Name))
			{
				return g.Name;
			}
			string fromHistory = Store.St.Mods.Values.FirstOrDefault(m => m.Domain == domain && !string.IsNullOrEmpty(m.Game))?.Game;
			return string.IsNullOrEmpty(fromHistory) ? domain : fromHistory;
		}
	}

	public static string ModName(string domain, int modId)
	{
		string key = Store.Key(domain, modId);
		lock (Store.Lock)
		{
			if (Store.Ca.Mods.TryGetValue(key, out ModInfo i) && !string.IsNullOrEmpty(i.Name))
			{
				return i.Name;
			}
			return Store.St.Mods.TryGetValue(key, out ModEntry e) ? e.Name : "";
		}
	}

	// ---- 判断更新 ----

	/// <summary>“我手上的版本”的时间：下载时间、本程序安装时间、本地压缩包的上传时间里最新的；都不知道时用第一次看到它的时间。</summary>
	private static long Baseline(ModEntry e)
	{
		long b = e.LastDownload;
		if (Store.St.Installs.TryGetValue(e.Key, out InstallRecord r))
		{
			b = Math.Max(b, r.InstalledAt);
		}
		if (Store.St.LocalFiles.TryGetValue(e.Key, out long local))
		{
			b = Math.Max(b, local);
		}
		return b > 0 ? b : e.FirstSeen;
	}

	/// <summary>粗略的“最后更新”信号（不用查文件列表）。</summary>
	private static long Signal(ModEntry e)
	{
		long s = e.HistoryUpdate;
		if (Store.Ca.FileActivity.TryGetValue(e.Key, out long fa))
		{
			s = Math.Max(s, fa);
		}
		if (Store.Ca.Mods.TryGetValue(e.Key, out ModInfo i))
		{
			s = Math.Max(s, i.Updated);
		}
		return s;
	}

	/// <summary>最新文件的上传时间：有文件列表就用文件列表（准确），否则用粗略信号。</summary>
	private static long Latest(ModEntry e)
	{
		if (Store.Ca.Files.TryGetValue(e.Key, out ModFiles f) && f.Files.Count > 0)
		{
			long sig = e.HistoryUpdate;
			if (Store.Ca.FileActivity.TryGetValue(e.Key, out long fa))
			{
				sig = Math.Max(sig, fa);
			}
			long files = f.Files.Where(x => x.Current).Select(x => x.Uploaded).DefaultIfEmpty(0).Max();
			// 文件列表是在最新信号之后取的才可信
			if (f.FetchedAt + Slack >= sig)
			{
				return files;
			}
			return Math.Max(files, sig);
		}
		return Signal(e);
	}

	// ---- 按文件判断：知道你手上有哪些文件时，只看这些文件有没有新版本 ----

	/// <summary>你手上有的文件 ID：本程序装的、mod 文件夹里认出来的。</summary>
	private static HashSet<int> OwnedIdsLocked(ModEntry e, ModFiles files)
	{
		var ids = new HashSet<int>();
		string prefix = e.Key + "#";
		foreach (KeyValuePair<string, InstallRecord> kv in Store.St.InstallsByFile)
		{
			if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && kv.Value.FileId > 0)
			{
				ids.Add(kv.Value.FileId);
			}
		}
		if (Store.St.Installs.TryGetValue(e.Key, out InstallRecord rec) && rec.FileId > 0)
		{
			ids.Add(rec.FileId);
		}
		if (Store.St.Found.TryGetValue(e.Key, out LocalFound found) && found.FileId > 0)
		{
			ids.Add(found.FileId);
		}
		if (files != null && Store.St.LocalFileNames.TryGetValue(e.Key, out string localName) && !string.IsNullOrEmpty(localName))
		{
			string bare = BareName(localName);
			NexusFile byName = files.Files.FirstOrDefault(f => string.Equals(BareName(f.FileName), bare, StringComparison.OrdinalIgnoreCase));
			if (byName != null)
			{
				ids.Add(byName.FileId);
			}
		}
		return ids;
	}

	private static string BareName(string n) => System.Text.RegularExpressions.Regex.Replace(n ?? "", @"\.(zip|7z|rar)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

	/// <summary>
	/// 你手上这个文件的新版本：先沿作者设置的「旧文件 → 新文件」更新链找，再找同名的、更新的现行文件。
	/// 返回 null：这个文件就是最新的。lost = 文件已经下架了，又找不到对应的新文件。
	/// </summary>
	private static NexusFile SuccessorLocked(NexusFile owned, ModFiles files, out bool lost)
	{
		lost = false;
		int id = owned.FileId;
		var seen = new HashSet<int>();
		while (seen.Add(id))
		{
			FileUpdateLink next = files.Updates.Where(u => u.OldId == id).OrderByDescending(u => u.Uploaded).FirstOrDefault();
			if (next == null)
			{
				break;
			}
			id = next.NewId;
		}
		NexusFile chained = files.Files.FirstOrDefault(f => f.FileId == id && f.Current);
		if (chained != null && chained.FileId != owned.FileId)
		{
			return chained;
		}
		NexusFile same = files.Files.Where(f => f.Current && f.FileId != owned.FileId && string.Equals(f.Name, owned.Name, StringComparison.OrdinalIgnoreCase) && f.Uploaded > owned.Uploaded + Slack)
			.OrderByDescending(f => f.Uploaded).FirstOrDefault();
		if (same != null)
		{
			return same;
		}
		lost = !owned.Current;
		return null;
	}

	/// <summary>
	/// 知道你手上有哪些文件、文件列表也是新的时：owned = 你的文件，targets = 要更新成的新文件（已经去掉「标记为最新」之前的）。
	/// 返回 false 就按老办法（下载时间之后有没有新文件）判断。
	/// </summary>
	private static bool KnownLocked(ModEntry e, out ModFiles files, out List<NexusFile> owned, out List<NexusFile> targets)
	{
		owned = null;
		targets = null;
		if (!Store.Ca.Files.TryGetValue(e.Key, out files) || files.Files.Count == 0)
		{
			return false;
		}
		long sig = e.HistoryUpdate;
		if (Store.Ca.FileActivity.TryGetValue(e.Key, out long fa))
		{
			sig = Math.Max(sig, fa);
		}
		if (files.FetchedAt + Slack < sig)
		{
			return false;
		}
		HashSet<int> ids = OwnedIdsLocked(e, files);
		ModFiles fs = files;
		owned = files.Files.Where(f => ids.Contains(f.FileId)).ToList();
		if (owned.Count == 0)
		{
			return false;
		}
		var t = new List<NexusFile>();
		foreach (NexusFile o in owned)
		{
			NexusFile next = SuccessorLocked(o, fs, out bool lost);
			if (lost)
			{
				return false;
			}
			if (next != null && next.Uploaded > e.MarkedLatest + Slack && !t.Contains(next) && !ids.Contains(next.FileId))
			{
				t.Add(next);
			}
		}
		targets = t;
		return true;
	}

	/// <summary>你的版本、最新、有没有更新。</summary>
	private static (long Baseline, long Latest, bool Has) EvalLocked(ModEntry e)
	{
		if (KnownLocked(e, out _, out List<NexusFile> owned, out List<NexusFile> targets))
		{
			long mine = owned.Max(f => f.Uploaded);
			return (mine, targets.Count > 0 ? targets.Max(f => f.Uploaded) : mine, targets.Count > 0);
		}
		long b = Baseline(e);
		long l = Latest(e);
		return (b, l, l > b + Slack);
	}

	private static string IgnoreState(ModEntry e, long latest, out bool active)
	{
		active = false;
		if (!Store.St.Ignores.TryGetValue(e.Key, out IgnoreEntry ig))
		{
			return "none";
		}
		if (ig.Kind == "perm")
		{
			active = true;
			return "perm";
		}
		active = latest <= ig.UntilUpdate + Slack;
		return "temp";
	}

	public static JArray ListJson()
	{
		var arr = new JArray();
		lock (Store.Lock)
		{
			foreach (ModEntry e in Store.St.Mods.Values)
			{
				Store.Ca.Mods.TryGetValue(e.Key, out ModInfo info);
				Store.St.Installs.TryGetValue(e.Key, out InstallRecord rec);
				(long baseline, long latest, bool has) = EvalLocked(e);
				string ignore = IgnoreState(e, latest, out bool ignoreActive);
				bool hasUpdate = has && (info == null || info.Available);
				var sources = new JArray();
				if (e.InHistory)
				{
					sources.Add("history");
				}
				if (e.Tracked)
				{
					sources.Add("tracked");
				}
				if (e.Local || Store.St.LocalFiles.ContainsKey(e.Key))
				{
					sources.Add("local");
				}
				if (rec != null)
				{
					sources.Add("app");
				}
				var o = new JObject
				{
					["key"] = e.Key,
					["domain"] = e.Domain,
					["modId"] = e.ModId,
					["game"] = GameNameLocked(e),
					["name"] = info != null && info.Name != "" ? info.Name : e.Name,
					["summary"] = info?.Summary ?? "",
					["picture"] = info?.Picture ?? "",
					["version"] = info?.Version ?? "",
					["author"] = info?.Author ?? "",
					["available"] = info?.Available ?? true,
					["status"] = info?.Status ?? "",
					["adult"] = info?.Adult ?? false,
					["endorsements"] = info?.Endorsements ?? 0,
					["lastDownload"] = e.LastDownload,
					["baseline"] = baseline,
					["latest"] = latest,
					["hasUpdate"] = hasUpdate,
					["ignore"] = ignore,
					["ignoreActive"] = ignoreActive,
					["sources"] = sources,
					["hasInfo"] = info != null,
					["busy"] = Downloader.Busy(e.Key)
				};
				if (rec != null)
				{
					o["installed"] = new JObject { ["version"] = rec.Version, ["fileName"] = rec.FileName, ["path"] = rec.Path, ["at"] = rec.InstalledAt };
				}
				arr.Add(o);
			}
		}
		return arr;
	}

	private static string GameNameLocked(ModEntry e) => GameCatalog.DisplayLocked(e.Domain);

	public static JObject StatusJson()
	{
		lock (Store.Lock)
		{
			return new JObject
			{
				["user"] = Store.St.UserName,
				["avatar"] = Store.St.Avatar,
				["premium"] = Store.St.Premium,
				["keyValid"] = Store.St.KeyValid,
				["hasKey"] = !string.IsNullOrWhiteSpace(Store.Cfg.ApiKey),
				["lastSync"] = Store.St.LastSync,
				["note"] = Store.St.LastSyncNote,
				["syncing"] = Syncing,
				["daily"] = NexusApi.DailyRemaining,
				["hourly"] = NexusApi.HourlyRemaining,
				["autoTranslate"] = Store.Cfg.AutoTranslate && Translator.Ready,
				["wantTranslate"] = Store.Cfg.AutoTranslate,
				["translateReady"] = Translator.Ready,
				["lang"] = Store.Cfg.TargetLang,
				["lastGame"] = Store.Cfg.LastGame ?? "",
				["games"] = new JArray(Store.Cfg.Games.Values.Where(g => !string.IsNullOrEmpty(g.Folder)).Select(g => g.Domain))
			};
		}
	}

	// ---- 同步 ----

	/// <summary>
	/// 同步：读下载记录、追踪列表（不花多少次数），然后只检查 domain 这一个游戏的更新。
	/// domain 为空时只读下载记录，不查任何游戏的更新。
	/// </summary>
	public static async Task SyncAsync(string domain = null)
	{
		domain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim().ToLowerInvariant();
		if (Syncing)
		{
			return;
		}
		Syncing = true;
		Changed();
		var notes = new List<string>();
		try
		{
			bool hasKey = NexusApi.HasKey;
			Config cfg = Store.Cfg;
			if (hasKey)
			{
				Progress("正在验证 API Key…");
				try
				{
					JObject u = await NexusApi.ValidateAsync().ConfigureAwait(false);
					lock (Store.Lock)
					{
						Store.St.KeyValid = true;
						Store.St.UserName = u.Value<string>("name") ?? "";
						Store.St.Avatar = u.Value<string>("profile_url") ?? "";
						Store.St.Premium = u.Value<bool?>("is_premium") ?? false;
					}
				}
				catch (NexusException ex) when (ex.Status == 401)
				{
					lock (Store.Lock)
					{
						Store.St.KeyValid = false;
					}
					notes.Add(ex.Message);
					hasKey = false;
				}
			}
			else
			{
				notes.Add("没有填写 API Key，只能读下载记录，查不到更新详情（设置 → 账号）");
			}

			// 1. 网页下载记录
			if (cfg.UseHistory)
			{
				var progress = new Progress<string>(s => Progress(s));
				HistoryResult h = await HistoryReader.ReadAsync(new SyncProgressShim()).ConfigureAwait(false);
				if (!string.IsNullOrEmpty(h.Note))
				{
					notes.Add(h.Note);
				}
				if (h.Ok)
				{
					lock (Store.Lock)
					{
						long now = Store.Now;
						foreach (HistoryRow r in h.Rows)
						{
							string key = Store.Key(r.Domain, r.ModId);
							if (!Store.St.Mods.TryGetValue(key, out ModEntry e))
							{
								e = new ModEntry { Domain = r.Domain, ModId = r.ModId, FirstSeen = now };
								Store.St.Mods[key] = e;
							}
							e.InHistory = true;
							if (!string.IsNullOrEmpty(r.Name))
							{
								e.Name = r.Name;
							}
							if (!string.IsNullOrEmpty(r.Game))
							{
								e.Game = r.Game;
							}
							e.LastDownload = Math.Max(e.LastDownload, r.LastDownload);
							e.HistoryUpdate = Math.Max(e.HistoryUpdate, r.LastUpdate);
						}
						// 下载记录是完整的：不在里面的不再算「下载过」；只靠下载记录来的、也没装过忽略过的，从列表里去掉
						// （也会清掉之前按错列读出来的条目）
						if (h.Rows.Count > 0)
						{
							var seen = new HashSet<string>(h.Rows.Select(r => Store.Key(r.Domain, r.ModId)), StringComparer.OrdinalIgnoreCase);
							foreach (ModEntry e in Store.St.Mods.Values.Where(m => m.InHistory && !seen.Contains(m.Key)).ToList())
							{
								e.InHistory = false;
								if (!e.Tracked && !e.Local && !Store.St.Installs.ContainsKey(e.Key) && !Store.St.Ignores.ContainsKey(e.Key) && !Store.St.LocalFiles.ContainsKey(e.Key))
								{
									Store.St.Mods.Remove(e.Key);
								}
							}
						}
						Store.St.LastHistory = now;
					}
				}
			}

			// 2. 追踪的 mod
			if (cfg.UseTracked && hasKey)
			{
				Progress("正在读取追踪的 mod…");
				try
				{
					JArray tracked = await NexusApi.TrackedAsync().ConfigureAwait(false);
					lock (Store.Lock)
					{
						foreach (ModEntry e in Store.St.Mods.Values)
						{
							e.Tracked = false;
						}
						long now = Store.Now;
						foreach (JObject t in tracked.OfType<JObject>())
						{
							string td = (t.Value<string>("domain_name") ?? "").ToLowerInvariant();
							int id = t.Value<int?>("mod_id") ?? 0;
							if (td == "" || id == 0)
							{
								continue;
							}
							string key = Store.Key(td, id);
							if (!Store.St.Mods.TryGetValue(key, out ModEntry e))
							{
								e = new ModEntry { Domain = td, ModId = id, FirstSeen = now };
								Store.St.Mods[key] = e;
							}
							e.Tracked = true;
						}
					}
				}
				catch (NexusException ex)
				{
					notes.Add("读取追踪列表失败：" + ex.Message);
				}
			}

			// 3. 本地文件夹
			if (cfg.ScanFolders && domain != null)
			{
				Progress("正在扫描 mod 文件夹…");
				List<GameSetting> games;
				lock (Store.Lock)
				{
					games = Store.Cfg.Games.Values.Where(g => !string.IsNullOrEmpty(g.Folder) && string.Equals(g.Domain, domain, StringComparison.OrdinalIgnoreCase)).ToList();
				}
				foreach (GameSetting g in games)
				{
					Dictionary<int, (long Ts, string Name)> found = await Task.Run(() => Installer.Scan(g.Folder)).ConfigureAwait(false);
					lock (Store.Lock)
					{
						long now = Store.Now;
						foreach (KeyValuePair<int, (long Ts, string Name)> kv in found)
						{
							string key = Store.Key(g.Domain, kv.Key);
							if (!Store.St.Mods.TryGetValue(key, out ModEntry e))
							{
								e = new ModEntry { Domain = g.Domain.ToLowerInvariant(), ModId = kv.Key, FirstSeen = now, Name = kv.Value.Name };
								Store.St.Mods[key] = e;
							}
							e.Local = true;
							Store.St.LocalFiles[key] = kv.Value.Ts;
							Store.St.LocalFileNames[key] = kv.Value.Name;
						}
					}
				}
			}
			Changed();

			if (hasKey)
			{
				await RefreshFromApiAsync(notes, domain).ConfigureAwait(false);
				if (domain != null && cfg.ScanFolders)
				{
					string folder;
					lock (Store.Lock)
					{
						folder = Store.Cfg.Game(domain).Folder;
					}
					try
					{
						await LocalMatcher.MatchAsync(domain, folder, s => Progress(s), notes).ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						Logger.Log("核对 mod 文件夹失败：" + ex);
					}
				}
			}
			lock (Store.Lock)
			{
				// 检查过的游戏加到设置里，方便设置文件夹
				if (domain != null)
				{
					GameSetting g = Store.Cfg.Game(domain);
					if (string.IsNullOrEmpty(g.Name))
					{
						g.Name = EnglishName(domain);
					}
				}
				Store.St.LastSync = Store.Now;
				Store.St.LastSyncNote = string.Join("；", notes.Distinct());
			}
			Store.SaveConfig();
			Store.SaveSoon();
		}
		catch (Exception ex)
		{
			Logger.Log("同步失败：" + ex);
			lock (Store.Lock)
			{
				Store.St.LastSyncNote = "同步失败：" + ex.Message;
			}
		}
		finally
		{
			Syncing = false;
			Progress("", 100);
			Changed();
		}
	}

	private sealed class SyncProgressShim : IProgress<string>
	{
		public void Report(string value) => Progress(value);
	}

	private static async Task RefreshFromApiAsync(List<string> notes, string only)
	{
		// 游戏列表（名字、ID），一周取一次
		bool needGames;
		lock (Store.Lock)
		{
			needGames = Store.Now - Store.Ca.GamesFetched > 7 * 86400 || Store.St.Mods.Values.Any(m => !Store.Ca.Games.ContainsKey(m.Domain));
			needGames &= Store.Now - Store.Ca.GamesFetched > 3600;
		}
		if (needGames)
		{
			Progress("正在获取游戏列表…");
			try
			{
				JArray games = await NexusApi.GamesAsync().ConfigureAwait(false);
				lock (Store.Lock)
				{
					foreach (JObject g in games.OfType<JObject>())
					{
						string d = g.Value<string>("domain_name") ?? "";
						if (d != "")
						{
							Store.Ca.Games[d] = new GameInfo { Id = g.Value<int?>("id") ?? 0, Domain = d, Name = g.Value<string>("name") ?? d };
						}
					}
					Store.Ca.GamesFetched = Store.Now;
				}
			}
			catch (NexusException ex)
			{
				notes.Add("获取游戏列表失败：" + ex.Message);
			}
		}

		// 每个游戏最近更新过的 mod（一次请求一个游戏）
		List<string> domains;
		lock (Store.Lock)
		{
			domains = only == null ? new List<string>() : Store.St.Mods.Values.Where(m => m.Domain == only && !IsPerm(m.Key)).Select(m => m.Domain).Distinct().ToList();
		}
		int di = 0;
		foreach (string domain in domains)
		{
			di++;
			Progress("正在检查《" + GameName(domain) + "》最近的更新…");
			long since;
			lock (Store.Lock)
			{
				Store.Ca.UpdatedFetched.TryGetValue(domain, out since);
			}
			long age = Store.Now - since;
			string period = age < 86400 - 600 ? "1d" : (age < 7 * 86400 - 600 ? "1w" : "1m");
			try
			{
				JArray up = await NexusApi.UpdatedAsync(domain, period).ConfigureAwait(false);
				lock (Store.Lock)
				{
					foreach (JObject u in up.OfType<JObject>())
					{
						string key = Store.Key(domain, u.Value<int?>("mod_id") ?? 0);
						if (Store.St.Mods.ContainsKey(key))
						{
							long t = u.Value<long?>("latest_file_update") ?? 0;
							Store.Ca.FileActivity.TryGetValue(key, out long cur);
							Store.Ca.FileActivity[key] = Math.Max(cur, t);
						}
					}
					Store.Ca.UpdatedFetched[domain] = Store.Now;
				}
			}
			catch (NexusException ex)
			{
				if (ex.Status == 429)
				{
					notes.Add(ex.Message);
					return;
				}
				if (ex.Status != 404)
				{
					notes.Add("《" + GameName(domain) + "》：" + ex.Message);
				}
			}
		}

		// mod 信息：没有缓存的、或者之后有更新的
		List<ModEntry> needInfo;
		lock (Store.Lock)
		{
			needInfo = Store.St.Mods.Values.Where(e => e.Domain == only).Where(e =>
			{
				if (!Store.Ca.Mods.TryGetValue(e.Key, out ModInfo i))
				{
					// 下载记录里已经有名字和更新时间：没有更新迹象的先不查（下载记录可能上千条），打开详情时再查
					return !e.InHistory || string.IsNullOrEmpty(e.Name) || e.HistoryUpdate == 0 || (!IsPerm(e.Key) && Signal(e) > Baseline(e) + Slack);
				}
				if (IsPerm(e.Key))
				{
					return false;
				}
				long sig = e.HistoryUpdate;
				if (Store.Ca.FileActivity.TryGetValue(e.Key, out long fa))
				{
					sig = Math.Max(sig, fa);
				}
				return sig > i.FetchedAt || (Store.Now - i.FetchedAt > 30 * 86400);
			}).ToList();
		}
		await ForEachAsync(needInfo, "正在获取 mod 信息", notes, async e =>
		{
			try
			{
				ModInfo info = NexusApi.ParseMod(await NexusApi.ModAsync(e.Domain, e.ModId).ConfigureAwait(false));
				lock (Store.Lock)
				{
					Store.Ca.Mods[e.Key] = info;
				}
			}
			catch (NexusException ex) when (ex.Status == 404 || ex.Status == 403)
			{
				lock (Store.Lock)
				{
					Store.Ca.Mods[e.Key] = new ModInfo { Name = e.Name, Available = false, Status = ex.Status == 404 ? "removed" : "hidden", FetchedAt = Store.Now };
				}
			}
		}).ConfigureAwait(false);

		// 可能有更新的：取文件列表确认
		List<ModEntry> needFiles;
		lock (Store.Lock)
		{
			needFiles = Store.St.Mods.Values.Where(e => e.Domain == only).Where(e =>
			{
				if (IsPerm(e.Key))
				{
					return false;
				}
				if (Store.Ca.Mods.TryGetValue(e.Key, out ModInfo i) && !i.Available)
				{
					return false;
				}
				long sig = Signal(e);
				if (sig <= Baseline(e) + Slack)
				{
					return false;
				}
				return !Store.Ca.Files.TryGetValue(e.Key, out ModFiles f) || f.FetchedAt + Slack < sig;
			}).ToList();
		}
		await ForEachAsync(needFiles, "正在确认更新的文件", notes, async e =>
		{
			ModFiles files = NexusApi.ParseFiles(await NexusApi.FilesAsync(e.Domain, e.ModId).ConfigureAwait(false));
			lock (Store.Lock)
			{
				Store.Ca.Files[e.Key] = files;
			}
		}).ConfigureAwait(false);
	}

	private static async Task ForEachAsync(List<ModEntry> items, string label, List<string> notes, Func<ModEntry, Task> action)
	{
		int done = 0;
		bool stopped = false;
		var queue = new Queue<ModEntry>(items);
		async Task Worker()
		{
			while (true)
			{
				ModEntry e;
				lock (queue)
				{
					if (stopped || queue.Count == 0)
					{
						return;
					}
					e = queue.Dequeue();
				}
				// 留一些次数给手动操作
				if (NexusApi.Remaining < 60)
				{
					lock (queue)
					{
						if (!stopped)
						{
							stopped = true;
							notes.Add("N 网接口次数快用完了，剩下的下次再查");
						}
					}
					return;
				}
				try
				{
					await action(e).ConfigureAwait(false);
				}
				catch (NexusException ex) when (ex.Status == 429)
				{
					lock (queue)
					{
						stopped = true;
					}
					notes.Add(ex.Message);
				}
				catch (Exception ex)
				{
					Logger.Log(label + " " + e.Key + " 失败：" + ex.Message);
				}
				int n;
				lock (queue)
				{
					n = ++done;
				}
				Progress(label + "…（" + n + "/" + items.Count + "）", items.Count == 0 ? -1 : n * 100 / items.Count);
				if (n % 25 == 0)
				{
					Changed();
				}
			}
		}
		await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Worker())).ConfigureAwait(false);
	}

	private static bool IsPerm(string key) => Store.St.Ignores.TryGetValue(key, out IgnoreEntry ig) && ig.Kind == "perm";

	// ---- 忽略 ----

	/// <summary>kind：temp（暂时忽略这次更新）/ perm（永久忽略）/ none（取消忽略）。</summary>
	public static void SetIgnore(IEnumerable<string> keys, string kind)
	{
		lock (Store.Lock)
		{
			foreach (string key in keys)
			{
				if (!Store.St.Mods.TryGetValue(key, out ModEntry e))
				{
					continue;
				}
				if (kind == "none")
				{
					Store.St.Ignores.Remove(key);
				}
				else
				{
					Store.St.Ignores[key] = new IgnoreEntry { Kind = kind == "perm" ? "perm" : "temp", UntilUpdate = EvalLocked(e).Latest, At = Store.Now };
				}
			}
		}
		Store.SaveSoon();
		Changed();
	}

	/// <summary>不下载，直接当作已经是最新的（比如在别处更新过了）。</summary>
	public static void MarkUpToDate(IEnumerable<string> keys)
	{
		lock (Store.Lock)
		{
			foreach (string key in keys)
			{
				if (Store.St.Mods.TryGetValue(key, out ModEntry e))
				{
					long latest = EvalLocked(e).Latest;
					e.MarkedLatest = Math.Max(e.MarkedLatest, latest);
					e.LastDownload = Math.Max(e.LastDownload, Math.Max(Store.Now, latest));
					if (Store.St.Ignores.TryGetValue(key, out IgnoreEntry ig) && ig.Kind == "temp")
					{
						Store.St.Ignores.Remove(key);
					}
				}
			}
		}
		Store.SaveSoon();
		Changed();
	}

	public static void Remove(IEnumerable<string> keys)
	{
		lock (Store.Lock)
		{
			foreach (string key in keys)
			{
				Store.St.Mods.Remove(key);
				Store.St.Ignores.Remove(key);
			}
		}
		Store.SaveSoon();
		Changed();
	}

	// ---- 详情 ----

	private static async Task<ModFiles> FilesAsync(string domain, int modId, bool fresh)
	{
		string key = Store.Key(domain, modId);
		lock (Store.Lock)
		{
			if (Store.Ca.Files.TryGetValue(key, out ModFiles f) && (!fresh || Store.Now - f.FetchedAt < 600))
			{
				if (!Store.St.Mods.TryGetValue(key, out ModEntry e) || f.FetchedAt + Slack >= Signal(e))
				{
					return f;
				}
			}
		}
		ModFiles files = NexusApi.ParseFiles(await NexusApi.FilesAsync(domain, modId).ConfigureAwait(false));
		lock (Store.Lock)
		{
			Store.Ca.Files[key] = files;
		}
		Store.SaveSoon();
		return files;
	}

	public static async Task<NexusFile> FindFileAsync(string domain, int modId, int fileId)
	{
		ModFiles f = await FilesAsync(domain, modId, false).ConfigureAwait(false);
		NexusFile file = f.Files.FirstOrDefault(x => x.FileId == fileId);
		if (file == null)
		{
			f = await FilesAsync(domain, modId, true).ConfigureAwait(false);
			file = f.Files.FirstOrDefault(x => x.FileId == fileId);
		}
		return file;
	}

	public static async Task<NexusFile> FindFileByNameAsync(string domain, int modId, string fileName)
	{
		try
		{
			ModFiles f = await FilesAsync(domain, modId, true).ConfigureAwait(false);
			return f.Files.FirstOrDefault(x => string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase));
		}
		catch
		{
			return null;
		}
	}

	public static async Task<JObject> DetailAsync(string domain, int modId, bool force)
	{
		string key = Store.Key(domain, modId);
		ModInfo info;
		lock (Store.Lock)
		{
			Store.Ca.Mods.TryGetValue(key, out info);
		}
		var errors = new List<string>();
		if (NexusApi.HasKey && (info == null || force || Store.Now - info.FetchedAt > 6 * 3600 || string.IsNullOrEmpty(info.Description)))
		{
			try
			{
				info = NexusApi.ParseMod(await NexusApi.ModAsync(domain, modId).ConfigureAwait(false));
				lock (Store.Lock)
				{
					Store.Ca.Mods[key] = info;
				}
			}
			catch (NexusException ex)
			{
				errors.Add(ex.Message);
			}
		}
		ModFiles files = null;
		if (NexusApi.HasKey)
		{
			try
			{
				files = await FilesAsync(domain, modId, force).ConfigureAwait(false);
			}
			catch (NexusException ex)
			{
				errors.Add("文件列表：" + ex.Message);
			}
		}
		JToken changelog;
		lock (Changelogs)
		{
			Changelogs.TryGetValue(key, out changelog);
		}
		if (changelog == null && NexusApi.HasKey)
		{
			try
			{
				changelog = await NexusApi.ChangelogsAsync(domain, modId).ConfigureAwait(false);
				lock (Changelogs)
				{
					Changelogs[key] = changelog;
				}
			}
			catch (NexusException)
			{
			}
		}
		var o = new JObject { ["key"] = key, ["domain"] = domain, ["modId"] = modId, ["game"] = GameName(domain), ["errors"] = new JArray(errors) };
		lock (Store.Lock)
		{
			Store.St.Mods.TryGetValue(key, out ModEntry e);
			Store.St.Installs.TryGetValue(key, out InstallRecord rec);
			(long baseline, long latestNow, bool hasNow) = e != null ? EvalLocked(e) : (0L, 0L, false);
			if (info != null)
			{
				o["name"] = info.Name;
				o["summary"] = info.Summary;
				o["description"] = info.Description;
				o["picture"] = info.Picture;
				o["version"] = info.Version;
				o["author"] = info.Author;
				o["uploader"] = info.Uploader;
				o["created"] = info.Created;
				o["updated"] = info.Updated;
				o["endorsements"] = info.Endorsements;
				o["downloads"] = info.Downloads;
				o["available"] = info.Available;
				o["status"] = info.Status;
				o["adult"] = info.Adult;
			}
			else
			{
				o["name"] = e?.Name ?? "";
			}
			o["baseline"] = baseline;
			o["lastDownload"] = e?.LastDownload ?? 0;
			if (e != null)
			{
				o["ignore"] = IgnoreState(e, latestNow, out bool active);
				o["ignoreActive"] = active;
				o["latest"] = latestNow;
				o["hasUpdate"] = hasNow;
			}
			if (rec != null)
			{
				o["installed"] = new JObject { ["version"] = rec.Version, ["fileName"] = rec.FileName, ["fileId"] = rec.FileId, ["path"] = rec.Path, ["at"] = rec.InstalledAt };
			}
			Store.Cfg.Games.TryGetValue(domain, out GameSetting gs);
			o["folder"] = gs?.Folder ?? "";
			o["mode"] = gs?.Mode ?? "subfolder";
			if (files != null)
			{
				List<NexusFile> picks = e != null ? PlanLocked(e, files, out _, out _) : new List<NexusFile>();
				HashSet<int> mine = e != null ? OwnedIdsLocked(e, files) : new HashSet<int>();
				bool known = e != null && KnownLocked(e, out _, out _, out _);
				o["knownFiles"] = known;
				// 你没装的文件有更新：只提示，不算你的更新
				if (known)
				{
					long ownedMax = files.Files.Where(f => mine.Contains(f.FileId)).Select(f => f.Uploaded).DefaultIfEmpty(0).Max();
					o["otherNew"] = new JArray(files.Files.Where(f => f.Current && !mine.Contains(f.FileId) && picks.All(p => p.FileId != f.FileId) && f.Uploaded > ownedMax + Slack)
						.OrderBy(f => CatOrder(f.CategoryId)).Select(f => (f.CategoryId == 1 ? "主文件" : f.Category == "" ? "文件" : CatName(f.CategoryId)) + "「" + f.Name + "」"));
				}
				var arr = new JArray();
				foreach (NexusFile f in files.Files.OrderBy(f => CatOrder(f.CategoryId)).ThenByDescending(f => f.Uploaded))
				{
					arr.Add(new JObject
					{
						["fileId"] = f.FileId,
						["name"] = f.Name,
						["fileName"] = f.FileName,
						["version"] = f.Version,
						["categoryId"] = f.CategoryId,
						["category"] = f.Category,
						["uploaded"] = f.Uploaded,
						["sizeKb"] = f.SizeKb,
						["description"] = f.Description,
						["primary"] = f.Primary,
						["isNew"] = baseline > 0 && f.Uploaded > baseline + Slack,
						["recommended"] = picks.Any(p => p.FileId == f.FileId),
						["installed"] = mine.Contains(f.FileId)
					});
				}
				o["files"] = arr;
			}
		}
		o["changelog"] = ChangelogJson(changelog);
		Store.SaveSoon();
		Changed();
		return o;
	}

	private static string CatName(int c) => c switch { 1 => "主文件", 2 => "更新补丁", 3 => "附加文件", 4 => "旧版本", 5 => "其他文件", 7 => "已归档", _ => "文件" };

	private static int CatOrder(int c) => c switch { 1 => 0, 2 => 1, 3 => 2, 5 => 3, 4 => 4, 7 => 5, _ => 6 };

	private static JArray ChangelogJson(JToken t)
	{
		var arr = new JArray();
		if (t is JObject o)
		{
			foreach (JProperty p in o.Properties().Reverse())
			{
				arr.Add(new JObject { ["version"] = p.Name, ["lines"] = p.Value is JArray a ? a : new JArray(p.Value.ToString()) });
			}
		}
		return arr;
	}

	// ---- 选文件、下载 ----

	/// <summary>
	/// 更新时下哪些文件。
	/// 知道你手上有哪些文件：每个文件各自的新版本（你没装的文件不管）。
	/// 不知道：你下载之后只出了一个新文件就下它；只有一个文件就下它；否则列出来让你选（choices）。
	/// </summary>
	private static List<NexusFile> PlanLocked(ModEntry e, ModFiles files, out List<NexusFile> choices, out string reason)
	{
		choices = null;
		reason = "";
		if (KnownLocked(e, out _, out _, out List<NexusFile> targets))
		{
			if (targets.Count == 0)
			{
				reason = "你装的文件已经是最新的了";
			}
			return targets;
		}
		List<NexusFile> cur = files.Files.Where(f => f.Current).OrderBy(f => CatOrder(f.CategoryId)).ThenByDescending(f => f.Uploaded).ToList();
		if (cur.Count == 0)
		{
			reason = "这个 mod 现在没有可下载的文件";
			return new List<NexusFile>();
		}
		long baseline = Math.Max(Baseline(e), e.MarkedLatest);
		List<NexusFile> fresh = cur.Where(f => f.Uploaded > baseline + Slack).ToList();
		if (fresh.Count == 1)
		{
			return fresh;
		}
		if (fresh.Count == 0 && cur.Count == 1)
		{
			return cur;
		}
		choices = cur;
		reason = fresh.Count > 1 ? "你下载之后有 " + fresh.Count + " 个文件更新了，不知道你用的是哪个，请选择要下载的文件" : "有 " + cur.Count + " 个文件，请选择要下载哪一个";
		return new List<NexusFile>();
	}

	/// <summary>装 target 时要替换掉的旧版本：它的前一个版本（本程序装的，或 mod 文件夹里认出来的）。找不到就返回 null（不删任何东西）。</summary>
	public static InstallRecord OldFor(string domain, int modId, int targetFileId)
	{
		string key = Store.Key(domain, modId);
		lock (Store.Lock)
		{
			Store.Ca.Files.TryGetValue(key, out ModFiles files);
			NexusFile target = files?.Files.FirstOrDefault(f => f.FileId == targetFileId);
			var cands = new List<InstallRecord>();
			string prefix = key + "#";
			cands.AddRange(Store.St.InstallsByFile.Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value));
			if (Store.St.Installs.TryGetValue(key, out InstallRecord rec) && !cands.Contains(rec))
			{
				cands.Add(rec);
			}
			if (Store.St.Found.TryGetValue(key, out LocalFound found) && !string.IsNullOrEmpty(found.Path) && cands.All(c => !Installer.PathEq(c.Path, found.Path)))
			{
				cands.Add(new InstallRecord { Domain = domain, ModId = modId, FileId = found.FileId, FileName = found.FileName, Path = found.Path, Mode = Directory.Exists(found.Path) ? "subfolder" : "archive" });
			}
			if (cands.Count == 0)
			{
				return null;
			}
			if (target == null || files == null)
			{
				// 不知道要装的是哪个文件：只有一个旧版本时替换它（以前的做法）
				return cands.Count == 1 ? cands[0] : null;
			}
			foreach (InstallRecord c in cands.Where(c => c.FileId > 0))
			{
				NexusFile cf = files.Files.FirstOrDefault(f => f.FileId == c.FileId);
				if (cf == null)
				{
					continue;
				}
				if (cf.FileId == target.FileId || string.Equals(cf.Name, target.Name, StringComparison.OrdinalIgnoreCase) || SuccessorLocked(cf, files, out _)?.FileId == target.FileId)
				{
					return c;
				}
			}
			// 只认出了文件夹名的：新文件的压缩包里也是这个文件夹，就是同一个东西
			if (target.Manifest != "" && Store.Ca.Manifests.TryGetValue(target.Manifest, out Manifest man))
			{
				foreach (InstallRecord c in cands.Where(c => c.FileId == 0 && Directory.Exists(c.Path)))
				{
					string dir = Path.GetFileName(c.Path);
					if (man.Files.Any(f => f.P.Split('/')[0].Equals(dir, StringComparison.OrdinalIgnoreCase) && f.P.Contains("/")))
					{
						return c;
					}
				}
			}
			return null;
		}
	}

	public static async Task<bool> EnsureFolderAsync(string domain)
	{
		lock (Store.Lock)
		{
			if (!string.IsNullOrEmpty(Store.Cfg.Game(domain).Folder))
			{
				return true;
			}
		}
		return AskFolder != null && await AskFolder(domain).ConfigureAwait(false);
	}

	/// <summary>更新这些 mod：每个返回 {key, ok, message}。</summary>
	public static async Task<JArray> UpdateAsync(IList<string> keys)
	{
		var results = new JArray();
		var asked = new Dictionary<string, bool>();
		foreach (string key in keys)
		{
			ModEntry e;
			lock (Store.Lock)
			{
				Store.St.Mods.TryGetValue(key, out e);
			}
			if (e == null)
			{
				continue;
			}
			var r = new JObject { ["key"] = key, ["name"] = ModName(e.Domain, e.ModId) };
			try
			{
				if (!asked.TryGetValue(e.Domain, out bool hasFolder))
				{
					hasFolder = await EnsureFolderAsync(e.Domain).ConfigureAwait(false);
					asked[e.Domain] = hasFolder;
				}
				if (!hasFolder)
				{
					r["ok"] = false;
					r["message"] = "没有设置 mod 文件夹";
					results.Add(r);
					continue;
				}
				ModFiles files = await FilesAsync(e.Domain, e.ModId, true).ConfigureAwait(false);
				List<NexusFile> picks;
				List<NexusFile> choices;
				string reason;
				long baseline;
				lock (Store.Lock)
				{
					picks = PlanLocked(e, files, out choices, out reason);
					baseline = Math.Max(Baseline(e), e.MarkedLatest);
				}
				if (picks.Count > 0)
				{
					foreach (NexusFile pick in picks)
					{
						Downloader.Enqueue(e.Domain, e.ModId, pick, ModName(e.Domain, e.ModId));
					}
					r["ok"] = true;
					r["message"] = string.Join("、", picks.Select(p => p.Name + " " + p.Version));
				}
				else
				{
					r["ok"] = false;
					r["message"] = reason;
					if (choices != null)
					{
						r["needChoice"] = true;
						r["domain"] = e.Domain;
						r["modId"] = e.ModId;
						r["choices"] = new JArray(choices.Select(f => new JObject
						{
							["fileId"] = f.FileId,
							["name"] = f.Name,
							["version"] = f.Version,
							["category"] = CatName(f.CategoryId),
							["uploaded"] = f.Uploaded,
							["sizeKb"] = f.SizeKb,
							["isNew"] = f.Uploaded > baseline + Slack
						}));
					}
				}
			}
			catch (Exception ex)
			{
				r["ok"] = false;
				r["message"] = ex.Message;
			}
			results.Add(r);
		}
		Changed();
		return results;
	}

	public static async Task<JObject> DownloadFileAsync(string domain, int modId, int fileId)
	{
		if (!await EnsureFolderAsync(domain).ConfigureAwait(false))
		{
			return new JObject { ["ok"] = false, ["message"] = "没有设置 mod 文件夹" };
		}
		NexusFile f = await FindFileAsync(domain, modId, fileId).ConfigureAwait(false);
		if (f == null)
		{
			return new JObject { ["ok"] = false, ["message"] = "找不到这个文件" };
		}
		lock (Store.Lock)
		{
			string key = Store.Key(domain, modId);
			if (!Store.St.Mods.ContainsKey(key))
			{
				Store.St.Mods[key] = new ModEntry { Domain = domain, ModId = modId, FirstSeen = Store.Now, Name = ModName(domain, modId) };
			}
		}
		Downloader.Enqueue(domain, modId, f, ModName(domain, modId));
		Changed();
		return new JObject { ["ok"] = true };
	}

	/// <summary>下载安装完成：记下来，这个 mod 就算是最新的了。</summary>
	public static void OnInstalled(InstallRecord rec, InstallRecord old = null)
	{
		lock (Store.Lock)
		{
			string key = Store.Key(rec.Domain, rec.ModId);
			Store.St.Installs[key] = rec;
			if (old != null && old.FileId > 0 && old.FileId != rec.FileId)
			{
				Store.St.InstallsByFile.Remove(key + "#" + old.FileId);
			}
			if (rec.FileId > 0)
			{
				Store.St.InstallsByFile[key + "#" + rec.FileId] = rec;
			}
			if (old != null && Store.St.Found.TryGetValue(key, out LocalFound found) && Installer.PathEq(found.Path, old.Path))
			{
				Store.St.Found.Remove(key);
			}
			if (!Store.St.Mods.TryGetValue(key, out ModEntry e))
			{
				e = new ModEntry { Domain = rec.Domain, ModId = rec.ModId, FirstSeen = Store.Now, Name = ModName(rec.Domain, rec.ModId) };
				Store.St.Mods[key] = e;
			}
			e.LastDownload = Math.Max(e.LastDownload, rec.InstalledAt);
			if (Store.St.Ignores.TryGetValue(key, out IgnoreEntry ig) && ig.Kind == "temp")
			{
				Store.St.Ignores.Remove(key);
			}
		}
		Store.SaveSoon();
		Changed();
	}
}
