using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace NexusModUpdater;

/// <summary>一个游戏的 mod 文件夹设置。</summary>
public sealed class GameSetting
{
	public string Domain = "";

	public string Name = "";

	/// <summary>mod 文件夹；空 = 还没设置，下载时会问。</summary>
	public string Folder = "";

	/// <summary>subfolder = 解压到以 mod 命名的子文件夹；root = 解压到 mod 文件夹本身（覆盖）；archive = 只保存压缩包。</summary>
	public string Mode = "subfolder";

	/// <summary>更新时把旧版本移到回收站。</summary>
	public bool RemoveOld = true;
}

/// <summary>全部设置，存在 exe 旁边的 nmu.config.json。</summary>
public sealed class Config
{
	public string ApiKey = "";

	public string Theme = "system";

	public string Backdrop = "acrylic";

	public int TintPercent = 30;

	/// <summary>bing / google / openai / off</summary>
	public string TranslateEngine = "bing";

	/// <summary>zh-Hans / zh-Hant / en / ja / ko</summary>
	public string TargetLang = "zh-Hans";

	public bool AutoTranslate = true;

	/// <summary>N网页面（评论区、简介）打开时自动翻译。</summary>
	public bool TranslateSite = true;

	public string OpenAiBase = "https://api.deepseek.com/v1";

	public string OpenAiKey = "";

	public string OpenAiModel = "deepseek-chat";

	/// <summary>从网页的下载记录读取已下载的 mod（需要在「N网」页登录）。</summary>
	public bool UseHistory = true;

	/// <summary>把 N 网上「追踪」的 mod 也算进来。</summary>
	public bool UseTracked = true;

	/// <summary>扫描各游戏 mod 文件夹里 N 网命名的压缩包（名字-ID-版本-时间戳.zip）。</summary>
	public bool ScanFolders = true;

	public bool SyncOnStart = true;

	public bool CheckAppUpdate = true;

	public Dictionary<string, GameSetting> Games = new Dictionary<string, GameSetting>(StringComparer.OrdinalIgnoreCase);

	public GameSetting Game(string domain)
	{
		if (!Games.TryGetValue(domain, out GameSetting g))
		{
			g = new GameSetting { Domain = domain };
			Games[domain] = g;
		}
		return g;
	}

	public static Config Load(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				Config c = JsonConvert.DeserializeObject<Config>(File.ReadAllText(path, Encoding.UTF8)) ?? new Config();
				c.Games = new Dictionary<string, GameSetting>(c.Games ?? new Dictionary<string, GameSetting>(), StringComparer.OrdinalIgnoreCase);
				c.TintPercent = Math.Max(0, Math.Min(100, c.TintPercent));
				return c;
			}
		}
		catch (Exception ex)
		{
			Logger.Log("读取设置失败，使用默认设置：" + ex.Message);
		}
		return new Config();
	}

	public void Save(string path)
	{
		Store.WriteAtomic(path, JsonConvert.SerializeObject(this, Formatting.Indented));
	}
}
