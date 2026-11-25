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

    /// <summary>Imports one script file; returns the entry, or throws with a readable reason.</summary>
    public static LxEntry Import(string path)
    {
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
