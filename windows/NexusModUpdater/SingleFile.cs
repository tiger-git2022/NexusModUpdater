using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;

namespace NexusModUpdater;

/// <summary>
/// 程序是一个单独的 exe：.NET 依赖由 Costura 嵌在 exe 里；WebView2 的原生 WebView2Loader.dll 也嵌在里面，
/// 启动时释放到 webview2\loader 文件夹再告诉 WebView2 去那里找。
/// </summary>
public static class SingleFile
{
	/// <summary>以前的版本是 zip 包，程序文件夹里会有这些文件，现在都不需要了。</summary>
	private static readonly string[] OldFiles =
	{
		"Microsoft.Bcl.AsyncInterfaces.dll", "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "Microsoft.Web.WebView2.Wpf.dll",
		"Newtonsoft.Json.dll", "SharpCompress.dll", "System.Buffers.dll", "System.Memory.dll", "System.Numerics.Vectors.dll",
		"System.Runtime.CompilerServices.Unsafe.dll", "System.Text.Encoding.CodePages.dll", "System.Threading.Tasks.Extensions.dll",
		"WebView2Loader.dll", "Wpf.Ui.Abstractions.dll", "Wpf.Ui.dll", "ZstdSharp.dll", "NexusModUpdater.exe.config", "NexusModUpdater.pdb",
		@"runtimes\win-x64\native\WebView2Loader.dll"
	};

	public static void Prepare()
	{
		try
		{
			RemoveOldFiles();
		}
		catch (Exception e)
		{
			Logger.Log("清理旧版本文件失败：" + e.Message);
		}
		string dir = ExtractLoader(Path.Combine(Paths.WebData, "loader")) ?? ExtractLoader(Path.Combine(Path.GetTempPath(), "NexusModUpdater-loader"));
		if (dir != null)
		{
			UseLoader(dir);
		}
	}

	// 单独一个方法：WebView2 的程序集在清理完旧文件之后才加载
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void UseLoader(string dir) => CoreWebView2Environment.SetLoaderDllFolderPath(dir);

	/// <summary>只在确实是本程序的文件夹（有本程序的设置或记录）时才删，免得 exe 放在别的文件夹里误删别人的 dll。</summary>
	private static void RemoveOldFiles()
	{
		if (!File.Exists(Paths.Config) && !File.Exists(Paths.State))
		{
			return;
		}
		int n = 0;
		foreach (string name in OldFiles)
		{
			string p = Path.Combine(Paths.AppDir, name);
			if (File.Exists(p))
			{
				try
				{
					File.Delete(p);
					n++;
				}
				catch (Exception)
				{
				}
			}
		}
		foreach (string d in new[] { @"runtimes\win-x64\native", @"runtimes\win-x64", "runtimes" })
		{
			string p = Path.Combine(Paths.AppDir, d);
			if (Directory.Exists(p) && !Directory.EnumerateFileSystemEntries(p).Any())
			{
				Directory.Delete(p);
			}
		}
		if (n > 0)
		{
			Logger.Log("已删除旧版本留下的 " + n + " 个文件（现在都打包在 exe 里）");
		}
	}

	/// <summary>把 WebView2Loader.dll 释放到 dir，内容一样就不重写。失败返回 null。</summary>
	private static string ExtractLoader(string dir)
	{
		try
		{
			byte[] data;
			using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NexusModUpdater.WebView2Loader.dll"))
			using (var ms = new MemoryStream())
			{
				s.CopyTo(ms);
				data = ms.ToArray();
			}
			string path = Path.Combine(dir, "WebView2Loader.dll");
			if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(data))
			{
				return dir;
			}
			Directory.CreateDirectory(dir);
			string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			File.WriteAllBytes(tmp, data);
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			File.Move(tmp, path);
			return dir;
		}
		catch (Exception e)
		{
			Logger.Log("释放 WebView2Loader.dll 到 " + dir + " 失败：" + e.Message);
			return null;
		}
	}
}
