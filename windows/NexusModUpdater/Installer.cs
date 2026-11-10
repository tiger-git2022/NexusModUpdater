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
	public static readonly Regex NexusFileName = new Regex(@"^(?<name>.+?)-(?<id>\d+)-(?<ver>[\w\-\.]*?)-?(?<ts>\d{10})\.(?<ext>zip|7z|rar)$", RegexOptions.IgnoreCase);

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
				string dest = Path.Combine(folder, Safe(modName));
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

	private static bool PathEq(string a, string b)
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

	/// <summary>扫描 mod 文件夹（含一层子文件夹）里 N 网命名的压缩包：modID → (上传时间, 文件名)。</summary>
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
			files = Directory.EnumerateFiles(folder).Concat(Directory.EnumerateDirectories(folder).SelectMany(d =>
			{
				try
				{
					return Directory.EnumerateFiles(d);
				}
				catch
				{
					return Enumerable.Empty<string>();
				}
			}));
			foreach (string f in files)
			{
				Match m = NexusFileName.Match(Path.GetFileName(f));
				if (!m.Success)
				{
					continue;
				}
				int id = int.Parse(m.Groups["id"].Value);
				long ts = long.Parse(m.Groups["ts"].Value);
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
