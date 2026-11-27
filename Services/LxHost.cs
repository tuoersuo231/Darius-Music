using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Native.Function;

namespace CurrentLyrics.Services;

/// <summary>
/// LX Music v2 custom-source host: runs a third-party .js source script in a Jint sandbox and
/// exposes exactly the API surface the mobile protocol allows (lx.on / lx.send / lx.request /
/// lx.utils.buffer / lx.utils.crypto / setTimeout).
///
/// The protocol is documented at https://lxmusic.toside.cn/mobile/custom-source and the
/// Android build of this app already ships the same host (assets/lx/host.js).
/// </summary>
public sealed class LxHost : IDisposable
{
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly Engine _engine;
    private JsValue? _handler;
    private string _lastError = "";

    public string Name { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string Author { get; private set; } = "";
    public bool Inited { get; private set; }
    public string? InitError { get; private set; }
    public Dictionary<string, LxSourceInfo> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string RawScript { get; private set; } = "";

    public sealed record LxSourceInfo(string Key, string Name, string[] Actions, string[] Qualitys)
    {
        public bool SupportsLyric => Actions.Contains("lyric", StringComparer.OrdinalIgnoreCase);
    }

    private LxHost()
    {
        _engine = new Engine(options => options
            .TimeoutInterval(TimeSpan.FromSeconds(20))
            .LimitMemory(96 * 1024 * 1024)
            .MaxStatements(2_000_000));
    }

    /// <summary>Load and initialise a source script. Returns a host even when the script failed to
    /// init (check Inited / InitError), because the caller wants to report why.</summary>
    public static LxHost Load(string scriptPath)
    {
        var raw = File.ReadAllText(scriptPath, Encoding.UTF8);
        var host = new LxHost { RawScript = raw };
        host.ParseHeader(raw);
        host.Execute(raw);
        return host;
    }

    public static LxHost LoadFromText(string raw, string name = "inline")
    {
        var host = new LxHost { RawScript = raw, Name = name };
        host.ParseHeader(raw);
        host.Execute(raw);
        return host;
    }

    private void ParseHeader(string raw)
    {
        // The first block comment carries @name / @version / @author / @description / @homepage.
        var end = raw.IndexOf("*/", StringComparison.Ordinal);
        var head = end > 0 ? raw[..end] : raw.Length > 400 ? raw[..400] : raw;
        foreach (var line in head.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("*")) continue;
            t = t[1..].Trim();
            var sp = t.IndexOf(' ');
            if (sp <= 0) continue;
            var key = t[..sp];
            var value = t[(sp + 1)..].Trim();
            switch (key)
            {
                case "@name": Name = string.IsNullOrEmpty(Name) ? value : Name; break;
                case "@version": Version = value; break;
                case "@author": Author = value; break;
            }
        }
        if (string.IsNullOrEmpty(Name)) Name = "inline";
    }

    private const string Prelude = """
        (function () {
          const EVENT_NAMES = Object.freeze({ request: 'request', inited: 'inited', updateAlert: 'updateAlert' });
          const bufferFrom = function (value, encoding) { return __lx_buf_from(value, encoding); };
          const bufToString = function (buf, encoding) { return __lx_buf_to_string(buf, encoding); };
          const crypto = Object.freeze({
            md5: function (s) { return __lx_md5(s); },
            aesEncrypt: function (buf, mode, key, iv) { return __lx_aes_encrypt(buf, mode, key, iv); },
            randomBytes: function (n) { return __lx_random_bytes(n); },
            rsaEncrypt: function (buf, key) { return __lx_rsa_encrypt(buf, key); },
          });
          globalThis.lx = Object.freeze({
            EVENT_NAMES: EVENT_NAMES,
            version: '2.0.0',
            env: 'mobile',
            currentScriptInfo: __lx_script_info(),
            utils: Object.freeze({ buffer: Object.freeze({ from: bufferFrom, bufToString: bufToString }), crypto: crypto }),
            request: function (url, options, callback) { return __lx_request(url, options, callback); },
            on: function (event, callback) { __lx_on(event, callback); },
            send: function (event, data) { __lx_send(event, data); },
          });
          globalThis.setTimeout = function (fn, ms) { return __lx_set_timeout(fn, ms); };
          globalThis.clearTimeout = function (id) { __lx_clear_timeout(id); };
          globalThis.__lx_call = function (payload) {
            const cap = { done: false, value: null, error: null };
            try {
              if (typeof __lx_req_handler !== 'function') { cap.done = true; cap.error = 'no handler'; return cap; }
              const p = __lx_req_handler(payload);
              if (p && typeof p.then === 'function') {
                p.then(function (v) { cap.done = true; cap.value = v; },
                       function (e) { cap.done = true; cap.error = (e && (e.message || e.stack)) ? String(e.message || e.stack) : String(e); });
              } else { cap.done = true; cap.value = p; }
            } catch (e) { cap.done = true; cap.error = String((e && (e.message || e)) || e); }
            return cap;
          };
        })();
        """;

