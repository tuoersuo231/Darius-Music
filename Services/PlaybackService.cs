using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CurrentLyrics.Models;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace CurrentLyrics.Services;

/// <summary>
/// Streaming playback on the WinRT <see cref="MediaPlayer"/>.
///
/// CurrentMusic plays lossless files straight from the CDN: its player streams them with a small
/// buffer, so a 190 MB FLAC starts in well under a second. WPF's MediaPlayer cannot open http(s)
/// sources at all ("对于媒体仅支持源站点包 URI"), which is why this app used to download the whole
/// file first. WinRT's player supports http streaming and FLAC through Media Foundation, so the
/// same behaviour is available here: hand it the URL, it buffers a little, playback starts.
///
/// The karaoke sweep needs a smooth clock, so the reported position is interpolated between the
/// player's coarse updates and never allowed to step backwards.
/// </summary>
public sealed class PlaybackService : IDisposable
{
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _syncTimer;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private double _anchorMs;
    private bool _playing;
    private bool _opened;
    private bool _startFromZero;
    private Uri? _pendingUri;
    private int _openAttempts;
    private DispatcherTimer? _openWatch;
    private bool _disposed;

    public Song? Current { get; private set; }
    public event Action? StateChanged;
    public event EventHandler? Ended;
    public string? Failure { get; set; }

