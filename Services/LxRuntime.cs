using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Runtime.Interop;

namespace CurrentLyrics.Services;

/// <summary>
/// Host for "落雪" (LX Music) audio-source scripts: one script runs inside a Jint engine that is given
/// the <c>lx</c> object those scripts expect, and answers a single question — "give me a playable URL
/// for this track". Everything is synchronous on purpose: the script's request/response pipeline is
/// pumped through an eager promise implementation, so one C# call drives a whole chain of
/// <c>.then()</c>s without any async plumbing on either side.
/// </summary>
public sealed class LxRuntime : IDisposable
{
    private static readonly HttpClient Http = CreateHttp();
    private readonly Engine _engine;
    private readonly LxHost _host;
    private string? _scriptId;
    private string? _inited;

    public string? CapabilityJson => _inited;

    public sealed record Audio(string Url, string Quality, string Source);

    public LxRuntime()
    {
        _host = new LxHost();
        _engine = new Engine(options => options
            .TimeoutInterval(TimeSpan.FromSeconds(25))
            .LimitMemory(96 * 1024 * 1024)
            .MaxStatements(4_000_000)
            .Strict(false));
        _engine.SetValue("__lxHost", _host);
        _engine.Execute(Prelude);
    }

    /// <summary>Loads a script and returns the capabilities it declares (empty when it never inits).</summary>
    public string Load(string id, string script)
    {
        _scriptId = id;
        _inited = null;
        try
        {
            _engine.Execute(script);
        }
        catch (Exception e)
        {
            throw new LxException("脚本执行失败：" + Short(e.Message));
        }
        var raw = _engine.Evaluate("globalThis.__lxInited ? JSON.stringify(globalThis.__lxInited) : ''").AsString();
        if (string.IsNullOrEmpty(raw)) throw new LxException("脚本没有声明支持的平台（没有发 inited 事件）");
        _inited = raw;
        return raw;
    }

    /// <summary>
    /// Asks the script for a URL. <paramref name="quality"/> is tried first, then the usual descending
    /// fallbacks — a script that cannot serve 无损 must still be able to answer in 320k.
    /// </summary>
    public Audio Resolve(string source, string quality, JsonObject info, IReadOnlyList<string> declared)
    {
        var attempts = new List<string>();
        foreach (var candidate in QualityOrder(quality)) if (declared.Contains(candidate)) attempts.Add(candidate);
        if (attempts.Count == 0) attempts.AddRange(declared.Count > 0 ? declared : new List<string> { "320k" });

        LxException? last = null;
        foreach (var attempt in attempts)
        {
            try
            {
                var payload = info.DeepClone().AsObject();
                payload["type"] = attempt;
                var request = new JsonObject
                {
                    ["source"] = source,
                    ["action"] = "musicUrl",
                    ["info"] = payload,
                };
                var result = _engine.Invoke("__lxResolve", source, "musicUrl", request.ToJsonString());
                var value = result.AsString();
                if (string.IsNullOrWhiteSpace(value)) throw new LxException("脚本没有返回地址");
                return new Audio(value, attempt, source);
            }
            catch (LxException e)
            {
                last = e;
                if (!e.QualityMissing) throw;
            }
            catch (Exception e)
            {
                throw new LxException("脚本取地址失败：" + Short(e.Message));
            }
        }
        throw last ?? new LxException("脚本没有可用的音质");
    }

    /// <summary>Descending quality preference; the script's own declared list filters it.</summary>
    private static IEnumerable<string> QualityOrder(string quality) => quality switch
    {
        "lossless" => new[] { "flac", "flac24bit", "hires", "320k", "128k" },
        "hires" => new[] { "hires", "flac24bit", "flac", "320k", "128k" },
        "exhigh" => new[] { "320k", "128k" },
        "higher" => new[] { "320k", "128k" },
        _ => new[] { "128k", "320k" },
    };

    private static string Short(string text) => text.Length > 160 ? text[..160] + "…" : text;