    private void Execute(string raw)
    {
        var p = _engine;
        p.SetValue("__lx_buf_from", new Func<JsValue, JsValue, JsValue>(HostBufFrom));
        p.SetValue("__lx_buf_to_string", new Func<JsValue, JsValue, JsValue>(HostBufToString));
        p.SetValue("__lx_md5", new Func<JsValue, JsValue>(HostMd5));
        p.SetValue("__lx_aes_encrypt", new Func<JsValue, JsValue, JsValue, JsValue, JsValue>(HostAesEncrypt));
        p.SetValue("__lx_random_bytes", new Func<JsValue, JsValue>(HostRandomBytes));
        p.SetValue("__lx_rsa_encrypt", new Func<JsValue, JsValue, JsValue>(HostRsaEncrypt));
        p.SetValue("__lx_request", new Func<JsValue, JsValue, JsValue, JsValue>(HostRequest));
        p.SetValue("__lx_on", new Action<JsValue, JsValue>(HostOn));
        p.SetValue("__lx_send", new Action<JsValue, JsValue>(HostSend));
        p.SetValue("__lx_set_timeout", new Func<JsValue, JsValue, JsValue>(HostSetTimeout));
        p.SetValue("__lx_clear_timeout", new Action<JsValue>(_ => { }));
        p.SetValue("__lx_script_info", new Func<JsValue>(() => ScriptInfoObject()));

        try
        {
            p.Execute(Prelude);
            p.Execute(raw);
            if (!Inited) InitError ??= "script did not send the inited event";
        }
        catch (Exception ex)
        {
            InitError = ex.GetType().Name + ": " + ex.Message;
        }
    }

    private JsValue ScriptInfoObject()
    {
        var o = new JsObject(_engine);
        o.Set("name", JsValue.FromObject(_engine, Name));
        o.Set("description", JsValue.FromObject(_engine, ""));
        o.Set("version", JsValue.FromObject(_engine, Version));
        o.Set("author", JsValue.FromObject(_engine, Author));
        o.Set("homepage", JsValue.FromObject(_engine, ""));
        o.Set("rawScript", JsValue.FromObject(_engine, RawScript));
        return o;
    }

    // ---------- host API ----------

    private void HostOn(JsValue evt, JsValue callback)
    {
        if (!evt.IsString() || evt.AsString() != "request") return;
        _handler = callback;
        _engine.SetValue("__lx_req_handler", callback);
    }

    private void HostSend(JsValue evt, JsValue data)
    {
        if (!evt.IsString()) return;
        switch (evt.AsString())
        {
            case "inited":
                ReadSources(data);
                Inited = true;
                break;
            case "updateAlert":
                break;
        }
    }

    private void ReadSources(JsValue data)
    {
        if (data is not ObjectInstance o || !o.HasProperty("sources")) return;
        if (o.Get("sources") is not ObjectInstance sources) return;
        foreach (var prop in sources.GetOwnProperties())
        {
            var key = prop.Key.ToString();
            if (prop.Value.Value is not ObjectInstance src) continue;
            var name = src.HasProperty("name") ? src.Get("name").AsString() : key;
            var actions = ReadStringArray(src.HasProperty("actions") ? src.Get("actions") : JsValue.Undefined);
            var qualitys = ReadStringArray(src.HasProperty("qualitys") ? src.Get("qualitys") : JsValue.Undefined);
            Sources[key] = new LxSourceInfo(key, name, actions, qualitys);
        }
    }