    public PlaybackService()
    {
        _player.AutoPlay = false;
        _player.Volume = 0.85;
        _player.MediaOpened += (_, _) => OnUi(() =>
        {
            _opened = true;
            var duration = _player.PlaybackSession.NaturalDuration;
            if (_pendingSeekMs > 0) ApplyPendingSeek();
            if (_pendingPlay) { _pendingPlay = false; Play(); }
            if (Current != null && duration > TimeSpan.Zero) Current.Duration = duration;
            // A reused MediaPlayer keeps the outgoing session's position, so the next local file
            // opened straight after one that played to its end started *at its own end*: the bar was
            // full on the first frame and the track ended again immediately, which advanced the queue
            // a second time (user report: 自动切下一首时进度条已经拉满然后跳到下首).
            if (_startFromZero)
            {
                _startFromZero = false;
                try { _player.PlaybackSession.Position = TimeSpan.Zero; } catch { }
                _anchorMs = 0;
                _watch.Restart();
            }
            StateChanged?.Invoke();
        });
        _player.MediaEnded += (_, _) => OnUi(() =>
        {
            // Replacing the source (Stop()/Open()) also raises MediaEnded for the outgoing media.
            // Treating that as "the track finished" advanced the queue a second time — the next song
            // was skipped (or the same one replayed) and the seek row showed the previous track's
            // full length while the new title was already up. A real end means *this* session is at
            // its end and has finished opening; anything else is a stale notification.
            double total = DurationMs;
            double position = PositionMs;
            if (!_opened || (total > 0 && position < total - 800))
            {
                StateChanged?.Invoke();
                return;
            }
            _playing = false;
            _anchorMs = DurationMs;
            StateChanged?.Invoke();
            Ended?.Invoke(this, EventArgs.Empty);
        });
        _player.MediaFailed += (_, args) => OnUi(() =>
        {
            _playing = false;
            Failure = args.ErrorMessage;
            StateChanged?.Invoke();
        });

        _syncTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };
        _syncTimer.Tick += (_, _) => Resync();
        _syncTimer.Start();
    }

    public bool IsPlaying => _playing;

    public double DurationMs
    {
        get
        {
            var duration = _player.PlaybackSession.NaturalDuration;
            return duration > TimeSpan.Zero ? duration.TotalMilliseconds : (Current?.Duration.TotalMilliseconds ?? 0);
        }
    }

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 1);
    }

    public double PositionMs
    {
        get
        {
            if (!_opened) return 0;
            var ms = _playing ? _anchorMs + _watch.Elapsed.TotalMilliseconds : _anchorMs;
            var total = DurationMs;
            return total > 0 ? Math.Clamp(ms, 0, total) : Math.Max(0, ms);
        }
    }

    private double _pendingSeekMs;
    private bool _pendingPlay;

    /// <summary>
    /// MediaPlayer 在「打开后从没播放过」的暂停态里会**静默忽略** Position 赋值（实测 session 一直是 0）。
    /// 所以恢复进度要这样跳：先把音量压到 0、播一帧、再写 Position、立刻暂停、最后把音量放回去 ——
    /// 用户听不到任何声音，看到的却是上次那个位置。
    /// </summary>
    private void ApplyPendingSeek()
    {
        if (_pendingSeekMs <= 0) return;
        double target = _pendingSeekMs;
        _pendingSeekMs = 0;
        double volume = _player.Volume;
        try { _player.Volume = 0; _player.Play(); } catch { }
        int attempts = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        timer.Tick += (_, _) =>
        {
            double now;
            try { now = _player.PlaybackSession.Position.TotalMilliseconds; }
            catch { now = -1; }
            if (Math.Abs(now - target) < 1200 || ++attempts >= 3)
            {
                timer.Stop();
                try { _player.Pause(); } catch { }
                _playing = false;
                _anchorMs = target;
                _watch.Restart();
                try { _player.Volume = volume; } catch { }
                StateChanged?.Invoke();
                return;
            }
            try { _player.PlaybackSession.Position = TimeSpan.FromMilliseconds(target); } catch { }
        };
        timer.Start();
    }

    /// <summary>QA: 装载/跳转的中间状态，用来判断「恢复进度」到底卡在哪一步。</summary>
    public string DebugLoad
    {
        get
        {
            string session = "?";
            try { session = $"session={_player.PlaybackSession.Position.TotalMilliseconds:0} state={_player.PlaybackSession.PlaybackState}"; }
            catch (Exception ex) { session = "session-err " + ex.GetType().Name; }
            return $"opened={_opened} playing={_playing} anchor={_anchorMs:0} pending={_pendingSeekMs:0} {session}";
        }
    }

    /// <summary>想跳到某个位置，但媒体可能还没打开：排队到 MediaOpened 里再执行。
    /// 用于「上次听到一半」的恢复 —— 装载但不自动播放，位置照样在。</summary>
    public void SeekTo(double ms)
    {
        if (ms <= 50) return;
        _pendingSeekMs = ms;
        if (_opened) { _pendingSeekMs = 0; Seek(ms); }
    }

    /// <summary>Opens a stream URL or a local file and starts buffering/playing.</summary>
    public void Open(Song song, string? source = null, bool autoplay = true)
    {
        Failure = null;
        Current = song;
        _opened = false;
        _startFromZero = true;
        _anchorMs = 0;
        _watch.Restart();

        var target = source ?? song.Path;
        if (string.IsNullOrWhiteSpace(target))
        {
            Failure = "没有可播放的地址";
            StateChanged?.Invoke();
            return;
        }

        try
        {
            var uri = target.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new Uri(target)
                : new Uri(Path.GetFullPath(target));
            _pendingUri = uri;
            _openAttempts = 1;
            _player.Source = MediaSource.CreateFromUri(uri);
            if (autoplay) Play();
            else StateChanged?.Invoke();
            ArmOpenWatchdog();
        }
        catch (Exception ex)
        {
            Failure = "无法打开音频：" + ex.Message;
            _playing = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Auto-advance right after a track ended sometimes left the next one at 0:00 with no audio: the
    /// MediaPlayer occasionally ignores a Source that is assigned while it is still tearing the
    /// previous one down, so MediaOpened never fires and nothing plays. One silent retry clears it.
    /// </summary>
    private void ArmOpenWatchdog()
    {
        _openWatch?.Stop();
        _openWatch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1400) };
        _openWatch.Tick += (_, _) =>
        {
            _openWatch!.Stop();
            if (_opened || Failure != null || Current == null || _pendingUri == null) return;
            if (_openAttempts > 1) return;   // one retry only, then stay quiet and keep the failure path
            _openAttempts++;
            OpenRetries++;
            try
            {
                _player.Source = MediaSource.CreateFromUri(_pendingUri);
                Play();
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Failure = "无法打开音频：" + ex.Message;
                StateChanged?.Invoke();
            }
            ArmOpenWatchdog();
        };
        _openWatch.Start();
    }

    /// <summary>QA counter: how often the 0:00 "nothing opened" retry had to fire.</summary>
    public int OpenRetries { get; private set; }

    /// <summary>Silences the current track immediately.</summary>
    public void Stop()
    {
        // Pause only — clearing Source here and immediately assigning the next one in Open() is the
        // race that left the following track at 0:00 with no sound. Open() replaces the source anyway,
        // and a paused player is silent, so the audible result is the same.
        try { _player.Pause(); } catch { }
        _playing = false;
        _opened = false;
        _anchorMs = 0;
        _watch.Restart();
        StateChanged?.Invoke();
    }

    public void Play()
    {
        if (Current == null) return;
        // 恢复的会话只装载不播放：如果这一步因为任何原因没打开成功（或 Source 被清掉），
        // 直接 Play() 会毫无反应（用户报的「播放键按了没动静」）。这里补一次装载，开好再播。
        if (!_opened)
        {
            _pendingPlay = true;
            if (_player.Source == null) Open(Current, null, autoplay: false);
            else _player.Play();
            return;
        }
        if (DurationMs > 0 && PositionMs >= DurationMs - 80) Seek(0);
        _player.Play();
        _playing = true;
        _watch.Restart();
        StateChanged?.Invoke();
    }

    public void Pause()
    {
        _player.Pause();
        _anchorMs = PositionMs;
        _playing = false;
        StateChanged?.Invoke();
    }

    public void Toggle()
    {
        if (_playing) Pause(); else Play();
    }

    public void Seek(double ms)
    {
        if (Current == null) return;
        var total = DurationMs;
        var target = total > 0 ? Math.Clamp(ms, 0, total - 100) : Math.Max(0, ms);
        double before;
        try { before = _player.PlaybackSession.Position.TotalMilliseconds; } catch { before = -1; }
        // 已经在目标位置就别再写一次：一次点击会被松手和失去捕获各触发一次，重复 seek 会打断播放。
        if (before >= 0 && Math.Abs(before - target) < 150)
        {
            _anchorMs = target;
            _watch.Restart();
            return;
        }
        string error = "";
        try { _player.PlaybackSession.Position = TimeSpan.FromMilliseconds(target); }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
        _anchorMs = target;
        _watch.Restart();
        StateChanged?.Invoke();

        // 有的媒体会话会静默忽略 Position 赋值：锚点已经跳到目标，播放器却没动，
        // 于是进度条先跳过去、再由 Resync 缩回来（用户看到的就是闪一下）。
        // 这里 120 ms 后回读一次真实位置，没生效就把锚点改回实际值，并记下原因。
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            double after;
            try { after = _player.PlaybackSession.Position.TotalMilliseconds; } catch { return; }
            bool took = Math.Abs(after - target) < 1500 || Math.Abs(after - before) > 200;
            if (!took)
            {
                _anchorMs = after;
                _watch.Restart();
                StateChanged?.Invoke();
            }
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.DataDir, "seek.log"),
                    $"{DateTime.Now:HH:mm:ss} target={target:0} before={before:0} after={after:0} took={took} canSeek={SafeCanSeek()} err={error}\n");
            }
            catch { }
        };
        timer.Start();
    }

    private string SafeCanSeek()
    {
        try { return _player.PlaybackSession.CanSeek.ToString(); } catch { return "?"; }
    }

    public void SetVolume(double value) => _player.Volume = Math.Clamp(value, 0, 1);

    private void Resync()
    {
        if (!_opened) return;
        double playerMs;
        try { playerMs = _player.PlaybackSession.Position.TotalMilliseconds; }
        catch { return; }

        if (!_playing)
        {
            _anchorMs = playerMs;
            _watch.Restart();
            return;
        }

        // The engine reports coarse positions; adopting them blindly made the karaoke ink step
        // back. Only advance, or accept a real discontinuity (seek / stall) when the gap is large.
        var estimated = _anchorMs + _watch.Elapsed.TotalMilliseconds;
        if (playerMs > estimated + 5 || estimated - playerMs > 500)
        {
            _anchorMs = playerMs;
            _watch.Restart();
        }
    }

    private void OnUi(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        _disposed = true;
        _syncTimer.Stop();
        try { _player.Dispose(); } catch { }
    }
}
