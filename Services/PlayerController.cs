using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CurrentLyrics.Models;

namespace CurrentLyrics.Services;

/// <summary>
/// Binds the local folder, the NetEase account and the queue mirrored from the WSA app into one
/// library view, and drives playback plus lyric loading for whichever source a song comes from.
/// </summary>
public sealed class PlayerController : IDisposable
{
    public PlaybackService Playback { get; } = new();
    public AppSettings Settings { get; }
    public LibraryService LocalLibrary { get; } = new();

    public ObservableCollection<Song> Songs { get; } = new();
    public ObservableCollection<Playlist> Playlists { get; } = new();
    /// <summary>The playlist currently displayed, so reordering can be persisted.</summary>
    private Playlist? _activePlaylist;
    public event Action? ModeChanged;

    public Song? Current { get; private set; }
    public LyricDocument? Lyrics { get; private set; }
    public string ViewName { get; private set; } = "音乐库";
    public string ViewSubtitle { get; private set; } = "从左边选择音乐服务开始播放";
    public string? ViewCoverUrl { get; private set; }

    /// <summary>Cover of the current view when it does not come from a URL (playlists and the local
    /// library resolve to an already-decoded bitmap). Takes precedence over <see cref="ViewCoverUrl"/>.</summary>
    public BitmapSource? ViewCoverImage { get; private set; }
    public bool Busy { get; private set; }

    /// <summary>Short status while a cloud track is being cached (null when idle).</summary>
    public string? Preparing { get; private set; }

    /// <summary>One-line message about the last action that failed or needs the user (null when fine).</summary>
    public string? Notice { get; private set; }

    /// <summary>Shows a one-line status (used by the shell after settings changes).</summary>
    public void SetNotice(string? notice)
    {
        Notice = notice;
        StateChanged?.Invoke();
    }

    /// <summary>True while the list shows online search results instead of a local collection.</summary>
    public bool IsOnlineView { get; private set; }

    /// <summary>The keyword the online view was last searched with. Unlike <see cref="OnlineKeyword"/>
    /// this survives leaving the view, so clicking 在线搜索 again (or coming back from 本地音乐)
    /// restores the search instead of wiping it.</summary>
    public string LastOnlineKeyword { get; private set; } = "";

    /// <summary>Keyword of the online search currently shown (empty for other views).</summary>
    public string OnlineKeyword { get; private set; } = "";

    /// <summary>True when the resolved stream is a short trial clip (VIP track without login).</summary>
    public bool IsTrial { get; private set; }

    public event Action? TrackChanged;
    public event Action? StateChanged;
    public event Action? LyricsChanged;
    public event Action? ViewChanged;
    public event Action<double>? Tick;

    private readonly DispatcherTimer _clock;
    private readonly AudioVolumeKeeper _mixerVolume;

    public PlayerController(AppSettings settings)
    {
        Settings = settings;
        Playback.Volume = settings.Volume;
        Playback.StateChanged += () =>
        {
            StateChanged?.Invoke();
        };
        Playback.Ended += (_, _) => OnTrackEnded();
        _clock = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _clock.Tick += (_, _) => Tick?.Invoke(PositionMs);
        _clock.Start();
        _mixerVolume = new AudioVolumeKeeper(settings);
        foreach (var playlist in PlaylistStore.Load())
        {
            playlist.CoverImage = ResolvePlaylistCover(playlist);
            Playlists.Add(playlist);
        }
        RefreshPlaylistCounts();
    }

    /// <summary>Recomputes how many of each playlist's paths still exist, so the sidebar count and
    /// the list agree (a playlist can hold paths whose file was moved or deleted).</summary>
    public void RefreshPlaylistCounts()
    {
        foreach (var playlist in Playlists)
            playlist.ExistingCount = playlist.Songs.Count(File.Exists);
    }

    /// <summary>Drops the paths whose file is gone. Returns how many entries were removed.</summary>
    public int CleanMissing(Playlist playlist)
    {
        int before = playlist.Songs.Count;
        playlist.Songs.RemoveAll(path => !File.Exists(path));
        int removed = before - playlist.Songs.Count;
        if (removed == 0) return 0;
        if (playlist.CoverImage == null) playlist.CoverImage = ResolvePlaylistCover(playlist);
        PlaylistStore.Save(Playlists);
        RefreshPlaylistCounts();
        PlaylistsChanged?.Invoke();
        RefreshActivePlaylistCover(playlist);
        return removed;
    }

    /// <summary>Offset configured for the playing song (0 when it has none).</summary>
    public long CurrentSongOffset =>
        Current != null && Settings.SongOffsets.TryGetValue(Current.Key, out var value) ? value : 0;

    public double PositionMs => Playback.PositionMs + Settings.OffsetMs + CurrentSongOffset;
    public double RawPositionMs => Playback.PositionMs;
    public bool PlaybackState => Playback.IsPlaying;

    // ---------- account ----------
                    // ---------- local + WSA ----------
    // ---------- playlists ----------
    public Playlist CreatePlaylist(string name)
    {
        var playlist = new Playlist { Name = string.IsNullOrWhiteSpace(name) ? "新建歌单" : name.Trim() };
        Playlists.Add(playlist);
        PlaylistStore.Save(Playlists);
        PlaylistsChanged?.Invoke();
        return playlist;
    }

    public void RenamePlaylist(Playlist playlist, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        playlist.Name = name.Trim();
        PlaylistStore.Save(Playlists);
        PlaylistsChanged?.Invoke();
    }

    public void DeletePlaylist(Playlist playlist)
    {
        Playlists.Remove(playlist);
        PlaylistStore.Save(Playlists);
        PlaylistsChanged?.Invoke();
    }

    public void SetPlaylistCover(Playlist playlist, string imagePath)
    {
        playlist.CoverPath = imagePath ?? "";
        playlist.CoverImage = LoadBitmap(playlist.CoverPath);
        PlaylistStore.Save(Playlists);
        PlaylistsChanged?.Invoke();
        RefreshActivePlaylistCover(playlist);
    }

    /// <summary>Keeps the header cover in sync when the visible playlist's artwork changes.</summary>
    private void RefreshActivePlaylistCover(Playlist playlist)
    {
        if (!ReferenceEquals(_activePlaylist, playlist)) return;
        ViewCoverUrl = null;
        ViewCoverImage = playlist.CoverImage ?? ResolvePlaylistCover(playlist);
        ViewChanged?.Invoke();
    }