    private string[] ReadStringArray(JsValue value)
    {
        if (value is not ObjectInstance o) return Array.Empty<string>();
        var list = new List<string>();
        var len = o.HasProperty("length") ? (int)o.Get("length").AsNumber() : 0;
        for (int i = 0; i < len; i++) list.Add(o.Get(i).AsString());
        return list.ToArray();
    }

    private JsValue HostRequest(JsValue urlValue, JsValue options, JsValue callback)
    {
        try
        {
            var url = urlValue.AsString();
            var method = HttpMethod.Get;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? body = null;
            string? form = null;

            if (options is ObjectInstance opt)
            {
                if (opt.HasProperty("method") && opt.Get("method").IsString())
                    method = new HttpMethod(opt.Get("method").AsString().ToUpperInvariant());
                if (opt.HasProperty("headers") && opt.Get("headers") is ObjectInstance ho)
                    foreach (var h in ho.GetOwnProperties())
                        headers[h.Key.ToString()] = h.Value.Value.IsString() ? h.Value.Value.AsString() : h.Value.Value.ToString();
                if (opt.HasProperty("body") && opt.Get("body").IsString())
                    body = opt.Get("body").AsString();
                if (opt.HasProperty("form") && opt.Get("form") is ObjectInstance fo)
                {
                    var pairs = new List<string>();
                    foreach (var f in fo.GetOwnProperties())
                        pairs.Add(Uri.EscapeDataString(f.Key.ToString()) + "=" +
                                  Uri.EscapeDataString(f.Value.Value.IsString() ? f.Value.Value.AsString() : f.Value.Value.ToString()));
                    form = string.Join("&", pairs);
                }
            }

            using var request = new HttpRequestMessage(method, url);
            foreach (var kv in headers) request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            if (form != null)
                request.Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
            else if (body != null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = SharedHttp.Send(request);
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            var respObj = new JsObject(_engine);
            respObj.Set("statusCode", JsValue.FromObject(_engine, (int)response.StatusCode));
            respObj.Set("body", JsValue.FromObject(_engine, text));
            var respHeaders = new JsObject(_engine);
            foreach (var h in response.Headers)
                respHeaders.Set(h.Key.ToLowerInvariant(), JsValue.FromObject(_engine, string.Join(",", h.Value)));
            foreach (var h in response.Content.Headers)
                respHeaders.Set(h.Key.ToLowerInvariant(), JsValue.FromObject(_engine, string.Join(",", h.Value)));
            respObj.Set("headers", respHeaders);

            if (callback is JsValue cb && cb.IsObject() && cb.AsObject() is Jint.Native.Function.Function fn)
                fn.Call(JsValue.Undefined, new[] { JsValue.Undefined, respObj });
            return JsValue.Undefined;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            if (callback is JsValue cb2 && cb2.AsObject() is Jint.Native.Function.Function fn2)
                fn2.Call(JsValue.Undefined, new[] { JsValue.FromObject(_engine, ex.Message), JsValue.Undefined });
            return JsValue.Undefined;
        }
    }

    private JsValue HostBufFrom(JsValue value, JsValue encoding)
    {
        var enc = encoding.IsUndefined() || encoding.IsNull() ? "utf8" : encoding.AsString();
        if (value.IsString())
        {
            var s = value.AsString();
            var bytes = enc switch
            {
                "hex" => Convert.FromHexString(s),
                "base64" => Convert.FromBase64String(
                    s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '=')),
                "binary" or "latin1" => s.Select(c => (byte)c).ToArray(),
                _ => Encoding.UTF8.GetBytes(s),
            };
            return JsValue.FromObject(_engine, ToIntArray(bytes));
        }
        return JsValue.FromObject(_engine, ToIntArray(ToBytes(value)));
    }

    private JsValue HostBufToString(JsValue value, JsValue encoding)
    {
        var bytes = ToBytes(value);
        var enc = encoding.IsUndefined() || encoding.IsNull() ? "utf8" : encoding.AsString();
        return enc switch
        {
            "hex" => JsValue.FromObject(_engine, Convert.ToHexString(bytes).ToLowerInvariant()),
            "base64" => JsValue.FromObject(_engine, Convert.ToBase64String(bytes)),
            "binary" or "latin1" => JsValue.FromObject(_engine, new string(bytes.Select(b => (char)b).ToArray())),
            _ => JsValue.FromObject(_engine, Encoding.UTF8.GetString(bytes)),
        };
    }

