using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NexusModUpdater;

/// <summary>
/// 翻译：MyMemory（免费公开接口，不需要 Key）、DeepSeek、微软 Azure、DeepL、谷歌云（官方接口，需要自己的 Key），
/// 或 OpenAI 兼容接口（本地模型等）。结果缓存到 nmu.translate.json。
/// </summary>
public static class Translator
{
	private static readonly HttpClient Http = CreateClient();

	private static readonly object CacheLock = new object();

	private static Dictionary<string, string> _cache;

	private static Timer _saveTimer;

	private static readonly SemaphoreSlim Gate = new SemaphoreSlim(3);

	public static readonly (string Code, string Name)[] Languages =
	{
		("zh-Hans", "简体中文"), ("zh-Hant", "繁體中文"), ("en", "English"), ("ja", "日本語"), ("ko", "한국어")
	};

	/// <summary>翻译服务设置好了吗（选了服务、填了需要的 Key）。</summary>
	public static bool Ready
	{
		get
		{
			lock (Store.Lock)
			{
				Config c = Store.Cfg;
				return c.TranslateEngine switch
				{
					"mymemory" => true,
					"deepseek" => !string.IsNullOrWhiteSpace(c.DeepSeekKey),
					"azure" => !string.IsNullOrWhiteSpace(c.AzureKey),
					"deepl" => !string.IsNullOrWhiteSpace(c.DeepLKey),
					"google" => !string.IsNullOrWhiteSpace(c.GoogleKey),
					"openai" => !string.IsNullOrWhiteSpace(c.OpenAiBase),
					_ => false
				};
			}
		}
	}

	private static HttpClient CreateClient()
	{
		ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
		var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
		var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
		c.DefaultRequestHeaders.UserAgent.ParseAdd("NexusModUpdater/" + Updater.CurrentVersion);
		return c;
	}

