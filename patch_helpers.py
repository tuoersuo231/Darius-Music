import io
p = r"E:\DSH\工作区\CurrentLyrics\Services\LxHost.cs"
s = io.open(p, encoding="utf-8").read()
helper = '''
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
'''
i = s.rindex("}")
s = s[:i] + helper + "}\n"
io.open(p,"w",encoding="utf-8",newline="\n").write(s)
print("helpers added")