    private static int[] ToIntArray(byte[] bytes)
    {
        var arr = new int[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) arr[i] = bytes[i];
        return arr;
    }

    private static byte[] ToBytes(JsValue value)
    {
        // A JS array (what the sandbox hands back for a buffer) has to be read element-wise;
        // ToObject() on it is a JsArray, not a CLR array.
        if (value.IsObject() && value.AsObject() is ObjectInstance arr && arr.IsArray())
        {
            var len = (int)arr.Get("length").AsNumber();
            var bytes = new byte[Math.Clamp(len, 0, 64 * 1024 * 1024)];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)arr.Get(i).AsNumber();
            return bytes;
        }
        var obj = value.IsUndefined() || value.IsNull() ? null : value.ToObject();
        return obj switch
        {
            byte[] b => b,
            int[] ints => ints.Select(i => (byte)i).ToArray(),
            string s => Encoding.UTF8.GetBytes(s),
            Jint.Runtime.Interop.ObjectWrapper w when w.Target is byte[] wb => wb,
            Jint.Runtime.Interop.ObjectWrapper w2 when w2.Target is int[] wi => wi.Select(i => (byte)i).ToArray(),
            _ => Array.Empty<byte>(),
        };
    }

    private JsValue HostMd5(JsValue value)
    {
        var input = value.IsString() ? Encoding.UTF8.GetBytes(value.AsString()) : ToBytes(value);
        return JsValue.FromObject(_engine, Convert.ToHexString(MD5.HashData(input)).ToLowerInvariant());
    }

    private JsValue HostAesEncrypt(JsValue data, JsValue mode, JsValue key, JsValue iv)
    {
        var bytes = ToBytes(data);
        var keyBytes = ToBytes(key);
        var ivBytes = iv.IsUndefined() || iv.IsNull() ? null : ToBytes(iv);
        var algorithm = (mode.IsString() ? mode.AsString() : "aes-128-cbc").ToLowerInvariant();
        using var aes = Aes.Create();
        aes.Key = keyBytes;
        if (algorithm.Contains("ecb"))
        {
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;
        }
        else
        {
            aes.Mode = CipherMode.CBC;
            aes.IV = ivBytes ?? new byte[16];
            aes.Padding = PaddingMode.PKCS7;
        }
        using var encryptor = aes.CreateEncryptor();
        return JsValue.FromObject(_engine, ToIntArray(encryptor.TransformFinalBlock(bytes, 0, bytes.Length)));
    }

    private JsValue HostRandomBytes(JsValue size)
    {
        int n = size.IsNumber() ? (int)size.AsNumber() : 16;
        if (n <= 0 || n > 1024) n = 16;
        var bytes = RandomNumberGenerator.GetBytes(n);
        return JsValue.FromObject(_engine, Convert.ToHexString(bytes).ToLowerInvariant());
    }

    private JsValue HostRsaEncrypt(JsValue data, JsValue key)
        => throw new NotSupportedException("lx.utils.crypto.rsaEncrypt is not available in this host");

    private JsValue HostSetTimeout(JsValue fn, JsValue ms)
    {
        // The protocol only guarantees setTimeout for short defers; running it inline keeps the
        // sandbox single-threaded and is what every source script actually relies on.
        if (fn.AsObject() is Jint.Native.Function.Function f) f.Call(JsValue.Undefined, Array.Empty<JsValue>());
        return JsValue.Undefined;
    }

    // ---------- invocation ----------

    /// <summary>Call the script's request handler and return its promise result.</summary>
    public LxResult Request(string source, string action, JsValue info)
    {
        if (_handler == null) return new LxResult(false, null, "no request handler registered");
        var payload = new JsObject(_engine);
        payload.Set("source", JsValue.FromObject(_engine, source));
        payload.Set("action", JsValue.FromObject(_engine, action));
        payload.Set("info", info);

        try
        {
            var call = _engine.Evaluate("__lx_call");
            if (call is not Jint.Native.Function.Function fn)
                return new LxResult(false, null, "host prelude missing");
            var cap = fn.Call(JsValue.Undefined, new[] { (JsValue)payload });
            // run any pending promise continuations
            try { _engine.Advanced.ProcessTasks(); } catch { }
            if (cap is not ObjectInstance co) return new LxResult(false, null, "bad handler result");
            var done = co.HasProperty("done") && co.Get("done").AsBoolean();
            if (!done) return new LxResult(false, null, "handler promise did not settle");
            var error = co.HasProperty("error") ? co.Get("error") : JsValue.Null;
            if (!error.IsNull() && !error.IsUndefined())
                return new LxResult(false, null, error.IsString() ? error.AsString() : error.ToString());
            return new LxResult(true, co.Get("value"), null);
        }
        catch (Exception ex)
        {
            return new LxResult(false, null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    public void Dispose() => _engine.Dispose();

    public sealed record LxResult(bool Ok, JsValue? Value, string? Error);

    /// <summary>Build the { musicInfo: {...} } payload the protocol hands to a source script.</summary>
    public JsValue MakeMusicInfo(string songmid, string name, string singer, string album)
    {
        var mi = new JsObject(_engine);
        mi.Set("songmid", JsValue.FromObject(_engine, songmid));
        mi.Set("name", JsValue.FromObject(_engine, name));
        mi.Set("singer", JsValue.FromObject(_engine, singer));
        mi.Set("albumName", JsValue.FromObject(_engine, album));
        mi.Set("source", JsValue.FromObject(_engine, "kw"));
        var info = new JsObject(_engine);
        info.Set("musicInfo", mi);
        return info;
    }

    /// <summary>Exercise buffer / md5 / http once so the caller can see whether the shims work.</summary>
    public string ProbeUtilities()
    {
        var parts = new List<string>();
        try
        {
            var md5 = _engine.Evaluate("lx.utils.crypto.md5('abc')");
            parts.Add("md5(abc)=" + md5.AsString());
        }
        catch (Exception ex) { parts.Add("md5 FAILED " + ex.Message); }
        try
        {
            var b64 = _engine.Evaluate("lx.utils.buffer.bufToString(lx.utils.buffer.from('AQID', 'base64'), 'hex')");
            parts.Add("base64->hex=" + b64.AsString());
        }
        catch (Exception ex) { parts.Add("buffer FAILED " + ex.Message); }
        try
        {
            var rnd = _engine.Evaluate("lx.utils.crypto.randomBytes(8)");
            parts.Add("randomBytes=" + rnd.AsString());
        }
        catch (Exception ex) { parts.Add("random FAILED " + ex.Message); }
        try
        {
            _engine.SetValue("__probe_url", "https://music.163.com/api/song/lyric?id=1901371647&lv=-1&kv=-1&tv=-1&yv=-1");
            var body = _engine.Evaluate("lx.request(__probe_url, {}, function(e, r){ globalThis.__probe_resp = e ? ('ERR ' + e) : r.body.length })");
            var resp = _engine.Evaluate("globalThis.__probe_resp");
            parts.Add("http=" + (resp.IsUndefined() ? "(no callback)" : resp.ToString()));
        }
        catch (Exception ex) { parts.Add("http FAILED " + ex.Message); }
        return string.Join(" | ", parts);
    }

    /// <summary>Short human-readable rendering of a script result, for QA output.</summary>
    public static string Describe(JsValue? value)
    {
        if (value == null || value.IsUndefined() || value.IsNull()) return "(null)";
        if (value.IsString()) return value.AsString();
        if (value.IsNumber()) return value.AsNumber().ToString("0.###");
        if (value is ObjectInstance o)
        {
            var parts = new List<string>();
            foreach (var prop in o.GetOwnProperties().Take(8))
            {
                var v = prop.Value.Value;
                var text = v.IsString() ? v.AsString() : v.ToString();
                if (text.Length > 60) text = text[..60] + "...";
                parts.Add(prop.Key + "=" + text);
            }
            return "{" + string.Join(", ", parts) + "}";
        }
        return value.ToString();
    }
}
