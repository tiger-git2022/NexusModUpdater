using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace NexusModUpdater;

/// <summary>
/// 游戏的显示名和封面。
/// 名字：自己改的名字 → 内置的常用中文名 → Steam 上的简体中文名 → N 网的英文名。
/// 封面：N 网的游戏封面图（按游戏 ID）。
/// </summary>
public static class GameCatalog
{
	/// <summary>中文名查到了一批，界面刷新一下。</summary>
	public static event Action Changed;

	private static readonly HttpClient Http = CreateClient();

	private static bool _fetching;

	/// <summary>
	/// 常用游戏的中文名。Steam 上有些游戏国区也只写英文（如星露谷物语、荒野大镖客 2），
	/// 有的还带版本后缀，所以常见的直接写在这里。
	/// </summary>
	private static readonly Dictionary<string, string> BuiltIn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["site"] = "N 网工具（Mod 管理器等）",
		["skyrim"] = "上古卷轴5：天际",
		["skyrimspecialedition"] = "上古卷轴5：天际 特别版",
		["oblivion"] = "上古卷轴4：湮没",
		["oblivionremastered"] = "上古卷轴4：湮没 重制版",
		["morrowind"] = "上古卷轴3：晨风",
		["fallout3"] = "辐射3",
		["newvegas"] = "辐射：新维加斯",
		["fallout4"] = "辐射4",
		["fallout76"] = "辐射76",
		["starfield"] = "星空",
		["stardewvalley"] = "星露谷物语",
		["baldursgate3"] = "博德之门3",
		["witcher"] = "巫师",
		["witcher2"] = "巫师2：国王刺客",
		["witcher3"] = "巫师3：狂猎",
		["cyberpunk2077"] = "赛博朋克2077",
		["eldenring"] = "艾尔登法环",
		["darksouls"] = "黑暗之魂",
		["darksoulsremastered"] = "黑暗之魂 重制版",
		["darksouls2"] = "黑暗之魂2",
		["darksouls3"] = "黑暗之魂3",
		["sekiro"] = "只狼：影逝二度",
		["reddeadredemption"] = "荒野大镖客：救赎",
		["reddeadredemption2"] = "荒野大镖客：救赎2",
		["gta5"] = "侠盗猎车手5",
		["monsterhunterworld"] = "怪物猎人：世界",
		["monsterhunterrise"] = "怪物猎人：崛起",
		["monsterhunterwilds"] = "怪物猎人：荒野",
		["masseffect"] = "质量效应",
		["masseffect2"] = "质量效应2",
		["masseffect3"] = "质量效应3",
		["masseffectlegendaryedition"] = "质量效应：传奇版",
		["masseffectandromeda"] = "质量效应：仙女座",
		["dragonage"] = "龙腾世纪：起源",
		["dragonage2"] = "龙腾世纪2",
		["dragonageinquisition"] = "龙腾世纪：审判",
		["dragonagetheveilguard"] = "龙腾世纪：影障守护者",
		["kingdomcomedeliverance"] = "天国：拯救",
		["kingdomcomedeliverance2"] = "天国：拯救2",
		["hollowknight"] = "空洞骑士",
		["hollowknightsilksong"] = "空洞骑士：丝之歌",
		["slaythespire"] = "杀戮尖塔",
		["slaythespire2"] = "杀戮尖塔2",
		["hades"] = "哈迪斯",
		["hades2"] = "哈迪斯2",
		["residentevil22019"] = "生化危机2 重制版",
		["residentevil32020"] = "生化危机3 重制版",
		["residentevilvillage"] = "生化危机8：村庄",
		["residentevil42023"] = "生化危机4 重制版",
		["finalfantasy7remake"] = "最终幻想7 重制版",
		["finalfantasy7rebirth"] = "最终幻想7 重生",
		["granbluefantasyrelink"] = "碧蓝幻想 Relink",
		["clairobscurexpedition33"] = "光与影：33号远征队",
		["crimsondesert"] = "红色沙漠",
		["phantombladezero"] = "影之刃零",
		["blackmythwukong"] = "黑神话：悟空",
		["stellarblade"] = "剑星",
		["remnant2"] = "遗迹2",
		["borderlands2"] = "无主之地2",
		["borderlands3"] = "无主之地3",
		["bloodstainedritualofthenight"] = "血污：夜之仪式",
		["divinityoriginalsin"] = "神界：原罪",
		["divinityoriginalsinenhancededition"] = "神界：原罪 加强版",
		["divinityoriginalsin2"] = "神界：原罪2",
		["divinityoriginalsin2definitiveedition"] = "神界：原罪2 终极版",
		["deusexmankinddivided"] = "杀出重围：人类分裂",
		["lifeistrange"] = "奇异人生",
		["shadowofthetombraider"] = "古墓丽影：暗影",
		["riseofthetombraider"] = "古墓丽影：崛起",
		["talesofarise"] = "破晓传说",
		["yakuza0"] = "如龙0：誓约的场所",
		["runefactory4special"] = "符文工房4 特别版",
		["snowbreakcontainmentzone"] = "尘白禁区",
		["thelegendofheroestrailsbeyondthehorizon"] = "英雄传说：界之轨迹",
		["trailsintheskyfirstchapter"] = "空之轨迹 the 1st",
		["genshinimpact"] = "原神",
		["honkaistarrail"] = "崩坏：星穹铁道",
		["zenlesszonezero"] = "绝区零",
		["wutheringwaves"] = "鸣潮",
		["palworld"] = "幻兽帕鲁",
		["valheim"] = "英灵神殿",
		["terraria"] = "泰拉瑞亚",
		["mountandblade2bannerlord"] = "骑马与砍杀2：霸主",
		["xcom2"] = "幽浮2",
		["dyinglight"] = "消逝的光芒",
		["dyinglight2"] = "消逝的光芒2",
		["nierautomata"] = "尼尔：机械纪元",
		["dragonsdogma"] = "龙之信条：黑暗觉者",
		["dragonsdogma2"] = "龙之信条2",
		["ghostoftsushima"] = "对马岛之魂",
		["godofwar"] = "战神",
		["horizonzerodawn"] = "地平线：零之曙光",
		["horizonforbiddenwest"] = "地平线：西之绝境",
		["deathstranding"] = "死亡搁浅",
		["subnautica"] = "深海迷航",
		["nomanssky"] = "无人深空",
		["rimworld"] = "环世界",
		["citiesskylines"] = "城市：天际线",
		["thesims4"] = "模拟人生4",
		["pathfinderwrathoftherighteous"] = "开拓者：正义之怒",
		["pillarsofeternity"] = "永恒之柱",
		["darkestdungeon"] = "暗黑地牢",
		["sifu"] = "师父",
		["devilmaycry5"] = "鬼泣5"
	};

	private static HttpClient CreateClient()
	{
		ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
		var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = TimeSpan.FromSeconds(20) };
		c.DefaultRequestHeaders.UserAgent.ParseAdd("NexusModUpdater/" + Updater.CurrentVersion);
		return c;
	}

	private static bool HasCjk(string s) => !string.IsNullOrEmpty(s) && s.Any(ch => ch >= 0x3400 && ch <= 0x9fff);

	/// <summary>N 网上的英文名。要在 Store.Lock 里调用。</summary>
	private static string EnglishLocked(string domain)
	{
		if (Store.Ca.Games.TryGetValue(domain ?? "", out GameInfo g) && !string.IsNullOrEmpty(g.Name))
		{
			return g.Name;
		}
		if (Store.Cfg.Games.TryGetValue(domain ?? "", out GameSetting s) && !string.IsNullOrEmpty(s.Name))
		{
			return s.Name;
		}
		return domain ?? "";
	}

	/// <summary>中文名（没有就是空）。要在 Store.Lock 里调用。</summary>
	private static string ChineseLocked(string domain)
	{
		if (BuiltIn.TryGetValue(domain ?? "", out string zh))
		{
			return zh;
		}
		return Store.Ca.GameMeta.TryGetValue(domain ?? "", out GameMeta m) && HasCjk(m.Zh) ? m.Zh : "";
	}

	/// <summary>界面上显示的名字。要在 Store.Lock 里调用。</summary>
	public static string DisplayLocked(string domain)
	{
		if (Store.Cfg.Games.TryGetValue(domain ?? "", out GameSetting s) && !string.IsNullOrWhiteSpace(s.Alias))
		{
			return s.Alias.Trim();
		}
		string zh = ChineseLocked(domain);
		return zh != "" ? zh : EnglishLocked(domain);
	}

	public static string Display(string domain)
	{
		lock (Store.Lock)
		{
			return DisplayLocked(domain);
		}
	}

	/// <summary>N 网的游戏封面（竖版 2:3，和 N 网游戏列表一样）。</summary>
	private static string CoverLocked(string domain)
	{
		return Store.Ca.Games.TryGetValue(domain ?? "", out GameInfo g) && g.Id > 0 ? "https://images.nexusmods.com/images/games/v2/" + g.Id + "/tile.jpg" : "";
	}

	/// <summary>界面用的游戏列表：下载记录里出现过的、快速切换里的、上次看的。</summary>
	public static JArray Json()
	{
		var arr = new JArray();
		lock (Store.Lock)
		{
			var domains = new HashSet<string>(Store.St.Mods.Values.Select(m => m.Domain), StringComparer.OrdinalIgnoreCase);
			domains.UnionWith(Store.Cfg.QuickGames);
			if (!string.IsNullOrEmpty(Store.Cfg.LastGame))
			{
				domains.Add(Store.Cfg.LastGame);
			}
			foreach (string d in domains)
			{
				Store.Cfg.Games.TryGetValue(d, out GameSetting s);
				string en = EnglishLocked(d);
				arr.Add(new JObject
				{
					["domain"] = d,
					["name"] = DisplayLocked(d),
					["en"] = en,
					["alias"] = s?.Alias ?? "",
					["cover"] = CoverLocked(d),
					["pinned"] = Store.Cfg.QuickGames.IndexOf(d.ToLowerInvariant()),
					["folder"] = s?.Folder ?? ""
				});
			}
		}
		_ = FetchMissingAsync();
		return arr;
	}

	public static void SetAlias(string domain, string alias)
	{
		lock (Store.Lock)
		{
			Store.Cfg.Game(domain).Alias = (alias ?? "").Trim();
		}
		Store.SaveConfig();
		Engine.Changed();
	}

	public static void SetPinned(string domain, bool on)
	{
		domain = (domain ?? "").ToLowerInvariant();
		lock (Store.Lock)
		{
			Store.Cfg.QuickGames.Remove(domain);
			if (on && domain != "")
			{
				Store.Cfg.QuickGames.Add(domain);
			}
		}
		Store.SaveConfig();
	}

	public static void SetLast(string domain)
	{
		lock (Store.Lock)
		{
			Store.Cfg.LastGame = (domain ?? "").ToLowerInvariant();
		}
		Store.SaveConfig();
	}

	private static string Norm(string s) => Regex.Replace((s ?? "").ToLowerInvariant().Replace("&", "and"), @"[^\p{L}\p{N}]+", "");

	/// <summary>
	/// 后台在 Steam 上查还没有中文名的游戏：先用英文名搜到 Steam 的游戏（名字完全一样才算），再取简体中文名。
	/// 只查一次，查到的存进缓存；一个一个慢慢查，不给 Steam 添麻烦。
	/// </summary>
	private static async Task FetchMissingAsync()
	{
		if (_fetching)
		{
			return;
		}
		_fetching = true;
		try
		{
			List<(string Domain, string En)> todo;
			lock (Store.Lock)
			{
				long now = Store.Now;
				todo = Store.St.Mods.Values.Select(m => m.Domain).Concat(Store.Cfg.QuickGames).Distinct(StringComparer.OrdinalIgnoreCase)
					.Where(d => !BuiltIn.ContainsKey(d) && Store.Ca.Games.ContainsKey(d))
					.Where(d => !Store.Ca.GameMeta.TryGetValue(d, out GameMeta m) || (!HasCjk(m.Zh) && now - m.FetchedAt > 30 * 86400))
					.Select(d => (d, EnglishLocked(d)))
					.ToList();
			}
			int done = 0;
			foreach ((string domain, string en) in todo)
			{
				var meta = new GameMeta { FetchedAt = Store.Now };
				try
				{
					string search = await Http.GetStringAsync("https://store.steampowered.com/api/storesearch/?l=english&cc=US&term=" + Uri.EscapeDataString(en)).ConfigureAwait(false);
					JToken hit = (JObject.Parse(search)["items"] as JArray)?.FirstOrDefault(i => i.Value<string>("type") == "app" && Norm(i.Value<string>("name")) == Norm(en));
					if (hit != null)
					{
						meta.SteamId = hit.Value<int>("id");
						await Task.Delay(800).ConfigureAwait(false);
						string details = await Http.GetStringAsync("https://store.steampowered.com/api/appdetails?filters=basic&l=schinese&cc=cn&appids=" + meta.SteamId).ConfigureAwait(false);
						string zh = JObject.Parse(details)[meta.SteamId.ToString()]?["data"]?.Value<string>("name") ?? "";
						meta.Zh = HasCjk(zh) ? zh.Trim() : "";
					}
				}
				catch (Exception ex)
				{
					// 网络不通、被限流：这次先不查了，下次打开再试
					Logger.Log("查游戏中文名失败（" + en + "）：" + ex.Message);
					break;
				}
				lock (Store.Lock)
				{
					Store.Ca.GameMeta[domain] = meta;
				}
				done++;
				if (done % 5 == 0)
				{
					Store.SaveSoon();
					RaiseChanged();
				}
				await Task.Delay(1200).ConfigureAwait(false);
			}
			if (done > 0)
			{
				Store.SaveSoon();
				RaiseChanged();
			}
		}
		finally
		{
			_fetching = false;
		}
	}

	private static void RaiseChanged()
	{
		try
		{
			Changed?.Invoke();
		}
		catch
		{
		}
	}
}
