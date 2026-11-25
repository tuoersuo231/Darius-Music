using System.Globalization;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CurrentLyrics.Services;

/// <summary>
/// Minimal NetEase Cloud Music client: the web ("weapi") request encoding plus the three calls this
/// player needs — search, play URL, lyrics. No account is required for free tracks; pasting the
/// MUSIC_U cookie raises the account's own limits (higher quality, more tracks), which is all it is.
///
/// Protocol notes (same as the public web client): the JSON body is AES-128-CBC encrypted twice —
/// once with the fixed key, once with a random 16-char secret — and the secret itself is sent
/// RSA-encrypted without padding as <c>encSecKey</c>. Everything after that is a plain form POST.
/// </summary>
public static class NeteaseApi
{
    private const string WebKey = "0CoJUm6Qyw8W8jud";
    private const string WebIv = "0102030405060708";
    private const string PublicKey =
        "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDgtQn2JZ34ZC28NWYpAUd98iZ37BUrX/aKzmFbt7clFSs6sXqHauqKWqdtLkF2KexO40H1YTX8z2lSgBBOAxLsvaklV8k4cBFK9snQXE9/DDaFt6Rr7iVZMldczhC0JNgTz+SHXT6CBHuX3e9SdB1Ua44oncaTWz7OBGLbCiK45wIDAQAB";
    private const string SecretAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36";

    /// <summary>Quality tiers the endpoint accepts; lossless needs a VIP account or it downgrades.</summary>
    public static readonly (string Value, string Label)[] QualityChoices =
    {
        ("standard", "标准 128k"),
        ("higher", "较高 192k"),
        ("exhigh", "较高 320k"),
        ("lossless", "无损 FLAC"),
    };

    private static readonly HttpClient Http = CreateHttp();
    private static readonly byte[] Modulus = ReadModulus();

    /// <summary>Raw cookie text from the settings (<c>MUSIC_U=...; __csrf=...</c>), empty when anonymous.</summary>
    public static string Cookie { get; set; } = "";

    public static string Quality { get; set; } = "exhigh";

    public static string? LastError { get; private set; }

    public static bool HasLogin => Cookie.Contains("MUSIC_U", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.163.com/");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return client;
    }

    // ---------- protocol ----------

    private static byte[] ReadModulus()
    {
        // SubjectPublicKeyInfo: 30 81 9F .. 03 81 8D 00 30 81 89 02 81 81 00 <128 bytes> ...
        var der = Convert.FromBase64String(PublicKey);
        var modulus = new byte[128];
        Buffer.BlockCopy(der, 29, modulus, 0, 128);
        return modulus;
    }

    private static byte[] AesCbc(byte[] data, string key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Encoding.UTF8.GetBytes(key);
        aes.IV = Encoding.UTF8.GetBytes(WebIv);
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(data, 0, data.Length);
    }

    private static BigInteger FromBigEndian(byte[] bigEndian)
    {
        var little = new byte[bigEndian.Length + 1];
        for (int i = 0; i < bigEndian.Length; i++) little[i] = bigEndian[bigEndian.Length - 1 - i];
        return new BigInteger(little);
    }

    /// <summary>RSA without padding: the secret is reversed, read as a big integer and raised to e.</summary>
    private static string RawRsa(string secret)
    {
        var reversed = new string(secret.Reverse().ToArray());
        var message = FromBigEndian(Encoding.UTF8.GetBytes(reversed));
        var cipher = BigInteger.ModPow(message, new BigInteger(65537), FromBigEndian(Modulus));
        return cipher.ToString("x", CultureInfo.InvariantCulture).PadLeft(256, '0');
    }

    private static (string Params, string Key) Encode(string json)
    {
        var secret = new string(Enumerable.Range(0, 16).Select(_ => SecretAlphabet[Random.Shared.Next(SecretAlphabet.Length)]).ToArray());
        var first = Convert.ToBase64String(AesCbc(Encoding.UTF8.GetBytes(json), WebKey));
        var second = Convert.ToBase64String(AesCbc(Encoding.UTF8.GetBytes(first), secret));
        return (second, RawRsa(secret));
    }

