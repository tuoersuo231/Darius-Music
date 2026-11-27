using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CurrentLyrics.Services;

/// <summary>One imported 落雪 (LX) script, kept as the original file plus what it declared.</summary>
public sealed class LxEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string File { get; set; } = "";
    /// <summary>Platforms it can serve ("wy" NetEase, "tx" QQ) → qualities, as declared at init.</summary>
    public Dictionary<string, List<string>> Platforms { get; set; } = new();
    public string? Error { get; set; }

    public string Summary
    {
        get
        {
            if (Error != null) return "无法加载：" + Error;
            if (Platforms.Count == 0) return "未声明可用平台";
            var parts = Platforms.Select(p => (p.Key == "wy" ? "网易云" : p.Key == "tx" ? "QQ" : p.Key) +
                                               " " + string.Join("/", p.Value.Take(4)));
            return string.Join(" · ", parts);
        }
    }

    public List<string> QualitiesFor(string platform) =>
        Platforms.TryGetValue(platform, out var list) ? list : new List<string>();
}

/// <summary>
/// Script store: files under <c>data\lx</c>, an index beside them, and the one script that is active.
/// Importing probes the script immediately, because a script that cannot declare its platforms is
/// useless and the user should learn that at import time, not on the first play.
/// </summary>
public static class LxStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string Dir => Path.Combine(AppPaths.DataDir, "lx");
    private static string IndexPath => Path.Combine(Dir, "index.json");

    public static List<LxEntry> Entries { get; private set; } = new();
    public static string ActiveId { get; private set; } = "";
    public static bool SmartSwitch { get; private set; }
    public static bool Loaded { get; private set; }

    public static void Load()
    {
        if (Loaded) return;
        Loaded = true;
        try
        {
            Directory.CreateDirectory(Dir);
            if (!File.Exists(IndexPath)) return;
            var root = JsonNode.Parse(File.ReadAllText(IndexPath)) as JsonObject;
            ActiveId = root?["active"]?.GetValue<string>() ?? "";
            SmartSwitch = root?["smart"]?.GetValue<bool>() ?? false;
            var list = root?["scripts"] as JsonArray;
            if (list == null) return;
            foreach (var item in list)
            {
                var entry = item?.Deserialize<LxEntry>(Json);
                if (entry != null) Entries.Add(entry);
            }
        }
        catch
        {
            Entries = new List<LxEntry>();
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var root = new JsonObject
            {
                ["active"] = ActiveId,
                ["smart"] = SmartSwitch,
                ["scripts"] = JsonSerializer.SerializeToNode(Entries, Json),
            };
            File.WriteAllText(IndexPath, root.ToJsonString(Json));
        }
        catch { }
    }

    public static void SetActive(string id)
    {
        if (id.Length > 0 && Entries.All(e => e.Id != id)) return;
        ActiveId = id;
        Save();
    }

    public static void SetSmartSwitch(bool enabled)
    {
        SmartSwitch = enabled;
        Save();
    }

    public static void Remove(string id)
    {
        var entry = Entries.FirstOrDefault(e => e.Id == id);
        if (entry == null) return;
        try
        {
            var file = Path.Combine(Dir, entry.File);
            if (File.Exists(file)) File.Delete(file);
        }
        catch { }
        Entries.Remove(entry);
        if (ActiveId == id) ActiveId = "";
        Save();
    }

    /// <summary>
    /// 安装包里自带的落雪脚本：启动时把它装进 <c>data\lx</c>，这样一台干净机器装完就有音源，
    /// 不会出现「网易云说无版权、别的电脑却能放」。
    ///
    /// 用<b>随包的清单</b>而不是当场跑一遍脚本：脚本要联网自检（超时 3 s），放启动路径会拖慢开机。
    /// 只有在文件缺失/内容变了（脚本升级）时才写盘，其余时候只是一次读文件 + 一次比较。
    /// 不覆盖用户自己导入的同类脚本：同名不同 id 的旧版会被顶掉，并把「当前选中」跟过去。
    /// </summary>
    public static int SeedBundled()
    {
        Load();
        try
        {
            var bundledDir = Path.Combine(AppContext.BaseDirectory, "Assets", "lx");
            var manifest = Path.Combine(bundledDir, "bundled.json");
            if (!File.Exists(manifest)) return 0;
            var list = JsonNode.Parse(File.ReadAllText(manifest))?["scripts"] as JsonArray;
            if (list == null) return 0;

            int added = 0;
            bool dirty = false;
            Directory.CreateDirectory(Dir);
            foreach (var node in list)
            {
                var preset = node?.Deserialize<LxEntry>(Json);
                if (preset == null || preset.Id.Length == 0 || preset.File.Length == 0) continue;
                var source = Path.Combine(bundledDir, preset.File);
                if (!File.Exists(source)) continue;

                var target = Path.Combine(Dir, preset.File);
                bool changed = false;
                try
                {
                    var bytes = File.ReadAllBytes(source);
                    var existing = File.Exists(target) ? File.ReadAllBytes(target) : null;
                    if (existing == null || existing.Length != bytes.Length ||
                        !existing.AsSpan().SequenceEqual(bytes))
                    {
                        File.WriteAllBytes(target, bytes);
                        changed = true;
                    }
                }
                catch
                {
                    continue;
                }

                // 同名不同 id＝脚本升级：把旧版从列表里换掉，选中状态继续跟到新版。
                bool wasActive = ActiveId == preset.Id;
                var stale = Entries.FirstOrDefault(e => e.Id != preset.Id && e.Name == preset.Name);
                if (stale != null)
                {
                    if (ActiveId == stale.Id) wasActive = true;
                    Entries.Remove(stale);
                    try
                    {
                        var old = Path.Combine(Dir, stale.File);
                        if (File.Exists(old)) File.Delete(old);
                    }
                    catch { }
                    dirty = true;
                }

                var entry = Entries.FirstOrDefault(e => e.Id == preset.Id);
                if (entry == null)
                {
                    Entries.Add(preset);
                    added++;
                    dirty = true;
                }
                else
                {
                    // 元数据以随包清单为准，Error 保持用户自己那次探测的结果。
                    entry.Name = preset.Name;
                    entry.Version = preset.Version;
                    entry.Author = preset.Author;
                    entry.Description = preset.Description;
                    entry.File = preset.File;
                    entry.Platforms = preset.Platforms;
                    dirty = true;
                }

                if (ActiveId.Length == 0 || wasActive) ActiveId = preset.Id;
                if (changed || added > 0)
                    LogSeed($"{preset.Name} {preset.Version} -> {preset.File} changed={changed}");
            }
            if (dirty) Save();
            return added;
        }
        catch (Exception ex)
        {
            LogSeed("seed failed: " + ex.GetType().Name + " " + ex.Message);
            return 0;
        }
    }

    private static void LogSeed(string text)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "seed.log"),
                $"{DateTime.Now:MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Imports one script file; returns the entry, or throws with a readable reason.</summary>
    public static LxEntry Import(string path)    {
        Load();
        var script = File.ReadAllText(path);
        var info = ReadHeader(script);
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(script))).ToLowerInvariant();

        var existing = Entries.FirstOrDefault(e => e.Id == id);
        if (existing != null) return existing;

        Directory.CreateDirectory(Dir);
        var fileName = id[..16] + ".js";
        File.WriteAllText(Path.Combine(Dir, fileName), script);

        var entry = new LxEntry
        {
            Id = id,
            Name = info.GetValueOrDefault("name", Path.GetFileNameWithoutExtension(path)),
            Version = info.GetValueOrDefault("version", ""),
            Author = info.GetValueOrDefault("author", ""),
            Description = info.GetValueOrDefault("description", ""),
            File = fileName,
            Error = null,
        };
        Probe(entry, script);
        Entries.Add(entry);
        if (ActiveId.Length == 0 && entry.Error == null) ActiveId = id;
        Save();
        return entry;
    }

    /// <summary>Runs the script once to read what it declares; a load failure is recorded, not thrown.</summary>
    private static void Probe(LxEntry entry, string script)
    {
        try
        {
            using var runtime = new LxRuntime();
            var capabilities = runtime.Load(entry.Id, script);
            var root = JsonNode.Parse(capabilities) as JsonObject;
            var sources = root?["sources"] as JsonObject;
            if (sources == null)
            {
                entry.Error = "脚本没有声明 sources";
                return;
            }
            foreach (var (platform, value) in sources)
            {
                if (value is not JsonObject source) continue;
                if ((source["type"]?.GetValue<string>() ?? "") != "music") continue;
                var qualities = (source["qualitys"] as JsonArray)?.Select(q => q?.GetValue<string>() ?? "")
                    .Where(q => q.Length > 0).ToList() ?? new List<string>();
                var actions = (source["actions"] as JsonArray)?.Select(a => a?.GetValue<string>() ?? "").ToList()
                              ?? new List<string>();
                if (!actions.Contains("musicUrl")) continue;
                entry.Platforms[platform] = qualities;
            }
            if (entry.Platforms.Count == 0) entry.Error = "没有声明 musicUrl 接口";
        }
        catch (Exception e)
        {
            entry.Error = e.Message.Length > 120 ? e.Message[..120] : e.Message;
        }
    }

    /// <summary>Reads the <c>@name</c>/<c>@version</c> block that every LX script starts with.</summary>
    private static Dictionary<string, string> ReadHeader(string script)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var end = script.IndexOf("*/", StringComparison.Ordinal);
        if (end < 0) return fields;
        foreach (var line in script[..end].Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('*').Trim();
            if (!trimmed.StartsWith('@')) continue;
            var space = trimmed.IndexOf(' ');
            if (space < 0) continue;
            fields[trimmed[1..space]] = trimmed[(space + 1)..].Trim();
        }
        return fields;
    }
}
