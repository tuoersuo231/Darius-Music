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

/// <summary>Persists playlists in data\playlists.json.</summary>
public static class PlaylistStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
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
