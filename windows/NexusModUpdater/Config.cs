using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

	/// <summary>自己起的显示名（比如中文名）；空 = 自动。</summary>
	public string Alias = "";
}

/// <summary>全部设置，存在 exe 旁边的 nmu.config.json。</summary>
public sealed class Config
{
	public string ApiKey = "";

	public string Theme = "system";

	public string Backdrop = "acrylic";

	public int TintPercent = 30;

	/// <summary>mymemory（免费，不用 Key）/ deepseek / azure / deepl / google / openai / off</summary>
	public string TranslateEngine = "mymemory";

	public string MyMemoryEmail = "";

	public string DeepSeekKey = "";

	/// <summary>DeepSeek 模型：deepseek-flash（便宜、快）/ deepseek-v4-pro（更强、更贵），也可以填新出的模型名。</summary>
	public string DeepSeekModel = "deepseek-flash";

	/// <summary>DeepSeek 思考模式：off 关闭 / auto 长文才开 / on 总是开。</summary>
	public string DeepSeekThink;

	/// <summary>auto 时，超过这么多词（中日文按字数）才开思考。</summary>
	public int DeepSeekThinkWords = 300;

	/// <summary>思考强度：low / high / max。</summary>
	public string DeepSeekEffort = "high";

	/// <summary>1.0.2 的开关，只用来迁移到 DeepSeekThink。</summary>
	public bool DeepSeekThinking = false;

	/// <summary>大模型翻译用流式输出。</summary>
	public bool LlmStream = true;

	public string AzureKey = "";

	/// <summary>Azure 翻译资源的区域（如 eastasia）；全局资源留空。</summary>
	public string AzureRegion = "";

	public string DeepLKey = "";

	public string GoogleKey = "";

	/// <summary>zh-Hans / zh-Hant / en / ja / ko</summary>
	public string TargetLang = "zh-Hans";

	public bool AutoTranslate = true;

	/// <summary>N网页面（评论区、简介）打开时自动翻译。</summary>
	public bool TranslateSite = true;

	public string OpenAiBase = "https://api.deepseek.com";

	public string OpenAiKey = "";

	public string OpenAiModel = "deepseek-flash";

	/// <summary>从网页的下载记录读取已下载的 mod（需要在「N网」页登录）。</summary>
	public bool UseHistory = true;

	/// <summary>把 N 网上「追踪」的 mod 也算进来。</summary>
	public bool UseTracked = true;

	/// <summary>扫描各游戏 mod 文件夹里 N 网命名的压缩包（名字-ID-版本-时间戳.zip）。</summary>
	public bool ScanFolders = true;

	public bool SyncOnStart = true;

	public bool CheckAppUpdate = true;

	public Dictionary<string, GameSetting> Games = new Dictionary<string, GameSetting>(StringComparer.OrdinalIgnoreCase);

	/// <summary>上次看的游戏（域名），下次打开直接进这个游戏；空 = 显示游戏列表。</summary>
	public string LastGame = "";

	/// <summary>快速切换栏里的游戏，按添加顺序。</summary>
	public List<string> QuickGames = new List<string>();

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
				c.QuickGames = (c.QuickGames ?? new List<string>()).Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.ToLowerInvariant()).Distinct().ToList();
				c.LastGame = (c.LastGame ?? "").ToLowerInvariant();
				// 1.0.0 的 bing（非官方接口）已去掉；1.0.1 的“还没设置”改成默认的免费翻译
				if (string.IsNullOrEmpty(c.TranslateEngine) || c.TranslateEngine == "bing")
				{
					c.TranslateEngine = "mymemory";
				}
				// 1.0.2 用的 deepseek-chat / deepseek-reasoner 已停用，改成现在的模型名和 thinking 参数
				if (c.DeepSeekThink != "off" && c.DeepSeekThink != "auto" && c.DeepSeekThink != "on")
				{
					c.DeepSeekThink = c.DeepSeekThinking ? "on" : "auto";
				}
				if (string.IsNullOrWhiteSpace(c.DeepSeekModel))
				{
					c.DeepSeekModel = "deepseek-flash";
				}
				if (c.DeepSeekEffort != "low" && c.DeepSeekEffort != "high" && c.DeepSeekEffort != "max")
				{
					c.DeepSeekEffort = "high";
				}
				c.DeepSeekThinkWords = Math.Max(1, c.DeepSeekThinkWords);
				if ((c.OpenAiBase ?? "").IndexOf("deepseek.com", StringComparison.OrdinalIgnoreCase) >= 0 && (c.OpenAiModel == "deepseek-chat" || c.OpenAiModel == "deepseek-reasoner"))
				{
					c.OpenAiModel = "deepseek-flash";
				}
				return c;
			}
		}
		catch (Exception ex)
		{
			Logger.Log("读取设置失败，使用默认设置：" + ex.Message);
		}
		return new Config { DeepSeekThink = "auto" };
	}

	public void Save(string path)
	{
		Store.WriteAtomic(path, JsonConvert.SerializeObject(this, Formatting.Indented));
	}
}
