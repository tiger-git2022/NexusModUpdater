using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace NexusModUpdater;

/// <summary>把下载好的文件放进游戏的 mod 文件夹：解压到子文件夹 / 解压到根目录 / 只保存压缩包。旧版本移到回收站。</summary>
public static class Installer
{
	private static readonly string[] ArchiveExt = { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz" };

	/// <summary>N 网压缩包的命名：名字-modID-版本-上传时间戳.扩展名</summary>
	/// <summary>N 网以前的文件名：名字-modID-版本-Unix时间.zip</summary>
	public static readonly Regex NexusFileName = new Regex(@"^(?<name>.+?)-(?<id>\d+)-(?<ver>[\w\-\.]*?)-?(?<ts>\d{10})\.(?<ext>zip|7z|rar)$", RegexOptions.IgnoreCase);

	/// <summary>
	/// N 网现在的文件名：名字 modID 版本 2026-11-07T13-16Z 随机串.zip（空格分隔；解压出来的文件夹也是这个名字，没有扩展名）。
	/// 靠后面的日期定位，日期前面两段就是 modID 和版本，所以名字里带数字、版本号也能认对。
	/// </summary>
	private static readonly Regex NexusFileNameV2 = new Regex(@"^(?<name>.+?)[ _](?<id>\d+)[ _](?<ver>\S+?)[ _](?<date>\d{4}-\d{2}-\d{2}T\d{2}-\d{2}(?:-\d{2})?Z)(?:[ _]\S*)?$", RegexOptions.IgnoreCase);

	private static readonly Regex ArchiveSuffix = new Regex(@"\.(zip|7z|rar)$", RegexOptions.IgnoreCase);

	/// <summary>
	/// 从 N 网下载的文件名（或解压出来的同名文件夹名）里认出 modID 和文件上传时间。新旧两种格式都认。
	/// </summary>
	public static bool ParseName(string name, out int modId, out long ts)
	{
		modId = 0;
		ts = 0;
		name = (name ?? "").Trim();
		Match m = NexusFileName.Match(name);
		if (m.Success)
		{
			modId = int.Parse(m.Groups["id"].Value);
			ts = long.Parse(m.Groups["ts"].Value);
			return true;
		}
		m = NexusFileNameV2.Match(ArchiveSuffix.Replace(name, ""));
		if (!m.Success || !int.TryParse(m.Groups["id"].Value, out modId) || modId <= 0)
		{
			modId = 0;
			return false;
		}
		string date = m.Groups["date"].Value;
		string[] formats = { "yyyy-MM-dd'T'HH-mm'Z'", "yyyy-MM-dd'T'HH-mm-ss'Z'" };
		if (DateTime.TryParseExact(date, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime dt))
		{
			ts = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeSeconds();
		}
		return ts > 0;
	}

	public static bool IsArchive(string path) => ArchiveExt.Contains(Path.GetExtension(path).ToLowerInvariant());

	public static string Safe(string name)
	{
		string s = new string((name ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
		if (s.Length > 80)
		{
			s = s.Substring(0, 80).Trim();
		}
		return s == "" ? "mod" : s;
	}

	/// <summary>安装，返回新的记录。old 是同一个 mod 上次安装的记录（可能为 null）。</summary>
	public static InstallRecord Install(string downloaded, string originalName, GameSetting game, string modName, InstallRecord old, Action<string> status)
	{
		string folder = game.Folder;
		Directory.CreateDirectory(folder);
		var rec = new InstallRecord { Mode = game.Mode, FileName = originalName, InstalledAt = Store.Now };
		bool archive = IsArchive(originalName);
		if (game.Mode == "archive" || !archive)
		{
			string dest = Path.Combine(folder, Safe(Path.GetFileNameWithoutExtension(originalName)) + Path.GetExtension(originalName));
			if (game.RemoveOld && old != null && !string.IsNullOrEmpty(old.Path) && !PathEq(old.Path, dest))
			{
				Recycle(old.Path);
			}
			if (File.Exists(dest))
			{
				Recycle(dest);
			}
			File.Move(downloaded, dest);
			rec.Path = dest;
			rec.Mode = "archive";
			return rec;
		}
		status?.Invoke("正在解压…");
		string tmp = Path.Combine(Paths.Temp, "x-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tmp);
		try
		{
			Extract(downloaded, tmp);
			// 压缩包里只有一个文件夹时，用它里面的内容
			string source = tmp;
			while (Directory.GetFiles(source).Length == 0 && Directory.GetDirectories(source).Length == 1)
			{
				source = Directory.GetDirectories(source)[0];
			}
			// 子文件夹模式下的文件夹名：作者在压缩包里起的文件夹名（UE4SS 之类按文件夹名加载的 mod 不能改名），
			// 压缩包里是散的文件、或者顶层是 ~mods / Content 这类结构文件夹时用 mod 名；
			// 替换旧版本时沿用旧文件夹的名字（你自己改过名也保留）
			string unit = source;
			string unitName = Safe(modName);
			string[] topDirs = Directory.GetDirectories(tmp);
			if (Directory.GetFiles(tmp).Length == 0 && topDirs.Length == 1 && !Structural(Path.GetFileName(topDirs[0])))
			{
				unit = topDirs[0];
				unitName = Path.GetFileName(topDirs[0]);
			}
			if (old != null && Directory.Exists(old.Path) && PathEq(Path.GetDirectoryName(old.Path), folder))
			{
				unitName = Path.GetFileName(old.Path);
			}
			if (game.Mode == "root")
			{
				if (game.RemoveOld && old != null && old.Mode == "root")
				{
					foreach (string rel in old.Files)
					{
						string p = Path.Combine(folder, rel);
						if (File.Exists(p))
						{
							Recycle(p);
						}
					}
				}
				else if (game.RemoveOld && old != null && !string.IsNullOrEmpty(old.Path) && (File.Exists(old.Path) || Directory.Exists(old.Path)) && !PathEq(old.Path, folder))
				{
					Recycle(old.Path);
				}
				rec.Files = CopyTree(source, folder);
				rec.Path = folder;
			}
			else
			{
				string dest = Path.Combine(folder, unitName);
				source = unit;
				if (game.RemoveOld && old != null && !string.IsNullOrEmpty(old.Path) && !PathEq(old.Path, dest) && !PathEq(old.Path, folder) && (Directory.Exists(old.Path) || File.Exists(old.Path)))
				{
					Recycle(old.Path);
				}
				if (Directory.Exists(dest))
				{
					if (game.RemoveOld)
					{
						Recycle(dest);
					}
				}
				Directory.CreateDirectory(dest);
				CopyTree(source, dest);
				rec.Path = dest;
				rec.Mode = "subfolder";
			}
			File.Delete(downloaded);
			return rec;
		}
		finally
		{
			try
			{
				Directory.Delete(tmp, true);
			}
			catch
			{
			}
		}
	}

	private static void Extract(string archive, string dest)
	{
		if (Path.GetExtension(archive).Equals(".zip", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
				string root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
				foreach (var e in zip.Entries)
				{
					string target = Path.GetFullPath(Path.Combine(dest, e.FullName));
					if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\"))
					{
						Directory.CreateDirectory(target);
						continue;
					}
					Directory.CreateDirectory(Path.GetDirectoryName(target));
					System.IO.Compression.ZipFileExtensions.ExtractToFile(e, target, true);
				}
				return;
			}
			catch (InvalidDataException)
			{
				// zip 里用了 .NET 不支持的压缩方式，交给 SharpCompress
				foreach (string d in Directory.GetDirectories(dest))
				{
					Directory.Delete(d, true);
				}
				foreach (string f in Directory.GetFiles(dest))
				{
					File.Delete(f);
				}
			}
		}
		using IArchive a = ArchiveFactory.Open(archive);
		string full = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
		foreach (IArchiveEntry e in a.Entries.Where(x => !x.IsDirectory))
		{
			string target = Path.GetFullPath(Path.Combine(dest, e.Key ?? ""));
			if (!target.StartsWith(full, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			Directory.CreateDirectory(Path.GetDirectoryName(target));
			e.WriteToFile(target, new ExtractionOptions { Overwrite = true });
		}
	}

	/// <summary>把 source 里的东西复制（覆盖）到 dest，返回相对路径列表。</summary>
	private static List<string> CopyTree(string source, string dest)
	{
		var list = new List<string>();
		foreach (string f in Directory.GetFiles(source, "*", System.IO.SearchOption.AllDirectories))
		{
			string rel = f.Substring(source.Length).TrimStart('\\', '/');
			string target = Path.Combine(dest, rel);
			Directory.CreateDirectory(Path.GetDirectoryName(target));
			File.Copy(f, target, true);
			list.Add(rel);
		}
		return list;
	}

	public static void Recycle(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
				Logger.Log("旧版本移到回收站：" + path);
			}
			else if (File.Exists(path))
			{
				FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
				Logger.Log("旧版本移到回收站：" + path);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("移到回收站失败：" + path + "：" + ex.Message);
		}
	}

	/// <summary>这些是游戏里的结构文件夹，不是 mod 自己的文件夹名。</summary>
	private static readonly HashSet<string> StructuralNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"~mods", "mods", "logicmods", "content", "paks", "binaries", "win64", "data", "plugins", "bepinex", "ue4ss", "scripts", "textures", "meshes", "interface", "skse", "f4se", "nativepc", "natives", "reframework", "game"
	};

	private static bool Structural(string name) => StructuralNames.Contains(name ?? "");

	public static bool PathEq(string a, string b)
	{
		try
		{
			return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// 扫描 mod 文件夹（含一层子文件夹）里 N 网命名的压缩包和解压出来的同名文件夹：modID → (上传时间, 文件名)。
	/// 自己改过名的文件夹认不出来（这些 mod 靠下载记录也能查到）。
	/// </summary>
	public static Dictionary<int, (long Ts, string Name)> Scan(string folder)
	{
		var map = new Dictionary<int, (long, string)>();
		if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
		{
			return map;
		}
		IEnumerable<string> files;
		try
		{
			// 第一层的文件和文件夹，再加每个子文件夹里的文件和文件夹（比如「已停用mod备份」里的）
			IEnumerable<string> Entries(string dir)
			{
				try
				{
					return Directory.EnumerateFiles(dir).Concat(Directory.EnumerateDirectories(dir)).ToList();
				}
				catch
				{
					return Enumerable.Empty<string>();
				}
			}
			files = Entries(folder).Concat(Directory.EnumerateDirectories(folder).SelectMany(Entries));
			foreach (string f in files)
			{
				if (!ParseName(Path.GetFileName(f), out int id, out long ts))
				{
					continue;
				}
				if (!map.TryGetValue(id, out var cur) || cur.Item1 < ts)
				{
					map[id] = (ts, Path.GetFileName(f));
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("扫描文件夹失败：" + folder + "：" + ex.Message);
		}
		return map;
	}
}
