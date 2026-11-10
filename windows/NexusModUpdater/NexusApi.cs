using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace NexusModUpdater;

public sealed class NexusException : Exception
{
	public int Status;

	public NexusException(int status, string message) : base(message)
	{
		Status = status;
	}
}

/// <summary>
/// N 网官方接口 v1（https://api.nexusmods.com），用个人 API Key。每天 2500 次、超过后每小时 100 次。
/// </summary>
public static class NexusApi
{
	private const string Base = "https://api.nexusmods.com/v1/";

	public static readonly HttpClient Http = CreateClient();

	// 接口限制每秒 30 次；同时最多 4 个请求
	private static readonly SemaphoreSlim Gate = new SemaphoreSlim(4);

	public static int HourlyRemaining = -1;

	public static int DailyRemaining = -1;

	private static HttpClient CreateClient()
	{
		ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
		var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
		var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd("NexusModUpdater/" + Updater.CurrentVersion + " (Windows; .NET Framework 4.8)");
		client.DefaultRequestHeaders.Add("Application-Name", "NexusModUpdater");
		client.DefaultRequestHeaders.Add("Application-Version", Updater.CurrentVersion);
		return client;
	}

	public static bool HasKey
	{
		get
		{
			lock (Store.Lock)
			{
				return !string.IsNullOrWhiteSpace(Store.Cfg.ApiKey);
			}
		}
	}

	private static string ApiKey
	{
		get
		{
			lock (Store.Lock)
			{
				return (Store.Cfg.ApiKey ?? "").Trim();
			}
		}
	}

	public static async Task<JToken> GetAsync(string path, string key = null)
	{
		key ??= ApiKey;
		if (string.IsNullOrEmpty(key))
		{
			throw new NexusException(401, "还没有填写 N 网 API Key（设置 → 账号）");
		}
		await Gate.WaitAsync().ConfigureAwait(false);
		try
		{
			for (int attempt = 0; ; attempt++)
			{
				using var req = new HttpRequestMessage(HttpMethod.Get, Base + path);
				req.Headers.Add("apikey", key);
				req.Headers.Accept.ParseAdd("application/json");
				HttpResponseMessage resp;
				try
				{
					resp = await Http.SendAsync(req).ConfigureAwait(false);
				}
				catch (TaskCanceledException)
				{
					if (attempt < 2)
					{
						continue;
					}
					throw new NexusException(0, "连接 N 网接口超时");
				}
				catch (HttpRequestException ex)
				{
					if (attempt < 2)
					{
						await Task.Delay(800).ConfigureAwait(false);
						continue;
					}
					throw new NexusException(0, "连不上 N 网接口：" + (ex.InnerException?.Message ?? ex.Message));
				}
				using (resp)
				{
					ReadLimits(resp);
					string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
					int status = (int)resp.StatusCode;
					if (status == 429 && attempt < 1 && HourlyRemaining != 0)
					{
						// 每秒请求太多（nginx），等一下再试
						await Task.Delay(1500).ConfigureAwait(false);
						continue;
					}
					if ((status == 502 || status == 503 || status == 504) && attempt < 2)
					{
						await Task.Delay(1500).ConfigureAwait(false);
						continue;
					}
					if (!resp.IsSuccessStatusCode)
					{
						string message = null;
						try
						{
							message = JObject.Parse(body).Value<string>("message") ?? JObject.Parse(body).Value<string>("error");
						}
						catch
						{
						}
						message = status switch
						{
							401 => "API Key 无效或已过期，请到 设置 → 账号 重新填写",
							429 => "N 网接口次数用完了（每天 2500 次，之后每小时 100 次），请稍后再试",
							_ => message ?? ("HTTP " + status)
						};
						throw new NexusException(status, message);
					}
					return string.IsNullOrWhiteSpace(body) ? JValue.CreateNull() : JToken.Parse(body);
				}
			}
		}
		finally
		{
			Gate.Release();
		}
	}

	private static void ReadLimits(HttpResponseMessage resp)
	{
		if (resp.Headers.TryGetValues("X-RL-Hourly-Remaining", out IEnumerable<string> h) && int.TryParse(h.FirstOrDefault(), out int hv))
		{
			HourlyRemaining = hv;
		}
		if (resp.Headers.TryGetValues("X-RL-Daily-Remaining", out IEnumerable<string> d) && int.TryParse(d.FirstOrDefault(), out int dv))
		{
			DailyRemaining = dv;
		}
	}

	/// <summary>还剩多少次可以用（每天的和每小时的取较大者；不知道时返回 int.MaxValue）。</summary>
	public static int Remaining
	{
		get
		{
			if (DailyRemaining < 0 && HourlyRemaining < 0)
			{
				return int.MaxValue;
			}
			return Math.Max(DailyRemaining, HourlyRemaining);
		}
	}

	public static async Task<JObject> ValidateAsync(string key = null)
	{
		return (JObject)await GetAsync("users/validate.json", key).ConfigureAwait(false);
	}

