using System.Diagnostics;
using System.Windows.Threading;

namespace CurrentLyrics.Services;

/// <summary>
/// Windows normally remembers each app's Volume Mixer level, but it only does so when it has a
/// stable per-executable entry. For this app no entry is ever written, so the mixer snaps back
/// to 100% on every launch. This keeper remembers the session volume itself and re-applies it.
/// </summary>
public sealed class AudioVolumeKeeper : IDisposable
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private readonly uint _pid = (uint)Process.GetCurrentProcess().Id;
    private bool _applied;
    private float _last = -1;
    private DateTime _lastSave = DateTime.MinValue;

    public AudioVolumeKeeper(AppSettings settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        var volume = AudioSession.GetVolume(_pid);
        if (volume is not { } current) return;

        if (!_applied)
        {
            _applied = true;
            if (_settings.MixerVolume > 0f && Math.Abs(_settings.MixerVolume - current) > 0.01f)
            {
                AudioSession.SetVolume(_pid, _settings.MixerVolume);
                _last = _settings.MixerVolume;
                Log($"applied stored {_last:0.00} (session was {current:0.00})");
            }
            else
            {
                _last = current;
            }
            return;
        }

        // WPF closes and reopens the media player on every track change, which destroys and
        // recreates the audio session: the new one starts at 100%. That is not the user moving
        // the slider — restore their level instead of overwriting it.
        bool looksLikeNewSession = current > 0.98f && _last > 0.02f && _last < 0.93f;
        if (looksLikeNewSession)
        {
            AudioSession.SetVolume(_pid, _last);
            Log($"restored {_last:0.00} (session was {current:0.00})");
            return;
        }

        // Someone moved the mixer slider: keep it for the next launch.
        if (Math.Abs(current - _last) > 0.01f)
        {
            _last = current;
            _settings.MixerVolume = current;
            Log($"captured {current:0.00}");
            if ((DateTime.UtcNow - _lastSave).TotalSeconds > 1)
            {
                _lastSave = DateTime.UtcNow;
                SettingsStore.Save(_settings);
            }
        }
    }

    private void Log(string message)
    {
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.DataDir, "volume.log"),
                $"{DateTime.Now:HH:mm:ss} {message}\n");
        }
        catch { }
    }

    public void Dispose()
    {
        _timer.Stop();
        try
        {
            if (AudioSession.GetVolume(_pid) is { } current)
            {
                _settings.MixerVolume = current;
                SettingsStore.Save(_settings);
            }
        }
        catch { }
    }
}
