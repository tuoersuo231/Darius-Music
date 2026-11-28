using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace CurrentLyrics.Models;

public enum SongSource
{
    Local,
    Netease,
}

/// <summary>One playable track. Local rows carry a file path; NetEase rows carry the cloud id,
/// a remote cover and a resolved stream URL.</summary>
public sealed class Song : INotifyPropertyChanged
{
    private BitmapSource? _cover;
    private bool _coverRequested;
    private bool _isCurrent;

    public SongSource Source { get; init; } = SongSource.Local;

    /// <summary>Local file path, or a synthetic key for cloud rows.</summary>
    public string Path { get; set; } = "";
    public long NeteaseId { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    /// <summary>Length of the track. A file with no duration tag reads as 0 until the media pipeline
    /// reports the real one, and the row has to refresh when that happens.</summary>
    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            if (_duration == value) return;
            _duration = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DurationText));
        }
    }

    private TimeSpan _duration;
    public string? CoverUrl { get; set; }
    public string? StreamUrl { get; set; }
    /// <summary>Quality level the cached StreamUrl was resolved for.</summary>
    public string? StreamLevel { get; set; }
    /// <summary>When the cached StreamUrl stops working. NetEase CDN links carry a short expiry, so a
    /// URL resolved for a previous attempt must not be reused minutes later.</summary>
    public DateTime StreamExpiresAt { get; set; }
    public string? LyricsPath { get; set; }
    public long AlbumId { get; set; }

    /// <summary>1-based row number inside the current library view.</summary>
    public int Index
    {
        get => _index;
        set
        {
            if (_index == value) return;
            _index = value;
            OnPropertyChanged();
        }
    }

    private int _index;

    /// <summary>True for the track that is loaded in the player (drives the row highlight).</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    public BitmapSource? Cover
    {
        get => _cover;
        set
        {
            if (ReferenceEquals(_cover, value)) return;
            _cover = value;
            OnPropertyChanged();
        }
    }

    internal bool CoverRequested
    {
        get => _coverRequested;
        set => _coverRequested = value;
    }

    public string FileName => Source == SongSource.Local ? System.IO.Path.GetFileName(Path) : $"{NeteaseId}.mp3";
    public string DurationText => Duration > TimeSpan.Zero ? Duration.ToString(@"m\:ss") : "--:--";
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title)
        ? (Source == SongSource.Local ? System.IO.Path.GetFileNameWithoutExtension(Path) : "未知歌曲")
        : Title;
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "未知歌手" : Artist;
    public string SourceLabel => Source == SongSource.Netease ? "网易云" : "本地";

    /// <summary>Stable key for per-song settings (lyric offset).</summary>
    public string Key => Source == SongSource.Local ? "local:" + Path : "netease:" + NeteaseId;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => $"{DisplayTitle} - {DisplayArtist}";
}