    private static async Task<JsonObject?> PostAsync(string path, JsonObject payload, CancellationToken token)
    {
        LastError = null;
        payload["csrf_token"] = CsrfToken();
        // The gateway occasionally answers a perfectly good request with an empty 200 (rate shaping);
        // three tries with a growing pause ride that out, and keep callers free of retry loops.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var (encrypted, key) = Encode(payload.ToJsonString());
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["params"] = encrypted,
                ["encSecKey"] = key,
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://music.163.com/weapi/" + path) { Content = content };
            if (!string.IsNullOrWhiteSpace(Cookie))
                request.Headers.TryAddWithoutValidation("Cookie", Cookie.Trim());

            try
            {
                using var response = await Http.SendAsync(request, token);
                var body = await response.Content.ReadAsStringAsync(token);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    var node = JsonNode.Parse(body) as JsonObject;
                    if (node != null) return node;
                    LastError = "接口返回了无法解析的内容";
                    return null;
                }
                LastError = $"HTTP {(int)response.StatusCode}：接口没有返回内容";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                LastError = "网络请求失败：" + e.Message;
                return null;
            }
            await Task.Delay(attempt == 0 ? 320 : 900, token);
        }
        return null;
    }

    private static string CsrfToken()
    {
        foreach (var part in Cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim().Equals("__csrf", StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..].Trim();
        }
        return "";
    }

    // ---------- public API ----------

    public sealed record SearchPage(List<Models.Song> Songs, int Total, string? Error);

    /// <summary>Repeated identical searches (typing, switching views) reuse the last answer for a
    /// minute; the keyword is part of the key so results never mix.</summary>
    private static readonly Dictionary<string, (DateTime At, SearchPage Page)> SearchCache = new();

    public static async Task<SearchPage> SearchAsync(string keyword, int limit = 50, int offset = 0,
        CancellationToken token = default)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0) return new SearchPage(new List<Models.Song>(), 0, null);

        var cacheKey = keyword.ToLowerInvariant() + "|" + limit + "|" + offset;
        lock (SearchCache)
        {
            if (SearchCache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(60))
                return hit.Page;
        }

        var payload = new JsonObject
        {
            ["s"] = keyword,
            ["type"] = 1,
            ["limit"] = limit,
            ["offset"] = offset,
            ["total"] = true,
        };
        // cloudsearch carries album art and exact durations; search/get is the older shape kept as a
        // fallback. The gateway intermittently answers an empty body, so cloudsearch gets one retry
        // before the shape without artwork is used.
        JsonObject? root = null;
        for (int attempt = 0; attempt < 2 && root == null; attempt++)
        {
            var candidate = await PostAsync("cloudsearch/pc", payload, token);
            if (candidate?["result"]?["songs"] is JsonArray) root = candidate;
            else if (attempt == 0) await Task.Delay(260, token);
        }
        if (root?["result"]?["songs"] is not JsonArray)
            root = await PostAsync("search/get", payload, token);
        if (root == null) return new SearchPage(new List<Models.Song>(), 0, LastError ?? "搜索失败");

        var result = root["result"] as JsonObject;
        var songs = new List<Models.Song>();
        if (result?["songs"] is JsonArray array)
        {
            foreach (var entry in array)
            {
                if (entry is not JsonObject item) continue;
                var id = item["id"]?.GetValue<long>() ?? 0;
                if (id <= 0) continue;
                var artists = new List<string>();
                if (item["ar"] is JsonArray newArtists) ReadArtists(newArtists, artists);
                else if (item["artists"] is JsonArray oldArtists) ReadArtists(oldArtists, artists);
                var album = (item["al"] ?? item["album"]) as JsonObject;
                var cover = album?["picUrl"]?.GetValue<string>();
                var durationMs = item["dt"]?.GetValue<double>() ?? item["duration"]?.GetValue<double>() ?? 0;
                var song = new Models.Song
                {
                    Source = Models.SongSource.Netease,
                    Path = "netease:" + id,
                    NeteaseId = id,
                    Title = item["name"]?.GetValue<string>() ?? "(无标题)",
                    Artist = string.Join(" / ", artists),
                    Album = album?["name"]?.GetValue<string>() ?? "",
                    Duration = TimeSpan.FromMilliseconds(durationMs),
                    CoverUrl = Upgrade(cover),
                };
                songs.Add(song);
            }
        }
        var total = result?["songCount"]?.GetValue<int>() ?? songs.Count;
        var page = new SearchPage(songs, total, null);
        if (songs.Count > 0)
        {
            lock (SearchCache)
            {
                if (SearchCache.Count > 24) SearchCache.Clear();
                SearchCache[cacheKey] = (DateTime.UtcNow, page);
            }
        }
        return page;
    }

    private static void ReadArtists(JsonArray array, List<string> into)
    {
        foreach (var artist in array)
            if (artist?["name"]?.GetValue<string>() is { Length: > 0 } name) into.Add(name);
    }

    /// <summary>Resolves a playable CDN URL. <c>Code -110</c> means the track is not available to this
    /// account (VIP/disabled/region), which is reported instead of silently failing.</summary>
    public sealed record UrlResult(string? Url, string Level, int Code, int ExpiresSeconds, bool Trial, string? Error);

    public static async Task<UrlResult> ResolveAsync(long id, string? level = null, CancellationToken token = default)
    {
        level ??= Quality;
        if (level is not ("standard" or "higher" or "exhigh" or "lossless" or "hires" or "jymaster"))
            level = "exhigh";

        var payload = new JsonObject
        {
            ["ids"] = "[" + id + "]",
            ["level"] = level,
            ["encodeType"] = "flac",
        };
        var root = await PostAsync("song/enhance/player/url/v1", payload, token);
        if (root == null)
        {
            // The gateway intermittently answers an empty 200; one quick retry usually gets the address
            // and spares the user a "接口没有返回内容" on a track that is perfectly playable.
            await Task.Delay(300, token);
            root = await PostAsync("song/enhance/player/url/v1", payload, token);
        }
        if (root == null) return new UrlResult(null, level, -1, 0, false, LastError ?? "取播放地址失败");

        var data = (root["data"] as JsonArray)?.FirstOrDefault() as JsonObject;
        if (data == null) return new UrlResult(null, level, -1, 0, false, "接口没有返回地址");

        var code = data["code"]?.GetValue<int>() ?? -1;
        var url = data["url"]?.GetValue<string>();
        var actualLevel = data["level"]?.GetValue<string>() ?? "";
        var expi = data["expi"]?.GetValue<int>() ?? 300;
        var trial = data["freeTrialInfo"] is JsonObject;
        string? error = null;
        if (string.IsNullOrEmpty(url))
        {
            error = code switch
            {
                -110 => "该曲目当前无版权或需要会员",
                -200 => "该曲目无版权",
                404 => "版权方未授权",
                _ when trial => "只有试听片段",
                _ => $"没有拿到地址（code={code}）",
            };
        }
        return new UrlResult(string.IsNullOrEmpty(url) ? null : Upgrade(url), actualLevel, code, expi, trial, error);
    }

    /// <summary>Lyrics in LRC form (translated lines merged as the second text), or null.</summary>
    public static async Task<string?> LyricAsync(long id, CancellationToken token = default)
    {
        var payload = new JsonObject { ["id"] = id, ["lv"] = -1, ["kv"] = -1, ["tv"] = -1 };
        var root = await PostAsync("song/lyric", payload, token);
        if (root != null)
        {
            var lrc = (root["lrc"] as JsonObject)?["lyric"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(lrc)) return lrc;
        }
        var legacy = await PostAsync("lyric", new JsonObject { ["id"] = id, ["lv"] = -1, ["kv"] = -1, ["tv"] = -1 }, token);
        if (legacy == null) return null;
        var fallback = (legacy["lrc"] as JsonObject)?["lyric"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    private static string? Upgrade(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        // The CDN serves the same object over https; the plain-http form breaks once a proxy blocks it.
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url[7..]
            : url;
    }
}
