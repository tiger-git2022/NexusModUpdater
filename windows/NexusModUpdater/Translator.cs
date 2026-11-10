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
/// 翻译：微软（Edge 浏览器内置翻译用的接口，国内可直连，免费）、谷歌（免费网页接口）、
/// 或 OpenAI 兼容接口（DeepSeek、本地模型等）。结果缓存到 nmu.translate.json。
/// </summary>
public static class Translator
{
	private static readonly HttpClient Http = CreateClient();

	private static readonly object CacheLock = new object();

	private static Dictionary<string, string> _cache;

	private static Timer _saveTimer;

	private static string _bingToken;

	private static DateTime _bingTokenAt;

	private static readonly SemaphoreSlim Gate = new SemaphoreSlim(3);

	private static readonly Regex Cjk = new Regex("[぀-ヿ㐀-鿿가-힯]");

	private static readonly Regex Letters = new Regex("[A-Za-zÀ-ɏЀ-ӿ]");

	public static readonly (string Code, string Name)[] Languages =
	{
		("zh-Hans", "简体中文"), ("zh-Hant", "繁體中文"), ("en", "English"), ("ja", "日本語"), ("ko", "한국어")
	};

	private static HttpClient CreateClient()
	{
		ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
		var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
		var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
		c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Edg/130.0");
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
				if (engine == "off" || !Needs(t, target))
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
				"google" => await GoogleAsync(unique, target).ConfigureAwait(false),
				"openai" => await OpenAiAsync(unique, target).ConfigureAwait(false),
				_ => await BingAsync(unique, target).ConfigureAwait(false)
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

	// ---- 微软 ----

	private static async Task<string> BingTokenAsync(bool force = false)
	{
		if (!force && _bingToken != null && DateTime.UtcNow - _bingTokenAt < TimeSpan.FromMinutes(8))
		{
			return _bingToken;
		}
		_bingToken = (await Http.GetStringAsync("https://edge.microsoft.com/translate/auth").ConfigureAwait(false)).Trim();
		_bingTokenAt = DateTime.UtcNow;
		return _bingToken;
	}

	private static async Task<Dictionary<string, string>> BingAsync(List<string> texts, string target)
	{
		var map = new Dictionary<string, string>();
		// 每次最多 100 段、约 40000 字
		var batch = new List<string>();
		int chars = 0;
		var batches = new List<List<string>>();
		foreach (string t in texts)
		{
			if (batch.Count >= 80 || chars + t.Length > 30000)
			{
				batches.Add(batch);
				batch = new List<string>();
				chars = 0;
			}
			batch.Add(t.Length > 30000 ? t.Substring(0, 30000) : t);
			chars += t.Length;
		}
		if (batch.Count > 0)
		{
			batches.Add(batch);
		}
		foreach (List<string> b in batches)
		{
			await Gate.WaitAsync().ConfigureAwait(false);
			try
			{
				for (int attempt = 0; ; attempt++)
				{
					string token = await BingTokenAsync(attempt > 0).ConfigureAwait(false);
					using var req = new HttpRequestMessage(HttpMethod.Post, "https://api-edge.cognitive.microsofttranslator.com/translate?api-version=3.0&to=" + target);
					req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
					req.Content = new StringContent(JsonConvert.SerializeObject(b.Select(t => new { Text = t })), Encoding.UTF8, "application/json");
					using HttpResponseMessage resp = await Http.SendAsync(req).ConfigureAwait(false);
					string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
					if ((int)resp.StatusCode == 401 && attempt == 0)
					{
						continue;
					}
					if (!resp.IsSuccessStatusCode)
					{
						throw new HttpRequestException("微软翻译 HTTP " + (int)resp.StatusCode + " " + Short(body));
					}
					JArray arr = JArray.Parse(body);
					for (int i = 0; i < b.Count && i < arr.Count; i++)
					{
						map[b[i]] = arr[i]["translations"]?.First?.Value<string>("text");
					}
					break;
				}
			}
			finally
			{
				Gate.Release();
			}
		}
		return map;
	}

	// ---- 谷歌 ----

	private static string GoogleLang(string target) => target switch
	{
		"zh-Hans" => "zh-CN",
		"zh-Hant" => "zh-TW",
		_ => target
	};

	private static async Task<Dictionary<string, string>> GoogleAsync(List<string> texts, string target)
	{
		var map = new Dictionary<string, string>();
		await Task.WhenAll(texts.Select(async t =>
		{
			await Gate.WaitAsync().ConfigureAwait(false);
			try
			{
				var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("q", t.Length > 5000 ? t.Substring(0, 5000) : t) });
				using HttpResponseMessage resp = await Http.PostAsync("https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&dt=t&tl=" + GoogleLang(target), content).ConfigureAwait(false);
				string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
				if (!resp.IsSuccessStatusCode)
				{
					throw new HttpRequestException("谷歌翻译 HTTP " + (int)resp.StatusCode);
				}
				JArray arr = JArray.Parse(body);
				var sb = new StringBuilder();
				foreach (JToken seg in arr[0] as JArray ?? new JArray())
				{
					sb.Append(seg[0]?.Value<string>());
				}
				lock (map)
				{
					map[t] = sb.ToString();
				}
			}
			finally
			{
				Gate.Release();
			}
		})).ConfigureAwait(false);
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