	private static Dictionary<string, string> CacheMap
	{
		get
		{
			if (_cache == null)
			{
				try
				{
					_cache = File.Exists(Paths.Translations) ? JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(Paths.Translations, Encoding.UTF8)) : null;
				}
				catch
				{
				}
				_cache ??= new Dictionary<string, string>();
			}
			return _cache;
		}
	}

	private static void SaveSoon()
	{
		_saveTimer ??= new Timer(_ =>
		{
			lock (CacheLock)
			{
				try
				{
					// 缓存太大时丢掉一半
					if (_cache.Count > 60000)
					{
						foreach (string k in _cache.Keys.Take(_cache.Count / 2).ToList())
						{
							_cache.Remove(k);
						}
					}
					Store.WriteAtomic(Paths.Translations, JsonConvert.SerializeObject(_cache));
				}
				catch (Exception ex)
				{
					Logger.Log("保存翻译缓存失败：" + ex.Message);
				}
			}
		}, null, Timeout.Infinite, Timeout.Infinite);
		_saveTimer.Change(3000, Timeout.Infinite);
	}

	public static void ClearCache()
	{
		lock (CacheLock)
		{
			CacheMap.Clear();
			SaveSoon();
		}
	}

	/// <summary>这段文字需要翻译吗（已经是目标语言、没有字母的不翻）。</summary>
	public static bool Needs(string text, string target)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		int han = 0, kana = 0, hangul = 0, latin = 0, other = 0;
		foreach (char ch in text)
		{
			if ((ch >= 0x3400 && ch <= 0x4dbf) || (ch >= 0x4e00 && ch <= 0x9fff) || (ch >= 0xf900 && ch <= 0xfaff))
			{
				han++;
			}
			else if (ch >= 0x3040 && ch <= 0x30ff)
			{
				kana++;
			}
			else if ((ch >= 0xac00 && ch <= 0xd7af) || (ch >= 0x1100 && ch <= 0x11ff) || (ch >= 0x3130 && ch <= 0x318f))
			{
				hangul++;
			}
			else if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
			{
				latin++;
			}
			else if (char.IsLetter(ch))
			{
				other++;
			}
		}
		switch (target)
		{
		case "zh-Hans":
		case "zh-Hant":
		{
			// 以汉字为主的不翻；日文（有假名）、韩文、英文、俄文等外文都翻
			if (kana >= 2 || hangul >= 2)
			{
				return true;
			}
			int foreign = latin + other;
			return foreign >= 2 && han * 2 < foreign;
		}
		case "ja":
			return kana < 2 && (hangul + han + latin + other) >= 2;
		case "ko":
			return hangul * 2 < (han + kana + latin + other) && (han + kana + latin + other) >= 2;
		default:
		{
			int nonLatin = han + kana + hangul + other;
			return nonLatin >= 2 && nonLatin * 2 > latin;
		}
		}
	}

	/// <summary>翻译一批文字；不需要翻译的原样返回，失败的返回 null。target 为空时用设置里的语言。</summary>
	public static async Task<List<string>> TranslateAsync(IList<string> texts, string target = null, string engine = null, Action<string> partial = null)
	{
		bool ready = Ready;
		lock (Store.Lock)
		{
			target ??= Store.Cfg.TargetLang;
			engine ??= Store.Cfg.TranslateEngine;
		}
		var result = new string[texts.Count];
		var todo = new List<int>();
		lock (CacheLock)
		{
			for (int i = 0; i < texts.Count; i++)
			{
				string t = texts[i] ?? "";
				if (!ready || !Needs(t, target))
				{
					result[i] = t;
				}
				else if (CacheMap.TryGetValue(engine + "|" + target + "|" + t, out string hit))
				{
					result[i] = hit;
				}
				else
				{
					todo.Add(i);
				}
			}
		}
		if (todo.Count == 0)
		{
			return result.ToList();
		}
		// 同一批里重复的文字只翻一次
		List<string> unique = todo.Select(i => texts[i]).Distinct().ToList();
		Dictionary<string, string> done;
		try
		{
			done = engine switch
			{
				"azure" => await AzureAsync(unique, target).ConfigureAwait(false),
				"deepl" => await DeepLAsync(unique, target).ConfigureAwait(false),
				"google" => await GoogleAsync(unique, target).ConfigureAwait(false),
				"mymemory" => await MyMemoryAsync(unique, target).ConfigureAwait(false),
				"deepseek" or "openai" => await LlmAsync(unique, target, engine, partial).ConfigureAwait(false),
				_ => throw new InvalidOperationException("还没有设置翻译服务（设置 → 翻译）")
			};
		}
		catch (Exception ex)
		{
			Logger.Log("翻译失败（" + engine + "）：" + ex.Message);
			throw new Exception("翻译失败：" + ex.Message);
		}
		lock (CacheLock)
		{
			foreach (KeyValuePair<string, string> kv in done)
			{
				if (!string.IsNullOrEmpty(kv.Value))
				{
					CacheMap[engine + "|" + target + "|" + kv.Key] = kv.Value;
				}
			}
			SaveSoon();
		}
		foreach (int i in todo)
		{
			result[i] = done.TryGetValue(texts[i], out string v) ? v : null;
		}
		return result.ToList();
	}

	public static async Task<string> TranslateOneAsync(string text, string target = null, Action<string> partial = null)
	{
		return (await TranslateAsync(new[] { text }, target, null, partial).ConfigureAwait(false))[0];
	}

	// ---- 分批 ----

	private static List<List<string>> Batches(List<string> texts, int maxItems, int maxChars)
	{
		var batches = new List<List<string>>();
		var batch = new List<string>();
		int chars = 0;
		foreach (string t in texts)
		{
			if (batch.Count > 0 && (batch.Count >= maxItems || chars + t.Length > maxChars))
			{
				batches.Add(batch);
				batch = new List<string>();
				chars = 0;
			}
			batch.Add(t);
			chars += t.Length;
		}
		if (batch.Count > 0)
		{
			batches.Add(batch);
		}
		return batches;
	}

	private static string Clip(string t, int max) => t.Length > max ? t.Substring(0, max) : t;

	private static async Task<string> PostJsonAsync(string url, object payload, Action<HttpRequestMessage> auth, string name)
	{
		await Gate.WaitAsync().ConfigureAwait(false);
		try
		{
			using var req = new HttpRequestMessage(HttpMethod.Post, url);
			auth?.Invoke(req);
			req.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
			using HttpResponseMessage resp = await Http.SendAsync(req).ConfigureAwait(false);
			string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
			if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
			{
				throw new HttpRequestException(name + "的 Key 无效或没有权限（HTTP " + (int)resp.StatusCode + "）");
			}
			if (!resp.IsSuccessStatusCode)
			{
				throw new HttpRequestException(name + " HTTP " + (int)resp.StatusCode + " " + Short(body));
			}
			return body;
		}
		finally
		{
			Gate.Release();
		}
	}

	// ---- 微软 Azure 翻译（官方接口，免费层每月 200 万字符）----

	private static async Task<Dictionary<string, string>> AzureAsync(List<string> texts, string target)
	{
		string key, region;
		lock (Store.Lock)
		{
			key = (Store.Cfg.AzureKey ?? "").Trim();
			region = (Store.Cfg.AzureRegion ?? "").Trim();
		}
		var map = new Dictionary<string, string>();
		foreach (List<string> b in Batches(texts, 100, 45000))
		{
			string body = await PostJsonAsync("https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=" + target, b.Select(t => new { Text = Clip(t, 45000) }), req =>
			{
				req.Headers.Add("Ocp-Apim-Subscription-Key", key);
				if (region != "")
				{
					req.Headers.Add("Ocp-Apim-Subscription-Region", region);
				}
			}, "微软翻译").ConfigureAwait(false);
			JArray arr = JArray.Parse(body);
			for (int i = 0; i < b.Count && i < arr.Count; i++)
			{
				map[b[i]] = arr[i]["translations"]?.First?.Value<string>("text");
			}
		}
		return map;
	}

	// ---- DeepL（官方接口，免费版每月 50 万字符）----

	private static string DeepLLang(string target) => target switch
	{
		"zh-Hans" => "ZH-HANS",
		"zh-Hant" => "ZH-HANT",
		"en" => "EN-US",
		_ => target.ToUpperInvariant()
	};

	private static async Task<Dictionary<string, string>> DeepLAsync(List<string> texts, string target)
	{
		string key;
		lock (Store.Lock)
		{
			key = (Store.Cfg.DeepLKey ?? "").Trim();
		}
		// 免费版的 Key 以 :fx 结尾，用 api-free 地址
		string url = key.EndsWith(":fx", StringComparison.OrdinalIgnoreCase) ? "https://api-free.deepl.com/v2/translate" : "https://api.deepl.com/v2/translate";
		var map = new Dictionary<string, string>();
		foreach (List<string> b in Batches(texts, 50, 100000))
		{
			string body = await PostJsonAsync(url, new { text = b, target_lang = DeepLLang(target) }, req => req.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + key), "DeepL").ConfigureAwait(false);
			JArray arr = JObject.Parse(body)["translations"] as JArray ?? new JArray();
			for (int i = 0; i < b.Count && i < arr.Count; i++)
			{
				map[b[i]] = arr[i].Value<string>("text");
			}
		}
		return map;
	}

	// ---- 谷歌云翻译（官方接口 v2，需要 API Key）----

	private static string GoogleLang(string target) => target switch
	{
		"zh-Hans" => "zh-CN",
		"zh-Hant" => "zh-TW",
		_ => target
	};

	private static async Task<Dictionary<string, string>> GoogleAsync(List<string> texts, string target)
	{
		string key;
		lock (Store.Lock)
		{
			key = (Store.Cfg.GoogleKey ?? "").Trim();
		}
		var map = new Dictionary<string, string>();
		foreach (List<string> b in Batches(texts, 100, 25000))
		{
			string body = await PostJsonAsync("https://translation.googleapis.com/language/translate/v2?key=" + Uri.EscapeDataString(key), new { q = b, target = GoogleLang(target), format = "text" }, null, "谷歌云翻译").ConfigureAwait(false);
			JArray arr = JObject.Parse(body)["data"]?["translations"] as JArray ?? new JArray();
			for (int i = 0; i < b.Count && i < arr.Count; i++)
			{
				map[b[i]] = WebUtility.HtmlDecode(arr[i].Value<string>("translatedText") ?? "");
			}
		}
		return map;
	}

	// ---- MyMemory（免费公开接口，不需要 Key；不填邮箱每天约 5000 字，填邮箱约 5 万字）----

	/// <summary>按句子切成小段（MyMemory 每次最多 500 字节）；换行单独成段，翻完原样拼回。</summary>
	private static List<string> Pieces(string text)
	{
		var list = new List<string>();
		string[] lines = text.Split('\n');
		for (int li = 0; li < lines.Length; li++)
		{
			string rest = lines[li];
			int ascii = rest.Count(c => c < 128);
			int max = ascii * 2 >= rest.Length ? 420 : 140;
			while (rest.Length > max)
			{
				int cut = -1;
				foreach (char c in new[] { '.', '!', '?', '。', '！', '？', ';', ',', '，', ' ' })
				{
					int i = rest.LastIndexOf(c, max - 1);
					if (i > max / 3)
					{
						cut = i + 1;
						break;
					}
				}
				if (cut <= 0)
				{
					cut = max;
				}
				list.Add(rest.Substring(0, cut));
				rest = rest.Substring(cut);
			}
			list.Add(rest);
			if (li < lines.Length - 1)
			{
				list.Add("\n");
			}
		}
		return list;
	}

	private static async Task<Dictionary<string, string>> MyMemoryAsync(List<string> texts, string target)
	{
		string email;
		lock (Store.Lock)
		{
			email = (Store.Cfg.MyMemoryEmail ?? "").Trim();
		}
		var map = new Dictionary<string, string>();
		await Task.WhenAll(texts.Select(async t =>
		{
			List<string> pieces = Pieces(t);
			var outs = new string[pieces.Count];
			await Task.WhenAll(pieces.Select(async (p, i) =>
			{
				if (string.IsNullOrWhiteSpace(p) || !Needs(p, target))
				{
					outs[i] = p;
					return;
				}
				await Gate.WaitAsync().ConfigureAwait(false);
				try
				{
					string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(p.Trim()) + "&langpair=Autodetect|" + GoogleLang(target) + (email != "" ? "&de=" + Uri.EscapeDataString(email) : "");
					using HttpResponseMessage resp = await Http.GetAsync(url).ConfigureAwait(false);
					string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
					JObject j = null;
					try
					{
						j = JObject.Parse(body);
					}
					catch
					{
					}
					if ((int)resp.StatusCode == 429 || (j?.Value<bool?>("quotaFinished") ?? false))
					{
						throw new HttpRequestException("MyMemory 今天的免费额度用完了（在设置里填邮箱可提高额度，或改用 DeepSeek 等）");
					}
					int status = j?["responseStatus"]?.Type == JTokenType.Integer ? j.Value<int>("responseStatus") : (int)resp.StatusCode;
					string tr = j?["responseData"]?.Value<string>("translatedText");
					outs[i] = status == 200 && !string.IsNullOrEmpty(tr) ? WebUtility.HtmlDecode(tr) : p;
				}
				finally
				{
					Gate.Release();
				}
			})).ConfigureAwait(false);
			lock (map)
			{
				map[t] = string.Concat(outs);
			}
		})).ConfigureAwait(false);
		return map;
	}

	// ---- 大模型：DeepSeek / OpenAI 兼容 ----

	private static string LangName(string target) => Languages.FirstOrDefault(l => l.Code == target).Name ?? target;

	private sealed class LlmOptions
	{
		public string Name;

		public string Base;

		public string Key;

		public string Model;

		public bool Stream;
	}

	private static LlmOptions Llm(string engine)
	{
		lock (Store.Lock)
		{
			Config c = Store.Cfg;
			if (engine == "deepseek")
			{
				return new LlmOptions { Name = "DeepSeek", Base = "https://api.deepseek.com", Key = (c.DeepSeekKey ?? "").Trim(), Model = c.DeepSeekThinking ? "deepseek-reasoner" : "deepseek-chat", Stream = c.LlmStream };
			}
			return new LlmOptions { Name = "接口", Base = (c.OpenAiBase ?? "").Trim().TrimEnd('/'), Key = (c.OpenAiKey ?? "").Trim(), Model = (c.OpenAiModel ?? "").Trim(), Stream = c.LlmStream };
		}
	}

	private static string SinglePrompt(string target) => "你是游戏 mod 社区（N 网）的翻译。把用户给的文字翻译成" + LangName(target) + "。保留换行、数字、版本号、文件名和链接；mod 名、游戏专有名词可以保留原文，或在译名后用括号注明原文。只输出译文，不要解释，不要加引号。";

	private static string BatchPrompt(string target) => "你是游戏 mod 社区（N 网）的翻译。用户会给一个 JSON 字符串数组，每个元素是一段独立的文字（mod 简介、评论等）。把每一段分别翻译成" + LangName(target) + "，保留换行、数字、版本号、文件名和链接，mod 名和游戏专有名词可以保留原文。输出一个长度完全相同、顺序一致的 JSON 字符串数组，只输出这个数组，不要代码块，不要解释。";

	private static async Task<Dictionary<string, string>> LlmAsync(List<string> texts, string target, string engine, Action<string> partial)
	{
		LlmOptions o = Llm(engine);
		if (o.Base == "")
		{
			throw new InvalidOperationException("没有填写接口地址");
		}
		if (engine == "deepseek" && o.Key == "")
		{
			throw new InvalidOperationException("没有填写 DeepSeek API Key");
		}
		var map = new Dictionary<string, string>();
		// 多段合成一次请求：评论区几十段只要几次请求
		await Task.WhenAll(Batches(texts, 20, 5000).Select(async b =>
		{
			if (b.Count == 1)
			{
				string one = await ChatAsync(o, SinglePrompt(target), b[0], texts.Count == 1 ? partial : null).ConfigureAwait(false);
				lock (map)
				{
					map[b[0]] = one;
				}
				return;
			}
			JArray arr = ParseArray(await ChatAsync(o, BatchPrompt(target), JsonConvert.SerializeObject(b), null).ConfigureAwait(false));
			if (arr != null && arr.Count == b.Count)
			{
				lock (map)
				{
					for (int i = 0; i < b.Count; i++)
					{
						map[b[i]] = arr[i].Type == JTokenType.String ? arr[i].Value<string>() : arr[i].ToString();
					}
				}
				return;
			}
			Logger.Log("批量翻译返回的条数对不上，改成逐段翻译（" + b.Count + " 段）");
			foreach (string t in b)
			{
				string one = await ChatAsync(o, SinglePrompt(target), t, null).ConfigureAwait(false);
				lock (map)
				{
					map[t] = one;
				}
			}
		})).ConfigureAwait(false);
		return map;
	}

	private static JArray ParseArray(string s)
	{
		int a = s.IndexOf('[');
		int b = s.LastIndexOf(']');
		if (a < 0 || b <= a)
		{
			return null;
		}
		try
		{
			return JArray.Parse(s.Substring(a, b - a + 1));
		}
		catch
		{
			return null;
		}
	}

	private static readonly Regex Think = new Regex("<think>.*?(</think>|$)", RegexOptions.Singleline);

	private static string CleanLlm(string s)
	{
		s = Think.Replace(s ?? "", "").Trim();
		// 流式刚开始时收到半个 <think 标签
		if (s.Length < 7 && "<think>".StartsWith(s, StringComparison.Ordinal))
		{
			return "";
		}
		if (s.StartsWith("```"))
		{
			s = Regex.Replace(s, "^```[a-zA-Z]*\\s*|\\s*```$", "").Trim();
		}
		return s;
	}

	/// <summary>调用一次 chat/completions。流式时一边收一边把已收到的译文交给 partial。</summary>
	private static async Task<string> ChatAsync(LlmOptions o, string system, string user, Action<string> partial)
	{
		await Gate.WaitAsync().ConfigureAwait(false);
		try
		{
			var payload = new JObject
			{
				["model"] = o.Model,
				["stream"] = o.Stream,
				["messages"] = new JArray(new JObject { ["role"] = "system", ["content"] = system }, new JObject { ["role"] = "user", ["content"] = user })
			};
			if (o.Model.IndexOf("reasoner", StringComparison.OrdinalIgnoreCase) < 0)
			{
				payload["temperature"] = 0.2;
			}
			using var req = new HttpRequestMessage(HttpMethod.Post, o.Base + "/chat/completions");
			if (o.Key != "")
			{
				req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", o.Key);
			}
			req.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
			using HttpResponseMessage resp = await Http.SendAsync(req, o.Stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode)
			{
				string err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
				int code = (int)resp.StatusCode;
				throw new HttpRequestException(code switch
				{
					401 => o.Name + " 的 Key 无效",
					402 => o.Name + " 账户余额不足",
					429 => o.Name + " 请求太频繁，请稍后再试",
					_ => o.Name + " HTTP " + code + " " + Short(err)
				});
			}
			if (!o.Stream)
			{
				string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
				return CleanLlm(JObject.Parse(body)["choices"]?.First?["message"]?.Value<string>("content"));
			}
			var sb = new StringBuilder();
			using Stream s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
			using var reader = new StreamReader(s, Encoding.UTF8);
			string line;
			while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
			{
				if (!line.StartsWith("data:"))
				{
					continue;
				}
				string data = line.Substring(5).Trim();
				if (data == "[DONE]")
				{
					break;
				}
				string piece = null;
				try
				{
					// 只要正文；深度思考的 reasoning_content 不要
					piece = JObject.Parse(data)["choices"]?.First?["delta"]?.Value<string>("content");
				}
				catch
				{
				}
				if (!string.IsNullOrEmpty(piece))
				{
					sb.Append(piece);
					partial?.Invoke(CleanLlm(sb.ToString()));
				}
			}
			return CleanLlm(sb.ToString());
		}
		finally
		{
			Gate.Release();
		}
	}

	private static string Short(string s) => s == null ? "" : (s.Length > 200 ? s.Substring(0, 200) : s);
}