    public void Dispose() => _host.Dispose();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36");
        return client;
    }

    // ---------- host bridge (called from the prelude) ----------

    private sealed class LxHost : IDisposable
    {
        public string? RequestSync(string url, string optionsJson)
        {
            try
            {
                var options = string.IsNullOrWhiteSpace(optionsJson)
                    ? new JsonObject()
                    : JsonNode.Parse(optionsJson) as JsonObject ?? new JsonObject();
                var method = (options["method"]?.GetValue<string>() ?? "get").ToUpperInvariant();
                using var request = new HttpRequestMessage(new HttpMethod(method), url);
                if (options["headers"] is JsonObject headers)
                {
                    foreach (var (name, value) in headers)
                    {
                        var headerValue = value?.GetValue<string>();
                        if (!string.IsNullOrEmpty(headerValue)) request.Headers.TryAddWithoutValidation(name, headerValue);
                    }
                }
                if (options["body"] is JsonObject body && method != "GET")
                {
                    var json = body["json"] is JsonObject payload ? payload.ToJsonString() : body.ToJsonString();
                    var form = body["form"] is JsonObject formData ? formData : null;
                    if (form != null)
                        request.Content = new FormUrlEncodedContent(form.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value?.GetValue<string>() ?? "")));
                    else
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }
                else if (options["body"] is JsonValue scalar && method != "GET")
                {
                    request.Content = new StringContent(scalar.GetValue<string>(), Encoding.UTF8, "text/plain");
                }

                using var response = Http.SendAsync(request).GetAwaiter().GetResult();
                var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var result = new JsonObject
                {
                    ["statusCode"] = (int)response.StatusCode,
                    ["headers"] = new JsonObject(),
                    ["body"] = ParseBody(text, response.Content.Headers.ContentType?.MediaType ?? ""),
                    ["bodyB64"] = Convert.ToBase64String(response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()),
                };
                return result.ToJsonString();
            }
            catch (Exception e)
            {
                throw new LxException("网络请求失败：" + e.Message);
            }
        }

        private static JsonNode? ParseBody(string text, string mediaType)
        {
            if (text.Length == 0) return JsonValue.Create("");
            if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
            {
                try { return JsonNode.Parse(text); } catch { }
            }
            return JsonValue.Create(text);
        }

        public string Md5(string text) =>
            Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        public string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

        public string Base64Encode(string text) => Convert.ToBase64String(Encoding.Latin1.GetBytes(text));

        public string Base64Decode(string text) => Encoding.Latin1.GetString(Convert.FromBase64String(text));

        /// <summary>AES with the modes the scripts use, bridged as base64 so no CLR buffer type leaks.</summary>
        public string Aes(bool encrypt, string dataB64, string mode, string keyB64, string ivB64)
        {
            var data = Convert.FromBase64String(dataB64);
            var key = Convert.FromBase64String(keyB64);
            var iv = string.IsNullOrEmpty(ivB64) ? Array.Empty<byte>() : Convert.FromBase64String(ivB64);
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            var lower = mode.ToLowerInvariant();
            if (lower.Contains("ecb"))
            {
                aes.Mode = CipherMode.ECB;
            }
            else
            {
                aes.Mode = CipherMode.CBC;
                aes.IV = iv.Length == 16 ? iv : new byte[16];
            }
            var output = encrypt
                ? aes.CreateEncryptor().TransformFinalBlock(data, 0, data.Length)
                : aes.CreateDecryptor().TransformFinalBlock(data, 0, data.Length);
            return Convert.ToBase64String(output);
        }

        public string Rsa(string dataB64, string publicKeyPem)
        {
            var data = Convert.FromBase64String(dataB64);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            return Convert.ToBase64String(rsa.Encrypt(data, RSAEncryptionPadding.Pkcs1));
        }

        public string Zlib(bool inflate, string dataB64)
        {
            var data = Convert.FromBase64String(dataB64);
            using var input = new System.IO.MemoryStream(data);
            if (inflate)
            {
                using var z = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
                using var inflated = new System.IO.MemoryStream();
                z.CopyTo(inflated);
                return Convert.ToBase64String(inflated.ToArray());
            }
            using var output = new System.IO.MemoryStream();
            using (var z = new System.IO.Compression.ZLibStream(output,
                       System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                input.CopyTo(z);
            }
            return Convert.ToBase64String(output.ToArray());
        }

        public void Dispose() { }
    }

    /// <summary>
    /// The <c>lx</c> surface. Promises are eager: our host calls are blocking, so a thenable that
    /// resolves immediately lets a whole <c>.then()</c> chain run inside one C# invoke.
    /// </summary>
    private static string Prelude => """
    (function () {
      const host = __lxHost;
      const b64 = {
        fromBytes: (bytes) => {
          let s = '';
          for (let i = 0; i < bytes.length; i++) s += String.fromCharCode(bytes[i]);
          return host.base64Encode(s);
        },
        toBytes: (text) => {
          const s = host.base64Decode(text);
          const out = new Uint8Array(s.length);
          for (let i = 0; i < s.length; i++) out[i] = s.charCodeAt(i);
          return out;
        },
      };

      class SyncPromise {
        constructor(stateOrExecutor, value) {
          if (typeof stateOrExecutor === 'function') {
            this._state = 'pending';
            this._value = undefined;
            const resolve = (v) => {
              if (this._state !== 'pending') return;
              if (v instanceof SyncPromise) { this._state = v._state; this._value = v._value; return; }
              this._state = 'fulfilled'; this._value = v;
            };
            const reject = (e) => {
              if (this._state !== 'pending') return;
              this._state = 'rejected'; this._value = e;
            };
            try { stateOrExecutor(resolve, reject); } catch (e) { reject(e); }
          } else {
            this._state = stateOrExecutor;
            this._value = value;
          }
        }
        static resolve(v) {
          if (v instanceof SyncPromise) return v;
          return new SyncPromise('fulfilled', v);
        }
        static reject(e) { return new SyncPromise('rejected', e); }
        static all(list) {
          const out = [];
          for (const item of list) {
            const p = SyncPromise.resolve(item);
            if (p._state === 'rejected') return p;
            out.push(p._value);
          }
          return SyncPromise.resolve(out);
        }
        static race(list) {
          for (const item of list) {
            const p = SyncPromise.resolve(item);
            if (p._state !== 'pending') return p;
          }
          return new SyncPromise('pending', undefined);
        }
        then(ok, err) {
          if (this._state === 'fulfilled') {
            if (typeof ok !== 'function') return this;
            try { return SyncPromise.resolve(ok(this._value)); }
            catch (e) { return SyncPromise.reject(e); }
          }
          if (typeof err !== 'function') return this;
          try { return SyncPromise.resolve(err(this._value)); }
          catch (e) { return SyncPromise.reject(e); }
        }
        catch(err) { return this.then(undefined, err); }
        finally(fn) {
          const run = () => { try { fn(); } catch (e) { } };
          this.then(run, run);
          return this;
        }
      }
      // The host is synchronous, so an eager promise runs a whole .then() chain inside one call.
      globalThis.Promise = SyncPromise;

      // Scripts schedule their initialisation ("send inited" after a request, a deferred retry, …).
      // There is no event loop here, so a timer runs inline; that keeps init deterministic and means
      // a script's readiness is known by the time its evaluation returns.
      globalThis.setTimeout = (fn, _ms, ...args) => { try { fn(...args); } catch (e) { } return 0; };
      globalThis.setImmediate = (fn, ...args) => { try { fn(...args); } catch (e) { } return 0; };
      globalThis.setInterval = () => 0;
      globalThis.clearTimeout = () => { };
      globalThis.clearInterval = () => { };
      globalThis.queueMicrotask = (fn) => { try { fn(); } catch (e) { } };
      globalThis.console = { log() { }, info() { }, warn() { }, error() { }, debug() { } };

      const handlers = {};
      globalThis.__lxInited = null;

      // Scripts pass keys both as buffers and as plain strings; normalise to bytes first.
      const toBytes = (value) => {
        if (value instanceof Uint8Array) return value;
        if (typeof value === 'string') return lx.utils.buffer.from(value);
        if (Array.isArray(value)) return new Uint8Array(value);
        return new Uint8Array(0);
      };
      const utf8Decode = (buffer) => {
        let out = '';
        for (let i = 0; i < buffer.length;) {
          const b = buffer[i++];
          if (b < 0x80) out += String.fromCharCode(b);
          else if (b < 0xe0) out += String.fromCharCode(((b & 0x1f) << 6) | (buffer[i++] & 0x3f));
          else if (b < 0xf0) {
            const b2 = buffer[i++], b3 = buffer[i++];
            out += String.fromCharCode(((b & 0x0f) << 12) | ((b2 & 0x3f) << 6) | (b3 & 0x3f));
          } else {
            const b2 = buffer[i++], b3 = buffer[i++], b4 = buffer[i++];
            const cp = ((b & 0x07) << 18) | ((b2 & 0x3f) << 12) | ((b3 & 0x3f) << 6) | (b4 & 0x3f);
            const rest = cp - 0x10000;
            out += String.fromCharCode(0xd800 + (rest >> 10), 0xdc00 + (rest & 0x3ff));
          }
        }
        return out;
      };

      const lx = {
        EVENT_NAMES: { request: 'request', inited: 'inited', updateAlert: 'updateAlert' },
        env: 'desktop',
        version: '2.0.0',
        currentScriptInfo: { name: '', description: '', version: '', author: '', homepage: '' },
        on(event, handler) { handlers[event] = handler; },
        send(event, data) { if (event === 'inited') globalThis.__lxInited = data; },
        request(url, options) {
          try {
            const raw = host.requestSync(url, JSON.stringify(options || {}));
            const parsed = JSON.parse(raw);
            if (options && options.responseType === 'arraybuffer' && parsed.bodyB64) {
              parsed.body = b64.toBytes(parsed.bodyB64);
            }
            return SyncPromise.resolve(parsed);
          } catch (e) {
            return SyncPromise.reject(new Error(String(e && e.message ? e.message : e)));
          }
        },
        utils: {
          crypto: {
            md5: (text) => host.md5(String(text)),
            randomBytes: (n) => {
              const size = n || 16;
              const bytes = new Uint8Array(size);
              const hex = host.randomHex(size);
              for (let i = 0; i < size; i++) bytes[i] = parseInt(hex.substr(i * 2, 2), 16);
              return bytes;
            },
            aesEncrypt: (data, mode, key, iv) => b64.toBytes(host.aes(true, b64.fromBytes(toBytes(data)), mode, b64.fromBytes(toBytes(key)), iv ? b64.fromBytes(toBytes(iv)) : '')),
            aesDecrypt: (data, mode, key, iv) => b64.toBytes(host.aes(false, b64.fromBytes(toBytes(data)), mode, b64.fromBytes(toBytes(key)), iv ? b64.fromBytes(toBytes(iv)) : '')),
            rsaEncrypt: (data, key) => b64.toBytes(host.rsa(b64.fromBytes(toBytes(data)), String(key))),
          },
          buffer: {
            from: (...args) => {
              if (args.length === 1 && typeof args[0] === 'number') return new Uint8Array(args[0]);
              if (args.length === 1 && args[0] instanceof Uint8Array) return args[0];
              if (args.length === 1 && typeof args[0] === 'string') {
                const enc = 'utf8';
                const text = args[0];
                const bytes = [];
                for (const ch of text) {
                  const code = ch.codePointAt(0);
                  if (code < 0x80) bytes.push(code);
                  else if (code < 0x800) bytes.push(0xc0 | (code >> 6), 0x80 | (code & 0x3f));
                  else if (code < 0x10000) bytes.push(0xe0 | (code >> 12), 0x80 | ((code >> 6) & 0x3f), 0x80 | (code & 0x3f));
                  else bytes.push(0xf0 | (code >> 18), 0x80 | ((code >> 12) & 0x3f), 0x80 | ((code >> 6) & 0x3f), 0x80 | (code & 0x3f));
                }
                return new Uint8Array(bytes);
              }
              return new Uint8Array(0);
            },
            concat: (list) => {
              let total = 0;
              for (const item of list) total += item.length;
              const out = new Uint8Array(total);
              let at = 0;
              for (const item of list) { out.set(item, at); at += item.length; }
              return out;
            },
            bufToString: (buffer, format) => {
              if (format === 'hex') return Array.from(buffer).map((b) => b.toString(16).padStart(2, '0')).join('');
              if (format === 'base64') return b64.fromBytes(buffer);
              return utf8Decode(buffer);
            },
          },
          str2b64: (text) => b64.fromBytes(lx.utils.buffer.from(String(text))),
          b642str: (text) => lx.utils.buffer.bufToString(b64.toBytes(String(text))),
          zlib: {
            inflate: (data) => b64.toBytes(host.zlib(true, b64.fromBytes(data))),
            deflate: (data) => b64.toBytes(host.zlib(false, b64.fromBytes(data))),
          },
        },
      };
      globalThis.lx = lx;
      globalThis.__lxResolve = (source, action, infoJson) => {
        const info = JSON.parse(infoJson);
        const handler = handlers[lx.EVENT_NAMES.request];
        if (typeof handler !== 'function') throw new Error('脚本没有注册 request 处理器');
        let result;
        try {
          result = handler({ source: info.source, action: info.action, info: info.info });
        } catch (e) {
          throw new Error(e && e.message ? e.message : String(e));
        }
        const promise = SyncPromise.resolve(result);
        if (promise._state === 'rejected') {
          const error = promise._value instanceof Error ? promise._value : new Error(String(promise._value));
          error.qualityMissing = /quality|音质|not support|不支持/i.test(error.message);
          throw error;
        }
        const url = promise._value;
        if (typeof url !== 'string' || url.length === 0) throw new Error('脚本返回的不是地址');
        return url;
      };
    })();
    """;
}

/// <summary>Failure raised by an LX script or by the host while driving one.</summary>
public sealed class LxException : Exception
{
    public bool QualityMissing { get; }

    public LxException(string message, bool qualityMissing = false) : base(message)
    {
        QualityMissing = qualityMissing;
    }
}
