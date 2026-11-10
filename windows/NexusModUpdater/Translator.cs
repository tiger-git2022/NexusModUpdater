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
/// 翻译：各家官方接口：微软 Azure、DeepL、谷歌云（都有免费额度，需要自己的 Key），
/// 或 OpenAI 兼容接口（DeepSeek、本地模型等）。结果缓存到 nmu.translate.json。
/// </summary>
public static class Translator
{
	private static readonly HttpClient Http = CreateClient();

	private static readonly object CacheLock = new object();

	private static Dictionary<string, string> _cache;

	private static Timer _saveTimer;

	private static readonly SemaphoreSlim Gate = new SemaphoreSlim(3);

	private static readonly Regex Cjk = new Regex("[぀-ヿ㐀-鿿가-힯]");

	private static readonly Regex Letters = new Regex("[A-Za-zÀ-ɏЀ-ӿ]");

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
		var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
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
		if (string.IsNullOrWhiteSpace(text) || !Letters.IsMatch(text))
		{
			return false;
		}
		if (target.StartsWith("zh") || target == "ja" || target == "ko")
		{
			int cjk = Cjk.Matches(text).Count;
			int letters = Letters.Matches(text).Count;
			if (cjk > 0 && cjk * 2 >= letters)
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>翻译一批文字；不需要翻译的原样返回，失败的返回 null。target 为空时用设置里的语言。</summary>
	public static async Task<List<string>> TranslateAsync(IList<string> texts, string target = null, string engine = null)
	{
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
				if (!Ready || !Needs(t, target))
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
				"openai" => await OpenAiAsync(unique, target).ConfigureAwait(false),
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

	public static async Task<string> TranslateOneAsync(string text, string target = null)
	{
		return (await TranslateAsync(new[] { text }, target).ConfigureAwait(false))[0];
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

	// ---- OpenAI 兼容 ----

	private static string LangName(string target) => Languages.FirstOrDefault(l => l.Code == target).Name ?? target;

	private static async Task<Dictionary<string, string>> OpenAiAsync(List<string> texts, string target)
	{
		string baseUrl, key, model;
		lock (Store.Lock)
		{
			baseUrl = (Store.Cfg.OpenAiBase ?? "").Trim().TrimEnd('/');
			key = (Store.Cfg.OpenAiKey ?? "").Trim();
			model = (Store.Cfg.OpenAiModel ?? "").Trim();
		}
		if (baseUrl == "")
		{
			throw new InvalidOperationException("没有填写 OpenAI 兼容接口地址");
		}
		var map = new Dictionary<string, string>();
		await Task.WhenAll(texts.Select(async t =>
		{
			await Gate.WaitAsync().ConfigureAwait(false);
			try
			{
				var payload = new
				{
					model,
					temperature = 0.2,
					messages = new object[]
					{
						new { role = "system", content = "你是游戏 mod 社区的翻译。把用户给的文字翻译成" + LangName(target) + "。保留换行、数字、版本号、文件名、链接和 mod / 游戏专有名词（可以在译名后括号保留原文）。只输出译文，不要解释。" },
						new { role = "user", content = t }
					}
				};
				using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
				if (key != "")
				{
					req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
				}
				req.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
				using HttpResponseMessage resp = await Http.SendAsync(req).ConfigureAwait(false);
				string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
				if (!resp.IsSuccessStatusCode)
				{
					throw new HttpRequestException("接口 HTTP " + (int)resp.StatusCode + " " + Short(body));
				}
				string text = JObject.Parse(body)["choices"]?.First?["message"]?.Value<string>("content") ?? "";
				text = Regex.Replace(text, "<think>.*?</think>", "", RegexOptions.Singleline).Trim();
				lock (map)
				{
					map[t] = text;
				}
			}
			finally
			{
				Gate.Release();
			}
		})).ConfigureAwait(false);
		return map;
	}

	private static string Short(string s) => s == null ? "" : (s.Length > 200 ? s.Substring(0, 200) : s);
}