	public static async Task<JObject> ModAsync(string domain, int id)
	{
		return (JObject)await GetAsync("games/" + domain + "/mods/" + id + ".json").ConfigureAwait(false);
	}

	public static async Task<JObject> FilesAsync(string domain, int id)
	{
		return (JObject)await GetAsync("games/" + domain + "/mods/" + id + "/files.json").ConfigureAwait(false);
	}

	public static async Task<JToken> ChangelogsAsync(string domain, int id)
	{
		return await GetAsync("games/" + domain + "/mods/" + id + "/changelogs.json").ConfigureAwait(false);
	}

	/// <summary>period: 1d / 1w / 1m</summary>
	public static async Task<JArray> UpdatedAsync(string domain, string period)
	{
		return await GetAsync("games/" + domain + "/mods/updated.json?period=" + period).ConfigureAwait(false) as JArray ?? new JArray();
	}

	public static async Task<JArray> TrackedAsync()
	{
		return await GetAsync("user/tracked_mods.json").ConfigureAwait(false) as JArray ?? new JArray();
	}

	public static async Task<JArray> GamesAsync()
	{
		return await GetAsync("games.json").ConfigureAwait(false) as JArray ?? new JArray();
	}

	/// <summary>文件的下载地址。会员不需要 key/expires；普通用户要用网页上点「Slow download」生成的 nxm 链接里的 key 和 expires。</summary>
	public static async Task<string> DownloadUrlAsync(string domain, int modId, int fileId, string key = null, string expires = null)
	{
		string path = "games/" + domain + "/mods/" + modId + "/files/" + fileId + "/download_link.json";
		if (!string.IsNullOrEmpty(key))
		{
			path += "?key=" + Uri.EscapeDataString(key) + "&expires=" + Uri.EscapeDataString(expires ?? "");
		}
		JToken t;
		try
		{
			t = await GetAsync(path).ConfigureAwait(false);
		}
		catch (NexusException ex) when (ex.Status == 403)
		{
			throw new NexusException(403, "不是会员，不能直接下载：需要在「N网」页里点 Slow download");
		}
		string uri = (t as JArray)?.OfType<JObject>().Select(o => o.Value<string>("URI")).FirstOrDefault(u => !string.IsNullOrEmpty(u));
		if (uri == null)
		{
			throw new NexusException(0, "N 网没有返回下载地址");
		}
		return uri;
	}

	public static ModInfo ParseMod(JObject o)
	{
		var user = o["user"] as JObject;
		return new ModInfo
		{
			Name = o.Value<string>("name") ?? "",
			Summary = WebUtility.HtmlDecode(o.Value<string>("summary") ?? "").Replace("<br />", "\n"),
			Description = o.Value<string>("description") ?? "",
			Picture = o.Value<string>("picture_url") ?? "",
			Version = o.Value<string>("version") ?? "",
			Author = o.Value<string>("author") ?? "",
			Uploader = o.Value<string>("uploaded_by") ?? user?.Value<string>("name") ?? "",
			Created = o.Value<long?>("created_timestamp") ?? 0,
			Updated = o.Value<long?>("updated_timestamp") ?? 0,
			Endorsements = o.Value<int?>("endorsement_count") ?? 0,
			Downloads = o.Value<int?>("mod_downloads") ?? 0,
			Status = o.Value<string>("status") ?? "",
			Available = o.Value<bool?>("available") ?? true,
			Adult = o.Value<bool?>("contains_adult_content") ?? false,
			CategoryId = o.Value<int?>("category_id") ?? 0,
			FetchedAt = Store.Now
		};
	}

	public static ModFiles ParseFiles(JObject o)
	{
		var mf = new ModFiles { FetchedAt = Store.Now };
		foreach (JObject f in (o["files"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
		{
			mf.Files.Add(new NexusFile
			{
				FileId = f.Value<int?>("file_id") ?? f["id"]?.First?.Value<int?>() ?? 0,
				Name = f.Value<string>("name") ?? "",
				FileName = f.Value<string>("file_name") ?? "",
				Version = f.Value<string>("version") ?? f.Value<string>("mod_version") ?? "",
				CategoryId = f.Value<int?>("category_id") ?? 0,
				Category = f.Value<string>("category_name") ?? "",
				Uploaded = f.Value<long?>("uploaded_timestamp") ?? 0,
				SizeKb = f.Value<long?>("size_kb") ?? f.Value<long?>("size") ?? 0,
				Description = WebUtility.HtmlDecode(f.Value<string>("description") ?? "").Replace("<br />", "\n"),
				Primary = (f.Value<int?>("is_primary") ?? 0) != 0
			});
		}
		foreach (JObject u in (o["file_updates"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
		{
			mf.Updates.Add(new FileUpdateLink
			{
				OldId = u.Value<int?>("old_file_id") ?? 0,
				NewId = u.Value<int?>("new_file_id") ?? 0,
				OldName = u.Value<string>("old_file_name") ?? "",
				NewName = u.Value<string>("new_file_name") ?? "",
				Uploaded = u.Value<long?>("uploaded_timestamp") ?? 0
			});
		}
		return mf;
	}
}
