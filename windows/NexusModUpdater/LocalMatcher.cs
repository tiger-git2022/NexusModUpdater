using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace NexusModUpdater;

/// <summary>
/// 认出 mod 文件夹里的东西是哪个 mod 的哪个文件：
/// 1. N 网命名的压缩包 / 同名文件夹：名字里就有 modID，再按文件名对上是哪个文件；
/// 2. 作者自己命名的文件夹（比如 PBZMapMarkers）、改过名的压缩包：拿 N 网给每个文件的「压缩包内容清单」
///    （里面有文件夹名、每个文件的大小和哈希）和本地文件比，对上了就知道是哪个文件的哪个版本。
/// 只看下载记录 / 追踪里这个游戏的 mod。清单不会变，取过就存下来。
/// </summary>
public static class LocalMatcher
{
	private static readonly HttpClient Http = CreateClient();

	/// <summary>一次同步最多给多少个 mod 新取文件列表（剩下的下次接着取）。</summary>
	private const int MaxNewModsPerSync = 60;

	/// <summary>一次同步最多算多少字节的哈希。</summary>
	private const long HashBudget = 1L << 30;

	private static HttpClient CreateClient()
	{
		ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
		var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = TimeSpan.FromSeconds(30) };
		c.DefaultRequestHeaders.UserAgent.ParseAdd("NexusModUpdater/" + Updater.CurrentVersion);
		return c;
	}

	private sealed class Cand
	{
		public string Key;

		public NexusFile File;

		public Manifest Manifest;
	}

	private sealed class Hit
	{
		public string Key;

		public NexusFile File;

		public string How;

		public double Score;
	}

	public static async Task MatchAsync(string domain, string folder, Action<string> progress, List<string> notes)
	{
		if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
		{
			return;
		}
		List<string> tops = Entries(folder);
		if (tops.Count == 0)
		{
			lock (Store.Lock)
			{
				foreach (string k in Store.St.Found.Keys.Where(k => k.StartsWith(domain + "/", StringComparison.OrdinalIgnoreCase)).ToList())
				{
					Store.St.Found.Remove(k);
				}
			}
			return;
		}

		// 这个游戏的 mod：最近下载的优先
		List<ModEntry> mods;
		lock (Store.Lock)
		{
			mods = Store.St.Mods.Values.Where(m => string.Equals(m.Domain, domain, StringComparison.OrdinalIgnoreCase)).OrderByDescending(m => m.LastDownload).ToList();
		}
		var modIds = new HashSet<int>(mods.Select(m => m.ModId));

		// N 网命名的：直接知道 modID
		var named = new List<(string Path, int ModId)>();
		var unnamed = new List<string>();
		void Sort(IEnumerable<string> paths)
		{
			foreach (string p in paths)
			{
				if (Installer.ParseName(Path.GetFileName(p), out int id, out _))
				{
					named.Add((p, id));
				}
				else
				{
					unnamed.Add(p);
				}
			}
		}
		Sort(tops);
		// 子文件夹（比如「已停用mod备份」）里的也看一层；只有在顶层文件夹本身认不出来时才进去
		var subs = tops.Where(Directory.Exists).Where(d => !Installer.ParseName(Path.GetFileName(d), out _, out _)).ToList();

		// 要哪些 mod 的文件列表：N 网命名的那些 + 有认不出的东西时，这个游戏的其他 mod
		var need = new List<ModEntry>();
		need.AddRange(mods.Where(m => named.Any(n => n.ModId == m.ModId)));
		if (unnamed.Count > 0 || subs.Count > 0)
		{
			need.AddRange(mods.Where(m => !need.Contains(m)));
		}
		int fetched = 0;
		int di = 0;
		foreach (ModEntry m in need)
		{
			di++;
			bool have;
			lock (Store.Lock)
			{
				have = Store.Ca.Files.TryGetValue(m.Key, out ModFiles mf) && mf.HasManifests;
			}
			if (have)
			{
				continue;
			}
			if (fetched >= MaxNewModsPerSync || NexusApi.Remaining < 80)
			{
				notes.Add("mod 文件夹里还有一些没核对完，下次检查时接着核对");
				break;
			}
			progress?.Invoke("正在核对 mod 文件夹里的文件…（取文件列表 " + di + "/" + need.Count + "）");
			try
			{
				ModFiles files = NexusApi.ParseFiles(await NexusApi.FilesAsync(m.Domain, m.ModId).ConfigureAwait(false));
				lock (Store.Lock)
				{
					Store.Ca.Files[m.Key] = files;
				}
				fetched++;
			}
			catch (NexusException ex) when (ex.Status == 404 || ex.Status == 403)
			{
				lock (Store.Lock)
				{
					Store.Ca.Files[m.Key] = new ModFiles { FetchedAt = Store.Now, HasManifests = true };
				}
			}
			catch (Exception ex)
			{
				Logger.Log("取文件列表失败（" + m.Key + "）：" + ex.Message);
			}
		}

		// 内容清单（只有有认不出的东西时才需要）
		var cands = new List<Cand>();
		if (unnamed.Count > 0 || subs.Count > 0)
		{
			var wantLinks = new List<(string Key, NexusFile File)>();
			lock (Store.Lock)
			{
				foreach (ModEntry m in mods)
				{
					if (Store.Ca.Files.TryGetValue(m.Key, out ModFiles mf))
					{
						// 每个 mod 最多看最近的 40 个文件
						foreach (NexusFile f in mf.Files.Where(f => !string.IsNullOrEmpty(f.Manifest)).OrderByDescending(f => f.Uploaded).Take(40))
						{
							wantLinks.Add((m.Key, f));
						}
					}
				}
			}
			var missing = wantLinks.Select(w => w.File.Manifest).Distinct().Where(l =>
			{
				lock (Store.Lock)
				{
					return !Store.Ca.Manifests.ContainsKey(l);
				}
			}).ToList();
			int done = 0;
			var gate = new SemaphoreSlim(4);
			await Task.WhenAll(missing.Select(async link =>
			{
				await gate.WaitAsync().ConfigureAwait(false);
				try
				{
					Manifest man = await FetchManifestAsync(link).ConfigureAwait(false);
					lock (Store.Lock)
					{
						Store.Ca.Manifests[link] = man;
					}
				}
				catch (Exception ex)
				{
					Logger.Log("取内容清单失败：" + ex.Message);
				}
				finally
				{
					gate.Release();
					int n = Interlocked.Increment(ref done);
					if (n % 10 == 0)
					{
						progress?.Invoke("正在核对 mod 文件夹里的文件…（内容清单 " + n + "/" + missing.Count + "）");
					}
				}
			})).ConfigureAwait(false);
			lock (Store.Lock)
			{
				foreach ((string key, NexusFile f) in wantLinks)
				{
					if (Store.Ca.Manifests.TryGetValue(f.Manifest, out Manifest man))
					{
						cands.Add(new Cand { Key = key, File = f, Manifest = man });
					}
				}
			}
		}

		progress?.Invoke("正在核对 mod 文件夹里的文件…");
		var hits = new List<(string Path, Hit Hit)>();
		long budget = HashBudget;
		await Task.Run(() =>
		{
			// 1. N 网命名的
			foreach ((string path, int id) in named)
			{
				if (!modIds.Contains(id))
				{
					continue;
				}
				string key = Store.Key(domain, id);
				NexusFile file = null;
				lock (Store.Lock)
				{
					if (Store.Ca.Files.TryGetValue(key, out ModFiles mf))
					{
						string bare = StripExt(Path.GetFileName(path));
						file = mf.Files.FirstOrDefault(f => string.Equals(StripExt(f.FileName), bare, StringComparison.OrdinalIgnoreCase));
					}
				}
				Installer.ParseName(Path.GetFileName(path), out _, out long ts);
				hits.Add((path, new Hit { Key = key, File = file ?? new NexusFile { Uploaded = ts, FileName = Path.GetFileName(path) }, How = "filename", Score = 1 }));
			}
			if (cands.Count == 0)
			{
				return;
			}
			// 2. 认不出名字的：先看顶层，顶层没对上的文件夹再看里面一层
			var index = BuildIndex(cands);
			var bySize = cands.Where(c => c.Manifest.ArchiveSize > 0).GroupBy(c => c.Manifest.ArchiveSize).ToDictionary(g => g.Key, g => g.ToList());
			var matchedTops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string p in unnamed)
			{
				Hit h = MatchEntry(p, index, bySize, ref budget, true);
				if (h != null)
				{
					hits.Add((p, h));
					matchedTops.Add(p);
				}
			}
			foreach (string d in subs.Where(d => !matchedTops.Contains(d)))
			{
				foreach (string p in Entries(d))
				{
					Hit h = Installer.ParseName(Path.GetFileName(p), out int id, out long ts) && modIds.Contains(id)
						? new Hit { Key = Store.Key(domain, id), File = new NexusFile { Uploaded = ts, FileName = Path.GetFileName(p) }, How = "filename", Score = 1 }
						: MatchEntry(p, index, bySize, ref budget, false);
					if (h != null)
					{
						hits.Add((p, h));
					}
				}
			}
		}).ConfigureAwait(false);

		// 每个 mod 留最可信的一条：内容对上 > 文件名 > 大小 > 只有名字；同样可信时取新的
		int Rank(string how) => how switch { "hash" => 4, "archive" => 4, "filename" => 3, "size" => 2, _ => 1 };
		lock (Store.Lock)
		{
			foreach (string k in Store.St.Found.Keys.Where(k => k.StartsWith(domain + "/", StringComparison.OrdinalIgnoreCase)).ToList())
			{
				Store.St.Found.Remove(k);
			}
			foreach (var g in hits.GroupBy(h => h.Hit.Key))
			{
				var best = g.OrderByDescending(h => Rank(h.Hit.How)).ThenByDescending(h => h.Hit.File?.Uploaded ?? 0).First();
				Store.St.Found[g.Key] = new LocalFound
				{
					Path = best.Path,
					FileId = best.Hit.File?.FileId ?? 0,
					FileName = best.Hit.File?.FileName ?? "",
					Uploaded = best.Hit.File?.Uploaded ?? 0,
					How = best.Hit.How
				};
				if (Store.St.Mods.TryGetValue(g.Key, out ModEntry e))
				{
					e.Local = true;
				}
				long up = best.Hit.File?.Uploaded ?? 0;
				if (up > 0 && best.Hit.How != "name")
				{
					Store.St.LocalFiles.TryGetValue(g.Key, out long cur);
					Store.St.LocalFiles[g.Key] = Math.Max(cur, up);
				}
				if (!string.IsNullOrEmpty(best.Hit.File?.FileName))
				{
					Store.St.LocalFileNames[g.Key] = best.Hit.File.FileName;
				}
			}
		}
		Logger.Log("核对《" + domain + "》的 mod 文件夹：" + tops.Count + " 项，认出 " + hits.Select(h => h.Hit.Key).Distinct().Count() + " 个 mod");
	}

	private static string StripExt(string name) => System.Text.RegularExpressions.Regex.Replace(name ?? "", @"\.(zip|7z|rar)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

	private static List<string> Entries(string dir)
	{
		try
		{
			return Directory.EnumerateFileSystemEntries(dir).ToList();
		}
		catch
		{
			return new List<string>();
		}
	}

	/// <summary>文件夹名（小写）→ 清单里哪些文件的第几层是这个名字。</summary>
	private static Dictionary<string, List<(Cand Cand, int Level)>> BuildIndex(List<Cand> cands)
	{
		var index = new Dictionary<string, List<(Cand, int)>>(StringComparer.OrdinalIgnoreCase);
		foreach (Cand c in cands)
		{
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (ManifestEntry f in c.Manifest.Files)
			{
				string[] segs = f.P.Split('/');
				for (int i = 0; i < segs.Length - 1; i++)
				{
					if (segs[i] != "" && seen.Add(i + "|" + segs[i]))
					{
						if (!index.TryGetValue(segs[i], out var list))
						{
							index[segs[i]] = list = new List<(Cand, int)>();
						}
						list.Add((c, i));
					}
				}
			}
		}
		return index;
	}

	private static Hit MatchEntry(string path, Dictionary<string, List<(Cand Cand, int Level)>> index, Dictionary<long, List<Cand>> bySize, ref long budget, bool top)
	{
		string name = Path.GetFileName(path);
		if (File.Exists(path))
		{
			// 改过名的压缩包：大小一样再比 MD5
			if (!Installer.IsArchive(path))
			{
				return null;
			}
			long size = new FileInfo(path).Length;
			if (!bySize.TryGetValue(size, out List<Cand> same))
			{
				return null;
			}
			string md5 = budget >= size ? Md5(path, ref budget) : "";
			Cand c = same.FirstOrDefault(x => md5 != "" && string.Equals(x.Manifest.ArchiveMd5, md5, StringComparison.OrdinalIgnoreCase));
			return c == null ? null : new Hit { Key = c.Key, File = c.File, How = "archive", Score = 1 };
		}
		if (!Directory.Exists(path) || !index.TryGetValue(name, out var list))
		{
			return null;
		}
		var scored = new List<(Cand Cand, int Level, double Hash, double Size)>();
		foreach ((Cand c, int level) in list)
		{
			int total = 0, sizeHit = 0, hashHit = 0;
			foreach (ManifestEntry f in c.Manifest.Files)
			{
				string[] segs = f.P.Split('/');
				if (segs.Length <= level + 1 || !string.Equals(segs[level], name, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				total++;
				string local = Path.Combine(path, string.Join(Path.DirectorySeparatorChar.ToString(), segs.Skip(level + 1)));
				FileInfo fi = new FileInfo(local);
				if (!fi.Exists || fi.Length != f.S)
				{
					continue;
				}
				sizeHit++;
				if (f.H != "" && budget >= fi.Length && string.Equals(Md5(local, ref budget), f.H, StringComparison.OrdinalIgnoreCase))
				{
					hashHit++;
				}
			}
			if (total > 0)
			{
				scored.Add((c, level, (double)hashHit / total, (double)sizeHit / total));
			}
		}
		if (scored.Count == 0)
		{
			return null;
		}
		var best = scored.OrderByDescending(s => s.Hash).ThenByDescending(s => s.Size).ThenBy(s => s.Level).ThenByDescending(s => s.Cand.File.Uploaded).First();
		if (best.Hash >= 0.5)
		{
			return new Hit { Key = best.Cand.Key, File = best.Cand.File, How = "hash", Score = best.Hash };
		}
		if (best.Size >= 0.8)
		{
			return new Hit { Key = best.Cand.Key, File = best.Cand.File, How = "size", Score = best.Size };
		}
		// 内容都对不上，只有文件夹名一样：只在第一层、而且只有一个 mod 有这个文件夹名时才算（不知道版本）
		var mods = scored.Where(s => s.Level == 0).Select(s => s.Cand.Key).Distinct().ToList();
		if (top && mods.Count == 1)
		{
			return new Hit { Key = mods[0], File = null, How = "name", Score = 0 };
		}
		return null;
	}

	private static string Md5(string path, ref long budget)
	{
		try
		{
			using var md5 = MD5.Create();
			using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
			budget -= s.Length;
			return BitConverter.ToString(md5.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
		}
		catch
		{
			return "";
		}
	}

	private static async Task<Manifest> FetchManifestAsync(string link)
	{
		string json = await Http.GetStringAsync(link).ConfigureAwait(false);
		JObject o = JObject.Parse(json);
		var man = new Manifest
		{
			ArchiveSize = o["archive"]?.Value<long?>("size") ?? 0,
			ArchiveMd5 = (o["archive"]?["hashes"]?.Value<string>("MD5") ?? "").ToLowerInvariant()
		};
		foreach (JObject f in (o["files"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
		{
			man.Files.Add(new ManifestEntry
			{
				P = (f.Value<string>("file_path") ?? "").Replace('\\', '/').TrimStart('/'),
				S = f.Value<long?>("file_size") ?? 0,
				H = (f["file_hashes"]?.Value<string>("MD5") ?? "").ToLowerInvariant()
			});
		}
		return man;
	}
}
