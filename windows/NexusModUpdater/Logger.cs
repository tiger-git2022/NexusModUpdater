using System;
using System.IO;
using System.Text;

namespace NexusModUpdater;

public static class Paths
{
	public static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

	public static string Config => Path.Combine(AppDir, "nmu.config.json");

	public static string State => Path.Combine(AppDir, "nmu.state.json");

	public static string Cache => Path.Combine(AppDir, "nmu.cache.json");

	public static string Translations => Path.Combine(AppDir, "nmu.translate.json");

	public static string Log => Path.Combine(AppDir, "nmu.log");

	public static string WebData => Path.Combine(AppDir, "webview2");

	public static string Temp
	{
		get
		{
			string d = Path.Combine(AppDir, "temp");
			Directory.CreateDirectory(d);
			return d;
		}
	}
}

/// <summary>日志：nmu.log，超过 2 MB 时把旧的改名为 nmu.log.old。</summary>
public static class Logger
{
	private static readonly object Lock = new object();

	public static void Log(string message)
	{
		try
		{
			lock (Lock)
			{
				var fi = new FileInfo(Paths.Log);
				if (fi.Exists && fi.Length > 2 * 1024 * 1024)
				{
					string old = Paths.Log + ".old";
					if (File.Exists(old))
					{
						File.Delete(old);
					}
					File.Move(Paths.Log, old);
				}
				File.AppendAllText(Paths.Log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine, Encoding.UTF8);
			}
		}
		catch
		{
		}
	}
}
