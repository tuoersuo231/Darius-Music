using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace CurrentLyrics.Services;

/// <summary>A user-made playlist: name, optional custom cover and the audio files it holds.</summary>
public sealed class Playlist : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新建歌单";
    public string CoverPath { get; set; } = "";
    public List<string> Songs { get; set; } = new();

    /// <summary>
    /// 在线曲目的元数据，key = 歌单条目（<c>netease:&lt;id&gt;</c>）。
    /// 歌单里只存 id + 这份元数据，**加歌不下载**：列表靠它离线渲染，真要听的时候才联网取流。
    /// </summary>
    public Dictionary<string, CloudTrack> Cloud { get; set; } = new();

    public const string CloudPrefix = "netease:";

    public static string CloudEntry(long id) => CloudPrefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool IsCloud(string? entry) =>
        entry != null && entry.StartsWith(CloudPrefix, StringComparison.OrdinalIgnoreCase);

    public static long CloudId(string? entry)
    {
        if (!IsCloud(entry)) return 0;
        return long.TryParse(entry!.AsSpan(CloudPrefix.Length), out var id) ? id : 0;
    }

    /// <summary>登记一条在线曲目（已存在就更新元数据），返回要放进 <see cref="Songs"/> 的条目。</summary>
    public string RememberCloud(CloudTrack track)
    {
        Cloud ??= new Dictionary<string, CloudTrack>();
        var entry = CloudEntry(track.Id);
        Cloud[entry] = track;
        return entry;
    }

    /// <summary>条目对应的曲目；元数据缺失时给一个只有 id 的壳，播放时照样能解析。</summary>
    public Models.Song ToSong(string entry)
    {
        var id = CloudId(entry);
        if (Cloud != null && Cloud.TryGetValue(entry, out var track))
        {
            var song = track.ToSong(entry);
            if (song.NeteaseId <= 0) song.NeteaseId = id;
            return song;
        }
        return new Models.Song
        {
            Source = Models.SongSource.Netease,
            Path = entry,
            NeteaseId = id,
            Title = id > 0 ? id.ToString() : "在线曲目",
        };
    }

    /// <summary>Resolved cover: custom image first, otherwise the first song's artwork.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public BitmapSource? CoverImage
    {
        get => _coverImage;
        set
        {
            if (ReferenceEquals(_coverImage, value)) return;
            _coverImage = value;
            OnPropertyChanged();
        }
    }

    private BitmapSource? _coverImage;

    /// <summary>How many of <see cref="Songs"/> still exist on disk. Refreshed by
    /// <c>PlayerController.RefreshPlaylistCounts()</c>; kept as a plain property so the sidebar can
    /// show the number the user will actually see in the list instead of the stored path count.
    /// Raises change notifications, otherwise a re-click that re-reads the playlist would update the
    /// rows but leave a stale "7 首" in the sidebar.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ExistingCount
    {
        get => _existingCount;
        set
        {
            if (_existingCount == value) return;
            _existingCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MissingCount));
            OnPropertyChanged(nameof(CountText));
        }
    }

    private int _existingCount;

    [System.Text.Json.Serialization.JsonIgnore]
    public int MissingCount => Math.Max(0, Songs.Count - ExistingCount);

    [System.Text.Json.Serialization.JsonIgnore]
    public string CountText => MissingCount > 0
        ? $"{ExistingCount} 首 · {MissingCount} 个失效"
        : $"{Songs.Count} 首";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>歌单里一条在线曲目需要的东西：够渲染列表，也够播放时按 id 取流。</summary>
public sealed class CloudTrack
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public double Seconds { get; set; }
    public string? CoverUrl { get; set; }

    public Models.Song ToSong(string entry) => new()
    {
        Source = Models.SongSource.Netease,
        Path = entry,
        NeteaseId = Id,
        Title = Title,
        Artist = Artist,
        Album = Album,
        Duration = TimeSpan.FromSeconds(Math.Max(0, Seconds)),
        CoverUrl = CoverUrl,
    };
}

/// <summary>Persists playlists in data\playlists.json.</summary>
public static class PlaylistStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 中文歌名别写成 \uXXXX：这份文件用户可能自己打开看。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static string FilePath => Path.Combine(AppPaths.DataDir, "playlists.json");

    public static List<Playlist> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize<List<Playlist>>(File.ReadAllText(FilePath), Options);
                if (list != null) return list;
            }
        }
        catch { }
        return new List<Playlist>();
    }

    public static void Save(IEnumerable<Playlist> playlists)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(playlists.ToList(), Options));
        }
        catch { }
    }
}