    /// <summary>Adds audio files to a playlist (non-audio paths are rejected). A track that is already
    /// there in another format is skipped too: our own downloads end with "[<id>]", so the same song
    /// saved once as flac must not appear again as mp3.</summary>
    public int AddToPlaylist(Playlist playlist, IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !TagReader.IsAudio(path)) continue;
            if (playlist.Songs.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            var tag = IdTag(path);
            if (tag != null && playlist.Songs.Any(existing => IdTag(existing) == tag)) continue;
            playlist.Songs.Add(path);
            added++;
        }
        if (added > 0)
        {
            if (playlist.CoverImage == null) playlist.CoverImage = ResolvePlaylistCover(playlist);
            PlaylistStore.Save(Playlists);
            RefreshPlaylistCounts();
            PlaylistsChanged?.Invoke();
            RefreshActivePlaylistCover(playlist);
        }
        return added;
    }

    public void RemoveFromPlaylist(Playlist playlist, string path)
    {
        if (playlist.Songs.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            PlaylistStore.Save(Playlists);
            RefreshPlaylistCounts();
            PlaylistsChanged?.Invoke();
        }
    }

    /// <summary>The "[<id>]" suffix our downloads carry, or null for files that were imported by hand.</summary>
    private static string? IdTag(string path)
    {
        try
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            int open = name.LastIndexOf('[');
            int close = name.LastIndexOf(']');
            if (open < 0 || close <= open + 1) return null;
            var inner = name[(open + 1)..close];
            return inner.Length > 0 && inner.All(char.IsDigit) ? inner : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the playlist already holds this cloud track in any format — used to skip a
    /// download before it starts.</summary>
    public bool PlaylistHasNetease(Playlist playlist, long neteaseId)
    {
        if (neteaseId <= 0) return false;
        return playlist.Songs.Any(path => IdTag(path) == neteaseId.ToString());
    }

    /// <summary>
    /// 本地已经有这首歌（同一个网易云 id）就用那份，不再下一遍。
    ///
    /// 以前只扫「下载目录」，但用户的歌经常被挪到别处（E:\音乐\好き 这种整理过的文件夹，或者
    /// 手机同步过来的目录），于是明明硬盘上躺着 220 MB 的 flac，还是又下一遍 —— 这就是"加到歌单
    /// 很慢"的真正原因。现在按「歌单里登记过的文件 → 手动加入曲库的文件 → 下载目录 → 曲库目录」
    /// 的顺序找，前三个都是内存里的列表，只有最后一个会真的扫目录。
    /// </summary>
    public string? FindDownloadedPath(long neteaseId)
    {
        if (neteaseId <= 0) return null;
        var id = neteaseId.ToString();
        try
        {
            foreach (var file in KnownAudioFiles())
                if (IdTag(file) == id) return file;

            // 最后才去曲库目录里按文件名找（一次目录遍历，比自己下一遍 200 MB 便宜太多）。
            var library = (Settings.MusicFolder ?? "").Trim();
            if (library.Length > 0 && Directory.Exists(library))
            {
                foreach (var file in Directory.EnumerateFiles(library, "*[" + id + "]*", SearchOption.AllDirectories))
                    if (TagReader.IsAudio(file) && IdTag(file) == id) return file;
            }
        }
        catch { }
        return null;
    }

    /// <summary>程序已经知道的所有音频文件（内存里的清单，不扫盘）。</summary>
    private IEnumerable<string> KnownAudioFiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in Playlists)
            foreach (var path in playlist.Songs)
                if (!string.IsNullOrEmpty(path) && seen.Add(path)) yield return path;
        foreach (var path in Settings.LibraryFiles ?? new List<string>())
            if (!string.IsNullOrEmpty(path) && seen.Add(path)) yield return path;

        var folder = Settings.ResolvedDownloadFolder;
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (!TagReader.IsAudio(path)) continue;
                if (seen.Add(path)) yield return path;
            }
        }
    }

    public BitmapSource? ResolvePlaylistCover(Playlist playlist)
    {
        if (!string.IsNullOrEmpty(playlist.CoverPath))
        {
            var custom = LoadBitmap(playlist.CoverPath);
            if (custom != null) return custom;
        }
        foreach (var path in playlist.Songs)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var song = TagReader.Read(path);
                if (song.Cover != null) return song.Cover;
            }
            catch { }
        }
        return null;
    }

    private static BitmapSource? LoadBitmap(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 256;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    /// <summary>Bumped by every view switch. A slow load that finishes after the user has already
    /// opened something else checks this and throws its result away instead of overwriting the
    /// current view (the startup library scan used to win that race against a playlist click).</summary>
    private int _viewGeneration;

    private int BeginView()
    {
        _viewGeneration++;
        IsOnlineView = false;
        OnlineKeyword = "";
        return _viewGeneration;
    }

    private bool StillWanted(int generation) => generation == _viewGeneration;

    /// <summary>
    /// Online search: the result list behaves like any other view (rows, covers, double-click play);
    /// the only difference is that a row is an id, and the address is resolved when it is played.
    /// </summary>
    public async Task ShowOnlineSearchAsync(string keyword)
    {
        keyword = keyword.Trim();
        if (keyword.Length > 0) LastOnlineKeyword = keyword;
        int generation = BeginView();
        IsOnlineView = true;
        OnlineKeyword = keyword;
        _activePlaylist = null;
        ActivePlaylistName = "";
        Busy = true;
        ViewName = "在线搜索";
        ViewSubtitle = keyword.Length == 0 ? "输入歌名、歌手或专辑后回车搜索" : $"正在搜索「{keyword}」…";
        ViewCoverUrl = null;
        ViewCoverImage = null;
        Notice = null;
        if (keyword.Length == 0)
        {
            Songs.Clear();
            Busy = false;
            ViewChanged?.Invoke();
            return;
        }
        ViewChanged?.Invoke();

        NeteaseApi.Cookie = Settings.NeteaseCookie;
        NeteaseApi.Quality = Settings.NeteaseQuality;
        var page = await Task.Run(() => NeteaseApi.SearchAsync(keyword));
        if (!StillWanted(generation)) return;

        Songs.Clear();
        foreach (var song in page.Songs) AddSong(song);
        Busy = false;
        // Say "shown / total" out loud when the list is a page, otherwise "共 303 首" claims more
        // rows than the list actually has.
        ViewSubtitle = page.Error ?? (page.Total > page.Songs.Count
            ? $"{keyword} · 共 {page.Total} 首，显示前 {page.Songs.Count} 首"
            : $"{keyword} · 共 {page.Total} 首");
        ViewCoverUrl = page.Songs.FirstOrDefault()?.CoverUrl;
        ViewChanged?.Invoke();
    }

    public async Task ShowPlaylistAsync(Playlist playlist)
    {
        int generation = BeginView();
        _activePlaylist = playlist;
        ActivePlaylistName = playlist.Name;
        ViewName = playlist.Name;
        Busy = true;
        ViewSubtitle = "正在读取歌单 …";
        ViewChanged?.Invoke();
        try
        {
            var songs = new List<Song>();
            await Task.Run(() =>
            {
                foreach (var path in playlist.Songs.ToList())
                {
                    try
                    {
                        if (File.Exists(path)) songs.Add(TagReader.Read(path));
                    }
                    catch { }
                }
            });
            if (!StillWanted(generation)) return;
            Songs.Clear();
            foreach (var song in songs) AddSong(song);
            ViewCoverUrl = null;
            ViewCoverImage = playlist.CoverImage ?? ResolvePlaylistCover(playlist);
            RefreshPlaylistCounts();
            int missing = playlist.Songs.Count - songs.Count;
            ViewSubtitle = missing > 0
                ? $"{songs.Count} 首 · {playlist.Name}（{missing} 个文件找不到）"
                : $"{songs.Count} 首 · {playlist.Name}";
            ViewChanged?.Invoke();
        }
        finally
        {
            if (StillWanted(generation)) Busy = false;
        }
    }

    public event Action? PlaylistsChanged;

    // ---------- removing a row ----------
    /// <summary>What a removal touched, so the snackbar can undo it exactly.</summary>
    public sealed record RemovedSong(Song Song, int Index, Playlist? Playlist, bool Hidden);

    /// <summary>True when the visible list belongs to a playlist (removal edits that playlist).</summary>
    public bool InPlaylist => _activePlaylist != null;

    /// <summary>The playlist shown in the list, or null when the view is the library.</summary>
    public Playlist? PlaylistInView => _activePlaylist;

    /// <summary>Where a removal will land, for the confirmation/snackbar wording.</summary>
    public string RemoveScopeText => _activePlaylist != null
        ? "已从歌单移除"
        : Settings.LibraryFiles.Count > 0 ? "已从曲库移除" : "已从列表隐藏";

    /// <summary>
    /// Drops one track from the visible list and from whatever produced it, without touching the
    /// audio file: a playlist loses the entry, a hand-picked library loses the path, a scanned
    /// folder gets the path remembered as hidden so the next scan does not bring it back.
    /// </summary>
    public RemovedSong? RemoveSong(Song song)
    {
        int index = Songs.IndexOf(song);
        if (index < 0) return null;

        bool hidden = false;
        if (_activePlaylist != null)
        {
            _activePlaylist.Songs.RemoveAll(p => string.Equals(p, song.Path, StringComparison.OrdinalIgnoreCase));
            PlaylistStore.Save(Playlists);
            PlaylistsChanged?.Invoke();
            RefreshActivePlaylistCover(_activePlaylist);
        }
        else if (Settings.LibraryFiles.Count > 0)
        {
            Settings.LibraryFiles.RemoveAll(p => string.Equals(p, song.Path, StringComparison.OrdinalIgnoreCase));
            SettingsStore.Save(Settings);
        }
        else
        {
            if (!Settings.HiddenFiles.Contains(song.Path, StringComparer.OrdinalIgnoreCase))
                Settings.HiddenFiles.Add(song.Path);
            SettingsStore.Save(Settings);
            hidden = true;
        }

        song.IsCurrent = false;
        Songs.Remove(song);
        RefreshCounts();
        return new RemovedSong(song, index, _activePlaylist, hidden);
    }

    /// <summary>Puts a removed track back exactly where it was.</summary>
    public void RestoreRemoved(RemovedSong record)
    {
        var song = record.Song;
        if (record.Playlist != null)
        {
            if (!record.Playlist.Songs.Contains(song.Path, StringComparer.OrdinalIgnoreCase))
                record.Playlist.Songs.Insert(Math.Min(record.Index, record.Playlist.Songs.Count), song.Path);
            PlaylistStore.Save(Playlists);
            PlaylistsChanged?.Invoke();
        }
        else if (record.Hidden)
        {
            Settings.HiddenFiles.RemoveAll(p => string.Equals(p, song.Path, StringComparison.OrdinalIgnoreCase));
            SettingsStore.Save(Settings);
        }
        else
        {
            if (!Settings.LibraryFiles.Contains(song.Path, StringComparer.OrdinalIgnoreCase))
                Settings.LibraryFiles.Insert(Math.Min(record.Index, Settings.LibraryFiles.Count), song.Path);
            SettingsStore.Save(Settings);
        }

        if (!Songs.Contains(song)) Songs.Insert(Math.Min(record.Index, Songs.Count), song);
        RefreshCounts();
    }

    /// <summary>Number of folder-scan tracks the user removed (0 when nothing is hidden).</summary>
    public int HiddenCount => Settings.HiddenFiles.Count;

    /// <summary>Brings every hidden track back into the current folder-scan view.</summary>
    public async Task<int> RestoreHiddenAsync()
    {
        if (Settings.HiddenFiles.Count == 0) return 0;
        int count = Settings.HiddenFiles.Count;
        Settings.HiddenFiles.Clear();
        SettingsStore.Save(Settings);
        if (string.IsNullOrWhiteSpace(Settings.MusicFolder)) return count;
        await ShowLocalAsync(Settings.MusicFolder);
        return count;
    }

    private void RefreshCounts()
    {
        ViewSubtitle = _activePlaylist != null
            ? $"{Songs.Count} 首 · {_activePlaylist.Name}"
            : $"{Songs.Count} 首 · 本地音乐";
        ViewChanged?.Invoke();
    }

    /// <summary>Shows the current library: hand-picked files first, otherwise the scanned folder.</summary>
    public async Task ShowLibraryAsync()
    {
        var files = Settings.LibraryFiles.Where(File.Exists).ToList();
        if (files.Count > 0)
        {
            await ShowFilesAsync(files, keepList: false);
            return;
        }
        if (!string.IsNullOrWhiteSpace(Settings.MusicFolder))
            await ShowLocalAsync(Settings.MusicFolder);
    }

    /// <summary>Adds audio files to the library list and displays the whole list.</summary>
    public async Task AddLibraryFilesAsync(IEnumerable<string> paths, bool replace = false)
    {
        var audio = paths.Where(p => File.Exists(p) && TagReader.IsAudio(p)).ToList();
        if (audio.Count == 0) return;
        if (replace) Settings.LibraryFiles.Clear();
        foreach (var path in audio)
        {
            if (!Settings.LibraryFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
                Settings.LibraryFiles.Add(path);
        }
        SettingsStore.Save(Settings);
        await ShowFilesAsync(Settings.LibraryFiles.Where(File.Exists).ToList(), keepList: true);
    }

    private async Task ShowFilesAsync(IReadOnlyList<string> files, bool keepList)
    {
        int generation = BeginView();
        _activePlaylist = null;
        ActivePlaylistName = "";
        Busy = true;
        ViewName = "本地音乐";
        ViewSubtitle = $"正在读取 {files.Count} 个文件 …";
        ViewChanged?.Invoke();
        try
        {
            var songs = new List<Song>(files.Count);
            await Task.Run(() =>
            {
                foreach (var path in files)
                {
                    try { songs.Add(TagReader.Read(path)); } catch { }
                }
            });
            if (!StillWanted(generation)) return;
            ViewCoverUrl = null;
            ViewCoverImage = songs.FirstOrDefault(s => s.Cover != null)?.Cover;
            Songs.Clear();
            foreach (var song in songs) AddSong(song);
            ViewSubtitle = $"{songs.Count} 首 · {(keepList ? "已添加的文件" : "本地音乐")}";
            ViewChanged?.Invoke();
        }
        finally
        {
            if (StillWanted(generation)) Busy = false;
        }
    }

    public async Task ShowLocalAsync(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !System.IO.Directory.Exists(folder)) return;
        int generation = BeginView();
        _activePlaylist = null;
        ActivePlaylistName = "";
        Busy = true;
        ViewSubtitle = "正在扫描本地文件夹 …";
        ViewChanged?.Invoke();
        try
        {
            Settings.MusicFolder = folder;
            SettingsStore.Save(Settings);
            var count = await LocalLibrary.ScanAsync(folder, Settings.HiddenFiles);
            if (!StillWanted(generation)) return;
            ViewName = "本地音乐";
            ViewSubtitle = Settings.HiddenFiles.Count > 0
                ? $"{count} 首 · {folder}（已隐藏 {Settings.HiddenFiles.Count} 首）"
                : $"{count} 首 · {folder}";
            ViewCoverUrl = null;
            ViewCoverImage = LocalLibrary.Songs.FirstOrDefault(s => s.Cover != null)?.Cover;
            Songs.Clear();
            foreach (var song in LocalLibrary.Songs) AddSong(song);
            ViewChanged?.Invoke();
        }
        finally
        {
            if (StillWanted(generation)) Busy = false;
        }
    }

    public async Task ShowWsaQueueAsync()
    {
        int generation = BeginView();
        Busy = true;
        ViewSubtitle = "正在读取流声音乐的播放队列 …";
        ViewChanged?.Invoke();
        try
        {
            var adb = WsaBridge.FindAdb(Settings.AdbPath);
            if (adb == null)
            {
                ViewName = "流声音乐 · 当前队列";
                ViewSubtitle = "没有找到 adb，可在设置里指定 platform-tools\\adb.exe";
                Songs.Clear();
                ViewChanged?.Invoke();
                return;
            }
            Settings.AdbPath = adb;
            var (songs, error) = await WsaBridge.ReadQueueAsync(adb, Settings.WsaSerial);
            if (!StillWanted(generation)) return;
            ViewName = "流声音乐 · 当前队列";
            ViewSubtitle = error ?? $"{songs.Count} 首 · 直接从 WSA 里的流声音乐读取";
            ViewCoverUrl = songs.FirstOrDefault()?.CoverUrl;
            ViewCoverImage = null;
            Songs.Clear();
            foreach (var song in songs) AddSong(song);
            ViewChanged?.Invoke();
        }
        finally
        {
            if (StillWanted(generation)) Busy = false;
        }
    }

    private void AddSong(Song song)
    {
        // Rebuilding a view creates fresh Song objects; keep the playing row highlighted.
        if (Current != null && string.Equals(Current.Path, song.Path, StringComparison.OrdinalIgnoreCase))
            song.IsCurrent = true;
        Songs.Add(song);
        RequestCover(song);
    }

    /// <summary>Cloud rows carry only a URL: the artwork is downloaded in the background and lands a
    /// moment later, so everything that painted "no cover" has to repaint when it arrives.</summary>
    public event Action<Song>? CoverChanged;

    /// <summary>Starts the one-off download for a song's remote cover (no-op when it already has one).</summary>
    public void RequestCover(Song song)
    {
        if (song.Cover != null || song.CoverUrl == null || song.CoverRequested) return;
        song.CoverRequested = true;
        _ = LoadCoverAsync(song);
    }

    private async Task LoadCoverAsync(Song song)
    {
        // 900 px, not the 300 px the list rows need: this same bitmap is what the desktop lyrics
        // backdrop and the player page blow up over the whole window, and a 300 px cover stretched to
        // a 700 px tall backdrop is what made it look like a thumbnail (user report: 背景图分辨率过低).
        var cover = await ImageCache.LoadAsync(song.CoverUrl, 900);
        if (cover == null) return;
        song.Cover = cover;
        CoverChanged?.Invoke(song);
    }

    // ---------- playback ----------
    // ---------- 上次听到哪（只认本地文件，在线曲目不记） ----------
    /// <summary>退出前记一笔：当前歌 + 进度 + 所在歌单。在线曲目不记（下次也没法直接放）。</summary>
    public void CaptureResume()
    {
        var song = Current;
        if (song == null || song.Source != SongSource.Local || string.IsNullOrWhiteSpace(song.Path))
        {
            Settings.ResumeSongPath = "";
            Settings.ResumePositionMs = 0;
            Settings.ResumePlaylist = "";
            return;
        }
        double ms = Playback.PositionMs;
        // 停在结尾几秒的算“听完了”，下次从这首歌开头开始，免得一进来就卡在末尾。
        if (song.Duration.TotalMilliseconds > 5000 && ms > song.Duration.TotalMilliseconds - 3000) ms = 0;
        Settings.ResumeSongPath = song.Path;
        Settings.ResumePositionMs = Math.Max(0, ms);
        Settings.ResumePlaylist = ActivePlaylistName ?? "";
    }

    /// <summary>上次那个歌单名（空 = 本地音乐）。</summary>
    public string ActivePlaylistName { get; private set; } = "";

    /// <summary>
    /// 「上次听到一半」的恢复：把歌放进 Current（标题/封面/歌词都跟着走），把媒体装载但不播放，
    /// 并把进度条推到上次的位置。整个流程不弹任何窗口。
    /// </summary>
    public void PrepareResume(Song song, double positionMs)
    {
        _playCts?.Cancel();
        Current = song;
        foreach (var item in Songs) item.IsCurrent = ReferenceEquals(item, song);
        RequestCover(song);
        Lyrics = null;
        IsTrial = false;
        Notice = null;
        TrackChanged?.Invoke();
        if (song.Source == SongSource.Local && !string.IsNullOrWhiteSpace(song.Path) && System.IO.File.Exists(song.Path))
        {
            Playback.Open(song, null, autoplay: false);   // 装载，不出声
            Playback.SeekTo(positionMs);
        }
        _ = LoadLyricsAsync(song);
        StateChanged?.Invoke();
    }

    /// <summary>Preview/QA: show a lyric document without a media source.</summary>
    public void LoadDemo(LyricDocument document, Song song)
    {
        Current = song;
        Lyrics = document;
        TrackChanged?.Invoke();
        LyricsChanged?.Invoke();
    }

    private CancellationTokenSource? _playCts;

    public async Task PlayAsync(Song song)
    {
        // A new pick must silence whatever was playing: otherwise the previous track keeps
        // sounding for as long as the new one takes to download.
        _playCts?.Cancel();
        var cts = new CancellationTokenSource();
        _playCts = cts;

        if (Current != null && !ReferenceEquals(Current, song)) Playback.Stop();
        Current = song;
        foreach (var item in Songs) item.IsCurrent = ReferenceEquals(item, song);
        // A row played from a list that was never rendered (or a restored queue) may not have asked
        // for its artwork yet; without this the window backdrop stayed a solid colour.
        RequestCover(song);
        Lyrics = null;
        IsTrial = false;
        Notice = null;
        TrackChanged?.Invoke();

        string? source = song.Path;
        if (song.Source == SongSource.Netease)
        {
            // Cloud rows carry an id, not an address: resolve one (cached until its own expiry) on the
            // thread pool so the UI keeps painting while the request is in flight.
            NeteaseApi.Cookie = Settings.NeteaseCookie;
            NeteaseApi.Quality = Settings.NeteaseQuality;
            try
            {
                var url = await ResolveStreamAsync(song, cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(Current, song)) return;
                if (url == null)
                {
                    Notice = _lastStreamError ?? NeteaseApi.LastError ?? "这首曲子当前无法播放";
                    StateChanged?.Invoke();
                    return;
                }
                source = url;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (cts.IsCancellationRequested || !ReferenceEquals(Current, song)) return;
        Playback.Open(song, source);
        _ = LoadLyricsAsync(song);
    }

    /// <summary>Resolves (and caches) a usable stream URL for a cloud row.</summary>
    private string? _lastStreamError;

    private async Task<string?> ResolveStreamAsync(Song song, CancellationToken token)
    {
        if (!string.IsNullOrEmpty(song.StreamUrl) && song.StreamExpiresAt > DateTime.UtcNow)
            return song.StreamUrl;

        var result = await Task.Run(() => NeteaseApi.ResolveAsync(song.NeteaseId, Settings.NeteaseQuality, token), token);
        if (result.Url == null)
        {
            // The platform refused this track (VIP / no licence). The desktop app solves that by asking
            // a 落雪 script for a URL instead, which is exactly what an imported script is for.
            var lx = await TryResolveWithLxAsync(song, token);
            if (lx != null)
            {
                song.StreamUrl = lx;
                song.StreamLevel = "lx";
                song.StreamExpiresAt = DateTime.UtcNow.AddMinutes(10);
                _lastStreamError = null;
                return lx;
            }
            song.StreamUrl = null;
            _lastStreamError = result.Error;
            return null;
        }
        _lastStreamError = null;
        song.StreamUrl = result.Url;
        song.StreamLevel = result.Level;
        song.StreamExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(30, result.ExpiresSeconds - 20));
        return result.Url;
    }

    /// <summary>Saves a cloud row as a normal local file (audio + cover + lyrics) and registers it with
    /// the library, so it keeps playing offline like any scanned track.</summary>
    public async Task<bool> DownloadSongAsync(Song song)
    {
        if (song.Source != SongSource.Netease || song.NeteaseId <= 0)
        {
            Notice = "只有在线曲目可以下载到本地";
            StateChanged?.Invoke();
            return false;
        }

        // Downloads go to their own folder (设置 → 下载与存储), not into the scanned music folder:
        // mixing them in with the user's own files was confusing and re-scanned the library.
        var folder = Settings.ResolvedDownloadFolder;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception e)
        {
            Notice = "下载目录不可用：" + folder + "（" + e.Message + "）";
            StateChanged?.Invoke();
            return false;
        }
        NeteaseApi.Cookie = Settings.NeteaseCookie;
        NeteaseApi.Quality = Settings.NeteaseQuality;
        Notice = null;
        Preparing = $"正在下载「{song.DisplayTitle}」…";
        StateChanged?.Invoke();

        // Same resolution path as playback, so a track that only a 落雪 script can serve still
        // downloads instead of failing here with the platform's refusal.
        string? url = null;
        try
        {
            url = await ResolveStreamAsync(song, CancellationToken.None);
        }
        catch (Exception e)
        {
            Preparing = null;
            Notice = "下载失败：" + e.Message;
            StateChanged?.Invoke();
            return false;
        }
        if (url == null)
        {
            Preparing = null;
            Notice = _lastStreamError ?? "拿不到下载地址";
            StateChanged?.Invoke();
            return false;
        }

        var result = await NeteaseDownload.SaveAsync(song, folder, Settings.NeteaseQuality, urlOverride: url);
        Preparing = null;
        if (!result.Ok || result.Path == null)
        {
            Notice = result.Error ?? "下载失败";
            StateChanged?.Invoke();
            return false;
        }

        if (Settings.AddDownloadedToLibrary) AddToLibrarySilently(result.Path);
        LastDownloadPath = result.Path;
        Notice = "已下载到 " + folder + "：" + Path.GetFileName(result.Path);
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Where the last successful download landed (the playlist path uses this to add the
    /// freshly saved file of a cloud track).</summary>
    public string? LastDownloadPath { get; private set; }

    /// <summary>另存到…: resolves a cloud track and writes it into <paramref name="folder"/>, which is
    /// whatever folder the user picked — unrelated to the configured download folder.</summary>
    public async Task<(bool Ok, string? Error, string? Saved)> SaveSongToAsync(Song song, string folder)
    {
        if (song.Source != SongSource.Netease || song.NeteaseId <= 0)
            return (false, "只有在线曲目需要这样另存", null);
        NeteaseApi.Cookie = Settings.NeteaseCookie;
        NeteaseApi.Quality = Settings.NeteaseQuality;
        string? url;
        try
        {
            url = await ResolveStreamAsync(song, CancellationToken.None);
        }
        catch (Exception e)
        {
            return (false, e.Message, null);
        }
        if (url == null) return (false, _lastStreamError ?? "拿不到下载地址", null);
        var result = await NeteaseDownload.SaveAsync(song, folder, Settings.NeteaseQuality, urlOverride: url);
        if (!result.Ok || result.Path == null) return (false, result.Error ?? "下载失败", null);
        if (Settings.AddDownloadedToLibrary) AddToLibrarySilently(result.Path);
        LastDownloadPath = result.Path;
        return (true, null, result.Path);
    }

    private void AddToLibrarySilently(string path)
    {
        if (Settings.LibraryFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        Settings.LibraryFiles.Add(path);
        SettingsStore.Save(Settings);
    }

    /// <summary>Name of the 落雪 script that supplied the last fallback URL (empty when unused).</summary>
    public string LastLxSource { get; private set; } = "";

    public string? LastLxError { get; private set; }

    private async Task<string?> TryResolveWithLxAsync(Song song, CancellationToken token)
    {
        LxStore.Load();
        LastLxError = null;
        var candidates = LxStore.SmartSwitch
            ? LxStore.Entries.Where(e => e.Error == null).ToList()
            : LxStore.Entries.Where(e => e.Id == LxStore.ActiveId && e.Error == null).ToList();
        if (candidates.Count == 0) return null;

        foreach (var entry in candidates)
        {
            if (token.IsCancellationRequested) return null;
            try
            {
                var url = await Task.Run(() => ResolveWithScript(entry, song), token);
                if (string.IsNullOrWhiteSpace(url)) continue;
                return url;
            }
            catch (Exception e)
            {
                LastLxError = entry.Name + "：" + e.Message;
            }
        }
        return null;
    }

    /// <summary>
    /// One script can usually serve several platforms (wy / tx / kg / kw / mg). The old code only ever
    /// asked for <c>wy</c>, so a script whose NetEase path was down looked like a dead script even
    /// though its QQ/Kugou paths worked. Now the platforms are tried in turn, and every URL the script
    /// hands back is probed first — a script happily invents an address for a track it cannot serve,
    /// and the player then fails silently. Only NetEase ids are known here, so every other platform is
    /// asked by name/artist (an empty <c>songmid</c>) instead of having another platform's id forced on it.
    /// </summary>
    private string? ResolveWithScript(LxEntry entry, Song song)
    {
        var file = Path.Combine(AppPaths.DataDir, "lx", entry.File);
        if (!File.Exists(file)) throw new LxException("脚本文件缺失");
        using var runtime = new LxRuntime();
        runtime.Load(entry.Id, File.ReadAllText(file));

        var seconds = (int)Math.Round(song.Duration.TotalSeconds);
        Exception? last = null;
        foreach (var platform in PlatformOrder(entry))
        {
            var media = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "320k",
                ["musicInfo"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["source"] = platform,
                    ["songmid"] = platform == "wy" ? song.NeteaseId.ToString() : "",
                    ["name"] = song.DisplayTitle,
                    ["singer"] = song.DisplayArtist,
                    ["albumName"] = song.Album,
                    ["interval"] = $"{seconds / 60:00}:{seconds % 60:00}",
                },
            };
            try
            {
                var audio = runtime.Resolve(platform, Settings.NeteaseQuality, media,
                    entry.QualitiesFor(platform));
                if (string.IsNullOrWhiteSpace(audio.Url)) continue;
                if (!LooksPlayable(audio.Url))
                {
                    last = new LxException(platform + " 返回的地址打不开");
                    continue;
                }
                LastLxSource = entry.Name + " · " + platform.ToUpperInvariant() + " " + audio.Quality;
                return audio.Url;
            }
            catch (Exception e)
            {
                last = e;
            }
        }
        if (last != null) throw last;
        return null;
    }

    /// <summary>NetEase first (its id is the only one we have), then the platforms that search by name.</summary>
    private static IEnumerable<string> PlatformOrder(LxEntry entry)
    {
        var order = new[] { "wy", "tx", "kg", "kw", "mg" };
        var present = order.Where(p => entry.Platforms.ContainsKey(p)).ToList();
        foreach (var key in entry.Platforms.Keys)
            if (!present.Contains(key)) present.Add(key);
        return present;
    }

    private static readonly HttpClient ProbeHttp = CreateProbeHttp();

    private static HttpClient CreateProbeHttp()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(6),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        return client;
    }

    /// <summary>
    /// Asks for the first two bytes. A refusal (404/403) proves the address is dead and lets the next
    /// platform be tried; a transport failure is treated as "unknown" and allowed through, because some
    /// hosts reject Range requests yet stream fine to the player.
    /// </summary>
    private static bool LooksPlayable(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1);
            using var response = ProbeHttp.Send(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return false;
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return true;
            if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return true;
            if (type.Contains("octet-stream", StringComparison.OrdinalIgnoreCase)) return true;
            return (response.Content.Headers.ContentLength ?? 0) > 1024;
        }
        catch
        {
            return true;
        }
    }

    private async Task LoadLyricsAsync(Song song)
    {
        await Task.Yield();
        // Local text first: a sidecar (or embedded tag) is the user's own file and may be hand-edited.
        LyricDocument? local = File.Exists(song.Path) ? LrcParser.LoadFor(song.Path) : null;
        LyricDocument? doc = local;
        // Cloud rows always go online. Files that were DOWNLOADED from the cloud are plain local rows
        // with no id, so the id is recovered from the file name this app writes ("… [123456789].mp3") —
        // otherwise an old download keeps its line-only sidecar and the karaoke stays an even spread.
        long cloudId = song.NeteaseId > 0 ? song.NeteaseId : GuessNeteaseIdFromPath(song.Path);
        if (cloudId > 0 && !LrcParser.HasWordTiming(local))
        {
            var online = await LoadOnlineLyricsAsync(song, cloudId).ConfigureAwait(false);
            // The id may have been guessed from the file name, so never let a wrong guess replace real
            // lyrics with another song's text: only take the online document when the visible text
            // agrees, or when there is no local text to lose.
            if (online != null && (local == null || LrcParser.HasWordTiming(online) && SameSong(local, online)))
                doc = online;
        }
        bool fromLocal = doc != null && ReferenceEquals(doc, local);
        if (doc != null)
            doc = LrcParser.WithOpeningCard(doc, song.DisplayTitle, song.DisplayArtist, song.Album);
        if (!ReferenceEquals(Current, song)) return;
        Lyrics = doc;
        LyricsChanged?.Invoke();
        // 落一份"最终用了哪份歌词"的日志：侧车/网易云/换源/本地对齐 四种来源一眼可分。
        LogApplied(song, cloudId, doc, fromLocal ? "local" : "online");
    }

    private static void LogApplied(Song song, long cloudId, LyricDocument? doc, string source)
    {
        try
        {
            int real = doc?.Lines.Count(l => l.HasWordTiming) ?? 0;
            int estimated = doc?.Lines.Count(l => l.WordsEstimated) ?? 0;
            var path = Path.Combine(AppPaths.DataDir, "lyrics.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                $"{DateTime.Now:MM-dd HH:mm:ss} applied id={cloudId} '{song.DisplayTitle}' src={source} " +
                $"lines={doc?.Lines.Count ?? 0} real={real} est={estimated}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Why a word-level upgrade or a local-align result never reached the screen. These paths
    /// used to end in a bare <c>catch { }</c>, which is why "在线歌没有逐字" could not be diagnosed
    /// from the log at all.</summary>
    private static void LogFailure(long cloudId, Song song, string stage, Exception error)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDir, "lyrics.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                $"{DateTime.Now:MM-dd HH:mm:ss} {stage}-FAILED id={cloudId} '{song.DisplayTitle}' " +
                $"{error.GetType().Name}: {error.Message}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>NeteaseDownload names saved tracks `"title - artist [id].ext"`; recover that id so a
    /// downloaded track can still ask for the word-level lyric it was downloaded without.</summary>
    /// <summary>Cheap identity check between a local sidecar and a fetched document. Compares whole
    /// lines rather than a prefix: the second lyric source may start with a different title card, so a
    /// prefix test would throw away perfectly good word timing.</summary>
    internal static bool SameSong(LyricDocument? local, LyricDocument? online)
    {
        if (local == null || online == null) return true;
        var a = LongLines(local);
        var b = LongLines(online);
        if (a.Count == 0 || b.Count == 0) return true;
        int shared = a.Count(b.Contains);
        int needed = Math.Max(1, Math.Min(a.Count, b.Count) / 5);
        return shared >= needed;
    }

    private static HashSet<string> LongLines(LyricDocument doc)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in doc.Lines)
        {
            var text = new string(line.Text.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c)).ToArray());
            if (text.Length >= 4) set.Add(text);
        }
        return set;
    }

    /// <summary>QA hook: run the lyric chain for one cloud row exactly as playback would, off the UI
    /// thread (the chain awaits, so calling it on the dispatcher would deadlock). The word-level
    /// upgrade runs in the background in normal use, so the hook waits for it to keep QA deterministic.</summary>
    internal LyricDocument? DebugLoadLyrics(Song song, bool waitForWordLevel = true)
    {
        _waitForWordLevel = waitForWordLevel;
        try
        {
            Current = song;
            LoadLyricsAsync(song).GetAwaiter().GetResult();
            return Lyrics;
        }
        finally
        {
            _waitForWordLevel = false;
        }
    }

    private bool _waitForWordLevel;

    internal static long GuessNeteaseIdFromPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        // 老版本把重名序号写在 id 后面（"… [123] (2).mp3"），新版本写在前面：两种都要认。
        var match = System.Text.RegularExpressions.Regex.Match(stem, @"\[(\d{5,})\](?:\s*\(\d+\))?$");
        if (!match.Success) return 0;
        return long.TryParse(match.Groups[1].Value, out var id) ? id : 0;
    }

    /// <summary>
    /// Fetches a cloud track's lyrics once, then keeps them next to the app's data so replays
    /// and downloads reuse the same text instead of asking the network again. `<id>.v2` marks a cache
    /// written by the word-level-aware request; caches without it are re-fetched once, so songs
    /// downloaded before that change stop sweeping at a constant rate.
    ///
    /// 网易云只有一部分歌带 <c>yrc</c>（词级），其余以前只能整句均分。现在当它没给词级时间时，
    /// 依次问 AMLL / 酷狗 / 腾讯 / LRCLIB 四家要一份真正的逐字时间；拿到就写进缓存，下次播放直接读盘。
    /// </summary>
    private async Task<LyricDocument?> LoadOnlineLyricsAsync(Song song, long cloudId)
    {
        var file = Path.Combine(AppPaths.DataDir, "lyrics", cloudId + ".lrc");
        var marker = Path.Combine(AppPaths.DataDir, "lyrics", cloudId + ".v2");
        // 第二源「已经问过」的记号：没有它的歌才会去联网，避免每次播放都多一次搜索。
        var secondSource = Path.Combine(AppPaths.DataDir, "lyrics", cloudId + ".qsb");
        LyricDocument? cached = TryReadLyric(file);

        // 第二源的词级缓存最优先：命中连网易云都不用问。AMLL（按 id 取，带译文/罗马音）先于 QRC。
        var wordLevel = AmllLyrics.TryCached(cloudId, cached)
                        ?? KugouLyric.TryCached(cloudId, cached)
                        ?? QqLyric.TryCached(cloudId, cached)
                        ?? LrclibLyric.TryCached(cloudId, cached)
                        ?? LocalAlign.TryCached(cloudId, cached);
        if (wordLevel != null) return wordLevel;
        if (cached is { Lines.Count: > 0 })
        {
            if (cached.HasWordTiming) return cached;
            // 第二源"已经问过"只代表不再联网。本地对齐不是网络源，模型可能是后来才装上的，
            // 所以这里还要试一次，否则装模型之前放过的歌永远不会被对齐。
            if (IsFresh(secondSource, 7))
            {
                ScheduleLocalAlign(cloudId, song, cached);
                return cached;
            }
        }

        string? text = null;
        try { text = await Task.Run(() => NeteaseApi.LyricAsync(cloudId)).ConfigureAwait(false); }
        catch { }
        if (string.IsNullOrWhiteSpace(text))
        {
            // Keep whatever cache we already had instead of losing the lyrics to a network hiccup.
            if (cached is { Lines.Count: > 0 }) return cached;
            // 网易云连文字都没有时，LRCLIB 的行级至少能给一份能滚动的歌词。
            var plain = await LrclibLyric
                .EnsurePlainAsync(cloudId, song.DisplayTitle, song.DisplayArtist,
                    (int)song.Duration.TotalSeconds)
                .ConfigureAwait(false);
            if (plain != null) return plain;
            return null;
        }

        var doc = LrcParser.Parse(text);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
            File.WriteAllText(marker, "word-level request v2");
        }
        catch { }

        if (!LrcParser.HasWordTiming(doc))
        {
            int seconds = (int)song.Duration.TotalSeconds;
            if (_waitForWordLevel)
            {
                var tried = new List<string>();
                var probe = System.Diagnostics.Stopwatch.StartNew();
                var inline = await FetchWordLevelAsync(cloudId, song, doc, seconds, tried).ConfigureAwait(false);
                LogLyric(song, cloudId, inline, tried, probe.ElapsedMilliseconds);
                if (inline != null) return inline;
                // 只有四家全失败才写"问过了"。以前在问之前就写：换源成功但缓存没落盘（写盘失败、
                // 被杀软挡住、两个实例抢文件）时，下次播放会因为标记新鲜直接退回均分，再也不问源 ——
                // 一首本来有逐字的歌就此永久钉在均分上。
                Touch(secondSource);
                ScheduleLocalAlign(cloudId, song, doc);
            }
            else
            {
                // Show the NetEase text now, swap in the word-level one when it arrives: waiting for
                // the network here would delay the lyrics by a second or two on every first play.
                _ = UpgradeToWordLevelAsync(cloudId, song, doc, seconds);
            }
        }
        return doc;
    }

    private async Task UpgradeToWordLevelAsync(long cloudId, Song song, LyricDocument baseDoc, int seconds)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var tried = new List<string>();
        try
        {
            var doc = await FetchWordLevelAsync(cloudId, song, baseDoc, seconds, tried).ConfigureAwait(false);
            LogLyric(song, cloudId, doc, tried, watch.ElapsedMilliseconds);
            if (doc == null)
            {
                // 四个源都没有 → 才是 D 方案允许调度的时刻，也才是记"问过了"的时刻。
                Touch(Path.Combine(AppPaths.DataDir, "lyrics", cloudId + ".qsb"));
                ScheduleLocalAlign(cloudId, song, baseDoc);
                return;
            }
            if (!ReferenceEquals(Current, song))
            {
                LogApplied(song, cloudId, null, "upgrade-skipped-track-changed");
                return;
            }
            // 这条路径是"先显示均分、几秒后热替换成逐字"，替换有没有落到界面上以前完全没有记录。
            var upgraded = LrcParser.WithOpeningCard(doc, song.DisplayTitle, song.DisplayArtist, song.Album);
            Lyrics = upgraded;
            LyricsChanged?.Invoke();
            LogApplied(song, cloudId, upgraded, "upgrade");
        }
        catch (Exception e)
        {
            LogFailure(cloudId, song, "upgrade", e);
        }
    }

    /// <summary>
    /// 三个源都没给词级时间时，才允许排一次本地 GPU 对齐。跑完的结果和换源成功一样热替换；
    /// 没模型、没 GPU、算不动就什么都不发生（界面保持均分 + 收短）。
    /// </summary>
    private void ScheduleLocalAlign(long cloudId, Song song, LyricDocument baseDoc)
    {
        if (!LocalAlign.Available) return;
        LocalAlign.Schedule(cloudId, song, baseDoc, Settings.NeteaseQuality, aligned =>
        {
            try
            {
                if (!ReferenceEquals(Current, song)) return;
                Lyrics = LrcParser.WithOpeningCard(aligned, song.DisplayTitle, song.DisplayArtist, song.Album);
                LyricsChanged?.Invoke();
                LogApplied(song, cloudId, aligned, "align");
            }
            catch (Exception e)
            {
                LogFailure(cloudId, song, "align-apply", e);
            }
        });
    }

    /// <summary>
    /// 逐字来源依次尝试：AMLL（按 id 取，带译文/罗马音）→ 酷狗 KRC（按 hash 对，最不容易串版本）→
    /// 腾讯 QRC（按歌名搜）→ LRCLIB（海外曲库，词级时间）。一个来源没有、或者拿到的候选过不了闸，
    /// 就继续下一个；全都不行返回 null，界面上仍是网易云那份文字 + 收短后的均分动画。
    /// </summary>
    private async Task<LyricDocument?> FetchWordLevelAsync(long cloudId, Song song, LyricDocument baseDoc,
        int seconds, List<string>? tried = null)
    {
        tried ??= new List<string>();

        LyricDocument? doc = null;
        try { doc = await AmllLyrics.EnsureAsync(cloudId, baseDoc, TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch { }
        tried.Add(doc != null ? "amll+" : "amll-");

        if (doc == null)
        {
            try
            {
                doc = await KugouLyric
                    .EnsureAsync(cloudId, song.DisplayTitle, song.DisplayArtist, seconds, baseDoc,
                        TimeSpan.FromSeconds(15))
                    .ConfigureAwait(false);
            }
            catch { }
            tried.Add(doc != null ? "kugou+" : "kugou-");
        }

        if (doc == null)
        {
            try
            {
                doc = await QqLyric
                    .EnsureAsync(cloudId, song.DisplayTitle, song.DisplayArtist, baseDoc, seconds,
                        TimeSpan.FromSeconds(20))
                    .ConfigureAwait(false);
            }
            catch { }
            tried.Add(doc != null ? "qq+" : "qq-");
        }

        if (doc == null)
        {
            try
            {
                doc = await LrclibLyric
                    .EnsureAsync(cloudId, song.DisplayTitle, song.DisplayArtist, seconds, baseDoc,
                        TimeSpan.FromSeconds(14))
                    .ConfigureAwait(false);
            }
            catch { }
            tried.Add(doc != null ? "lrclib+" : "lrclib-");
        }
        return doc;
    }

    /// <summary>What the second-source chain actually did, one line per song: which sources were tried
    /// (<c>+</c> hit / <c>-</c> miss), what the winner produced, and how long it took.</summary>
    private static void LogLyric(Song song, long cloudId, LyricDocument? doc, List<string> tried, long elapsedMs)
    {
        try
        {
            var line = doc == null
                ? $"{DateTime.Now:MM-dd HH:mm:ss} id={cloudId} '{song.DisplayTitle}' {string.Join(" ", tried)} " +
                  $"-> none ({elapsedMs}ms)"
                : $"{DateTime.Now:MM-dd HH:mm:ss} id={cloudId} '{song.DisplayTitle}' {string.Join(" ", tried)} " +
                  $"-> lines={doc.Lines.Count} words={doc.Lines.Count(l => l.Words.Count > 0)} " +
                  $"tr={doc.Lines.Count(l => l.Translation.Length > 0)} ({elapsedMs}ms)";
            var path = Path.Combine(AppPaths.DataDir, "lyrics.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch { }
    }

    private static LyricDocument? TryReadLyric(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var doc = LrcParser.Parse(File.ReadAllText(path));
            return doc.Lines.Count > 0 ? doc : null;
        }
        catch { return null; }
    }

    private static void Touch(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 版本前缀：旧版本写的记号（只有一行时间戳）不再被信任，所以升级后会重新联网试一次。
            File.WriteAllText(path, MarkerVersion + " " + DateTime.UtcNow.ToString("o"));
        }
        catch { }
    }

    // v3: LRCLIB 成为第四个逐字源，旧标记（v2）全部作废，让已经播过的歌重新问一次四源。
    private const string MarkerVersion = "v3";

    private static bool IsFresh(string path, double days)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (!File.ReadAllText(path).StartsWith(MarkerVersion, StringComparison.Ordinal)) return false;
            return (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalDays < days;
        }
        catch { return false; }
    }

        public PlayMode Mode => Settings.PlayMode;

    public void SetMode(PlayMode mode)
    {
        Settings.PlayMode = mode;
        SettingsStore.Save(Settings);
        ModeChanged?.Invoke();
    }

    public void ToggleMode() => SetMode(Mode == PlayMode.RepeatOne ? PlayMode.Sequential : PlayMode.RepeatOne);

    /// <summary>Repeat-one restarts the same track, otherwise the queue advances.</summary>
    private void OnTrackEnded()
    {
        if (Mode == PlayMode.RepeatOne && Current != null)
        {
            Playback.Seek(0);
            Playback.Play();
            return;
        }
        Next();
    }

    /// <summary>Reorders the visible list and writes the new order back to its source.</summary>
    public void MoveSong(int from, int to)
    {
        if (from < 0 || to < 0 || from >= Songs.Count || to >= Songs.Count || from == to) return;
        Songs.Move(from, to);
        var order = Songs.Select(s => s.Path).ToList();
        if (_activePlaylist != null)
        {
            _activePlaylist.Songs = order;
            PlaylistStore.Save(Playlists);
        }
        else
        {
            Settings.LibraryFiles = order;
            SettingsStore.Save(Settings);
        }
    }

    /// <summary>
    /// Replaces the queue order with the given sequence (used after a drag) and persists it. The
    /// view is the source of truth: writing it back wholesale avoids off-by-one index juggling.
    /// </summary>
    public void SetOrder(IEnumerable<Song> ordered)
    {
        var list = ordered.ToList();
        if (list.Count != Songs.Count) return;
        for (int i = 0; i < list.Count; i++)
        {
            int current = Songs.IndexOf(list[i]);
            if (current < 0) return;
            if (current != i) Songs.Move(current, i);
        }
        PersistOrder();
    }

    /// <summary>Writes the visible order back to whichever source produced the list.</summary>
    public void PersistOrder()
    {
        var order = Songs.Select(s => s.Path).ToList();
        if (_activePlaylist != null)
        {
            _activePlaylist.Songs = order;
            PlaylistStore.Save(Playlists);
        }
        else if (order.Count > 0)
        {
            Settings.LibraryFiles = order;
            SettingsStore.Save(Settings);
        }
    }

    public void Next() => Step(1);

    public void Previous()
    {
        if (Songs.Count == 0) return;
        int index = Current == null ? -1 : Songs.IndexOf(Current);
        if (ShouldRestartOnPrevious(index, Playback.PositionMs)) { Playback.Seek(0); return; }
        Step(-1);
    }

    /// <summary>The 4-second rule restarts the current track instead of stepping back — but never on
    /// the FIRST row: there is no earlier position to rewind to, and restarting the same track is what
    /// made 上一首 look dead at the top of the list (user report). Below the first row it still applies.</summary>
    internal static bool ShouldRestartOnPrevious(int index, double positionMs) => index > 0 && positionMs > 4000;

    private void Step(int direction)
    {
        if (Songs.Count == 0) return;
        int index = Current == null ? -1 : Songs.IndexOf(Current);
        int next = index < 0 ? 0 : (index + direction + Songs.Count) % Songs.Count;
        _ = PlayAsync(Songs[next]);
    }

    public void Toggle() => Playback.Toggle();

    /// <summary>
    /// Play/pause that also works before anything has been played. An empty deck means "start this
    /// list from the top" — the player bar already behaved that way, the full-screen page called
    /// <see cref="Toggle"/> straight into a no-op, so its triangle did nothing at all.
    /// </summary>
    public void ToggleOrStart()
    {
        if (Current != null)
        {
            Playback.Toggle();
            return;
        }
        if (Songs.Count > 0)
        {
            _ = PlayAsync(Songs[0]);
            return;
        }
        SetNotice("列表里还没有歌曲");
    }
    public void Seek(double ms) => Playback.Seek(ms);

    public (int index, LyricLine? line, double progress, string translation) CurrentCue()
    {
        if (Lyrics == null || Lyrics.Lines.Count == 0) return (-1, null, 0, "");
        int index = Lyrics.LineIndexAt((long)PositionMs, out var progress);
        if (index < 0) index = 0;
        var line = Lyrics.Lines[index];
        return (index, line, progress, line.Translation);
    }

    /// <summary>Adjusts the playing song's own lyric offset and refreshes the overlay.</summary>
    public long NudgeSongOffset(long deltaMs)
    {
        if (Current == null) return 0;
        long value = Math.Clamp(CurrentSongOffset + deltaMs, -30_000, 30_000);
        Settings.SongOffsets[Current.Key] = value;
        SettingsStore.Save(Settings);
        LyricsChanged?.Invoke();
        return value;
    }

    public void ResetSongOffset()
    {
        if (Current == null) return;
        Settings.SongOffsets.Remove(Current.Key);
        SettingsStore.Save(Settings);
        LyricsChanged?.Invoke();
    }

    public void Dispose()
    {
        _clock.Stop();
        _mixerVolume.Dispose();
        Playback.Dispose();
    }
}
