using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CurrentLyrics.Services;
using CurrentLyrics.Views;

namespace CurrentLyrics;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try { MainGuarded(args); }
        catch (Exception ex) { Crashing(ex, "main"); throw; }
    }

    private static void MainGuarded(string[] args)
    {
        // Updater mode: must not touch WPF, the target folder is being replaced under us.
        if (args.Length > 3 && args[0] == "--apply-update")
        {
            Environment.ExitCode = Services.UpdateService.RunApply(args);
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crashing(e.ExceptionObject as Exception, "domain");
        System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            Crashing(e.Exception, "dispatcher");
            e.Handled = true;
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => Crashing(e.Exception, "task");

        var app = new Application
        {
            // Closing the library window must end the process, otherwise the overlay can be
            // left on screen with a frozen frame.
            ShutdownMode = ShutdownMode.OnMainWindowClose,
        };
        // WPF samples every animation at 60 fps by default, whatever the monitor runs at. This machine
        // is 179 Hz, so the default cap was the reason motion looked like 60 Hz.
        System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(System.Windows.Media.Animation.Timeline),
            new FrameworkPropertyMetadata(Math.Clamp(NativeMethods.DisplayRefreshHz(), 60, 360)));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Theme.xaml", UriKind.Absolute),
        });

        var settings = SettingsStore.Load();

        // QA: force the 3D depth-ratio cap so the before/after of the projection can be compared.
        if (double.TryParse(Arg(args, "--depth"), out var depthRatio) && depthRatio > 0)
            Controls.PerspectiveWarp.MaxDepthRatio = depthRatio;
        // QA: render the lyric column with the old glyph-outline path for A/B screenshots.
        if (args.Contains("--nobake")) Controls.LyricStackView.UseBakedText = false;

        if (args.Contains("--media-test"))
        {
            RunMediaTest(app, args);
            return;
        }
        if (args.Contains("--audio-test"))
        {
            RunAudioTest(app, args, settings);
            return;
        }
        if (args.Contains("--stream-test"))
        {
            RunStreamTest(app, args, settings);
            return;
        }
        if (args.Contains("--wsa-test"))
        {
            RunWsaTest(args).GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--lx-test"))
        {
            RunLxTest(args);
            return;
        }
        if (args.Contains("--lx-import"))
        {
            // Import one or more LX scripts into this install's data folder without opening the UI.
            var lines = new List<string>();
            foreach (var path in args.SkipWhile(a => a != "--lx-import").Skip(1).TakeWhile(a => !a.StartsWith("--")))
            {
                try
                {
                    var entry = Services.LxStore.Import(path);
                    if (Arg(args, "--activate") != "no") Services.LxStore.SetActive(entry.Id);
                    lines.Add($"imported name={entry.Name} version={entry.Version} error={entry.Error ?? "-"} " +
                              $"platforms=[{string.Join(", ", entry.Platforms.Select(p => p.Key + ":" + string.Join("/", p.Value)))}] " +
                              $"active={Services.LxStore.ActiveId == entry.Id}");
                }
                catch (Exception ex)
                {
                    lines.Add($"failed {System.IO.Path.GetFileName(path)}: {ex.Message}");
                }
            }
            lines.Add("store=" + System.IO.Path.Combine(Services.AppPaths.DataDir, "lx"));
            File.WriteAllLines(Arg(args, "--report") ?? "lx-import.txt", lines);
            return;
        }
        if (args.Contains("--ne-test"))
        {
            RunNeteaseTest(args, settings);
            return;
        }
        if (args.Contains("--warp-test"))
        {
            var rect = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 550, 100));
            rect.Freeze();
            double angle = -32;
            double camera = Controls.PerspectiveWarp.CameraFor(550, angle, 1.6);
            double fit = Controls.PerspectiveWarp.FitFor(550, angle, camera);
            var warped = Controls.PerspectiveWarp.Warp(rect, angle, Controls.PerspectiveWarp.CameraFor(550, angle, 1.6), fit, 100);
            var flat = warped!.GetFlattenedPathGeometry(0.1, ToleranceType.Absolute);
            double yAtLeft = double.NaN, yAtRight = double.NaN;
            foreach (var fig in flat.Figures)
            {
                foreach (var pt in ((System.Windows.Media.PolyLineSegment)fig.Segments[0]).Points)
                {
                    if (pt.X < 60) yAtLeft = Math.Max(yAtLeft, pt.Y);
                    if (pt.X > flat.Bounds.Right - 60) yAtRight = Math.Max(yAtRight, pt.Y);
                }
            }
            File.WriteAllLines(Arg(args, "--report") ?? "warp-test.txt", new[]
            {
                $"fit={fit:0.000} camera={Controls.PerspectiveWarp.CameraFor(550, angle, 1.6):0.0}",
                $"bounds={flat.Bounds}",
                $"Map(0,0)={Controls.PerspectiveWarp.Map(0, 0, angle, camera, fit)}",
                $"Map(550,0)={Controls.PerspectiveWarp.Map(550, 0, angle, camera, fit)}",
                $"Map(0,100)={Controls.PerspectiveWarp.Map(0, 100, angle, camera, fit)}",
                $"Map(550,100)={Controls.PerspectiveWarp.Map(550, 100, angle, camera, fit)}",
                $"bottom y at left={yAtLeft:0.00}  right={yAtRight:0.00}",
            });
            return;
        }
        if (args.Contains("--anim"))
        {
            RunAnim(app, args, settings);
            return;
        }
        if (args.Contains("--video"))
        {
            RunVideoExtract(app, args);
            return;
        }
        if (args.Contains("--lyric-qa"))
        {
            // 3D 倾斜的"拉伸适配"量化对比：同一个 LRC、同一组播放器参数，只改允许的远近比。
            string qaDir = Arg(args, "--lyric-qa") ?? ".";
            Directory.CreateDirectory(qaDir);
            const string lrc = "[ti:願い～あの頃のキミへ～]\n[ar:當山みれい]\n" +
                "[00:00.00]願い～あの頃のキミへ～ (祈愿～致那个时候的你～) - 當山みれい、(当山真玲)\n" +
                "[00:06.00]词：Dohzi-T\n[00:10.00]曲：Dohzi-T/Shingo.S\n" +
                "[00:15.00]忘れられない あの頃のキミへ\n" +
                "[00:21.00]那时候的你没有说完的话 我一直都记着\n" +
                "[00:27.00]もう一度だけ 会いたいんだ\n" +
                "[00:33.00]风が吹いて 记忆が揺れる\n[00:39.00]遠い日の 約束を\n";
            var qaDoc = LrcParser.Parse(lrc);
            var report = new List<string>
            {
                $"player: font={settings.PlayerFontSize} lines={settings.PlayerVisibleLines} gap={settings.PlayerLineGap} " +
                $"cur={settings.PlayerCurrentScale} tilt={settings.PlayerTiltAngle} vp={settings.PlayerVanishingOffset} " +
                $"glow={settings.PlayerGlow}",
            };
            foreach (double fanRef in new[] { 0.0, 900.0 })
            {
                Controls.PerspectiveWarp.MaxDepthRatio = 1.40;
                // 931 / 619 = the two window widths the user compared; 344 = the portrait column.
                foreach (var (cw, w, h) in new[] { (931.0, 1240, 820), (619.0, 900, 820), (344.0, 520, 880) })
                {
                    var view = new Controls.LyricStackView { IsHitTestVisible = false, SoftNeighbours = true };
                    view.FanReferenceWidth = fanRef;
                    var s = settings.Clone();
                    s.FontSize = settings.PlayerFontSize;
                    s.VisibleLines = settings.PlayerVisibleLines;
                    s.CurrentScale = settings.PlayerCurrentScale;
                    s.LineGap = settings.PlayerLineGap;
                    s.Translation = settings.PlayerTranslation;
                    s.GlowEnabled = settings.PlayerGlow;
                    s.GlowStrength = settings.PlayerGlowStrength;
                    s.TextColor = settings.PlayerTextColor;
                    s.Perspective = true;
                    s.TiltAngle = settings.PlayerTiltAngle;
                    s.VanishingOffset = settings.PlayerVanishingOffset;
                    s.Backdrop = false;
                    s.LineWidth = cw;
                    view.ApplySettings(s, 1.0);
                    view.SetDocument(qaDoc, 21000);
                    view.Snap();
                    var host = new System.Windows.Controls.Border
                    {
                        Width = w, Height = h,
                        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1E)),
                        Child = view,
                    };
                    host.Measure(new Size(w, h));
                    host.Arrange(new Rect(0, 0, w, h));
                    host.UpdateLayout();
                    app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                    SaveVisual(host, w, h, Path.Combine(qaDir, $"fan{fanRef:0}-w{cw:0}.png"));
                    report.Add($"fanRef={fanRef:0} width={cw:0}: {view.DebugPlane} cue='{view.CurrentLine?.Text}'");
                }
            }
            File.WriteAllLines(Path.Combine(qaDir, "plane.txt"), report);
            return;
        }
        if (args.Contains("--karaoke-qa"))
        {
            // 逐字歌词 QA：在给定播放位置把歌词列画成 PNG，并报告读取行每个词的卡拉OK 状态
            // （时钟判定已开始的词数 / 已建好的词数 / 真正带贴图可以贴的碎片数）。
            string kqDir = Arg(args, "--karaoke-qa") ?? ".";
            Directory.CreateDirectory(kqDir);
            string? kqLrc = Arg(args, "--lrc");
            var kqDoc = kqLrc != null && File.Exists(kqLrc)
                ? LrcParser.Parse(File.ReadAllText(kqLrc))
                : LrcParser.Demo();
            long kqAt = long.TryParse(Arg(args, "--pos"), out var kqP) ? kqP : 40000;
            var kqReport = new List<string> { $"src={(kqLrc ?? "<demo>")} lines={kqDoc.Lines.Count} pos={kqAt}" };
            foreach (double kqSpread in new[] { 0.5, 1.0, 2.0 })
            {
            var view = new Controls.LyricStackView { IsHitTestVisible = false, SoftNeighbours = true };
            var s = settings.Clone();
            s.FontSize = settings.PlayerFontSize;
            s.VisibleLines = settings.PlayerVisibleLines;
            s.CurrentScale = settings.PlayerCurrentScale;
            s.LineGap = settings.PlayerLineGap;
            s.Translation = settings.PlayerTranslation;
            s.GlowEnabled = true;
            s.GlowStrength = Math.Max(1.0, settings.PlayerGlowStrength);
            s.GlowSpread = kqSpread;
            s.TextColor = settings.PlayerTextColor;
            s.Perspective = true;
            s.TiltAngle = settings.PlayerTiltAngle;
            s.VanishingOffset = settings.PlayerVanishingOffset;
            s.Backdrop = false;
            s.LineWidth = 931;
            view.ApplySettings(s, 1.0);
            view.SetDocument(kqDoc, kqAt);
            var host = new System.Windows.Controls.Border
            {
                Width = 1240, Height = 820,
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1E)),
                Child = view,
            };
            host.Measure(new Size(1240, 820));
            host.Arrange(new Rect(0, 0, 1240, 820));
            host.UpdateLayout();
            // 词段平时是分帧建出来的；离屏渲染拿不到几十个真实帧，这里一次性建完再画。
            view.DebugBuildAllWordParts();
            view.Snap();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            SaveVisual(host, 1240, 820, Path.Combine(kqDir, $"karaoke-s{kqSpread * 100:000}.png"));
            kqReport.Add($"spread={kqSpread:0.00} {view.DebugKaraoke}");
            kqReport.Add(view.DebugFont);
            }
            File.WriteAllLines(Path.Combine(kqDir, "karaoke.txt"), kqReport);
            // 3D 倾斜：滑块上的角度和真正画出来的远近比是不是一一对应（以前 >30° 全部饱和）。
            foreach (double kqTilt in new[] { 20.0, 30.0, 45.0, 60.0, 75.0 })
            {
                var tv = new Controls.LyricStackView { IsHitTestVisible = false, SoftNeighbours = true };
                var ts = settings.Clone();
                ts.FontSize = settings.PlayerFontSize;
                ts.VisibleLines = settings.PlayerVisibleLines;
                ts.CurrentScale = settings.PlayerCurrentScale;
                ts.LineGap = settings.PlayerLineGap;
                ts.Translation = settings.PlayerTranslation;
                ts.GlowEnabled = false;
                ts.Perspective = true;
                ts.TiltAngle = -kqTilt;
                ts.VanishingOffset = settings.PlayerVanishingOffset;
                ts.Backdrop = false;
                ts.LineWidth = 931;
                tv.ApplySettings(ts, 1.0);
                tv.SetDocument(kqDoc, kqAt);
                var thost = new System.Windows.Controls.Border
                {
                    Width = 1240, Height = 820,
                    Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1E)),
                    Child = tv,
                };
                thost.Measure(new Size(1240, 820));
                thost.Arrange(new Rect(0, 0, 1240, 820));
                thost.UpdateLayout();
                SaveVisual(thost, 1240, 820, Path.Combine(kqDir, $"tilt-{kqTilt:00}.png"));
                File.AppendAllLines(Path.Combine(kqDir, "karaoke.txt"), new[] { $"tilt=-{kqTilt:0} {tv.DebugPlane}" });
            }
            return;
        }
        if (args.Contains("--text-color"))
        {
            // 文字颜色到底有没有画上去：同一列先用白字渲一帧，再就地改色（几何不动、只该重烘贴图）渲
            // 一帧，数「红字像素」和白字像素。以前改色只动 _textColor，贴图还是旧颜色的，所以这里是 0。
            string tcDir = Arg(args, "--text-color") ?? ".";
            Directory.CreateDirectory(tcDir);
            string? tcLrc = Arg(args, "--lrc");
            var tcDoc = tcLrc != null && File.Exists(tcLrc)
                ? LrcParser.Parse(File.ReadAllText(tcLrc))
                : LrcParser.Demo();
            long tcAt = long.TryParse(Arg(args, "--pos"), out var tcP) ? tcP : 40000;
            string target = (Arg(args, "--c") ?? "FF3B30").TrimStart('#').ToUpperInvariant();
            var report = new List<string> { $"src={(tcLrc ?? "<demo>")} lines={tcDoc.Lines.Count} pos={tcAt} target=#{target}" };

            var tc = settings.Clone();
            tc.TextColor = "FFFFFF";
            tc.GlowEnabled = false;
            tc.Backdrop = false;
            tc.FontSize = 30;
            tc.LineWidth = 931;
            tc.VisibleLines = 7;
            tc.Perspective = false;
            tc.Translation = false;

            var view = new Controls.LyricStackView { IsHitTestVisible = false };
            view.ApplySettings(tc, 1.0);
            view.SetDocument(tcDoc, tcAt);
            var host = new System.Windows.Controls.Border
            {
                Width = 1100,
                Height = 520,
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1E)),
                Child = view,
            };
            host.Measure(new Size(1100, 520));
            host.Arrange(new Rect(0, 0, 1100, 520));
            host.UpdateLayout();
            view.DebugBuildAllWordParts();
            view.Snap();

            RenderTargetBitmap Shot()
            {
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                var rtb = new RenderTargetBitmap(1100, 520, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(host);
                return rtb;
            }

            (int white, int red) Count(RenderTargetBitmap rtb)
            {
                int w = rtb.PixelWidth, h = rtb.PixelHeight;
                var px = new byte[w * h * 4];
                rtb.CopyPixels(px, w * 4, 0);
                int white = 0, red = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                    if (a < 40) continue;
                    if (r > 150 && g < 90 && b < 90) red++;
                    else if (r > 190 && g > 190 && b > 190) white++;
                }
                return (white, red);
            }

            void Save(RenderTargetBitmap rtb, string name)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(Path.Combine(tcDir, name));
                encoder.Save(fs);
            }

            var first = Shot();
            Save(first, "text-before.png");
            var c0 = Count(first);
            report.Add($"white  white={c0.white} red={c0.red} " + view.DebugKaraoke);

            tc.TextColor = target;
            var applyClock = System.Diagnostics.Stopwatch.StartNew();
            view.ApplySettings(tc, 1.0);
            var second = Shot();
            applyClock.Stop();
            Save(second, "text-after-1frame.png");
            var c1 = Count(second);
            report.Add($"1 frame after recolour  white={c1.white} red={c1.red} apply={applyClock.Elapsed.TotalMilliseconds:0.0} ms");

            for (int i = 0; i < 6; i++) Shot();
            var settled = Shot();
            Save(settled, "text-after.png");
            var c2 = Count(settled);
            report.Add($"settled  white={c2.white} red={c2.red}");

            // 40 steps, like the colour square fires while dragging a colour (white → target).
            var dragClock = System.Diagnostics.Stopwatch.StartNew();
            double worst = 0;
            for (int i = 0; i < 40; i++)
            {
                double k = i / 39.0;
                string hex = string.Format("{0:X2}{1:X2}{2:X2}",
                    (int)(255 + (Convert.ToByte(target.Substring(0, 2), 16) - 255) * k),
                    (int)(255 + (Convert.ToByte(target.Substring(2, 2), 16) - 255) * k),
                    (int)(255 + (Convert.ToByte(target.Substring(4, 2), 16) - 255) * k));
                var step = System.Diagnostics.Stopwatch.StartNew();
                tc.TextColor = hex;
                view.ApplySettings(tc, 1.0);
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                step.Stop();
                worst = Math.Max(worst, step.Elapsed.TotalMilliseconds);
            }
            dragClock.Stop();
            var dragged = Shot();
            Save(dragged, "text-drag.png");
            var c3 = Count(dragged);
            report.Add($"drag 40 steps  avg={dragClock.Elapsed.TotalMilliseconds / 40:0.0} ms worst={worst:0.0} ms final white={c3.white} red={c3.red}");
            File.WriteAllLines(Path.Combine(tcDir, "text-color.txt"), report);
            return;
        }
        if (args.Contains("--shot"))
        {
            RunShot(app, args, settings);
            return;
        }
        if (args.Contains("--video-frames"))
        {
            // Decode a screen recording with the WPF media stack (no ffmpeg on this machine) and dump
            // one PNG per step so row motion can be measured frame by frame.
            string video = Arg(args, "--video-frames")!;
            string outDir = Arg(args, "--frames-out") ?? Path.Combine(Path.GetTempPath(), "vframes");
            double step = double.TryParse(Arg(args, "--frames-step") ?? "25", out var st) ? st : 25;
            Directory.CreateDirectory(outDir);
            var mp = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
            var opened = new ManualResetEventSlim();
            mp.MediaOpened += (_, _) => opened.Set();
            mp.MediaFailed += (_, _) => opened.Set();
            mp.Open(new Uri(video));
            mp.Play();
            opened.Wait(10000);
            double total = mp.NaturalDuration.HasTimeSpan ? mp.NaturalDuration.TimeSpan.TotalMilliseconds : 0;
            int vw = mp.NaturalVideoWidth, vh = mp.NaturalVideoHeight;
            File.WriteAllText(Path.Combine(outDir, "info.txt"), $"size={vw}x{vh} ms={total:0} step={step}");
            var visual = new DrawingVisual();
            int index = 0;
            for (double ms = 0; ms < total; ms += step, index++)
            {
                mp.Position = TimeSpan.FromMilliseconds(ms);
                for (int i = 0; i < 4; i++) { app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render); Thread.Sleep(10); }
                using (var dc = visual.RenderOpen())
                    dc.DrawVideo(mp, new Rect(0, 0, vw, vh));
                var rtb = new RenderTargetBitmap(vw, vh, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(Path.Combine(outDir, $"v{index:0000}.png"));
                encoder.Save(fs);
            }
            mp.Close();
            File.AppendAllText(Path.Combine(outDir, "info.txt"), $" frames={index}");
            return;
        }
        if (args.Contains("--video-grab"))
        {
            // Frame-exact grab: seek to each sample time and wait until the media clock reports it
            // arrived, instead of hoping a fixed sleep was long enough. Pumps a real dispatcher
            // frame so MediaPlayer events and scrub results actually get delivered.
            string video = Arg(args, "--video-grab")!;
            string outDir = Arg(args, "--grab-out") ?? Path.Combine(Path.GetTempPath(), "vgrab");
            double step = double.TryParse(Arg(args, "--grab-step") ?? "", out var gst) && gst > 0 ? gst : 1000.0 / 60;
            int hold = int.TryParse(Arg(args, "--grab-hold") ?? "", out var hd) ? hd : 40;
            string rectArg = Arg(args, "--grab-rect") ?? "";
            int rx = 0, ry = 0, rw = 0, rh = 0;
            if (rectArg.Length > 0)
            {
                var q = rectArg.Split(',');
                rx = int.Parse(q[0]); ry = int.Parse(q[1]); rw = int.Parse(q[2]); rh = int.Parse(q[3]);
            }
            Directory.CreateDirectory(outDir);
            var log = new System.Text.StringBuilder();

            void PumpFor(int ms)
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
            }

            var mp = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
            bool isOpen = false;
            string failure = "";
            mp.MediaOpened += (_, _) => isOpen = true;
            mp.MediaFailed += (_, e) => failure = e.ErrorException?.Message ?? "MediaFailed";
            mp.Open(new Uri(video));
            PumpFor(300);
            mp.Pause();
            for (int i = 0; i < 200 && !isOpen && failure.Length == 0; i++) PumpFor(50);
            PumpFor(200);
            double total = mp.NaturalDuration.HasTimeSpan ? mp.NaturalDuration.TimeSpan.TotalMilliseconds : 0;
            int vw = mp.NaturalVideoWidth, vh = mp.NaturalVideoHeight;
            if (rw <= 0 || rh <= 0) { rx = 0; ry = 0; rw = vw; rh = vh; }
            File.WriteAllText(Path.Combine(outDir, "info.txt"),
                $"open={isOpen} fail={failure} size={vw}x{vh} ms={total:0} step={step:0.###} rect={rx},{ry},{rw},{rh}");

            var crop = new DrawingVisual();
            var shot = new DrawingVisual();
            int idx = 0;
            for (double ms = 0; ms < total; ms += step, idx++)
            {
                var target = TimeSpan.FromMilliseconds(ms + step * 0.5);
                mp.Position = target;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                double reported = -1;
                while (sw.ElapsedMilliseconds < 900)
                {
                    PumpFor(6);
                    try { reported = mp.Position.TotalMilliseconds; } catch { }
                    if (reported >= 0 && Math.Abs(reported - target.TotalMilliseconds) <= 20) break;
                }
                if (sw.ElapsedMilliseconds < hold) PumpFor(hold - (int)sw.ElapsedMilliseconds);

                using (var dc = crop.RenderOpen())
                {
                    dc.PushClip(new RectangleGeometry(new Rect(0, 0, rw, rh)));
                    dc.PushTransform(new TranslateTransform(-rx, -ry));
                    dc.DrawVideo(mp, new Rect(0, 0, vw, vh));
                    dc.Pop();
                    dc.Pop();
                }
                var rtb = new RenderTargetBitmap(rw, rh, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(crop);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(Path.Combine(outDir, $"s{idx:0000}.png"))) enc.Save(fs);

                // every 6th sample keeps a half-size full view so the page context is visible
                if (idx % 6 == 0)
                {
                    using (var dc = shot.RenderOpen())
                    {
                        dc.PushTransform(new ScaleTransform(0.5, 0.5));
                        dc.DrawVideo(mp, new Rect(0, 0, vw, vh));
                        dc.Pop();
                    }
                    var rtb2 = new RenderTargetBitmap(vw / 2, vh / 2, 96, 96, PixelFormats.Pbgra32);
                    rtb2.Render(shot);
                    var enc2 = new PngBitmapEncoder();
                    enc2.Frames.Add(BitmapFrame.Create(rtb2));
                    using var fs2 = File.Create(Path.Combine(outDir, $"f{idx:0000}.png"));
                    enc2.Save(fs2);
                }
                log.AppendLine($"{idx} t={ms:0.0} target={target.TotalMilliseconds:0.0} reported={reported:0.0} settle={sw.ElapsedMilliseconds}");
            }
            mp.Close();
            File.AppendAllText(Path.Combine(outDir, "info.txt"), $" frames={idx}");
            File.WriteAllText(Path.Combine(outDir, "frames.log"), log.ToString());
            return;
        }
        if (args.Contains("--hit-test"))
        {
            // What does a click on the now-playing progress bar actually hit? And does a real
            // synthetic click move the playback position?
            string logPath = Arg(args, "--hit-log") ?? "hit.txt";
            var lines = new List<string>();
            var ctl = new PlayerController(settings);
            var win = new MainWindow(ctl)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 0,
                Top = 0,
                Width = 1400,
                Height = 900,
            };
            win.Show();
            win.AddHandler(UIElement.PreviewMouseDownEvent, new System.Windows.Input.MouseButtonEventHandler((_, e) =>
                lines.Add($"win previewdown src={Node(e.OriginalSource as DependencyObject)} handled={e.Handled}")), true);
            win.AddHandler(UIElement.MouseDownEvent, new System.Windows.Input.MouseButtonEventHandler((_, e) =>
                lines.Add($"win down src={Node(e.OriginalSource as DependencyObject)} handled={e.Handled}")), true);
            PumpFrames(400);
            RunBlocking(app, ctl.ShowLibraryAsync());
            PumpFrames(400);
            if (ctl.Songs.Count > 0)
            {
                ctl.Playback.SetVolume(0);
                RunBlocking(app, ctl.PlayAsync(ctl.Songs[0]));
                PumpFrames(1200);
            }
            // the library page player bar first: it is the same Slider pattern
            var bar = win.FindName("sldProgress") as System.Windows.Controls.Slider;
            if (bar != null && bar.IsVisible)
            {
                var br = bar.TransformToAncestor(win).TransformBounds(new Rect(0, 0, bar.ActualWidth, bar.ActualHeight));
                var bp = win.PointToScreen(new Point(br.X + br.Width * 0.5, br.Y + br.Height / 2));
                double b0 = ctl.RawPositionMs;
                SetCursorPos((int)Math.Round(bp.X), (int)Math.Round(bp.Y));
                PumpFrames(200);
                MouseEvent(0x0002, 0, 0, 0);
                PumpFrames(90);
                MouseEvent(0x0004, 0, 0, 0);
                PumpFrames(900);
                lines.Add($"playerbar rect={br.X:0.#},{br.Y:0.#},{br.Width:0.#}x{br.Height:0.#} click@0.5 posMs {b0:0} -> {ctl.RawPositionMs:0} value={bar.Value:0.###} max={bar.Maximum:0.###}");
            }
            else
            {
                lines.Add("playerbar not found");
            }
            var vol = win.FindName("sldVolume") as System.Windows.Controls.Slider;
            if (vol != null)
            {
                var vr = vol.TransformToAncestor(win).TransformBounds(new Rect(0, 0, vol.ActualWidth, vol.ActualHeight));
                lines.Add($"volume rect={vr.X:0.#},{vr.Y:0.#},{vr.Width:0.#}x{vr.Height:0.#} moveToPoint={vol.IsMoveToPointEnabled} value={vol.Value:0.###}");
                void VClick(double frac)
                {
                    var sc = win.PointToScreen(new Point(vr.X + vr.Width * frac, vr.Y + vr.Height / 2));
                    double v0 = vol.Value;
                    SetCursorPos((int)Math.Round(sc.X), (int)Math.Round(sc.Y));
                    PumpFrames(200);
                    MouseEvent(0x0002, 0, 0, 0);
                    PumpFrames(90);
                    MouseEvent(0x0004, 0, 0, 0);
                    PumpFrames(600);
                    lines.Add($"volume click@{frac:0.##} value {v0:0.###} -> {vol.Value:0.###} settings={ctl.Settings.Volume:0.###}");
                }
                VClick(0.25);
                VClick(0.8);
                {
                    var from = win.PointToScreen(new Point(vr.X + vr.Width * 0.8, vr.Y + vr.Height / 2));
                    var to = win.PointToScreen(new Point(vr.X + vr.Width * 0.35, vr.Y + vr.Height / 2));
                    double v0 = vol.Value;
                    SetCursorPos((int)Math.Round(from.X), (int)Math.Round(from.Y));
                    PumpFrames(200);
                    MouseEvent(0x0002, 0, 0, 0);
                    PumpFrames(120);
                    for (int i = 1; i <= 8; i++)
                    {
                        SetCursorPos((int)Math.Round(from.X + (to.X - from.X) * i / 8.0), (int)Math.Round(from.Y));
                        PumpFrames(60);
                    }
                    MouseEvent(0x0004, 0, 0, 0);
                    PumpFrames(600);
                    lines.Add($"volume drag 0.8->0.35 value {v0:0.###} -> {vol.Value:0.###} settings={ctl.Settings.Volume:0.###}");
                }
            }
            else
            {
                lines.Add("volume slider not found");
            }
            win.DebugOpenPlayerPage();
            PumpFrames(900);
            var view = win.PlayerView;
            lines.Add("page=" + win.DebugPlayerPageState());
            var slider = view == null ? null : FindDescendant<System.Windows.Controls.Slider>(view);
            if (slider == null)
            {
                lines.Add("no slider found");
            }
            else
            {
                var r = slider.TransformToAncestor(win).TransformBounds(new Rect(0, 0, slider.ActualWidth, slider.ActualHeight));
                lines.Add($"window={win.ActualWidth:0}x{win.ActualHeight:0} dpi={VisualTreeHelper.GetDpi(win).DpiScaleX:0.##}");
                lines.Add($"slider rect={r.X:0.#},{r.Y:0.#},{r.Width:0.#}x{r.Height:0.#} enabled={slider.IsEnabled} hit={slider.IsHitTestVisible} moveToPoint={slider.IsMoveToPointEnabled} value={slider.Value:0.###} max={slider.Maximum:0.###}");
                foreach (var part in TemplateParts(slider))
                    lines.Add("  part " + part);
                foreach (var frac in new[] { 0.1, 0.4, 0.75 })
                {
                    var p = new Point(r.X + r.Width * frac, r.Y + r.Height / 2);
                    var res = VisualTreeHelper.HitTest(win, p);
                    var chain = new List<string>();
                    for (var d = res?.VisualHit; d != null; d = VisualTreeHelper.GetParent(d))
                    {
                        chain.Add(Node(d));
                        if (d == slider) break;
                    }
                    lines.Add($"hit@{frac:0.##} ({p.X:0.#},{p.Y:0.#}) -> {string.Join(" > ", chain)}");
                }
                // real synthetic clicks at several positions, then a drag
                lines.Add($"current={ctl.Current?.DisplayTitle ?? "(null)"} dur={ctl.Playback.DurationMs:0} state={ctl.PlaybackState}");
                void ClickAt(double frac)
                {
                    var pt = new Point(r.X + r.Width * frac, r.Y + r.Height / 2);
                    var sc = win.PointToScreen(pt);
                    double before = ctl.RawPositionMs;
                    double vb = slider.Value;
                    SetCursorPos((int)Math.Round(sc.X), (int)Math.Round(sc.Y));
                    PumpFrames(200);
                    MouseEvent(0x0002, 0, 0, 0);
                    PumpFrames(90);
                    MouseEvent(0x0004, 0, 0, 0);
                    PumpFrames(800);
                    lines.Add($"click@{frac:0.##} screen={sc.X:0},{sc.Y:0} posMs {before:0} -> {ctl.RawPositionMs:0} value {vb:0.###} -> {slider.Value:0.###} state={ctl.PlaybackState}");
                }
                ClickAt(0.4);
                ClickAt(0.75);
                ClickAt(0.2);
                {
                    var from = win.PointToScreen(new Point(r.X + r.Width * 0.3, r.Y + r.Height / 2));
                    var to = win.PointToScreen(new Point(r.X + r.Width * 0.6, r.Y + r.Height / 2));
                    double before = ctl.RawPositionMs;
                    SetCursorPos((int)Math.Round(from.X), (int)Math.Round(from.Y));
                    PumpFrames(200);
                    MouseEvent(0x0002, 0, 0, 0);
                    PumpFrames(120);
                    for (int i = 1; i <= 6; i++)
                    {
                        SetCursorPos((int)Math.Round(from.X + (to.X - from.X) * i / 6.0), (int)Math.Round(from.Y));
                        PumpFrames(70);
                    }
                    MouseEvent(0x0004, 0, 0, 0);
                    PumpFrames(800);
                    lines.Add($"drag 0.3->0.6 posMs {before:0} -> {ctl.RawPositionMs:0} value={slider.Value:0.###}");
                }
            }
            File.WriteAllLines(logPath, lines);
            win.Close();
            return;
        }
        if (args.Contains("--update-feed-probe"))
        {
            string probe = Arg(args, "--update-feed-probe") ?? "update-feed.txt";
            var report = "baked=[" + Services.UpdateService.BakedFeed + "]\n" +
                         "resolved-empty=[" + Services.UpdateService.ResolveFeed("") + "]\n" +
                         "resolved-user=[" + Services.UpdateService.ResolveFeed("http://example/x") + "]\n" +
                         "dir=[" + AppContext.BaseDirectory + "]";
            File.WriteAllText(probe, report);
            Console.WriteLine(report);
            return;
        }
        if (args.Contains("--layout"))
        {
            string outPath = Arg(args, "--layout") ?? "layout.txt";
            var report = "root=[" + Services.Layout.Root + "]\n" +
                         "base=[" + Services.Layout.BaseDir + "]\n" +
                         "versioned=[" + Services.Layout.IsVersioned + "]\n" +
                         "current=[" + (Services.VersionStore.Current() ?? "(none)") + "]\n" +
                         "appVersion=[" + Services.UpdateService.CurrentVersion + "]\n" +
                         "launcher=[" + Services.Layout.LauncherPath + " launcherExists=" + File.Exists(Services.Layout.LauncherPath) + "]\n" +
                         "data=[" + AppPaths.DataDir + "]\n" +
                         "feeds=[" + string.Join(" | ", Services.UpdateService.ResolveFeeds(settings.UpdateFeed)) + "]\n" +
                         "hmacKey=[" + (Services.VersionStore.HmacKey() == null ? "(none)" : "set") + "]\n" +
                         "installed=[" + string.Join(", ", Services.VersionStore.Installed()) + "]";
            File.WriteAllText(outPath, report);
            Console.WriteLine(report);
            return;
        }
        if (args.Contains("--install-version"))
        {
            // QA：走完整的“检查 → 验签 → 下载 → 装版本 → 切指针”，但不重启程序。
            string feedArg = Arg(args, "--install-version") ?? "auto";
            string outPath = Arg(args, "--install-log") ?? "install-version.txt";
            var lines = new List<string>();
            try
            {
                var feeds = feedArg.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? Services.UpdateService.ResolveFeeds(settings.UpdateFeed)
                    : new List<string> { feedArg };
                lines.Add("feeds=[" + string.Join(" | ", feeds) + "]");
                var (manifest, hit) = Services.UpdateService.CheckAnyAsync(feeds).GetAwaiter().GetResult();
                if (manifest == null)
                {
                    lines.Add("no manifest");
                }
                else
                {
                    lines.Add($"hit=[{hit}] version={manifest.Version} v={manifest.Number} sha={manifest.Sha256[..12]} sig={(manifest.Sig == null ? "none" : "present")}");
                    lines.Add("signature=" + Services.UpdateService.VerifySignature(manifest, Services.VersionStore.HmacKey()));
                    lines.Add("newer=" + Services.UpdateService.IsNewerThanCurrent(manifest));
                    if (Services.UpdateService.IsNewerThanCurrent(manifest))
                    {
                        var installed = Services.UpdateService.InstallAsync(hit, manifest).GetAwaiter().GetResult();
                        lines.Add("installed=" + installed);
                        lines.Add("pointer=" + Services.VersionStore.Current());
                    }
                }
            }
            catch (Exception ex)
            {
                lines.Add("failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            lines.Add("installed-list=[" + string.Join(", ", Services.VersionStore.Installed()) + "]");
            File.WriteAllLines(outPath, lines);
            Console.WriteLine(string.Join("\n", lines));
            return;
        }
        if (args.Contains("--update-check"))
        {
            var feed = Arg(args, "--update-check")!;
            if (feed.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var candidates = Services.UpdateService.ResolveFeeds(settings.UpdateFeed);
                var (hit, used) = Services.UpdateService.CheckAnyAsync(candidates).GetAwaiter().GetResult();
                var detail = "candidates=[" + string.Join(" | ", candidates) + "]\n" +
                             "hit=[" + used + "]\n" +
                             (hit == null
                                 ? "none"
                                 : "latest=" + hit.Version + " current=" + Services.UpdateService.CurrentVersion +
                                   " newer=" + Services.UpdateService.IsNewer(hit.Version, Services.UpdateService.CurrentVersion) +
                                   " size=" + hit.Size + " sha=" + hit.Sha256[..12]);
                Console.WriteLine(detail);
                File.WriteAllText(Arg(args, "--update-log") ?? "update-check.txt", detail);
                return;
            }
            var m = Services.UpdateService.CheckAsync(feed).GetAwaiter().GetResult();
            var line = m == null
                ? $"no manifest at {feed} (reason={Services.UpdateService.LastFailure ?? "-"})"
                : $"feed={feed} latest={m.Version} current={Services.UpdateService.CurrentVersion} newer={Services.UpdateService.IsNewer(m.Version, Services.UpdateService.CurrentVersion)} size={m.Size} sha={m.Sha256[..12]}";
            Console.WriteLine(line);
            File.WriteAllText(Arg(args, "--update-log") ?? "update-check.txt", line);
            return;
        }
        if (args.Contains("--update-apply"))
        {
            // 完整更新链：读清单 → 下载/校验 → 交给更新器替换本目录 → 退出。
            string feed = Arg(args, "--update-apply")!;
            var manifest = Services.UpdateService.CheckAsync(feed).GetAwaiter().GetResult();
            if (manifest == null)
            {
                File.WriteAllText(Arg(args, "--update-log") ?? "update-apply.txt", $"no manifest at {feed}");
                return;
            }
            string zip;
            try
            {
                zip = Services.UpdateService.FetchAsync(feed, manifest).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Arg(args, "--update-log") ?? "update-apply.txt",
                    $"fetch failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            File.WriteAllText(Arg(args, "--update-log") ?? "update-apply.txt",
                $"apply {Services.UpdateService.CurrentVersion} -> {manifest.Version} zip={zip}");
            Services.UpdateService.ApplyAndRestart(zip);
            Environment.Exit(0);
            return;
        }
        if (args.Contains("--perf-drag"))
        {
            // Times a synthetic slider drag in both settings surfaces: the per-tick cost of the
            // change handlers plus the frame cadence while dragging.
            string path = Arg(args, "--perf-drag") ?? "perfdrag.txt";
            var pctl = new PlayerController(settings);
            var pwin = new MainWindow(pctl)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 20,
                Top = 20,
                Width = 1500,
                Height = 950,
            };
            pwin.Show();
            PumpFrames(500);
            RunBlocking(app, pctl.ShowLibraryAsync());
            PumpFrames(600);
            if (pctl.Songs.Count > 0)
            {
                pctl.Playback.SetVolume(0);
                RunBlocking(app, pctl.PlayAsync(pctl.Songs[0]));
                PumpFrames(1600);
            }
            var lines = new List<string>();

            string Drag(string tag, System.Windows.Controls.Slider s, int steps = 45)
            {
                double a = s.Minimum, b = s.Maximum;
                double lo = a + (b - a) * 0.25, hi = a + (b - a) * 0.75;
                s.Value = lo;
                PumpFrames(120);
                pwin.StartFrameMonitor(6000);
                double sum = 0, worst = 0;
                var sw = new System.Diagnostics.Stopwatch();
                for (int i = 0; i <= steps; i++)
                {
                    sw.Restart();
                    s.Value = lo + (hi - lo) * i / steps;
                    sw.Stop();
                    sum += sw.Elapsed.TotalMilliseconds;
                    if (sw.Elapsed.TotalMilliseconds > worst) worst = sw.Elapsed.TotalMilliseconds;
                    PumpFrames(14);
                }
                PumpFrames(400);
                return $"{tag,-22} setAvg={sum / (steps + 1),6:0.00}ms setWorst={worst,6:0.00}ms | {pwin.DebugFrameStats()}";
            }

            pwin.DebugOpenPlayerPage();
            PumpFrames(800);
            var page = pwin.PlayerView;
            if (page == null)
            {
                lines.Add("no player page");
            }
            else
            {
                page.DebugOpenMoreMenu();
                PumpFrames(400);
                var menuSliders = Collect<System.Windows.Controls.Slider>(page).Where(s => s.IsVisible).ToList();
                lines.Add($"player-settings sliders={menuSliders.Count}");
                for (int i = 0; i < menuSliders.Count; i++)
                    lines.Add(Drag($"player[{i}] {menuSliders[i].Minimum:0.#}..{menuSliders[i].Maximum:0.#}", menuSliders[i]));
            }

            pwin.DebugOpenDesktopSettings();
            PumpFrames(900);
            var dw = pwin.DebugSettingsWindow;
            if (dw == null)
            {
                lines.Add("no settings window");
            }
            else
            {
                foreach (var name in new[] { "sldFont", "sldGap", "sldTilt", "sldGlowStrength", "sldLines" })
                {
                    if (dw.FindName(name) is System.Windows.Controls.Slider ds)
                        lines.Add(Drag("desktop:" + name, ds, 30));
                }
            }
            File.WriteAllLines(path, lines);
            try { dw?.Close(); } catch { }
            pwin.Close();
            return;
        }
        if (args.Contains("--drop-cases"))
        {
            // Drops that do not change the order (released in the blank area under the list, dropped
            // back into its own slot, cancelled). The lifted row must come back every time.
            string path = Arg(args, "--drop-cases") ?? "dropcases.txt";
            var dctl = new PlayerController(settings);
            var dwin = new MainWindow(dctl)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 40,
                Top = 40,
                Width = 1100,
                Height = 820,
            };
            dwin.Show();
            PumpFrames(400);
            RunBlocking(app, dctl.ShowLibraryAsync());
            PumpFrames(700);
            var report = new List<string>();
            void RunCase(string name, int index, int slot, bool cancel)
            {
                report.Add($"== {name}");
                report.Add("  order-before=" + dwin.DebugOrder());
                report.Add("  before " + dwin.DebugRowVisibility());
                dwin.PrepareDropAtForQa(index, slot);
                PumpFrames(220);
                report.Add("  lifted " + dwin.DebugRowVisibility() + " " + dwin.DebugDropState());
                report.Add("  drop-> " + (cancel ? "cancel" : dwin.SimulateDropForQa()));
                if (cancel) dwin.SimulateDropCancelForQa();
                PumpFrames(900);
                report.Add("  after  " + dwin.DebugRowVisibility() + " " + dwin.DebugDropState());
                report.Add("  order-after =" + dwin.DebugOrder());
            }
            int last = Math.Max(0, dctl.Songs.Count - 1);
            RunCase("drop back into its own slot", 0, 0, false);
            RunCase("drop just below itself (slot 1)", 1, 2, false);
            RunCase("drop last row into blank space under list", last, dctl.Songs.Count, false);
            RunCase("cancel with Esc", 2, 4, true);
            File.WriteAllLines(path, report);
            dwin.Close();
            return;
        }
        if (args.Contains("--drop-screen"))
        {
            // Drop with the real render loop running: each wait is a nested dispatcher frame, so the
            // animation clock advances and the frame timings mean something.
            string dir2 = Arg(args, "--drop-screen") ?? "dropscreen";
            Directory.CreateDirectory(dir2);
            var ctl = new PlayerController(settings);
            var win = new MainWindow(ctl);
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = 40;
            win.Top = 40;
            win.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            RunBlocking(app, ctl.ShowLibraryAsync());
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(500);

            var origin = win.PointToScreen(new Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(win);
            int pw = (int)Math.Round(win.ActualWidth * dpi.DpiScaleX);
            int ph = (int)Math.Round(win.ActualHeight * dpi.DpiScaleY);
            var log = new List<string>();
            void Snap(string tag)
            {
                using (var bmp = new System.Drawing.Bitmap(pw, ph))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen((int)origin.X, (int)origin.Y, 0, 0, new System.Drawing.Size(pw, ph));
                    bmp.Save(Path.Combine(dir2, tag + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                log.Add(tag + " " + win.DebugDropState() + " | " + win.DebugRowVisibility() + " | " + win.DebugFrameStats());
            }
            void Wait(int ms)
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
                    (s, e) => { ((DispatcherTimer)s!).Stop(); frame.Continue = false; }, app.Dispatcher);
                Dispatcher.PushFrame(frame);
            }

            win.PrepareDropForQa(260);
            Wait(200);
            Snap("s00-lifted");
            win.SimulateDropForQa();
            Wait(70); Snap("s01-70ms");
            Wait(110); Snap("s02-180ms");
            Wait(160); Snap("s03-340ms");
            Wait(260); Snap("s04-600ms");
            File.WriteAllLines(Path.Combine(dir2, "frames.txt"), log);
            win.Close();
            return;
        }
        if (args.Contains("--drop-burst"))
        {
            string burstDir = Arg(args, "--drop-burst") ?? "burst";
            Directory.CreateDirectory(burstDir);
            var burstController = new PlayerController(settings);
            var burstWindow = new MainWindow(burstController);
            burstWindow.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            RunBlocking(app, burstController.ShowLibraryAsync());
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(400);
            burstWindow.PrepareDropForQa(200);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            for (int frame = 0; frame < 10; frame++)
            {
                if (frame == 2) burstWindow.SimulateDropForQa();
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                Save(burstWindow, Path.Combine(burstDir, $"f{frame:00}.png"));
                Thread.Sleep(35);
            }
            burstWindow.Close();
            return;
        }
        if (args.Contains("--drag-shot"))
        {
            string dragPath = Arg(args, "--drag-shot") ?? "drag.png";
            var dragController = new PlayerController(settings);
            var dragWindow = new MainWindow(dragController);
            dragWindow.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            RunBlocking(app, dragController.ShowLibraryAsync());
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(400);
            dragWindow.RunDragVisualTest(330);
            Thread.Sleep(800);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Save(dragWindow, dragPath);
            dragWindow.Close();
            return;
        }
        if (args.Contains("--anim-test"))
        {
            var animController = new PlayerController(settings);
            var animWindow = new MainWindow(animController);
            animWindow.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            RunBlocking(app, animController.ShowLibraryAsync());
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(500);
            var report = animWindow.RunDropAnimationTest(0, 2);
            System.IO.File.WriteAllText(Arg(args, "--anim-test") ?? "anim.txt", report);
            animWindow.Close();
            return;
        }
        if (args.Contains("--lrc-dump"))
        {
            // QA: prints the parsed cues of one LRC (count, start time, text/translation), which is
            // how "a line went missing" gets diagnosed without guessing.
            string lrcPath = Arg(args, "--lrc") ?? "";
            var doc = LrcParser.LoadFor(lrcPath);
            var dump = new List<string>();
            if (doc == null) dump.Add("parse failed");
            else
            {
                dump.Add($"file={Path.GetFileName(lrcPath)}");
                dump.Add($"cues={doc.Lines.Count} translationOnly={doc.Lines.Count(l => l.TranslationOnly)}");
                for (int i = 0; i < Math.Min(12, doc.Lines.Count); i++)
                {
                    var l = doc.Lines[i];
                    dump.Add($"{i,2} {l.StartMs,7}ms words={l.Words.Count,3} tr={(l.TranslationOnly ? "Y" : "-")} " +
                             $"[{l.Text}]" + (l.Translation.Length > 0 ? $" / [{l.Translation}]" : ""));
                }
            }
            File.WriteAllLines(Arg(args, "--report") ?? "lrc-dump.txt", dump);
            return;
        }
        if (args.Contains("--overlay-qa"))
        {
            // Renders the desktop overlay's own visual tree to a PNG (no screen capture), so a blank
            // overlay can be told apart from a transparent one.
            string qaDir = Arg(args, "--overlay-qa") ?? ".";
            Directory.CreateDirectory(qaDir);
            var qaController = new PlayerController(settings);
            var (qaDocContent, qaSongObj) = QaContent();
            qaController.LoadDemo(qaDocContent, qaSongObj);
            var qaWindow = new DesktopLyricsWindow(qaController, settings);
            qaWindow.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var notes = new List<string>();
            var qaPositions = new List<long>();
            if (long.TryParse(Arg(args, "--pos"), out var onePos)) qaPositions.Add(onePos);
            if (qaPositions.Count == 0) qaPositions.AddRange(new long[] { 17500, 60000 });
            foreach (var pos in qaPositions)
            {
                qaWindow.SetDemoPosition(pos);
                PumpFrames(400);
                qaWindow.RenderPreview(Path.Combine(qaDir, $"overlay-{pos}.png"));
                notes.Add($"pos={pos} lines={qaController.Lyrics?.Lines.Count ?? -1} " +
                          $"renders={qaWindow.DebugRenderCount} last={qaWindow.DebugLastRenderMs:0.00}ms " +
                          $"plane={qaController.Lyrics == null} {qaWindow.DebugStackState} {qaWindow.DebugInkState}");
            }
            File.WriteAllLines(Path.Combine(qaDir, "overlay.txt"), notes);
            qaWindow.Close();
            return;
        }
        if (args.Contains("--overlay-live"))
        {
            // The real sequence: a track is already loaded, then the overlay window is created (the
            // user switches desktop lyrics on mid-song). Creating the window *after* the lyrics is
            // what used to leave the column permanently empty, so this is the regression case.
            string liveDir = Arg(args, "--overlay-live") ?? ".";
            Directory.CreateDirectory(liveDir);
            var liveController = new PlayerController(settings);
            var (liveDoc, liveSong) = QaContent();
            liveController.LoadDemo(liveDoc, liveSong);
            var liveWindow = new DesktopLyricsWindow(liveController, settings);
            liveWindow.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            PumpFrames(1200);
            liveWindow.CaptureScreen(Path.Combine(liveDir, "live.png"));
            File.WriteAllLines(Path.Combine(liveDir, "live.txt"), new[]
            {
                $"lines={liveController.Lyrics?.Lines.Count ?? -1} {liveWindow.DebugInkState}",
                liveWindow.DebugStackState,
                liveWindow.DebugPaintState,
                liveWindow.DebugDocumentState,
            });
            liveWindow.Close();
            return;
        }
        if (args.Contains("--fps-test"))
        {
            // Runs the overlay under the same 16 ms tick the app uses, then reports how many times
            // WPF actually repainted the column (that is the real frame rate, not the tick rate).
            int seconds = int.TryParse(Arg(args, "--seconds"), out var secs) ? secs : 5;
            int startPos = int.TryParse(Arg(args, "--pos"), out var posMs) ? posMs : 17500;
            var fpsController = new PlayerController(settings);
            fpsController.LoadDemo(LrcParser.Demo(), DemoCover());
            var fpsWindow = new DesktopLyricsWindow(fpsController, settings);
            fpsWindow.Show();
            fpsWindow.SetDemoPosition(startPos);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(500);
            fpsWindow.ResetRenderStats();

            int ticks = 0;
            double lastTickMs = 0, tickSum = 0;
            var intervals = new List<double>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string mode = Arg(args, "--mode") ?? "timer";
            DispatcherTimer? timer = null;
            EventHandler? renderHandler = null;
            void Advance()
            {
                long now = clock.ElapsedMilliseconds;
                if (ticks > 0) { double dt = now - lastTickMs; tickSum += dt; intervals.Add(dt); }
                lastTickMs = now;
                ticks++;
                fpsWindow.DemoAdvance(startPos + now);
            }
            if (mode == "render")
            {
                // Frame-synced: one update per compositor frame instead of a timer racing the render.
                renderHandler = (_, _) => Advance();
                System.Windows.Media.CompositionTarget.Rendering += renderHandler;
            }
            else if (mode == "composite")
            {
                // Measure what the overlay itself will get: distinct compositor frames per second.
                fpsWindow.ResetFrameCount();
            }
            else
            {
                timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
                timer.Tick += (_, _) => Advance();
                timer.Start();
            }
            // Pump through a real dispatcher frame so the measurement is not skewed by a polling loop.
            var frame = new DispatcherFrame();
            var stopTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(seconds * 1000),
            };
            stopTimer.Tick += (_, _) =>
            {
                stopTimer.Stop();
                frame.Continue = false;
            };
            stopTimer.Start();
            Dispatcher.PushFrame(frame);
            timer?.Stop();
            if (renderHandler != null) System.Windows.Media.CompositionTarget.Rendering -= renderHandler;
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Thread.Sleep(150);

            double elapsed = clock.Elapsed.TotalSeconds;
            long renders = fpsWindow.DebugRenderCount;
            double msTotal = fpsWindow.DebugRenderMsTotal;
            File.WriteAllLines(Arg(args, "--report") ?? "fps.txt", new[]
            {
                $"mode={mode} hardware={(Environment.GetEnvironmentVariable("LYRIC_HW") == "1")}",
                $"seconds={elapsed:0.00}",
                $"ticks={ticks}  tick_rate={(ticks / elapsed):0.0}/s  avg_tick_interval={(ticks > 1 ? tickSum / (ticks - 1) : 0):0.00}ms",
                intervals.Count > 0
                    ? $"tick_interval min={intervals.Min():0.0} p50={intervals.OrderBy(v => v).ElementAt(intervals.Count / 2):0.0} max={intervals.Max():0.0}"
                    : "tick_interval n/a",
                $"renders={renders}  fps={(renders / elapsed):0.0}/s",
                $"compositor_frames={fpsWindow.FrameCount}  compositor_fps={(fpsWindow.FrameCount / elapsed):0.0}/s",
                $"draw_ms_avg={(renders > 0 ? msTotal / renders : 0):0.00}  draw_ms_total={msTotal:0}",
            });
            fpsWindow.Close();
            return;
        }
        if (args.Contains("--motion-screen"))
        {
            // Same as --motion-test but captures the *real screen*, so "does it glide on the desktop"
            // is answered by pixels instead of by the stack's internal numbers.
            string shotDir = Arg(args, "--motion-screen") ?? "motion-screen";
            Directory.CreateDirectory(shotDir);
            int start = int.TryParse(Arg(args, "--pos"), out var startMs) ? startMs : 18200;
            int span = int.TryParse(Arg(args, "--span"), out var spanMs) ? spanMs : 1400;
            var motionController = new PlayerController(settings);
            var (motionDoc, motionSong) = QaContent();
            motionController.LoadDemo(motionDoc, motionSong);
            var motionWindow = new DesktopLyricsWindow(motionController, settings);
            motionWindow.Show();
            motionWindow.SetDemoPosition(start);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(400);
            // Window geometry is in DIPs; CopyFromScreen wants device pixels, so every rect has to be
            // multiplied by the window's DPI scale or the capture lands 25% off on a 125% display.
            double shotScale = 1.0;
            try { shotScale = VisualTreeHelper.GetDpi(motionWindow).DpiScaleX; } catch { }
            var rows = new List<string> { "t_ms\twindow_x\twindow_y\tstack_state" };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int frame = 0;
            while (clock.ElapsedMilliseconds <= span)
            {
                motionWindow.DemoAdvance(start + clock.ElapsedMilliseconds);
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                int wx = (int)Math.Round(motionWindow.Left * shotScale);
                int wy = (int)Math.Round(motionWindow.Top * shotScale);
                int ww = (int)Math.Round(motionWindow.ActualWidth * shotScale);
                int wh = (int)Math.Round(motionWindow.ActualHeight * shotScale);
                using (var bmp = new System.Drawing.Bitmap(Math.Max(1, ww), Math.Max(1, wh)))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(wx, wy, 0, 0, new System.Drawing.Size(ww, wh));
                    bmp.Save(Path.Combine(shotDir, $"f{frame:000}.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                rows.Add($"{clock.ElapsedMilliseconds}\t{wx}\t{wy}\t{motionWindow.DebugMotion}");
                frame++;
                Thread.Sleep(16);
            }
            File.WriteAllLines(Path.Combine(shotDir, "motion.txt"), rows);
            motionWindow.Close();
            return;
        }
        if (args.Contains("--motion-test"))
        {
            // Ticks the real stack through a cue change in real time and logs the animation state,
            // so "does the column actually glide" is a number instead of an opinion.
            int start = int.TryParse(Arg(args, "--pos"), out var startMs) ? startMs : 8000;
            int span = int.TryParse(Arg(args, "--span"), out var spanMs) ? spanMs : 1600;
            var motionController = new PlayerController(settings);
            motionController.LoadDemo(LrcParser.Demo(), DemoCover());
            var motionWindow = new DesktopLyricsWindow(motionController, settings);
            motionWindow.Show();
            motionWindow.SetDemoPosition(start);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var rows = new List<string> { "t_ms\tstack_state" };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds <= span)
            {
                motionWindow.DemoAdvance(start + clock.ElapsedMilliseconds);
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                rows.Add($"{clock.ElapsedMilliseconds}\t{motionWindow.DebugMotion}");
                Thread.Sleep(16);
            }
            File.WriteAllLines(Arg(args, "--report") ?? "motion.txt", rows);
            motionWindow.Close();
            return;
        }
        if (args.Contains("--glyph-bench"))
        {
            int iterations = int.TryParse(Arg(args, "--n"), out var n) ? n : 120;
            var benchController = new PlayerController(settings);
            benchController.LoadDemo(LrcParser.Demo(), DemoCover());
            var benchWindow = new DesktopLyricsWindow(benchController, settings);
            benchWindow.Show();
            benchWindow.SetDemoPosition(16000);
            benchWindow.RefreshLyrics();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(600);
            string bench;
            try { bench = benchWindow.BenchGlyphs(iterations); } catch (Exception ex) { bench = "failed " + ex.Message; }
            File.WriteAllLines(Arg(args, "--report") ?? "glyph-bench.txt", new[]
            {
                $"iterations={iterations}",
                bench,
            });
            benchWindow.Close();
            return;
        }
        if (args.Contains("--flat-only"))
        {
            // Paints the whole desktop one flat colour and holds it, so a second process (an older
            // build, for A/B comparison) can be screenshotted against a known background.
            int hold = int.TryParse(Arg(args, "--hold"), out var holdMs) ? holdMs : 20000;
            var flatWindow = CreateFlatWindow(Arg(args, "--flat") ?? "000000");
            flatWindow.Show();
            var holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(hold) };
            holdTimer.Tick += (_, _) =>
            {
                holdTimer.Stop();
                flatWindow.Close();
                app.Shutdown();
            };
            holdTimer.Start();
            app.Run();
            return;
        }
        if (args.Contains("--foreground-test"))
        {
            string outPath = Arg(args, "--foreground-test") ?? "foreground.png";
            int waitMs = int.TryParse(Arg(args, "--wait"), out var w) ? w : 9000;

            // Optional known-flat desktop behind the overlay: makes the alpha ramp measurable from
            // the screenshot alone (screen = alpha * colour + (1 - alpha) * flat).
            Window? flat = null;
            string flatHex = Arg(args, "--flat") ?? "";
            if (flatHex.Length >= 6)
            {
                flat = CreateFlatWindow(flatHex);
                flat.Show();
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }

            var fgController = new PlayerController(settings);
            var fgWindow = new DesktopLyricsWindow(fgController, settings);
            fgWindow.Show();
            fgController.LoadDemo(LrcParser.Demo(), DemoCover());
            fgWindow.SetDemoPosition(16000);
            fgWindow.RefreshLyrics();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(Math.Max(1500, waitMs - 2000));

            // Force the overlay visible regardless of focus, then grab the *whole* desktop so the
            // layered window is composited over the real wallpaper.
            fgWindow.ShowOverlayForTest();
            // Re-assert topmost so the overlay sits above the flat surface even though both are
            // topmost windows (the flat one is created first).
            fgWindow.Topmost = false;
            fgWindow.Topmost = true;
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(1200);

            var bounds = new System.Drawing.Rectangle(
                (int)SystemParameters.VirtualScreenLeft, (int)SystemParameters.VirtualScreenTop,
                (int)SystemParameters.VirtualScreenWidth, (int)SystemParameters.VirtualScreenHeight);
            using (var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(bounds.Width, bounds.Height));
                bitmap.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            fgWindow.Close();
            flat?.Close();
            return;
        }
        if (args.Contains("--fade-live-test"))
        {
            string dir = Arg(args, "--fade-live-test") ?? "fade-live";
            Directory.CreateDirectory(dir);
            settings.Enabled = true;
            settings.BackdropMode = BackdropMode.Image;
            if (string.IsNullOrWhiteSpace(settings.BackdropImage) || !File.Exists(settings.BackdropImage))
            {
                // QA only: any jpg in the user's Music folder is enough to judge the fade.
                string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                settings.BackdropImage = Directory.Exists(music)
                    ? Directory.GetFiles(music, "*.jpg", SearchOption.AllDirectories).FirstOrDefault() ?? ""
                    : "";
            }
            settings.BackdropFadeEnabled = true;
            settings.BackdropFadeStrength = 100;
            var liveController = new PlayerController(settings);
            liveController.LoadDemo(LrcParser.Demo(), DemoCover());
            var liveWindow = new DesktopLyricsWindow(liveController, settings);
            liveWindow.Show();
            liveWindow.SetDemoPosition(16000);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var liveSettings = new SettingsWindow(settings, s => liveWindow.ApplySettings(s));
            liveSettings.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var slider = liveSettings.FindName("sldFadeH") as System.Windows.Controls.Slider;
            var report = new List<string> { "slider found=" + (slider != null) };
            if (slider != null)
            {
                foreach (var value in new[] { 3.0, 20.0 })
                {
                    slider.Value = value;
                    app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    Thread.Sleep(250);
                    var shot = Path.Combine(dir, $"h{(int)value}.png");
                    liveWindow.RenderPreview(shot);
                    report.Add($"slider={value} settingsFadeH={settings.BackdropFadeH} {liveWindow.DebugStackState} -> {shot}");
                }
            }
            File.WriteAllLines(Path.Combine(dir, "report.txt"), report);
            liveSettings.Close();
            liveWindow.Close();
            return;
        }
        if (args.Contains("--glow-picker"))
        {
            // 用户真实路径：设置窗口 → 点「辉光颜色」色块 → 在调色板里拖 → 歌词辉光必须当场变色。
            // 全程用真实鼠标消息进窗口，因为「颜色是不是实时演示」只能靠事件真的走到 App 的
            // apply 回调来证明；最后再验「取消要退回原色」「确定才写盘」。
            string dir = Arg(args, "--glow-picker") ?? "glow-picker";
            Directory.CreateDirectory(dir);
            var applied = new List<string>();
            var only = new SettingsWindow(settings, s => applied.Add(s.GlowColor));
            only.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(240);

            var hwnd = new System.Windows.Interop.WindowInteropHelper(only).Handle;
            var dpi = VisualTreeHelper.GetDpi(only);
            var lines = new List<string> { $"start: GlowColor={settings.GlowColor} applies={applied.Count}" };

            static System.Windows.Controls.Button? FindButton(DependencyObject root, string text)
            {
                if (root is System.Windows.Controls.Button b && (b.Content as string) == text) return b;
                int n = VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < n; i++)
                {
                    var hit = FindButton(VisualTreeHelper.GetChild(root, i), text);
                    if (hit != null) return hit;
                }
                return null;
            }

            void Click(Point spot)
            {
                PostMouse(hwnd, spot, dpi, 0x0201, 1);
                PostMouse(hwnd, spot, dpi, 0x0202, 0);
                Pump(app, 160);
            }

            var host = only.FindName("glowColorHost") as System.Windows.Controls.ContentControl;
            string atOpen = settings.GlowColor;
            System.Windows.Controls.Button? swatch = only.FindName("btnGlowColor") as System.Windows.Controls.Button;
            if (swatch != null)
            {
                // 那个色块在滚动区靠下、合成点击有时落在固定底栏上；用同一条 Click 处理器打开面板，
                // 面板内部才是要验的部分（面板里的每一次拖动都走真实鼠标消息）。
                only.DebugOpenGlowColorPicker();
                Pump(app, 260);
                host?.BringIntoView();
                Pump(app, 260);
                lines.Add($"picker opened: panel={(host?.Content?.GetType().Name ?? "null")}");
            }
            else lines.Add("swatch: missing");

            // 面板里的鼠标手势没法用合成消息驱动：ColorPickerPanel 的拖动处理器要求
            // e.LeftButton == Pressed，而这个进程收到的 PostMessage 不会让 WPF 认为左键按住
            // （滑块闸门不看按键状态，所以那边能过）。所以这里点「命中测试 + 真事件」分开验：
            // 坐标确实落在面板上（InputHitTest），颜色变化走面板自己的 TextChanged → ColorChanged。
            if (host?.Content is FrameworkElement panelEl)
            {
                int before = applied.Count;
                var p0 = panelEl.TranslatePoint(new Point(panelEl.ActualWidth * 0.8, 130), only);
                var hit = only.InputHitTest(p0) as FrameworkElement;
                lines.Add($"panel={panelEl.ActualWidth:0}x{panelEl.ActualHeight:0} probe={p0.X:0},{p0.Y:0} " +
                          $"hit={hit?.GetType().Name ?? "null"} window={only.ActualWidth:0}x{only.ActualHeight:0}");

                if (FindDescendant<System.Windows.Controls.TextBox>(panelEl) is { } hexBox)
                {
                    hexBox.Text = "FF0000";
                    Pump(app, 240);
                    lines.Add($"live recolour (hex field): GlowColor={settings.GlowColor} " +
                              $"applies+={applied.Count - before} applied=[{string.Join(",", applied.Skip(before))}]");
                    Save(only, Path.Combine(dir, "picker-open.png"));
                }
                else lines.Add("hex field: missing");

                if (FindButton(panelEl, "取消") is { } cancel)
                {
                    cancel.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Pump(app, 220);
                    lines.Add($"cancel: GlowColor={settings.GlowColor} restored={(settings.GlowColor == atOpen ? 1 : 0)} " +
                              $"panel={(host?.Content?.GetType().Name ?? "null")}");
                }
                else lines.Add("cancel: missing");

                // 确定路径：重开、改色、确定
                only.DebugOpenGlowColorPicker();
                Pump(app, 240);
                host?.BringIntoView();
                Pump(app, 200);
                if (host?.Content is FrameworkElement again &&
                    FindDescendant<System.Windows.Controls.TextBox>(again) is { } hexAgain)
                {
                    hexAgain.Text = "00FF00";
                    Pump(app, 240);
                    string previewed = settings.GlowColor;
                    if (FindButton(again, "确定") is { } ok)
                    {
                        ok.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        Pump(app, 220);
                        lines.Add($"commit: previewed={previewed} committed={settings.GlowColor} " +
                                  $"panel={(host?.Content?.GetType().Name ?? "null")} applies={applied.Count}");
                    }
                    else lines.Add("ok: missing");
                }
                else lines.Add($"reopen: failed ({(host?.Content?.GetType().Name ?? "null")})");
            }
            File.WriteAllLines(Path.Combine(dir, "report.txt"), lines);
            only.Close();
            app.Shutdown();
            return;
        }
        if (args.Contains("--appearance-slider"))
        {
            // 界面外观窗口单独跑：它是这个进程里唯一的窗口，合成点击才真的会进窗口（和主 QA 不同）。
            string dir = Arg(args, "--appearance-slider") ?? "appearance-slider";
            Directory.CreateDirectory(dir);
            var only = new AppearanceWindow(settings, _ => { });
            only.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(220);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(only).Handle;
            var dpi = VisualTreeHelper.GetDpi(only);
            var rows = new List<string>();
            foreach (var name in new[] { "sldThemeOpacity", "sldThemeBlur" })
            {
                if (only.FindName(name) is not System.Windows.Controls.Slider slider) { rows.Add($"{name}: missing"); continue; }
                double before = slider.Value;
                Controls.DragOnlySlider.ResetCounters();
                var spot = slider.TranslatePoint(new Point(slider.ActualWidth * 0.85, slider.ActualHeight / 2), only);
                for (int i = 0; i < 3; i++)
                {
                    PostMouse(hwnd, spot, dpi, 0x0201, 1);
                    PostMouse(hwnd, spot, dpi, 0x0202, 0);
                    Pump(app, 90);
                }
                double afterClicks = slider.Value;
                var track = slider.Template?.FindName("PART_Track", slider) as System.Windows.Controls.Primitives.Track;
                if (track?.Thumb is { } thumb)
                {
                    thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(24, 0)
                    {
                        RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent,
                    });
                    Pump(app, 90);
                }
                rows.Add($"{name} [{slider.GetType().Name}] grooveX3 blocked={Controls.DragOnlySlider.BlockedPresses} " +
                         $"allowed={Controls.DragOnlySlider.AllowedPresses} valueDelta={afterClicks - before:+0.###;-0.###;0} | " +
                         $"dragEvent(+24) delta={slider.Value - afterClicks:+0.###;-0.###;0} final={slider.Value:0.###}");
            }
            File.WriteAllLines(Path.Combine(dir, "report.txt"), rows);
            only.Close();
            app.Shutdown();
            return;
        }
        if (args.Contains("--slider-click"))
        {
            // 用户报：点一下配置滑杆，参数会自己挪一小格。这里用真实消息（PostMessage 0x0201/0x0200/0x0202）
            // 先点三下轨道、再拖一次滑块，把「点击不该改值 / 拖动必须改值」变成数字。
            string dir = Arg(args, "--slider-click") ?? "slider-click";
            Directory.CreateDirectory(dir);
            var probeWindow = new SettingsWindow(settings, _ => { });
            probeWindow.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(220);

            var hwnd = new System.Windows.Interop.WindowInteropHelper(probeWindow).Handle;
            var dpi = VisualTreeHelper.GetDpi(probeWindow);
            var lines = new List<string>();
            foreach (var name in new[] { "sldFont", "sldTilt", "sldWidth" })
            {
                if (probeWindow.FindName(name) is not System.Windows.Controls.Slider slider)
                {
                    lines.Add($"{name}: missing");
                    continue;
                }
                double before = slider.Value;
                string kind = slider.GetType().Name;
                Controls.DragOnlySlider.ResetCounters();
                // three clicks at 85% of the groove — must not move the value at all
                var spot = slider.TranslatePoint(new Point(slider.ActualWidth * 0.85, slider.ActualHeight / 2), probeWindow);
                for (int i = 0; i < 3; i++)
                {
                    PostMouse(hwnd, spot, dpi, 0x0201, 1);
                    PostMouse(hwnd, spot, dpi, 0x0202, 0);
                    Pump(app, 90);
                }
                double afterClicks = slider.Value;
                int blocked = Controls.DragOnlySlider.BlockedPresses;
                int allowed = Controls.DragOnlySlider.AllowedPresses;

                // press exactly on the knob: the gate must let that press through to the Thumb
                var track = slider.Template?.FindName("PART_Track", slider) as System.Windows.Controls.Primitives.Track;
                var knob = track?.Thumb != null
                    ? track.Thumb.TranslatePoint(new Point(track.Thumb.ActualWidth / 2, track.Thumb.ActualHeight / 2), probeWindow)
                    : slider.TranslatePoint(new Point(slider.ActualWidth / 2, slider.ActualHeight / 2), probeWindow);
                PostMouse(hwnd, knob, dpi, 0x0201, 1);
                Pump(app, 80);
                int allowedOnKnob = Controls.DragOnlySlider.AllowedPresses;
                int blockedOnKnob = Controls.DragOnlySlider.BlockedPresses;
                for (int step = 1; step <= 6; step++)
                {
                    PostMouse(hwnd, new Point(knob.X + step * 12, knob.Y), dpi, 0x0200, 1);
                    Pump(app, 45);
                }
                PostMouse(hwnd, new Point(knob.X + 72, knob.Y), dpi, 0x0202, 0);
                Pump(app, 120);
                double afterDrag = slider.Value;

                // The synthetic mouse cannot hold a real button down, so the drag gesture itself is
                // replayed through the very event the Thumb raises while dragging.
                if (track?.Thumb is { } thumb)
                {
                    thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(28, 0)
                    {
                        RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent,
                    });
                    Pump(app, 90);
                }
                double afterDragEvent = slider.Value;

                lines.Add($"{name} [{kind}] min={slider.Minimum:0.##} max={slider.Maximum:0.##} " +
                          $"small={slider.SmallChange:0.##} large={slider.LargeChange:0.##} moveToPoint={slider.IsMoveToPointEnabled} | " +
                          $"grooveClicks x3: blocked={blocked} allowed={allowed} valueDelta={afterClicks - before:+0.###;-0.###;0} | " +
                          $"knobPress: blocked={blockedOnKnob - blocked} allowed={allowedOnKnob - allowed} | " +
                          $"drag: valueDelta={afterDrag - afterClicks:+0.###;-0.###;0} | " +
                          $"dragEvent(+28): valueDelta={afterDragEvent - afterDrag:+0.###;-0.###;0} final={afterDragEvent:0.###}");
                Save(probeWindow, Path.Combine(dir, name + ".png"));
            }
            File.WriteAllLines(Path.Combine(dir, "report.txt"), lines);

            // 界面外观窗口的滑块必须同样只能拖动（用户 06:00 又碰到一次）。它必须在设置窗口还开着
            // 的时候创建：先关掉唯一的窗口会触发 OnLastWindowClose，新窗口就永远不布局（0x0）。
            var appearance = new AppearanceWindow(settings, _ => { });
            appearance.Show();
            appearance.Activate();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(200);
            var aLines = new List<string>();
            var aHwnd = new System.Windows.Interop.WindowInteropHelper(appearance).Handle;
            var aDpi = VisualTreeHelper.GetDpi(appearance);
            foreach (var name in new[] { "sldThemeOpacity", "sldThemeBlur" })
            {
                if (appearance.FindName(name) is not System.Windows.Controls.Slider slider) { aLines.Add($"{name}: missing"); continue; }
                double before = slider.Value;
                Controls.DragOnlySlider.ResetCounters();
                var spot = slider.TranslatePoint(new Point(slider.ActualWidth * 0.85, slider.ActualHeight / 2), appearance);
                for (int i = 0; i < 3; i++)
                {
                    PostMouse(aHwnd, spot, aDpi, 0x0201, 1);
                    PostMouse(aHwnd, spot, aDpi, 0x0202, 0);
                    Pump(app, 90);
                }
                aLines.Add($"{name} [{slider.GetType().Name}] clicks: blocked={Controls.DragOnlySlider.BlockedPresses} " +
                           $"allowed={Controls.DragOnlySlider.AllowedPresses} valueDelta={slider.Value - before:+0.###;-0.###;0}" +
                           $" spot=({spot.X:0},{spot.Y:0}) slider={slider.ActualWidth:0}x{slider.ActualHeight:0} " +
                           $"window={appearance.ActualWidth:0}x{appearance.ActualHeight:0}");
            }
            File.WriteAllLines(Path.Combine(dir, "appearance.txt"), aLines);
            appearance.Close();
            probeWindow.Close();
            app.Shutdown();
            return;
        }
        if (args.Contains("--backdrop-bench"))
        {
            // 背景模糊的底图分辨率/耗时：用户报「背景图分辨率过低」。这里对同一张封面按指定尺寸
            // 跑 FrostToSize，记下每档模糊的耗时，并把结果存成 PNG 供锐度对比。
            string dir = Arg(args, "--backdrop-bench") ?? "backdrop-bench";
            Directory.CreateDirectory(dir);
            string cover = Arg(args, "--cover") ?? "";
            if (!File.Exists(cover))
            {
                File.WriteAllText(Path.Combine(dir, "report.txt"), "cover missing: " + cover);
                app.Shutdown();
                return;
            }
            var src = new System.Windows.Media.Imaging.BitmapImage();
            src.BeginInit();
            src.UriSource = new Uri(cover);
            src.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            src.EndInit();
            src.Freeze();
            int w = int.TryParse(Arg(args, "--w"), out var bw) ? bw : 584;
            int h = int.TryParse(Arg(args, "--h"), out var bh) ? bh : 890;
            var lines = new List<string> { $"cover={Path.GetFileName(cover)} {src.PixelWidth}x{src.PixelHeight} target={w}x{h}" };
            foreach (var radius in new[] { 10, 18, 42 })
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                System.Windows.Media.Imaging.BitmapSource? shot = null;
                const int runs = 3;
                for (int i = 0; i < runs; i++)
                {
                    // Opacity is part of the cache key; nudging it forces a real rebuild.
                    shot = Controls.BackdropBlur.FrostToSize(src, radius, w, h, 100, 9.4, 14, 0.42, 0.97 + i * 0.0001);
                }
                watch.Stop();
                if (shot != null)
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));
                    using var fs = File.Create(Path.Combine(dir, $"bg-r{radius}.png"));
                    encoder.Save(fs);
                }
                lines.Add($"radius={radius} avg_ms={watch.Elapsed.TotalMilliseconds / runs:0.0} bitmap={(shot?.PixelWidth ?? 0)}x{(shot?.PixelHeight ?? 0)}");
            }
            File.WriteAllLines(Path.Combine(dir, "report.txt"), lines);
            app.Shutdown();
            return;
        }
        if (args.Contains("--glow-audit"))
        {
            // 用户报「辉光效果没有了」。先别改代码：用**当前设置原样**渲一遍，再把同一个场景
            // 换成亮色渲一遍，两张「开/关」对比 —— 差值大小就是"看不看得见"的证据。
            string dir = Arg(args, "--glow-audit") ?? "glow-audit";
            Directory.CreateDirectory(dir);
            settings.Enabled = false;   // 不让叠加层窗口自己冒出来抢焦点
            var auditController = new PlayerController(settings);
            auditController.LoadDemo(LrcParser.Demo(), DemoCover());
            var auditWindow = new DesktopLyricsWindow(auditController, settings);
            auditWindow.Show();
            long auditPos = long.TryParse(Arg(args, "--pos"), out var auditPin) ? auditPin : 23900;
            auditWindow.SetDemoPosition(auditPos);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            for (int i = 0; i < 8; i++) Pump(app, 40);

            string originalColour = settings.GlowColor;
            string originalText = settings.TextColor;
            var report = new List<string>
            {
                $"settings: GlowEnabled={settings.GlowEnabled} strength={settings.GlowStrength:0.00} " +
                $"spread={settings.GlowSpread:0.00} color=#{originalColour} text=#{originalText} " +
                $"font={settings.DisplayFontSize:0} perspective={settings.Perspective}",
            };
            void Shot(string name)
            {
                for (int i = 0; i < 3; i++) auditWindow.DebugGlowCounts();
                auditWindow.RenderPreview(Path.Combine(dir, name + ".png"));
                report.Add($"{name}: {auditWindow.DebugGlowState} | {auditWindow.DebugGlowPixels()}");
            }

            Shot("keep-on");
            settings.GlowEnabled = false;
            auditWindow.ApplySettings(settings);
            Shot("keep-off");
            settings.GlowEnabled = true;

            // 同一个场景换亮色（文字色 = 用户自己的 #FCFBFB）
            settings.GlowColor = originalText;
            auditWindow.ApplySettings(settings);
            Shot("light-on");
            settings.GlowEnabled = false;
            auditWindow.ApplySettings(settings);
            Shot("light-off");
            settings.GlowEnabled = true;
            settings.GlowColor = originalColour;
            auditWindow.ApplySettings(settings);

            // 亮度：把颜色换算成相对亮度，方便说明"这个颜色本身有多暗"
            static double Luminance(string hex)
            {
                double Ch(int v)
                {
                    double c = v / 255.0;
                    return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
                }
                int r = Convert.ToInt32(hex[..2], 16), g = Convert.ToInt32(hex[2..4], 16), b = Convert.ToInt32(hex[4..], 16);
                return 0.2126 * Ch(r) + 0.7152 * Ch(g) + 0.0722 * Ch(b);
            }
            report.Add($"luminance: glow#{originalColour}={Luminance(originalColour):0.0000} " +
                       $"text#{originalText}={Luminance(originalText):0.0000}");
            File.WriteAllLines(Path.Combine(dir, "report.txt"), report);
            auditWindow.Close();
            app.Shutdown();
            return;
        }
        if (args.Contains("--end-advance"))
        {
            // 用户报：一首歌自然放完、自动切下一首时，播放条进度已经拉满才跳走（手动点歌却正常）。
            // 这里拿两个几秒长的 wav 真的放到结尾，把进度条/时长/当前曲目逐 100 ms 记下来。
            string dir = Arg(args, "--end-advance") ?? "end-advance";
            Directory.CreateDirectory(dir);
            var ctl = new PlayerController(settings);
            ctl.Playback.Volume = 0;
            var host = new MainWindow(ctl);
            host.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            string folder = Arg(args, "--folder") ?? "";
            RunBlocking(app, ctl.ShowLocalAsync(folder));
            var trace = new List<string>
            {
                "songs=" + ctl.Songs.Count + " [" + string.Join(" | ",
                    ctl.Songs.Select(s => s.DisplayTitle + " " + s.Duration.TotalSeconds.ToString("0.0") + "s")) + "]",
            };
            if (ctl.Songs.Count > 0)
            {
                RunBlocking(app, ctl.PlayAsync(ctl.Songs[0]));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                int limit = int.TryParse(Arg(args, "--ms"), out var lim) ? lim : 9000;
                while (clock.ElapsedMilliseconds < limit)
                {
                    Pump(app, 100);
                    trace.Add($"{clock.ElapsedMilliseconds,5}ms {host.DebugProgressBar()}");
                }
                Save(host, Path.Combine(dir, "bar.png"));
                // Selection must not paint a block any more: select a row and capture it.
                trace.Add("select row1: " + host.DebugSelectRow(1));
                for (int i = 0; i < 6; i++) Pump(app, 60);
                Save(host, Path.Combine(dir, "selected-row.png"));
                trace.Add($"openRetries={ctl.Playback.OpenRetries}");
            }
            File.WriteAllLines(Path.Combine(dir, "trace.txt"), trace);
            host.Close();
            app.Shutdown();
            return;
        }
        if (args.Contains("--text-live"))
        {
            // 真机复核「文字颜色改不动」：走设置窗口里色盘真正触发的那条路径（Preview → QueueApply →
            // overlay.ApplySettings），改一次色渲一张图，数字才是用户眼前的效果。
            string dir = Arg(args, "--text-live") ?? "text-live";
            Directory.CreateDirectory(dir);
            settings.Enabled = true;
            settings.BackdropMode = BackdropMode.Transparent;
            settings.TextColor = "FFFFFF";
            settings.GlowEnabled = false;
            if (args.Contains("--flat")) settings.Perspective = false;
            var tcController = new PlayerController(settings);
            tcController.LoadDemo(LrcParser.Demo(), DemoCover());
            var tcWindow = new DesktopLyricsWindow(tcController, settings);
            tcWindow.Show();
            tcWindow.SetDemoPosition(long.TryParse(Arg(args, "--pos"), out var tcLivePos) ? tcLivePos : 23900);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var tcSettingsWindow = new SettingsWindow(settings, s => tcWindow.ApplySettings(s));
            tcSettingsWindow.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            (int white, int red) Stats(string path)
            {
                var frame = new BitmapImage();
                frame.BeginInit();
                frame.CacheOption = BitmapCacheOption.OnLoad;
                frame.UriSource = new Uri(path);
                frame.EndInit();
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                int w = converted.PixelWidth, h = converted.PixelHeight;
                var px = new byte[w * h * 4];
                converted.CopyPixels(px, w * 4, 0);
                int white = 0, red = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                    if (a < 40) continue;
                    if (r > 150 && g < 90 && b < 90) red++;
                    else if (r > 190 && g > 190 && b > 190) white++;
                }
                return (white, red);
            }

            var tcReport = new List<string> { $"src=demo pos={(Arg(args, "--pos") ?? "23900")}" };
            foreach (var hex in new[] { "FFFFFF", "FF3B30", "4C8DFF" })
            {
                tcSettingsWindow.DebugPreviewTextColor(hex);
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Thread.Sleep(260);
                var file = Path.Combine(dir, $"text-{hex}.png");
                tcWindow.RenderPreview(file);
                tcWindow.CaptureScreen(Path.Combine(dir, $"desktop-{hex}.png"));
                var st = Stats(file);
                tcReport.Add($"preview=#{hex} settings.TextColor={settings.TextColor} white={st.white} red={st.red}");
            }
            File.WriteAllLines(Path.Combine(dir, "text-live.txt"), tcReport);
            tcSettingsWindow.Close();
            tcWindow.Close();
            return;
        }
        if (args.Contains("--glow-live"))
        {
            // 用户报「歌词辉光开与关没有区别」：这里真的去设置窗口里拨那个开关，并让桌面歌词按
            // 回调重新应用设置，前后各渲一张图 —— 差多少个像素就是开关到底有没有生效。
            string dir = Arg(args, "--glow-live") ?? "glow-live";
            Directory.CreateDirectory(dir);
            settings.Enabled = true;
            if (args.Contains("--flat")) settings.Perspective = false;
            var glowController = new PlayerController(settings);
            glowController.LoadDemo(LrcParser.Demo(), DemoCover());
            var glowWindow = new DesktopLyricsWindow(glowController, settings);
            glowWindow.Show();
            glowWindow.SetDemoPosition(double.TryParse(Arg(args, "--pos"), out var glowPos) ? (long)glowPos : 23900);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var glowSettings = new SettingsWindow(settings, s => glowWindow.ApplySettings(s));
            glowSettings.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var check = glowSettings.FindName("chkGlow") as System.Windows.Controls.CheckBox;
            var report = new List<string>
            {
                $"checkbox={check != null} perspective={settings.Perspective} tilt={settings.TiltAngle:0.0} " +
                $"spread={settings.GlowSpread:0.00} radiusScale={settings.GlowRadiusScale:0.000}",
            };
            foreach (var state in new[] { true, false })
            {
                if (check != null) check.IsChecked = state;
                else settings.GlowEnabled = state;
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Thread.Sleep(280);
                var shot = Path.Combine(dir, $"glow-{(state ? "on" : "off")}.png");
                glowWindow.RenderPreview(shot);
                // Also grab the real desktop under the layered window: same pixels the user sees.
                glowWindow.CaptureScreen(Path.Combine(dir, $"desktop-{(state ? "on" : "off")}.png"));
                report.Add($"toggle={state} settings.GlowEnabled={settings.GlowEnabled} " +
                           $"{glowWindow.DebugGlowState} -> {Path.GetFileName(shot)}");
            }
            File.WriteAllLines(Path.Combine(dir, "report.txt"), report);
            glowSettings.Close();
            glowWindow.Close();
            return;
        }
        if (args.Contains("--glow-color"))
        {
            // 用户报：在桌面歌词里调辉光颜色会把辉光「卡掉」，颜色也不是实时演示的。
            // 这里照抄调色板的真实路径（每个预览事件 = 一次 panel.ColorChanged → Preview），
            // 用三个数把「卡」和「不实时」变成可核对的东西：
            //   ① 改色后第几帧才看见新颜色（1 帧 = 跟着鼠标走，十几帧 = 明显滞后）
            //   ② 一次改色把 UI 线程占用多久（旧行为：整屏几何 + 13 个光罩重画）
            //   ③ 拖动过程中每个事件当帧就换色吗
            string dir = Arg(args, "--glow-color") ?? "glow-color";
            Directory.CreateDirectory(dir);
            settings.Enabled = true;
            settings.GlowEnabled = true;
            settings.GlowStrength = 0.54;
            settings.GlowSpread = 0.75;
            settings.GlowColor = "E0E0E0";
            if (args.Contains("--tilt"))
            {
                // 用户开着 3D 倾斜歌词，光罩是沿透视四边形裁出来的，这条路径要单独量。
                settings.Perspective = true;
                settings.TiltAngle = -25.35;
                settings.VanishingOffset = 2.9;
            }
            var colorController = new PlayerController(settings);
            colorController.LoadDemo(LrcParser.Demo(), DemoCover());
            var colorWindow = new DesktopLyricsWindow(colorController, settings);
            colorWindow.Show();
            long pin = long.TryParse(Arg(args, "--pos"), out var pinMs) ? pinMs : 23900;
            colorWindow.SetDemoPosition(pin);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var colorSettings = new SettingsWindow(settings, s => colorWindow.ApplySettings(s));
            colorSettings.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            for (int i = 0; i < 10; i++) Pump(app, 40);

            // DebugGlowCounts() 走的是同一套 DrawCore，所以调用一次 = 一帧。
            // 设置窗口把 apply 合并到下一帧，改完要先让那一帧跑完（Settle）。
            void Settle() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            int FramesUntil(Func<(long Lit, long Red, long Green, long Blue, long Neutral), bool> hit, int budget = 60)
            {
                for (int f = 1; f <= budget; f++)
                {
                    if (hit(colorWindow.DebugGlowCounts())) return f;
                }
                return -1;
            }

            void ResetGrey()
            {
                colorSettings.DebugCommitGlowColor("E0E0E0");
                Settle();
                FramesUntil(c => c.Red == 0 && c.Green == 0 && c.Blue == 0);
            }

            var sweep = new[] { "FF0000", "00FF00", "0000FF", "FF8000", "8000FF", "0080FF" };
            var log = new List<string>
            {
                $"start: {colorWindow.DebugGlowState} | {colorWindow.DebugGlowPixels()}",
            };
            colorWindow.RenderPreview(Path.Combine(dir, "0-grey.png"));

            // ① 改色后几帧才上屏：旧行为（改色丢几何）对比新行为（只换色罩）。
            //    这里故意不 Settle，让重建帧一帧一帧地走。
            ResetGrey();
            colorWindow.DebugSetGlowColorRebuild(true);
            settings.GlowColor = "FF0000";
            colorWindow.ApplySettings(settings);
            log.Add($"OLD (rebuild on colour): framesUntilRed={FramesUntil(c => c.Red > 0)} | {colorWindow.DebugGlowState}");

            ResetGrey();
            colorWindow.DebugSetGlowColorRebuild(false);
            settings.GlowColor = "FF0000";
            colorWindow.ApplySettings(settings);
            log.Add($"NEW (tint only): framesUntilRed={FramesUntil(c => c.Red > 0)} | {colorWindow.DebugGlowState}");
            Settle();
            colorWindow.RenderPreview(Path.Combine(dir, "1-grey-to-red.png"));

            // ② 一次改色到底占多久：apply + 让这次改动真正落到帧上（旧路径就是一串重建帧）
            double ColourEventCost(bool rebuild)
            {
                colorWindow.DebugSetGlowColorRebuild(rebuild);
                double total = 0;
                for (int i = 0; i < sweep.Length; i++)
                {
                    settings.GlowColor = sweep[i];
                    var t = System.Diagnostics.Stopwatch.StartNew();
                    colorWindow.ApplySettings(settings);
                    Settle();
                    t.Stop();
                    total += t.Elapsed.TotalMilliseconds;
                }
                colorWindow.DebugSetGlowColorRebuild(false);
                return total / sweep.Length;
            }
            ResetGrey();
            double oldCost = ColourEventCost(true);
            ResetGrey();
            double newCost = ColourEventCost(false);
            log.Add($"one colour event costs: OLD={oldCost:0.0}ms NEW={newCost:0.0}ms ({(newCost > 0.01 ? oldCost / newCost : 0):0.0}x)");

            // ③ 连续拖动：24 个事件交替红/绿/蓝，每个事件后只画一帧 —— 颜色必须当帧就在
            ResetGrey();
            var dragHits = new List<string>();
            var dragClock = System.Diagnostics.Stopwatch.StartNew();
            const int steps = 24;
            for (int i = 0; i < steps; i++)
            {
                string hex = sweep[i % sweep.Length];
                colorSettings.DebugPreviewGlowColor(hex);
                Settle();
                var c = colorWindow.DebugGlowCounts();
                bool redWanted = hex[..2] == "FF";
                bool greenWanted = !redWanted && hex[2..4] == "FF";
                bool ok = redWanted ? c.Red > 0 && c.Green == 0 && c.Blue == 0
                        : greenWanted ? c.Green > 0 && c.Red == 0 && c.Blue == 0
                        : c.Blue > 0 && c.Red == 0 && c.Green == 0;
                dragHits.Add(ok ? "1" : "0");
                if (!ok)
                    log.Add($"  miss ev{i,2} {hex}: red{c.Red} green{c.Green} blue{c.Blue} neutral{c.Neutral}");
            }
            dragClock.Stop();
            log.Add($"drag: {steps} events in {dragClock.Elapsed.TotalMilliseconds:0}ms, same-frame colour = " +
                    $"[{string.Join(",", dragHits)}] ({dragHits.Count(h => h == "1")}/{steps}) | {colorWindow.DebugGlowState}");

            // ④ 一次 apply 的成本：只改颜色（背景没变）对比改背景不透明度（必须重建磨砂）
            colorSettings.DebugCommitGlowColor("FF0000");
            Settle();
            FramesUntil(c => c.Red > 0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            colorWindow.ApplySettings(settings);
            sw.Stop();
            double colourApply = sw.Elapsed.TotalMilliseconds;
            var before = settings.BackdropOpacity;
            settings.BackdropOpacity = Math.Clamp(before - 0.05, 0.1, 1);
            sw.Restart();
            colorWindow.ApplySettings(settings);
            sw.Stop();
            double backdropApply = sw.Elapsed.TotalMilliseconds;
            settings.BackdropOpacity = before;
            colorWindow.ApplySettings(settings);
            log.Add($"apply cost: colour-only={colourApply:0.00}ms backdrop-rebuild={backdropApply:0.00}ms");

            // ⑤ 反复换色：辉光不能掉
            foreach (var (hex, expect) in new[]
                     {
                         ("FF0000", "red"), ("00FF00", "green"), ("E0E0E0", "grey"),
                     })
            {
                colorSettings.DebugCommitGlowColor(hex);
                Settle();
                int frames = FramesUntil(c => expect switch
                {
                    "red" => c.Red > 0,
                    "green" => c.Green > 0,
                    _ => c.Red == 0 && c.Green == 0 && c.Blue == 0,
                });
                var c = colorWindow.DebugGlowCounts();
                log.Add($"commit {hex} ({expect}): frames={frames} lit={c.Lit} red={c.Red} green={c.Green} blue={c.Blue} | {colorWindow.DebugGlowState}");
                colorWindow.RenderPreview(Path.Combine(dir, $"2-{hex}.png"));
            }
            File.WriteAllLines(Path.Combine(dir, "report.txt"), log);
            colorSettings.Close();
            colorWindow.Close();
            app.Shutdown();
            return;
        }
        if (args.Contains("--settings-test"))        {
            var probe = new AppSettings
            {
                BackdropFadeH = 17,
                BackdropFadeV = 19,
                BackdropFadeStrength = 55,
                BackdropWidth = 640,
                BackdropHeight = 420,
                BackdropMode = BackdropMode.Image,
                BackdropBlur = 33,
                BackdropOpacity = 0.64,
                Translation = false,
            };
            probe.SongOffsets["local:test"] = 750;
            probe.CopyFrom(probe.Normalized());
            var lines = new List<string>
            {
                $"mode={probe.BackdropMode} fadeH={probe.BackdropFadeH} fadeV={probe.BackdropFadeV} strength={probe.BackdropFadeStrength}",
                $"w={probe.BackdropWidth} h={probe.BackdropHeight} blur={probe.BackdropBlur} op={probe.BackdropOpacity}",
                $"translation={probe.Translation} offsets={probe.SongOffsets.Count}",
            };
            System.IO.File.WriteAllLines(Arg(args, "--settings-test") ?? "settings-test.txt", lines);
            return;
        }
        if (args.Contains("--order-test"))
        {
            var orderController = new PlayerController(settings);
            RunBlocking(app, orderController.ShowLibraryAsync());
            var before = orderController.Songs.Select(s => System.IO.Path.GetFileNameWithoutExtension(s.Path)).ToList();
            orderController.MoveSong(0, Math.Min(2, orderController.Songs.Count - 1));
            var after = orderController.Songs.Select(s => System.IO.Path.GetFileNameWithoutExtension(s.Path)).ToList();
            var saved = settings.LibraryFiles.Select(System.IO.Path.GetFileNameWithoutExtension).ToList();
            var lines = new List<string> { "before=" + string.Join(" | ", before), "after=" + string.Join(" | ", after), "saved=" + string.Join(" | ", saved), "mode=" + orderController.Mode };
            System.IO.File.WriteAllLines(Arg(args, "--order-test") ?? "order.txt", lines);
            return;
        }
        if (args.Contains("--menu-shot"))
        {
            RunMenuShot(app, args, settings);
            return;
        }
        if (args.Contains("--nowplaying"))
        {
            RunNowPlaying(app, args, settings);
            return;
        }
        if (args.Contains("--cue-perf"))
        {
            RunCuePerf(app, args, settings);
            return;
        }
        if (args.Contains("--cue-film"))
        {
            RunCueFilm(app, args, settings);
            return;
        }
        if (args.Contains("--glow-drag"))
        {
            // QA: 拖「辉光范围」。滑条每帧改的是 PlayerGlowSpread → _glowRadius；以前它算几何变化，
            // 每步都丢掉整列几何，朗读行会掉回没有逐字墨水的渲染（只剩两三个亮词、光晕消失）。
            // 这里扫一遍范围，记录每一步的逐字状态和骨架帧耗时。
            string gdDir = Arg(args, "--glow-drag") ?? ".";
            Directory.CreateDirectory(gdDir);
            settings.Enabled = false;
            var gdController = new PlayerController(settings);
            string? gdLrc = Arg(args, "--lrc");
            var gdDoc = gdLrc != null && File.Exists(gdLrc)
                ? LrcParser.LoadFor(gdLrc) ?? LrcParser.Demo()
                : LrcParser.Demo();
            gdController.LoadDemo(gdDoc, DemoCover());
            var gdView = new NowPlayingView(gdController, settings);
            var gdWindow = new Window
            {
                Title = "QA · 辉光范围拖动",
                Width = 1240,
                Height = 820,
                Background = Brushes.Black,
                Content = gdView,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            gdWindow.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            for (int i = 0; i < 10; i++) Pump(app, 50);

            settings.PlayerGlow = true;
            if (settings.PlayerGlowStrength < 0.2) settings.PlayerGlowStrength = 1.0;   // QA profile may ship glow off
            long gdPos = long.TryParse(Arg(args, "--pos"), out var gdP) ? gdP : 40000;
            gdView.RefreshFromSettings();
            gdView.DebugPinPosition(gdPos);
            gdView.DebugBuildAllWordParts();
            for (int i = 0; i < 6; i++) Pump(app, 50);

            var gdLines = new List<string> { $"pos={gdPos} start={gdView.DebugKaraoke}" };
            double gdOriginal = settings.PlayerGlowSpread;
            for (int i = 0; i <= 24; i++)
            {
                settings.PlayerGlowSpread = 0.5 + i * (1.5 / 24.0);
                var swStep = System.Diagnostics.Stopwatch.StartNew();
                gdView.RefreshFromSettings();
                Pump(app, 36);
                swStep.Stop();
                gdLines.Add($"step={i:00} spread={settings.PlayerGlowSpread:0.00} {gdView.DebugGlowState} " +
                            $"{gdView.DebugKaraoke} 2frames={swStep.Elapsed.TotalMilliseconds:0.0}ms");
                if (i is 0 or 8 or 16 or 24)
                    Save(gdWindow, Path.Combine(gdDir, $"glow-{settings.PlayerGlowSpread * 100:000}.png"));
            }
            settings.PlayerGlowSpread = gdOriginal;
            gdView.RefreshFromSettings();
            File.WriteAllLines(Path.Combine(gdDir, "glow-drag.txt"), gdLines);
            gdWindow.Close();
            app.Shutdown();
            return;
        }
        if (args.Contains("--player-glow"))        {
            RunPlayerGlow(app, args, settings);
            return;
        }
        if (args.Contains("--clean-test"))
        {
            var cleanController = new PlayerController(settings);
            var cleanLines = new List<string>();
            foreach (var p in cleanController.Playlists)
                cleanLines.Add($"before: {p.Name} songs={p.Songs.Count} existing={p.ExistingCount} missing={p.MissingCount}");
            var target = cleanController.Playlists.FirstOrDefault(p => p.MissingCount > 0);
            if (target != null)
                cleanLines.Add($"clean '{target.Name}' removed={cleanController.CleanMissing(target)}");
            else
                cleanLines.Add("clean: no playlist with missing entries");
            foreach (var p in cleanController.Playlists)
                cleanLines.Add($"after: {p.Name} songs={p.Songs.Count} existing={p.ExistingCount} missing={p.MissingCount}");
            File.WriteAllLines(Arg(args, "--clean-test") ?? "clean.txt", cleanLines);
            return;
        }
        if (args.Contains("--menu-qa"))
        {
            RunMenuQa(app, args, settings);
            return;
        }
        if (args.Contains("--rowremove"))
        {
            RunRowRemove(app, args, settings);
            return;
        }
        if (args.Contains("--playlist-reclick"))
        {
            RunPlaylistReclick(app, args, settings);
            return;
        }
        if (args.Contains("--addtoplaylist"))
        {
            RunAddToPlaylist(app, args, settings);
            return;
        }
        if (args.Contains("--addtoplaylist-online"))
        {
            RunAddOnlineToPlaylist(app, args, settings);
            return;
        }
        if (args.Contains("--saveas"))
        {
            RunSaveAs(app, args, settings);
            return;
        }
        if (args.Contains("--songmenu"))
        {
            RunSongMenu(app, args, settings);
            return;
        }
        if (args.Contains("--rightmenu"))
        {
            RunRightMenu(app, args, settings);
            return;
        }
        if (args.Contains("--ui-shot"))
        {
            RunUiShot(app, args, settings);
            return;
        }
        if (args.Contains("--liveshot"))
        {
            RunLiveShot(app, args, settings);
            return;
        }
        if (args.Contains("--selftest"))
        {
            RunSelfTest(app, args, settings);
            return;
        }

        var controller = new PlayerController(settings);
        var main = new MainWindow(controller);
        main.Show();
        app.Run();
    }

    private static void RunMediaTest(Application app, string[] args)
    {
        string url = Arg(args, "--media-test") ?? "";
        string report = Arg(args, "--report") ?? "media-test.txt";
        var variants = new List<(string name, string value)>
        {
            ("raw", url),
            ("no-query", url.Split('?')[0]),
            ("escaped-query", url.Split('?')[0] + "?" + Uri.EscapeDataString(url.Contains('?') ? url.Split('?', 2)[1] : "")),
        };
        var lines = new List<string>();
        int index = 0;

        void TryNext()
        {
            if (index >= variants.Count)
            {
                File.WriteAllLines(report, lines);
                app.Shutdown();
                return;
            }
            var (name, value) = variants[index++];
            var player = new MediaPlayer();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            bool settled = false;
            player.MediaOpened += (_, _) =>
            {
                if (settled) return;
                settled = true;
                lines.Add($"{name}: opened dur={player.NaturalDuration.TimeSpan.TotalSeconds:0.0}");
                timer.Stop();
                player.Close();
                TryNext();
            };
            player.MediaFailed += (_, e) =>
            {
                if (settled) return;
                settled = true;
                lines.Add($"{name}: failed {e.ErrorException?.Message}");
                timer.Stop();
                player.Close();
                TryNext();
            };
            try
            {
                player.Open(new Uri(value));
                player.Volume = 0;
            }
            catch (Exception e)
            {
                lines.Add($"{name}: threw {e.Message}");
                TryNext();
                return;
            }
            timer.Tick += (_, _) =>
            {
                if (settled) return;
                settled = true;
                lines.Add($"{name}: timeout state={player.Source}");
                timer.Stop();
                player.Close();
                TryNext();
            };
            timer.Start();
        }

        TryNext();
        app.Run();
    }

    /// <summary>Diagnostic: compare the WPF player volume with the real Windows session volume.</summary>
    private static void RunAudioTest(Application app, string[] args, AppSettings settings)
    {
        string report = Arg(args, "--report") ?? "audio-test.txt";
        string folder = Arg(args, "--folder") ?? "";
        settings.Enabled = false;
        var controller = new PlayerController(settings);
        controller.Playback.Volume = 0.42;          // a value easy to spot in the mixer
        var lines = new List<string> { "wpf volume = 0.42" };
        var pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        RunBlocking(app, controller.ShowLocalAsync(folder));
        if (controller.Songs.Count == 0)
        {
            File.WriteAllLines(report, lines.Append("no songs"));
            app.Shutdown();
            return;
        }
        RunBlocking(app, controller.PlayAsync(controller.Songs[0]));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        int tick = 0;
        timer.Tick += (_, _) =>
        {
            tick++;
            var session = AudioSession.GetVolume(pid);
            lines.Add($"t={tick}s playing={controller.PlaybackState} sessionVolume={(session?.ToString("0.000") ?? "none")}");
            if (tick == 3)
            {
                bool ok = AudioSession.SetVolume(pid, 0.25f);
                lines.Add("set 0.25 -> " + ok + " read back " + (AudioSession.GetVolume(pid)?.ToString("0.000") ?? "none"));
            }
            if (tick == 5)
            {
                timer.Stop();
                File.WriteAllLines(report, lines);
                controller.Dispose();
                app.Shutdown();
            }
        };
        timer.Start();
        app.Run();
    }

    /// <summary>Plays a NetEase track end-to-end and reports the media clock and lyric timing.</summary>
    private static void RunStreamTest(Application app, string[] args, AppSettings settings)
    {
        string report = Arg(args, "--report") ?? "stream-test.txt";
        long id = long.TryParse(Arg(args, "--id"), out var parsed) ? parsed : 524149974L;
        var controller = new PlayerController(settings);
        var song = new Models.Song { Source = Models.SongSource.Netease, Path = $"netease:{id}", NeteaseId = id, Title = "stream test" };
        var lines = new List<string>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
        int tick = 0;
        timer.Tick += async (_, _) =>
        {
            tick++;
            if (tick == 1) await controller.PlayAsync(song);
            if (tick == 1) lines.Add("url=" + (song.StreamUrl ?? "null"));
            lines.Add($"t={controller.RawPositionMs:0} playing={controller.PlaybackState} dur={controller.Playback.DurationMs:0} fail={controller.Playback.Failure ?? "-"}");
            if (controller.Lyrics != null) lines.Add("  lyrics=" + controller.Lyrics.Lines.Count + " lines");
            if (tick >= 10)
            {
                timer.Stop();
                File.WriteAllLines(report, lines);
                controller.Dispose();
                app.Shutdown();
            }
        };
        timer.Start();
        app.Run();
    }

    /// <summary>QA: exercises the NetEase client end to end without any UI (search → URL → lyric).</summary>
    /// <summary>QA: runs one 落雪 (LX) script through the host and resolves a track with it.</summary>
    private static void RunLxTest(string[] args)
    {
        var lines = new List<string>();
        string? path = Arg(args, "--lx-test");
        if (path == null || !File.Exists(path))
        {
            File.WriteAllLines(Arg(args, "--report") ?? "lx-test.txt", new[] { "script not found: " + (path ?? "(null)") });
            return;
        }
        var script = File.ReadAllText(path);
        using var runtime = new Services.LxRuntime();
        try
        {
            var capabilities = runtime.Load("test", script);
            lines.Add("capabilities=" + capabilities);

            long id = long.TryParse(Arg(args, "--id"), out var parsed) ? parsed : 524149974L;
            var info = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "320k",
                ["musicInfo"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["source"] = "wy",
                    ["songmid"] = id.ToString(),
                    ["name"] = Arg(args, "--name") ?? "打ち上げ花火",
                    ["singer"] = Arg(args, "--singer") ?? "Daoko / 米津玄師",
                    ["albumName"] = "",
                    ["interval"] = "04:49",
                },
            };
            var declared = new List<string> { "128k", "320k", "flac" };
            var audio = runtime.Resolve("wy", "exhigh", info, declared);
            lines.Add($"resolved quality={audio.Quality} source={audio.Source} url={audio.Url}");
        }
        catch (Exception e)
        {
            lines.Add("failed: " + e.GetType().Name + ": " + e.Message);
        }
        File.WriteAllLines(Arg(args, "--report") ?? "lx-test.txt", lines);
    }

    private static void RunNeteaseTest(string[] args, AppSettings settings)
    {
        Services.NeteaseApi.Cookie = Arg(args, "--cookie") ?? settings.NeteaseCookie ?? "";
        Services.NeteaseApi.Quality = Arg(args, "--level") ?? settings.NeteaseQuality ?? "exhigh";
        string keyword = Arg(args, "--q") ?? "LOSER";
        var lines = new List<string>
        {
            $"login={(Services.NeteaseApi.HasLogin ? "yes" : "no")} level={Services.NeteaseApi.Quality} keyword={keyword}",
        };
        // Task.Run keeps the continuations off the WPF dispatcher, which nothing is pumping here.
        var page = Task.Run(() => Services.NeteaseApi.SearchAsync(keyword, 5)).GetAwaiter().GetResult();
        lines.Add($"search total={page.Total} got={page.Songs.Count} error={page.Error ?? "-"}");
        foreach (var song in page.Songs)
            lines.Add($"  {song.NeteaseId,-12} {song.DisplayTitle} - {song.DisplayArtist}  {song.DurationText}  cover={song.CoverUrl != null}");

        foreach (var song in page.Songs.Take(3))
        {
            var url = Task.Run(() => Services.NeteaseApi.ResolveAsync(song.NeteaseId)).GetAwaiter().GetResult();
            lines.Add($"url {song.NeteaseId}: level={url.Level} code={url.Code} expi={url.ExpiresSeconds} trial={url.Trial} error={url.Error ?? "-"}");
            if (url.Url != null) lines.Add("  " + url.Url[..Math.Min(140, url.Url.Length)]);
            var lyric = Task.Run(() => Services.NeteaseApi.LyricAsync(song.NeteaseId)).GetAwaiter().GetResult();
            lines.Add($"  lyric lines={(lyric == null ? 0 : lyric.Split('\n').Length)}");
        }
        File.WriteAllLines(Arg(args, "--report") ?? "ne-test.txt", lines);
    }

    private static async Task RunWsaTest(string[] args)    {
        var lines = new List<string>();
        var adb = WsaBridge.FindAdb(Arg(args, "--adb"));
        lines.Add("adb: " + (adb ?? "not found"));
        if (adb != null)
        {
            lines.Add("online: " + WsaBridge.IsDeviceOnline(adb, WsaBridge.DefaultSerial));
            var (songs, error) = await WsaBridge.ReadQueueAsync(adb, WsaBridge.DefaultSerial);
            lines.Add($"songs={songs.Count} error={error}");
            foreach (var s in songs.Take(10)) lines.Add($"  {s.NeteaseId} {s.DisplayTitle} - {s.DisplayArtist} {s.DurationText} cover={s.CoverUrl != null}");
            lines.Add("--- adb log ---");
            lines.Add(WsaBridge.LastLog);
        }
        File.WriteAllLines(Arg(args, "--report") ?? "wsa-test.txt", lines);
    }

        /// <summary>Renders a cue transition frame by frame so the scroll curve can be inspected.</summary>
    private static void RunAnim(Application app, string[] args, AppSettings settings)
    {
        string dir = Arg(args, "--anim") ?? "anim";
        long startMs = long.TryParse(Arg(args, "--startms"), out var st) ? st : 15000;
        int frames = int.TryParse(Arg(args, "--frames"), out var fc) ? fc : 40;
        Directory.CreateDirectory(dir);
        settings.Enabled = true;

        var controller = new PlayerController(settings);
        var lrcPath = Arg(args, "--lrc");
        if (lrcPath != null && System.IO.File.Exists(lrcPath))
        {
            var doc = LrcParser.LoadFor(lrcPath);
            controller.LoadDemo(doc ?? LrcParser.Demo(), DemoCover());
        }
        else
        {
            controller.LoadDemo(LrcParser.Demo(), DemoCover());
        }
        var window = new DesktopLyricsWindow(controller, settings);
        window.Show();
        window.SetDemoPosition(startMs);
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        // Warm up: park just before the cue, then advance at 60 Hz.
        for (int i = 0; i < frames; i++)
        {
            long pos = startMs + i * 16;
            window.DemoAdvance(pos);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            if (i % 2 == 0)
            {
                window.RenderPreview(Path.Combine(dir, $"a{i:000}.png"));
            }
            System.Threading.Thread.Sleep(4);
        }
        window.Close();
        app.Shutdown();
    }

    /// <summary>Decodes a recording into PNG frames with WPF's media stack, so the motion of a
    /// reference clip can be measured frame by frame without ffmpeg on the machine.</summary>
    private static void RunVideoExtract(Application app, string[] args)
    {
        string path = Arg(args, "--video")!;
        string dir = Arg(args, "--vout") ?? "frames";
        double start = double.TryParse(Arg(args, "--start"), out var s) ? s : 0;
        double fps = double.TryParse(Arg(args, "--fps"), out var f) ? f : 60;
        int count = int.TryParse(Arg(args, "--count"), out var c) ? c : 60;
        Directory.CreateDirectory(dir);

        var player = new MediaPlayer { ScrubbingEnabled = true, Volume = 0 };
        bool opened = false;
        string? failure = null;
        player.MediaOpened += (_, _) => opened = true;
        player.MediaFailed += (_, e) => failure = e.ErrorException?.ToString() ?? "media failed";
        player.Open(new Uri(path));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!opened && failure == null && clock.ElapsedMilliseconds < 15000) Pump(60);
        if (!opened)
        {
            File.WriteAllText(Path.Combine(dir, "error.txt"), failure ?? "timeout opening media");
            app.Shutdown();
            return;
        }

        int w = player.NaturalVideoWidth, h = player.NaturalVideoHeight;
        double duration = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : 0;
        for (int i = 0; i < count; i++)
        {
            double t = start + i / fps;
            player.Position = TimeSpan.FromSeconds(t);
            Pump(110);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawVideo(player, new Rect(0, 0, w, h));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, $"f{i:000}.png"));
            encoder.Save(fs);
        }

        File.WriteAllText(Path.Combine(dir, "info.txt"), $"duration={duration:0.00}s size={w}x{h} fps={fps} start={start} count={count}");
        app.Shutdown();
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Visual QA: renders the desktop island at a fixed lyric position to a PNG.</summary>
    private static void RunShot(Application app, string[] args, AppSettings settings)
    {
        string path = Arg(args, "--shot") ?? "preview.png";
        long position = 0;
        if (long.TryParse(Arg(args, "--pos"), out var parsed)) position = parsed;

        settings.Enabled = true;
        settings.Translation = true;
        if (args.Contains("--noglow")) settings.GlowEnabled = false;
        if (double.TryParse(Arg(args, "--scale"), out var scaleValue)) settings.Scale = scaleValue;
        if (double.TryParse(Arg(args, "--font"), out var fontValue)) settings.FontSize = fontValue;
        if (Arg(args, "--translation") == "0") settings.Translation = false;

        var controller = new PlayerController(settings);
        var lrcPath = Arg(args, "--lrc");
        // QA: real artwork for the backdrop, so "背景图分辨率过高/过低" can be judged on pixels.
        Models.Song demoCover = DemoCover();
        string? coverPath = Arg(args, "--cover");
        if (coverPath != null && System.IO.File.Exists(coverPath))
        {
            var art = new System.Windows.Media.Imaging.BitmapImage();
            art.BeginInit();
            art.UriSource = new Uri(coverPath);
            art.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            int decode = int.TryParse(Arg(args, "--cover-decode"), out var decodeWidth) ? decodeWidth : 0;
            if (decode > 0) art.DecodePixelWidth = decode;
            art.EndInit();
            art.Freeze();
            demoCover.Cover = art;
        }
        if (lrcPath != null && System.IO.File.Exists(lrcPath))
        {
            var doc = LrcParser.LoadFor(lrcPath);
            // QA: LYRIC_OPENING="title|artist|album" exercises the synthesized opening card.
            string? opening = Environment.GetEnvironmentVariable("LYRIC_OPENING") ?? Arg(args, "--opening");
            if (doc != null && !string.IsNullOrWhiteSpace(opening))
            {
                var parts = opening.Split('|');
                doc = LrcParser.WithOpeningCard(doc,
                    parts.ElementAtOrDefault(0) ?? "", parts.ElementAtOrDefault(1) ?? "", parts.ElementAtOrDefault(2) ?? "");
            }
            controller.LoadDemo(doc ?? LrcParser.Demo(), demoCover);
        }
        else
        {
            controller.LoadDemo(LrcParser.Demo(), demoCover);
        }

        var window = new DesktopLyricsWindow(controller, settings);
        var bgHex = Arg(args, "--shotbg");
        if (bgHex != null)
        {
            var hex = bgHex.TrimStart('#');
            var colour = System.Windows.Media.Color.FromRgb(
                Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
            window.Background = new System.Windows.Media.SolidColorBrush(colour);
        }
        window.Show();
        window.SetDemoPosition(position);
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.RenderPreview(path);
        window.Close();
        app.Shutdown();
    }

    /// <summary>End-to-end check: real media, real LRC, real overlay.</summary>
    private static void RunLiveShot(Application app, string[] args, AppSettings settings)
    {
        string path = Arg(args, "--liveshot") ?? "live.png";
        string folder = Arg(args, "--folder") ?? "";
        int wait = int.TryParse(Arg(args, "--wait"), out var ms) ? ms : 6000;
        settings.Enabled = true;

        var controller = new PlayerController(settings);
        controller.ShowLocalAsync(folder).GetAwaiter().GetResult();
        if (controller.Songs.Count == 0)
        {
            File.WriteAllText(path + ".txt", "no songs in " + folder);
            app.Shutdown();
            return;
        }

        var window = new DesktopLyricsWindow(controller, settings);
        window.Show();
        controller.PlayAsync(controller.Songs[0]).GetAwaiter().GetResult();
        window.ResetRenderStats();
        window.ResetFrameCount();
        var liveClock = System.Diagnostics.Stopwatch.StartNew();

        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(wait) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            window.RenderPreview(path);
            double liveSeconds = liveClock.Elapsed.TotalSeconds;
            File.WriteAllText(path + ".txt", "pos=" + controller.RawPositionMs + " playing=" + controller.PlaybackState
                + $" seconds={liveSeconds:0.00}"
                + $" frames={window.FrameCount} ({(window.FrameCount / liveSeconds):0.0}/s)"
                + $" renders={window.DebugRenderCount} ({(window.DebugRenderCount / liveSeconds):0.0}/s)"
                + " draw_ms_avg=" + (window.DebugRenderCount > 0
                    ? (window.DebugRenderMsTotal / window.DebugRenderCount).ToString("0.00") : "0"));
            window.Close();
            app.Shutdown();
        };
        timer.Start();
        app.Run();
    }

    /// <summary>Visual QA for the library and settings windows.</summary>
    /// <summary>Opens the playlist context menu and grabs the desktop, popups included.</summary>
    private static void RunMenuShot(Application app, string[] args, AppSettings settings)
    {
        string path = Arg(args, "--menu-shot") ?? "menu.png";
        if (args.Contains("--combo"))
        {
var settingsWindow = new SettingsWindow(settings, _ => { });
            settingsWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            settingsWindow.Left = 60;
            settingsWindow.Top = 40;
            settingsWindow.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            foreach (var viewer in Descendants(settingsWindow).OfType<System.Windows.Controls.ScrollViewer>())
                viewer.ScrollToVerticalOffset(Math.Max(0, viewer.ScrollableHeight - 300));
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(400);
            if (settingsWindow.FindName("cmbBackdropMode") is System.Windows.Controls.ComboBox combo)
            {
                combo.IsDropDownOpen = true;
                combo.Focus();
            }
            Thread.Sleep(700);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            try
            {
                int width = (int)settingsWindow.ActualWidth + 40, height = (int)settingsWindow.ActualHeight + 320;
                using var bitmap = new System.Drawing.Bitmap(width, height);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen((int)settingsWindow.Left, (int)settingsWindow.Top, 0, 0,
                        new System.Drawing.Size(width, height));
                }
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception ex)
            {
                File.WriteAllText(path + ".error.txt", ex.Message);
            }
            settingsWindow.Close();
            return;
        }

        var controller = new PlayerController(settings);
        var main = new MainWindow(controller);
        main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.Left = 60;
        main.Top = 60;
        main.Show();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Thread.Sleep(600);

        if (main.FindName("lstPlaylists") is System.Windows.Controls.ListBox list && list.ContextMenu != null)
        {
            list.SelectedIndex = 0;
            list.ContextMenu.PlacementTarget = list;
            list.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
            list.ContextMenu.IsOpen = true;
        }
        Thread.Sleep(700);
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        try
        {
            var window = main;
            int width = (int)window.ActualWidth + 260, height = (int)window.ActualHeight + 80;
            using var bitmap = new System.Drawing.Bitmap(width, height);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen((int)window.Left, (int)window.Top, 0, 0,
                    new System.Drawing.Size(width, height));
            }
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex)
        {
            File.WriteAllText(path + ".error.txt", ex.Message);
        }

        if (main.FindName("lstPlaylists") is System.Windows.Controls.ListBox list2 && list2.ContextMenu != null)
            list2.ContextMenu.IsOpen = false;
        main.Close();
    }

    /// <summary>QA for the now-playing page: opens it twice, portrait and wide, with the audio muted.</summary>
    /// <summary>QA for the full-screen page's own glow rows: the ⋯ sheet must offer 强度/范围/颜色,
    /// and changing range or colour must reach the pixels the page paints.</summary>
    /// <summary>QA: 换行期间的帧间隔（不抓图，避免 RTB 干扰）。每段先静置到边界前 500 ms，再跨过去，
    /// 记录 700 ms 内每个合成帧的时间戳，输出 worst/over9 —— 卡顿会在这里露出来。</summary>
    private static void RunCuePerf(Application app, string[] args, AppSettings settings)
    {
        string dir = Arg(args, "--cue-perf") ?? "cueperf";
        Directory.CreateDirectory(dir);
        settings.Enabled = false;
        var controller = new PlayerController(settings);
        string? lrcPath = Arg(args, "--lrc");
        var doc = lrcPath != null && File.Exists(lrcPath) ? LrcParser.LoadFor(lrcPath) ?? LrcParser.Demo() : LrcParser.Demo();
        controller.LoadDemo(doc, DemoCover());
        var view = new NowPlayingView(controller, settings);
        var window = new Window
        {
            Title = "QA · 换行帧率",
            Width = 1240,
            Height = 820,
            Background = Brushes.Black,
            Content = view,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.Show();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        for (int i = 0; i < 10; i++) Pump(app, 50);

        var lines = new List<string>();
        int startCue = int.TryParse(Arg(args, "--cue"), out var ci) ? ci : 2;
        for (int n = 0; n < 5; n++)
        {
            int cue = Math.Min(startCue + n, doc.Lines.Count - 1);
            if (cue <= 0) continue;
            long cueMs = doc.Lines[cue].StartMs;
            view.DebugPinPosition(cueMs - 500);
            for (int i = 0; i < 8; i++) Pump(app, 40);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var stamps = new List<double>();
            System.EventHandler onRender = (_, _) => stamps.Add(clock.Elapsed.TotalMilliseconds);
            System.Windows.Media.CompositionTarget.Rendering += onRender;
            view.DebugPinPositionAnimated(cueMs + 10);
            PumpFrames(700);
            System.Windows.Media.CompositionTarget.Rendering -= onRender;

            double worst = 0, total = 0;
            int over9 = 0, over20 = 0;
            for (int k = 1; k < stamps.Count; k++)
            {
                double d = stamps[k] - stamps[k - 1];
                total += d;
                if (d > worst) worst = d;
                if (d > 9) over9++;
                if (d > 20) over20++;
            }
            double avg = stamps.Count > 1 ? total / (stamps.Count - 1) : 0;
            lines.Add($"cue {cue} @{cueMs}ms frames={stamps.Count} avg={avg:0.0}ms worst={worst:0.0}ms over9={over9} over20={over20}");
        }
        File.WriteAllLines(Path.Combine(dir, "perf.txt"), lines);
        window.Close();
        app.Shutdown();
    }

    /// <summary>QA: 换行动画逐帧抓图。定位到某条歌词开始前 400 ms，然后跨过边界每 38 ms 拍一张，
    /// 输出 f00..f09.png + film.txt，用来判断「滑+放大」的手感而不是靠想象。</summary>
    private static void RunCueFilm(Application app, string[] args, AppSettings settings)
    {
        string dir = Arg(args, "--cue-film") ?? "cuefilm";
        Directory.CreateDirectory(dir);
        settings.Enabled = false;
        var controller = new PlayerController(settings);
        string? lrcPath = Arg(args, "--lrc");
        var doc = lrcPath != null && File.Exists(lrcPath) ? LrcParser.LoadFor(lrcPath) ?? LrcParser.Demo() : LrcParser.Demo();
        controller.LoadDemo(doc, DemoCover());
        var view = new NowPlayingView(controller, settings);
        var window = new Window
        {
            Title = "QA · 换行动画",
            Width = 1240,
            Height = 820,
            Background = Brushes.Black,
            Content = view,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.Show();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        for (int i = 0; i < 10; i++) Pump(app, 50);

        int cueIndex = int.TryParse(Arg(args, "--cue"), out var ci) ? ci : Math.Min(3, doc.Lines.Count - 1);
        long cueMs = doc.Lines[Math.Clamp(cueIndex, 0, doc.Lines.Count - 1)].StartMs;
        var lines = new List<string> { $"cue={cueIndex} at {cueMs}ms frames=10 step=38ms" };

        view.DebugPinPosition(cueMs - 400);
        for (int i = 0; i < 10; i++) Pump(app, 50);
        // 逐字墨水平时是分帧建的；定格画面里一次性建完，才能拍到「上一行几乎唱满」时换行的样子。
        view.DebugBuildAllWordParts();
        for (int i = 0; i < 6; i++) Pump(app, 50);
        Save(window, Path.Combine(dir, "f-before.png"));
        lines.Add("before " + view.DebugKaraoke);

        for (int f = 0; f < 10; f++)
        {
            view.DebugPinPositionAnimated(cueMs + f * 4);
            Thread.Sleep(25);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Save(window, Path.Combine(dir, $"f{f:00}.png"));
            lines.Add($"f{f:00} ~{(f + 1) * 25}ms {view.DebugState()} | {view.DebugKaraoke}");
        }
        File.WriteAllLines(Path.Combine(dir, "film.txt"), lines);
        window.Close();
        app.Shutdown();
    }

    private static void RunPlayerGlow(Application app, string[] args, AppSettings settings)
    {
        string dir = Arg(args, "--player-glow") ?? ".";
        Directory.CreateDirectory(dir);
        settings.Enabled = false;
        var controller = new PlayerController(settings);
        string? lrcPath = Arg(args, "--lrc");
        long pinPos = long.TryParse(Arg(args, "--pos"), out var pinParsed) ? pinParsed : 23900;
        Models.LyricDocument doc = lrcPath != null && System.IO.File.Exists(lrcPath)
            ? LrcParser.LoadFor(lrcPath) ?? LrcParser.Demo()
            : LrcParser.Demo();
        string? opening = Arg(args, "--opening");
        if (!string.IsNullOrWhiteSpace(opening))
        {
            var parts = opening.Split('|');
            doc = LrcParser.WithOpeningCard(doc,
                parts.ElementAtOrDefault(0) ?? "", parts.ElementAtOrDefault(1) ?? "", parts.ElementAtOrDefault(2) ?? "") ?? doc;
        }
        controller.LoadDemo(doc, DemoCover());
        var view = new NowPlayingView(controller, settings);
        var window = new Window
        {
            Title = "QA · 播放器辉光设置",
            Width = 520,
            Height = 880,
            Background = Brushes.Black,
            Content = view,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.Show();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        for (int i = 0; i < 8; i++) Pump(app, 50);

        var lines = new List<string>
        {
            $"doc: lines={doc.Lines.Count} wordTimedLines={doc.Lines.Count(l => l.Words.Count > 0)} pos={pinPos} " +
            $"source={(lrcPath != null && System.IO.File.Exists(lrcPath) ? System.IO.Path.GetFileName(lrcPath) : "demo")}",
        };
        settings.PlayerGlow = true;
        settings.PlayerGlowStrength = 1.2;
        settings.PlayerGlowSpread = 1.0;
        settings.PlayerGlowColor = "E0E0E0";
        view.RefreshFromSettings();
        view.DebugPinPosition(pinPos);
        for (int i = 0; i < 8; i++) Pump(app, 50);
        lines.Add("start: " + view.DebugGlowState);
        Save(window, Path.Combine(dir, "glow-gray-r100.png"));

        // baseline with the switch off, so the pixel diff can prove the halo really is painted
        settings.PlayerGlow = false;
        view.RefreshFromSettings();
        view.DebugPinPosition(pinPos);
        for (int i = 0; i < 8; i++) Pump(app, 50);
        lines.Add("switch-off: " + view.DebugGlowState);
        Save(window, Path.Combine(dir, "glow-off.png"));
        settings.PlayerGlow = true;
        view.RefreshFromSettings();
        view.DebugPinPosition(pinPos);
        for (int i = 0; i < 8; i++) Pump(app, 50);

        view.DebugOpenMoreMenu();
        for (int i = 0; i < 8; i++) Pump(app, 50);
        lines.Add("rows: " + view.DebugMoreMenuRows());
        Save(window, Path.Combine(dir, "menu.png"));

        // drive the two new rows exactly like a drag
        double spread = view.DebugSetRowValue("辉光范围", 180);
        double strength = view.DebugSetRowValue("辉光强度", 140);
        for (int i = 0; i < 8; i++) Pump(app, 50);
        lines.Add($"row 辉光范围 -> {spread:0.##} spread={settings.PlayerGlowSpread:0.00} " +
                  $"row 辉光强度 -> {strength:0.##} strength={settings.PlayerGlowStrength:0.00}");
        view.DebugOpenMoreMenu();   // toggles the sheet closed, so the lyrics are visible again
        view.DebugPinPosition(pinPos);
        for (int i = 0; i < 8; i++) Pump(app, 50);
        lines.Add("spread180: " + view.DebugGlowState);
        Save(window, Path.Combine(dir, "glow-gray-r180.png"));

        view.DebugSetGlowColor("FF0000");
        view.DebugPinPosition(pinPos);
        for (int i = 0; i < 8; i++) Pump(app, 50);
        lines.Add("color-red: " + view.DebugGlowState);
        Save(window, Path.Combine(dir, "glow-red-r180.png"));

        // decoupling: the desktop overlay's colour must not leak into the page any more
        settings.GlowColor = "00FF00";
        view.RefreshFromSettings();
        view.DebugPinPosition(pinPos);
        for (int i = 0; i < 8; i++) Pump(app, 50);
        lines.Add("desktopGlowColor=00FF00: " + view.DebugGlowState);
        Save(window, Path.Combine(dir, "glow-after-desktop-change.png"));

        // Round-trip: the two new fields must survive Normalized() (it rebuilds a fresh instance).
        SettingsStore.Save(settings);
        var reloaded = SettingsStore.Load();
        lines.Add($"reload: spread={reloaded.PlayerGlowSpread:0.00} color=#{reloaded.PlayerGlowColor} " +
                  $"strength={reloaded.PlayerGlowStrength:0.00} glow={(reloaded.PlayerGlow ? 1 : 0)} " +
                  $"(in-memory spread={settings.PlayerGlowSpread:0.00} color=#{settings.PlayerGlowColor})");

        File.WriteAllLines(Path.Combine(dir, "report.txt"), lines);
        window.Close();
        app.Shutdown();
    }

    private static void RunNowPlaying(Application app, string[] args, AppSettings settings)
    {
        string dir = Arg(args, "--nowplaying") ?? ".";
        Directory.CreateDirectory(dir);
        settings.Enabled = false;
        var controller = new PlayerController(settings);
        // The page lives inside MainWindow in the product; this QA host is just a window to render it.
        var view = new NowPlayingView(controller, settings);
        var window = new Window
        {
            Title = "QA · 正在播放页",
            Width = 520,
            Height = 880,
            Background = Brushes.Black,
            Content = view,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.Show();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        var lines = new List<string>();
        if (controller.Songs.Count == 0) RunBlocking(app, controller.ShowLibraryAsync());
        if (controller.Songs.Count > 0)
        {
            controller.Playback.SetVolume(0);
            var pick = controller.Songs[Math.Min(1, controller.Songs.Count - 1)];
            RunBlocking(app, controller.PlayAsync(pick));
            for (int i = 0; i < 20; i++)
            {
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(60);
            }
            lines.Add($"playing='{pick.DisplayTitle}' pos={controller.RawPositionMs:0} lyrics={(controller.Lyrics?.Lines.Count ?? 0)}");
        }

        foreach (var (label, width, height) in new[] { ("narrow", 520.0, 880.0), ("wide", 1240.0, 820.0) })
        {
            window.Width = width;
            window.Height = height;
            for (int i = 0; i < 12; i++) Pump(app, 40);
            Save(window, Path.Combine(dir, $"{label}.png"));
            lines.Add($"{label}: {view.DebugState()}");
            lines.Add($"   {view.DebugLyrics()}");
        }

        // 竖版封面页（点击标题区切换）
        window.Width = 520;
        window.Height = 880;
        view.DebugSetCompactHeader(false);
        for (int i = 0; i < 12; i++) Pump(app, 40);
        Save(window, Path.Combine(dir, "narrow-cover.png"));
        lines.Add($"narrow-cover: {view.DebugState()}");
        File.WriteAllLines(Path.Combine(dir, "report.txt"), lines);
        window.Close();
        app.Shutdown();
    }

    /// <summary>QA for the overlay's right-click menu: opens it at four screen points (including two
    /// corners) and records where the popup actually landed, with one screenshot per point.</summary>
    private static void RunMenuQa(Application app, string[] args, AppSettings settings)
    {
        string dir = Arg(args, "--menu-qa") ?? ".";
        Directory.CreateDirectory(dir);
        settings.Enabled = true;
        var controller = new PlayerController(settings);
        var window = new DesktopLyricsWindow(controller, settings);
        window.ShowOverlayForTest();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Thread.Sleep(400);

        var points = new (int x, int y)[] { (400, 300), (1500, 900), (60, 60), (1900, 1100) };
        var lines = new List<string>();
        for (int i = 0; i < points.Length; i++)
        {
            var (x, y) = points[i];
            lines.Add(window.DebugOpenMenuAt(x, y));
            for (int pump = 0; pump < 12; pump++)
            {
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(30);
            }
            using (var bitmap = new System.Drawing.Bitmap(2560, 1440))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(2560, 1440));
                bitmap.Save(Path.Combine(dir, $"menu-{i}.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            window.DebugCloseMenu();
            Thread.Sleep(260);
        }

        // Same thing once more, but through the real message path: PostMessage(WM_NCRBUTTONUP)
        // with packed screen coordinates is byte-for-byte what a right-click delivers.
        window.DebugPostNcRightUp(700, 480);
        for (int pump = 0; pump < 10; pump++)
        {
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(30);
        }
        lines.Add($"posted=700,480 {window.DebugMenuOrigin()}");
        window.DebugCloseMenu();
        Thread.Sleep(200);

        File.WriteAllLines(Path.Combine(dir, "menu-qa.txt"), lines);
        window.Close();
        app.Shutdown();
    }

    /// <summary>QA for the row-remove feature: remove → undo, checking playlists.json each time and
    /// capturing the hover state (mouse parked on the row) plus the undo bar.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>Posts one mouse message in DIPs relative to the window, so a QA can click like a user.</summary>
    private static void PostMouse(IntPtr hwnd, Point client, DpiScale dpi, int message, int buttons)
    {
        int x = (int)Math.Round(client.X * dpi.DpiScaleX);
        int y = (int)Math.Round(client.Y * dpi.DpiScaleY);
        int packed = ((y & 0xFFFF) << 16) | (x & 0xFFFF);
        PostMessage(hwnd, message, (IntPtr)buttons, (IntPtr)packed);
    }

    /// <summary>Short description of a visual, for reports.</summary>
    private static string Describe(DependencyObject? node) => node switch
    {
        null => "(null)",
        System.Windows.Controls.ListBoxItem item => $"ListBoxItem '{(item.DataContext as Playlist)?.Name}'",
        System.Windows.FrameworkElement element => $"{element.GetType().Name} '{element.Name}'",
        _ => node.GetType().Name,
    };

    /// <summary>Clicks a sidebar playlist row twice with the real mouse: the second click must reload
    /// it (it used to do nothing, because the row was already selected).</summary>
    private static void RunPlaylistReclick(Application app, string[] args, AppSettings settings)
    {
        string dir = Arg(args, "--playlist-reclick") ?? ".";
        Directory.CreateDirectory(dir);
        settings.Enabled = false;
        var controller = new PlayerController(settings);
        var main = new MainWindow(controller);
        main.Show();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        var lines = new List<string>();
        var wanted = Arg(args, "--name") ?? "";
        var playlist = controller.Playlists.FirstOrDefault(p => p.Name == wanted)
                       ?? controller.Playlists.FirstOrDefault();
        if (playlist == null)
        {
            lines.Add("no playlists in data\\playlists.json");
            File.WriteAllLines(Path.Combine(dir, "report.txt"), lines);
            main.Close();
            app.Shutdown();
            return;
        }

        main.Activate();
        main.Topmost = true;
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var list = main.FindName("lstPlaylists") as System.Windows.Controls.ListBox;
        System.Windows.Controls.ListBoxItem? row = null;
        if (list != null)
        {
            list.UpdateLayout();
            row = list.ItemContainerGenerator.ContainerFromItem(playlist) as System.Windows.Controls.ListBoxItem;
        }
        if (list == null || row == null)
        {
            lines.Add($"no row for playlist '{playlist.Name}' (list={list != null} row={row != null})");
            File.WriteAllLines(Path.Combine(dir, "report.txt"), lines);
            main.Topmost = false;
            main.Close();
            app.Shutdown();
            return;
        }

        var point = row.PointToScreen(new Point(row.ActualWidth / 2, row.ActualHeight / 2));

        // Always read the *current* container: Items.Refresh() replaces it.
        string RowText()
        {
            if (list.ItemContainerGenerator.ContainerFromItem(playlist) is not System.Windows.Controls.ListBoxItem current)
                return "(no container)";
            return string.Join(" | ", Descendants(current).OfType<System.Windows.Controls.TextBlock>().Select(t => t.Text ?? ""));
        }
        lines.Add($"playlist='{playlist.Name}' stored={playlist.Songs.Count} row={row.ActualWidth:0}x{row.ActualHeight:0} at {point.X:0},{point.Y:0}");
        // A click only reaches the app when the window is really on top and the point lands on the
        // row; log what is under that pixel so a miss is not mistaken for a dead handler.
        var under = main.InputHitTest(main.PointFromScreen(point)) as DependencyObject;
        lines.Add($"hit-under-point='{Describe(under)}' active={main.IsActive} topmost={main.Topmost}");

        void Click()
        {
            main.Activate();
            main.Topmost = true;
            Pump(app, 200);
            SetCursorPos((int)point.X, (int)point.Y);
            Pump(app, 250);
            MouseEvent(0x0002, 0, 0, 0);
            Pump(app, 80);
            MouseEvent(0x0004, 0, 0, 0);
            Pump(app, 1000);
        }

        // mouse_event is a no-op from a non-interactive runner, so the click path is exercised with
        // the same routed event WPF raises: PreviewMouseLeftButtonDown, delivered to the ListBox.
        void SyntheticClick(DependencyObject target)
        {
            if (target is not System.Windows.UIElement element) return;
            // PreviewMouseLeftButtonDown is a Direct event: real input raises it on every element of
            // the route, so raising it here on the ListBox (with the label as source) is the same call
            // the ListBox handler receives from a physical click.
            var clickArgs = new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.UIElement.PreviewMouseLeftButtonDownEvent,
                Source = element,
            };
            // Must run on the dispatcher: the handler touches UI (and a cross-thread raise makes its
            // Items.Refresh() throw "calling thread cannot access this object").
            app.Dispatcher.Invoke(() => list.RaiseEvent(clickArgs));
            Pump(app, 400);
        }
        var label = Descendants(row).OfType<System.Windows.Controls.TextBlock>()
            .FirstOrDefault(t => t.Text == playlist.Name) ?? (DependencyObject)row;

        // First click: the ListBox selects the row — that is what raises SelectionChanged in the app.
        list.SelectedItem = playlist;
        Pump(app, 900);
        lines.Add($"click 1 (select row): opens={main.DebugPlaylistOpens} view='{controller.ViewName}' " +
                  $"songs={controller.Songs.Count} selected='{(list.SelectedItem as Playlist)?.Name}' " +
                  $"subtitle='{controller.ViewSubtitle}'");
        Save(main, Path.Combine(dir, "click1.png"));

        // Second click on the same, already-selected row: used to do nothing at all. To prove it is a
        // real re-read and not just "open the same cached rows", a file is appended to the playlist
        // in memory first — the refresh must pick it up.
        var notifications = new List<string>();
        System.ComponentModel.PropertyChangedEventHandler watch = (_, ev) => notifications.Add(ev.PropertyName ?? "?");
        playlist.PropertyChanged += watch;
        var extraSource = playlist.Songs.FirstOrDefault(File.Exists);
        if (extraSource != null)
        {
            var extra = Path.Combine(AppPaths.DataDir, "extra-clip" + Path.GetExtension(extraSource));
            try { File.Copy(extraSource, extra, true); } catch { }
            if (File.Exists(extra))
            {
                playlist.Songs.Add(extra);
                lines.Add($"added '{Path.GetFileName(extra)}' in memory -> playlist now {playlist.Songs.Count} entries");
            }
        }
        SyntheticClick(label);
        Pump(app, 700);
        playlist.PropertyChanged -= watch;
        lines.Add($"click 2 (same row):    opens={main.DebugPlaylistOpens} view='{controller.ViewName}' " +
                  $"songs={controller.Songs.Count} subtitle='{controller.ViewSubtitle}'");
        lines.Add($"  handler: {main.DebugPlaylistClickLog}");
        lines.Add($"  notifications: {string.Join(",", notifications)}");
        lines.Add($"  sidebar row text: {RowText()}");
        lines.Add($"  model: entries={playlist.Songs.Count} existing={playlist.ExistingCount} countText='{playlist.CountText}'");
        Save(main, Path.Combine(dir, "click2.png"));

        // And once through the real window-message path (WM_LBUTTONDOWN/UP posted to the window),
        // which is what a mouse click actually delivers to the app.
        var clientPoint = list.ItemContainerGenerator.ContainerFromItem(playlist) is System.Windows.Controls.ListBoxItem liveRow
            ? liveRow.TranslatePoint(new Point(liveRow.ActualWidth / 2, liveRow.ActualHeight / 2), main)
            : new Point(0, 0);
        int beforePost = main.DebugPlaylistOpens;
        main.DebugPostLeftClick(clientPoint.X, clientPoint.Y);
        Pump(app, 900);
        lines.Add($"posted click:        opens delta={(main.DebugPlaylistOpens - beforePost)} " +
                  $"view='{controller.ViewName}' handler='{main.DebugPlaylistClickLog}'");
        Save(main, Path.Combine(dir, "click3.png"));

        // A click on a row that is not selected must not open it twice: selection change does that.
        var other = controller.Playlists.FirstOrDefault(p => !ReferenceEquals(p, playlist));
        if (other != null && list.ItemContainerGenerator.ContainerFromItem(other) is System.Windows.Controls.ListBoxItem otherRow)
        {
            var otherLabel = Descendants(otherRow).OfType<System.Windows.Controls.TextBlock>()
                .FirstOrDefault(t => t.Text == other.Name) ?? (DependencyObject)otherRow;
            int beforeOther = main.DebugPlaylistOpens;
            SyntheticClick(otherLabel);
            lines.Add($"click other '{other.Name}': opens delta={(main.DebugPlaylistOpens - beforeOther)} " +
                      $"view='{controller.ViewName}' selected='{(list.SelectedItem as Playlist)?.Name}'");
        }

        main.Topmost = false;
        File.WriteAllLines(Path.Combine(dir, "report.txt"), lines);
        main.Close();
        app.Shutdown();
    }

    /// <summary>QA: add a CLOUD row to a playlist — it has to download the track first and then put
    /// that saved file into the playlist.</summary>
    private static void RunAddOnlineToPlaylist(Application app, string[] args, AppSettings settings)
    {
        string dir = Arg(args, "--addtoplaylist-online") ?? ".";
        Directory.CreateDirectory(dir);
        settings.Enabled = false;
        // Keep the QA download out of the user's real download folder.
        settings.DownloadFolder = Path.Combine(Path.GetFullPath(dir), "dl");
        var controller = new PlayerController(settings);
        var main = new MainWindow(controller);
        main.Show();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        var keyword = Arg(args, "--online") ?? "打ち上げ花火";
        RunBlocking(app, controller.ShowOnlineSearchAsync(keyword));
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        var lines = new List<string>
        {
            $"keyword='{keyword}' view='{controller.ViewName}' rows={controller.Songs.Count} playlists={controller.Playlists.Count}",
            "menu: " + main.DebugAddPlaylistMenu(),
            "select: " + main.DebugSelectRow(0),
        };
        var add = main.DebugAddSelectedToPlaylist(Arg(args, "--name"));
        RunBlocking(app, add);
        lines.Add("add: " + add.Result);
        lines.Add("notice: " + (controller.Notice ?? "-"));
        lines.Add("download dir: " + settings.DownloadFolder);
        try
        {
            foreach (var file in Directory.GetFiles(settings.DownloadFolder))
                lines.Add($"  file: {Path.GetFileName(file)} {new FileInfo(file).Length / 1024} KB");
        }
        catch (Exception ex) { lines.Add("  files: " + ex.GetType().Name); }
        foreach (var playlist in controller.Playlists)
        {
            lines.Add($"  playlist '{playlist.Name}' entries={playlist.Songs.Count}");
            foreach (var entry in playlist.Songs) lines.Add("    - " + Path.GetFileName(entry));
        }

        Save(main, Path.Combine(dir, "online-add.png"));
        File.WriteAllLines(Path.Combine(dir, "report.txt"), lines);
        main.Close();
        app.Shutdown();
    }

    /// <summary>QA for "添加到歌单": the submenu content, adding to an existing playlist, and the
    /// no-playlist fallback that has to create 默认歌单.</summary>
    private static void RunAddToPlaylist(Application app, string[] args, AppSettings settings)
    {
        string dir = Arg(args, "--addtoplaylist") ?? ".";
        Directory.CreateDirectory(dir);
        settings.Enabled = false;
        var controller = new PlayerController(settings);
        var main = new MainWindow(controller);
        main.Show();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        var lines = new List<string>();
        var folder = Arg(args, "--folder");
        if (folder != null) RunBlocking(app, controller.ShowLocalAsync(folder));
        else RunBlocking(app, controller.ShowLibraryAsync());
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        lines.Add($"view='{controller.ViewName}' rows={main.DebugViewCount} playlists={controller.Playlists.Count}");
        lines.Add("menu: " + main.DebugAddPlaylistMenu());

        if (args.Contains("--delete-playlists"))
        {
            foreach (var playlist in controller.Playlists.ToList()) controller.DeletePlaylist(playlist);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            lines.Add($"after deleting all: playlists={controller.Playlists.Count} menu: {main.DebugAddPlaylistMenu()}");
            if (args.Contains("--picker"))
            {
                lines.Add("real picker: " + PickThroughDialog(app, main, lines, controller));
            }
            else
            {
                lines.Add("add with none:  " + main.DebugAddToPlaylist(0, null));
                lines.Add("menu again:     " + main.DebugAddPlaylistMenu());
                lines.Add("add again 2nd:  " + main.DebugAddToPlaylist(1, null));
            }
        }
        else
        {
            if (args.Contains("--picker"))
            {
                lines.Add("real picker: " + PickThroughDialog(app, main, lines, controller));
            }
            else
            {
                var name = Arg(args, "--name");
                var target = name == null ? controller.Playlists.FirstOrDefault() : controller.Playlists.FirstOrDefault(p => p.Name == name);
                lines.Add($"add to '{(target?.Name ?? "(none)")}': " + main.DebugAddToPlaylist(0, target?.Name));
                lines.Add("add same again: " + main.DebugAddToPlaylist(0, target?.Name));
            }
        }

        // What actually landed on disk.
        var file = Path.Combine(AppPaths.DataDir, "playlists.json");
        if (File.Exists(file))
        {
            foreach (var playlist in controller.Playlists)
                lines.Add($"  disk: '{playlist.Name}' entries={playlist.Songs.Count} " +
                          (playlist.Songs.Count > 0 ? $"first='{Path.GetFileName(playlist.Songs[0])}'" : ""));
        }

        Save(main, Path.Combine(dir, "library.png"));
        File.WriteAllLines(Path.Combine(dir, "report.txt"), lines);
        main.Close();
        app.Shutdown();
    }

    /// <summary>
    /// Drives the real 「添加到歌单…」 handler: it opens a modal picker, so a timer picks the first row
    /// while the dialog is running. This is the only way to test "no playlists → 默认歌单" the way the
    /// user hits it (the direct DebugAddToPlaylist call bypasses the picker entirely).
    /// </summary>
    private static string PickThroughDialog(Application app, MainWindow main, List<string> log, PlayerController controller)
    {
        string rows = "(picker never opened)";
        main.DebugSelectRow(0);
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var picker = main.DebugLastPicker;
            if (picker == null) { log.Add("  picker: not opened (added straight into 默认歌单)"); return; }
            rows = string.Join(" | ", picker.DebugRows());
            log.Add("  picker rows: " + rows);
            picker.DebugPick(0);
            log.Add("  picker closed");
        };
        timer.Start();
        main.DebugOpenAddToPlaylist();
        for (int i = 0; i < 14; i++) Pump(app, 40);
        log.Add("  notice: " + (controller.Notice ?? "-"));
        return $"rows=[{rows}] playlists={main.DebugPlaylistNames()}";
    }

    /// <summary>QA: open the row menu with 添加到歌单 expanded and grab the screen, because a popup is
    /// its own window and cannot be captured by rendering the visual tree.</summary>
    private static void RunSongMenu(Application app, string[] args, AppSettings settings)
    {
        string dir = Arg(args, "--songmenu") ?? ".";
        Directory.CreateDirectory(dir);
        settings.Enabled = false;
        var controller = new PlayerController(settings);
        var main = new MainWindow(controller);
        main.Width = 1180;
        main.Height = 760;
        main.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        main.Show();
        main.Activate();
        main.Topmost = true;
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        RunBlocking(app, controller.ShowLibraryAsync());
        Pump(app, 700);

        var lines = new List<string> { "menu: " + main.DebugAddPlaylistMenu() };
        lines.Add("open: " + main.DebugOpenSongMenu());
        Pump(app, 1000);

        var dpi = VisualTreeHelper.GetDpi(main);
        double screenW = SystemParameters.PrimaryScreenWidth;
        double screenH = SystemParameters.PrimaryScreenHeight;
        int x = Math.Max(0, (int)Math.Round(main.Left * dpi.DpiScaleX) - 16);
        int y = Math.Max(0, (int)Math.Round(main.Top * dpi.DpiScaleY) - 16);
        int w = Math.Min((int)Math.Round(screenW * dpi.DpiScaleX) - x, (int)Math.Round(main.ActualWidth * dpi.DpiScaleX) + 120);
        int h = Math.Min((int)Math.Round(screenH * dpi.DpiScaleY) - y, (int)Math.Round(main.ActualHeight * dpi.DpiScaleY) + 60);
        try
        {
            using var bitmap = new System.Drawing.Bitmap(Math.Max(1, w), Math.Max(1, h));
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(Math.Max(1, w), Math.Max(1, h)));
            }
            bitmap.Save(Path.Combine(dir, "songmenu.png"), System.Drawing.Imaging.ImageFormat.Png);
            lines.Add($"shot {w}x{h} at {x},{y} dpi={dpi.DpiScaleX:0.00}");
        }
        catch (Exception ex)
        {
            lines.Add("shot failed: " + ex.GetType().Name + ": " + ex.Message);
        }

        main.DebugCloseSongMenu();
        Pump(app, 200);
        main.Topmost = false;
        File.WriteAllLines(Path.Combine(dir, "report.txt"), lines);
        main.Close();
        app.Shutdown();
    }

    /// <summary>QA: 另存到 for a local row (file copy + sidecars) and for a cloud row (download into
    /// the picked folder instead of the configured download folder).</summary>
    private static void RunSaveAs(Application app, string[] args, AppSettings settings)
    {
        string dir = Arg(args, "--saveas") ?? ".";
        Directory.CreateDirectory(dir);
        settings.Enabled = false;
        var controller = new PlayerController(settings);
        var main = new MainWindow(controller);
        main.Show();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        RunBlocking(app, controller.ShowLibraryAsync());
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        var lines = new List<string>();
        var localOut = Path.Combine(Path.GetFullPath(dir), "local-out");
        lines.Add("local: " + main.DebugSaveLocalTo(0, localOut));
        try
        {
            foreach (var file in Directory.GetFiles(localOut))
                lines.Add($"  file: {Path.GetFileName(file)} {new FileInfo(file).Length / 1024} KB");
        }
        catch (Exception ex) { lines.Add("  files: " + ex.GetType().Name); }

        // Cloud row: the folder the user picked is where the audio must land, not the configured one.
        var keyword = Arg(args, "--online") ?? "打ち上げ花火";
        RunBlocking(app, controller.ShowOnlineSearchAsync(keyword));
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        if (controller.Songs.Count > 0)
        {
            var song = controller.Songs[0];
            var cloudOut = Path.Combine(Path.GetFullPath(dir), "cloud-out");
            var saved = controller.SaveSongToAsync(song, cloudOut);
            RunBlocking(app, saved);
            lines.Add($"cloud: ok={saved.Result.Ok} error={saved.Result.Error ?? "-"} " +
                      $"file={Path.GetFileName(saved.Result.Saved ?? "-")}");
            try
            {
                foreach (var file in Directory.GetFiles(cloudOut))
                    lines.Add($"  file: {Path.GetFileName(file)} {new FileInfo(file).Length / 1024} KB");
            }
            catch (Exception ex) { lines.Add("  files: " + ex.GetType().Name); }
        }
        else
        {
            lines.Add("cloud: search returned nothing");
        }

        File.WriteAllLines(Path.Combine(dir, "report.txt"), lines);
        main.Close();
        app.Shutdown();
    }

    /// <summary>QA: right-click a row for real (posted WM_RBUTTONDOWN/UP), then hover 添加到歌单 so the
    /// submenu has to open by itself — this is what the user reported as broken.</summary>
    private static void RunRightMenu(Application app, string[] args, AppSettings settings)
    {
        string dir = Arg(args, "--rightmenu") ?? ".";
        Directory.CreateDirectory(dir);
        settings.Enabled = false;
        var controller = new PlayerController(settings);
        var main = new MainWindow(controller);
        main.Width = 1180;
        main.Height = 760;
        main.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        main.Show();
        main.Activate();
        main.Topmost = true;
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        RunBlocking(app, controller.ShowLibraryAsync());
        Pump(app, 700);

        var lines = new List<string> { "before: " + main.DebugSongMenuState() };
        var list = main.FindName("lstSongs") as System.Windows.Controls.ListBox;
        System.Windows.Controls.ListBoxItem? row = null;
        if (list != null)
        {
            list.UpdateLayout();
            row = list.ItemContainerGenerator.ContainerFromIndex(0) as System.Windows.Controls.ListBoxItem;
        }
        if (row == null)
        {
            lines.Add("no rows to click");
            File.WriteAllLines(Path.Combine(dir, "report.txt"), lines);
            main.Close();
            app.Shutdown();
            return;
        }

        var clientPoint = row.TranslatePoint(new Point(120, row.ActualHeight / 2), main);
        main.DebugPostRightClick(clientPoint.X, clientPoint.Y);
        Pump(app, 900);
        lines.Add("after right click: " + main.DebugSongMenuState());
        Pump(app, 500);
        // The picker is a real window, so it can be rendered off-screen for the report.
        lines.Add("picker: " + main.DebugOpenPlaylistPicker());
        Pump(app, 400);
        Save(main.DebugPickerWindow, Path.Combine(dir, "picker.png"));

        var pick = main.DebugPickRow(int.TryParse(Arg(args, "--pick"), out var pickRow) ? pickRow : 0);
        lines.Add("pick: " + pick);
        var match = System.Text.RegularExpressions.Regex.Match(pick, "picked='([^']*)'");
        var pickedName = match.Success ? match.Groups[1].Value : "";
        var add = main.DebugAddSelectedToPlaylist(pickedName == "(default)" || pickedName.Length == 0 ? null : pickedName);
        RunBlocking(app, add);
        lines.Add("add: " + add.Result);
        lines.Add("notice: " + (controller.Notice ?? "-"));
        foreach (var playlist in controller.Playlists)
            lines.Add($"  playlist '{playlist.Name}' entries={playlist.Songs.Count}");

        // Grab the screen: menus are separate windows, so the visual tree cannot be rendered.
        var dpi = VisualTreeHelper.GetDpi(main);
        int x = Math.Max(0, (int)Math.Round(main.Left * dpi.DpiScaleX) - 16);
        int y = Math.Max(0, (int)Math.Round(main.Top * dpi.DpiScaleY) - 16);
        int w = (int)Math.Round(main.ActualWidth * dpi.DpiScaleX) + 140;
        int h = (int)Math.Round(main.ActualHeight * dpi.DpiScaleY) + 80;
        w = Math.Min(w, (int)Math.Round(SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX) - x);
        h = Math.Min(h, (int)Math.Round(SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY) - y);
        try
        {
            using var bitmap = new System.Drawing.Bitmap(Math.Max(1, w), Math.Max(1, h));
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(Math.Max(1, w), Math.Max(1, h)));
            bitmap.Save(Path.Combine(dir, "rightmenu.png"), System.Drawing.Imaging.ImageFormat.Png);
            lines.Add($"shot {w}x{h}");
        }
        catch (Exception ex) { lines.Add("shot failed: " + ex.GetType().Name); }

        main.DebugCloseMenus();
        main.Topmost = false;
        File.WriteAllLines(Path.Combine(dir, "report.txt"), lines);
        main.Close();
        app.Shutdown();
    }

    private static void RunRowRemove(Application app, string[] args, AppSettings settings)
    {
        string dir = Arg(args, "--rowremove") ?? ".";
        Directory.CreateDirectory(dir);
        settings.Enabled = false;
        var controller = new PlayerController(settings);
        var main = new MainWindow(controller);
        main.Show();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        var playlist = controller.Playlists.FirstOrDefault();
        var lines = new List<string>();
        if (args.Contains("--library"))
        {
            RunBlocking(app, controller.ShowLibraryAsync());
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            lines.Add($"view='{controller.ViewName}' songs={controller.Songs.Count} libraryFiles={settings.LibraryFiles.Count}");
        }
        else if (playlist != null)
        {
            RunBlocking(app, controller.ShowPlaylistAsync(playlist));
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            lines.Add($"view='{controller.ViewName}' songs={controller.Songs.Count}");
        }

        string dataFile = Path.Combine(AppPaths.DataDir, args.Contains("--library") ? "settings.json" : "playlists.json");
        string Before() => File.Exists(dataFile) ? File.ReadAllText(dataFile) : "";
        string before = Before();
        lines.Add($"before entries={SongCount(before)}");

        // Park the mouse on row #2 so the duration→remove swap is actually visible in the shot.
        main.Activate();
        main.Topmost = true;
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        if (main.FindName("lstSongs") is System.Windows.Controls.ListBox list
            && list.ItemContainerGenerator.ContainerFromIndex(1) is System.Windows.Controls.ListBoxItem row)
        {
            var point = row.PointToScreen(new Point(row.ActualWidth - 40, row.ActualHeight / 2));
            SetCursorPos((int)point.X, (int)point.Y);
            for (int i = 0; i < 10; i++)
            {
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(60);
            }
            Save(main, Path.Combine(dir, "hover.png"));
            var button = Descendants(row).OfType<System.Windows.Controls.Button>()
                .FirstOrDefault(b => b.ToolTip is string tip && tip.StartsWith("从当前列表移除"));
            lines.Add($"hover row#2 at {point.X:0},{point.Y:0} isMouseOver={row.IsMouseOver} " +
                      $"deleteOpacity={(button?.Opacity ?? -1):0.00} hitTest={button?.IsHitTestVisible}");
        }
        main.Topmost = false;

        int target = int.TryParse(Arg(args, "--index"), out var index) ? index : 2;
        lines.Add(main.DebugRemoveAt(target));
        Pump(app, 500);
        lines.Add("bar " + main.DebugUndoState());
        Save(main, Path.Combine(dir, "after-remove.png"));
        lines.Add($"after entries={SongCount(Before())}");

        lines.Add(main.DebugUndoRemove());
        Pump(app, 500);
        string restored = Before();
        lines.Add($"restored entries={SongCount(restored)} identical={restored == before}");
        Save(main, Path.Combine(dir, "after-undo.png"));

        File.WriteAllLines(Path.Combine(dir, "report.txt"), lines);
        main.Close();
        app.Shutdown();
    }

    /// <summary>Lets animations advance: sleeping alone freezes the WPF clock, so keep the
    /// dispatcher busy for <paramref name="ms"/>.</summary>
    private static void Pump(Application app, int ms)
    {        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Thread.Sleep(16);
        }
    }

    /// <summary>Runs a real dispatcher frame, so timers, layout and media events actually fire.</summary>
    private static void PumpFrames(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var deep = FindDescendant<T>(child);
            if (deep != null) return deep;
        }
        return null;
    }

    private static List<T> Collect<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) found.Add(hit);
            found.AddRange(Collect<T>(child));
        }
        return found;
    }

    private static string Node(DependencyObject? d)
    {
        if (d == null) return "(null)";
        var name = d is FrameworkElement fe ? fe.Name : "";
        var type = d.GetType().Name;
        if (d is FrameworkElement e2) return $"{type}{(name.Length > 0 ? "#" + name : "")}[{e2.ActualWidth:0}x{e2.ActualHeight:0}]";
        return type;
    }

    private static List<string> TemplateParts(FrameworkElement root)
    {
        var list = new List<string>();
        void Walk(DependencyObject d)
        {
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(d, i);
                if (child is System.Windows.Controls.Primitives.Thumb or System.Windows.Controls.Primitives.Track
                    or System.Windows.Controls.Primitives.RepeatButton)
                {
                    var b = ((FrameworkElement)child).TransformToAncestor(root)
                        .TransformBounds(new Rect(0, 0, ((FrameworkElement)child).ActualWidth, ((FrameworkElement)child).ActualHeight));
                    list.Add($"{Node(child)} at {b.X:0.#},{b.Y:0.#} {b.Width:0.#}x{b.Height:0.#}");
                }
                Walk(child);
            }
        }
        Walk(root);
        return list;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    private static void MouseEvent(uint flags, uint dx, uint dy, uint data) => mouse_event(flags, dx, dy, data, UIntPtr.Zero);

    /// <summary>Counts audio entries in playlists.json without deserialising.</summary>
    private static int SongCount(string json)
    {
        int count = 0, i = 0;
        while ((i = json.IndexOf(".flac", i, StringComparison.OrdinalIgnoreCase)) >= 0) { count++; i += 5; }
        i = 0;
        while ((i = json.IndexOf(".mp3", i, StringComparison.OrdinalIgnoreCase)) >= 0) { count++; i += 4; }
        return count;
    }

    private static void RunUiShot(Application app, string[] args, AppSettings settings)
    {
        string dir = Arg(args, "--ui-shot") ?? ".";
        Directory.CreateDirectory(dir);
        var folder = Arg(args, "--folder");

        var controller = new PlayerController(settings);
        var main = new MainWindow(controller);
        if (double.TryParse(Arg(args, "--winwidth"), out var winWidth) && winWidth >= 900)
            main.Width = winWidth;
        main.Show();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        if (folder != null)
            RunBlocking(app, controller.ShowLocalAsync(folder));

        // QA: open the online search view with a keyword, then report what came back.
        var onlineKeyword = Arg(args, "--online");
        if (onlineKeyword != null)
        {
            RunBlocking(app, controller.ShowOnlineSearchAsync(onlineKeyword));
            var notes = new List<string>
            {
                $"keyword={onlineKeyword} songs={controller.Songs.Count} subtitle={controller.ViewSubtitle} " +
                $"cover={(controller.ViewCoverUrl != null ? "url" : "none")} notice={controller.Notice ?? "-"}",
            };
            for (int i = 0; i < Math.Min(6, controller.Songs.Count); i++)
            {
                var song = controller.Songs[i];
                notes.Add($"  {i + 1}. {song.DisplayTitle} - {song.DisplayArtist}  {song.DurationText}  netease={song.NeteaseId} coverLoaded={song.Cover != null}");
            }
            var onlinePlay = args.Contains("--online-play");
            var lxImport = Arg(args, "--lx-import");
            // The real app has the keyword sitting in the search box; put it there so the
            // leave/return flow is exercised the way the user triggers it.
            if (args.Contains("--then-local") || args.Contains("--then-online"))
                main.DebugSetSearchText(onlineKeyword);
            if (lxImport != null)
            {
                try
                {
                    var entry = Services.LxStore.Import(lxImport);
                    Services.LxStore.SetActive(entry.Id);
                    notes.Add($"lx import: name={entry.Name} error={entry.Error ?? "-"} " +
                              $"platforms=[{string.Join(", ", entry.Platforms.Select(p => p.Key + ":" + string.Join("/", p.Value)))}] " +
                              $"active={Services.LxStore.ActiveId == entry.Id} smart={Services.LxStore.SmartSwitch}");
                }
                catch (Exception ex)
                {
                    notes.Add("lx import failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            if (onlinePlay && controller.Songs.Count > 0)
            {
                if (args.Contains("--cover-backdrop")) main.DebugUseCoverBackdrop();
                controller.Playback.SetVolume(0);
                RunBlocking(app, controller.PlayAsync(controller.Songs[0]));
                for (int i = 0; i < 40; i++)
                {
                    app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    Thread.Sleep(150);
                    if (controller.RawPositionMs > 1200) break;
                }
                // The artwork is a second network request; give it time before judging the backdrop.
                for (int i = 0; i < 60; i++)
                {
                    app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    Thread.Sleep(150);
                    if (main.DebugCoverState().Contains("backdrop=bitmap")) break;
                }
                notes.Add("cover: " + main.DebugCoverState());
                Save(main, Path.Combine(dir, "online-cover.png"));
                notes.Add($"play: state={controller.PlaybackState} pos={controller.RawPositionMs:0} " +
                          $"dur={controller.Playback.DurationMs:0} level={controller.Songs[0].StreamLevel ?? "-"} " +
                          $"url={(controller.Songs[0].StreamUrl == null ? "null" : "ok")} notice={controller.Notice ?? "-"} " +
                          $"lyrics={controller.Lyrics?.Lines.Count ?? 0} lx={controller.LastLxSource} " +
                          $"lxError={controller.LastLxError ?? "-"}");
            }

            if (args.Contains("--online-download") && controller.Songs.Count > 0)            {
                // Keep the QA download out of the real download folder.
                controller.Settings.DownloadFolder = Path.Combine(Path.GetFullPath(dir), "dl");
                Directory.CreateDirectory(controller.Settings.DownloadFolder);
                var downloadFolder = controller.Settings.DownloadFolder!;
                var download = controller.DownloadSongAsync(controller.Songs[0]);
                RunBlocking(app, download);
                var files = Directory.GetFiles(downloadFolder).Select(Path.GetFileName).ToList();
                notes.Add($"download ok={download.IsCompletedSuccessfully && download.Result} notice={controller.Notice ?? "-"} " +
                          $"files=[{string.Join(", ", files)}] " +
                          $"sizes=[{string.Join(", ", files.Select(f => (new FileInfo(Path.Combine(downloadFolder, f!)).Length / 1024) + "KB"))}]");
            }
            if (args.Contains("--then-local"))
            {
                // Regression: switching from the online view back to the library must not inherit the
                // search keyword, which used to filter every local row away.
                RunBlocking(app, controller.ShowLibraryAsync());
                notes.Add($"then local: view={controller.ViewName} online={controller.IsOnlineView} " +
                          $"songs={controller.Songs.Count} rows={main.DebugViewCount}");
                notes.Add("  " + main.DebugOnlineState());
            }
            if (args.Contains("--then-online"))
            {
                // Regression (2026-11-26): clicking 在线搜索 again, or coming back from 本地音乐, must
                // restore the keyword and its results instead of blanking the box and the list.
                if (!args.Contains("--then-local")) RunBlocking(app, controller.ShowLibraryAsync());
                main.DebugEnterOnline();
                for (int i = 0; i < 100; i++)
                {
                    app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    Thread.Sleep(120);
                    if (controller.IsOnlineView && !controller.Busy && controller.Songs.Count > 0) break;
                }
                notes.Add("re-enter online: " + main.DebugOnlineState());
                // Clicking it again while already there must not re-search into an empty keyword.
                main.DebugEnterOnline();
                for (int i = 0; i < 6; i++) Pump(app, 80);
                notes.Add("click again:    " + main.DebugOnlineState());
            }
            File.WriteAllLines(Path.Combine(dir, "online.txt"), notes);
        }

        // QA: open a playlist by name (or the first one) so its header can be screenshotted.
        var playlistName = Arg(args, "--playlist");
        if (playlistName != null)
        {
            var target = controller.Playlists.FirstOrDefault(p => p.Name == playlistName)
                         ?? controller.Playlists.FirstOrDefault();
            if (target != null) RunBlocking(app, controller.ShowPlaylistAsync(target));
            File.WriteAllText(Path.Combine(dir, "playlist.txt"),
                $"opened={target?.Name ?? "(none)"} cover={(controller.ViewCoverImage != null ? "bitmap" : controller.ViewCoverUrl ?? "none")}");
        }

        // QA: play a local row with the output muted, so the player bar and the highlight on the
        // current row can be screenshotted without making noise.
        var playIndex = Arg(args, "--playindex");
        if (playIndex != null && int.TryParse(playIndex, out var rowIndex))
        {
            // The startup library load is async; make sure the list exists before indexing it.
            if (controller.Songs.Count == 0) RunBlocking(app, controller.ShowLibraryAsync());
            if (rowIndex >= 0 && rowIndex < controller.Songs.Count)
            {
                controller.Playback.SetVolume(0);
                var pick = controller.Songs[rowIndex];
                RunBlocking(app, controller.PlayAsync(pick));
                for (int i = 0; i < 12; i++)
                {
                    app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    Thread.Sleep(120);
                }
                File.WriteAllText(Path.Combine(dir, "play.txt"),
                    $"playing={pick.DisplayTitle} current={controller.Current?.DisplayTitle} " +
                    $"isCurrent={pick.IsCurrent} state={controller.PlaybackState} pos={controller.RawPositionMs:0}");
            }
        }

        var playId = Arg(args, "--playid");        if (playId != null && long.TryParse(playId, out var id))
        {
            var song = new Models.Song
            {
                Source = Models.SongSource.Netease,
                Path = $"netease:{id}",
                NeteaseId = id,
                Title = "願い～あの頃のキミへ～",
                Artist = "當山みれい",
                Album = "願い〜あの頃のキミへ〜",
                Duration = TimeSpan.FromSeconds(340),
            };
            RunBlocking(app, controller.PlayAsync(song));
            for (int i = 0; i < 20; i++)
            {
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(150);
            }
        }

        // Let the startup WSA import / cover downloads settle before the shot.
        for (int i = 0; i < 20; i++)
        {
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(200);
        }
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Save(main, Path.Combine(dir, "main.png"));
        File.WriteAllText(Path.Combine(dir, "layout.txt"), main.DebugLayout());

        // QA: open one tooltip so its themed look can be screenshotted (the stock WPF one is white).
        if (args.Contains("--tooltip"))
        {
            var tip = new System.Windows.Controls.ToolTip
            {
                Content = "点击空白处打开正在播放页",
                PlacementTarget = main,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Center,
                StaysOpen = true,
                IsOpen = true,
            };
            for (int i = 0; i < 6; i++) Pump(app, 60);
            // A tooltip is its own window, so rendering the visual tree cannot see it; grab pixels.
            var tipDpi = VisualTreeHelper.GetDpi(main);
            int sx = (int)Math.Round(main.Left * tipDpi.DpiScaleX);
            int sy = (int)Math.Round(main.Top * tipDpi.DpiScaleY);
            int sw = Math.Max(1, (int)Math.Round(main.ActualWidth * tipDpi.DpiScaleX));
            int sh = Math.Max(1, (int)Math.Round(main.ActualHeight * tipDpi.DpiScaleY));
            using (var bmp = new System.Drawing.Bitmap(sw, sh))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(sx, sy, 0, 0, new System.Drawing.Size(sw, sh));
                bmp.Save(Path.Combine(dir, "tooltip.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            tip.IsOpen = false;
        }

        // In-window now-playing page: narrow → wide → back to the library, all in the same window.
        if (args.Contains("--playerpage"))
        {
            var pageLines = new List<string>();
            main.DebugOpenPlayerPage();
            for (int i = 0; i < 10; i++) Pump(app, 40);
            Save(main, Path.Combine(dir, "page-narrow.png"));
            pageLines.Add($"narrow: {main.DebugPlayerPageState()}");

            main.Width = 1240;
            main.Height = 820;
            for (int i = 0; i < 12; i++) Pump(app, 40);
            Save(main, Path.Combine(dir, "page-wide.png"));
            pageLines.Add($"wide: {main.DebugPlayerPageState()}");

            main.Width = 1180;
            main.Height = 760;
            main.DebugClosePlayerPage();
            for (int i = 0; i < 10; i++) Pump(app, 40);
            Save(main, Path.Combine(dir, "page-closed.png"));
            pageLines.Add($"closed: {main.DebugPlayerPageState()}");

            // 拖动性能：连续改宽度，统计每帧的 CPU 时间（之前每帧都在跑 42px 高斯模糊）
            main.DebugOpenPlayerPage();
            main.Width = 1180;
            main.Height = 760;
            for (int i = 0; i < 8; i++) Pump(app, 40);
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            var cpu0 = proc.TotalProcessorTime;
            var swResize = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 40; i++)
            {
                main.Width = 1180 - i * 12;
                main.UpdateLayout();
                Pump(app, 12);
            }
            swResize.Stop();
            var cpu1 = proc.TotalProcessorTime;
            pageLines.Add($"resize 40 steps: wall={swResize.ElapsedMilliseconds}ms " +
                          $"cpu={(cpu1 - cpu0).TotalMilliseconds:0}ms ({((cpu1 - cpu0).TotalMilliseconds / 40):0.0} ms/step)");

            // 竖版形态：把窗口压到 520×880（页面打开时窗口最小尺寸已放宽）
            main.Width = 520;
            main.Height = 880;
            for (int i = 0; i < 12; i++) Pump(app, 40);
            Save(main, Path.Combine(dir, "page-portrait.png"));
            pageLines.Add($"portrait: {main.DebugPlayerPageState()} window={main.ActualWidth:0}x{main.ActualHeight:0} " +
                          $"{main.PlayerView?.DebugState()}");

            // ⋯ 菜单是 Popup（独立 HWND），RenderTargetBitmap 拍不到，只能抓屏
            main.PlayerView?.DebugOpenMoreMenu();
            for (int i = 0; i < 10; i++) Pump(app, 40);
            using (var bitmap = new System.Drawing.Bitmap(2560, 1440))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(2560, 1440));
                bitmap.Save(Path.Combine(dir, "page-more.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            pageLines.Add("more menu: screen captured");
            // —— 拖动/缩放帧率：页面开着时分开量「静态 / 移动 / 缩放 / 关掉主题背景再缩放」——
            // 移动和缩放都用真实的窗口属性驱动，走的是用户拖窗口时同一条 SetWindowPos 路径。
            if (args.Contains("--page-fps"))
            {
                main.DebugOpenPlayerPage();
                main.Width = 1240;
                main.Height = 820;
                for (int i = 0; i < 16; i++) PumpFrames(40);
                pageLines.Add("backdrop: " + (main.PlayerView?.DebugBackdropEdge() ?? "null"));
                pageLines.Add("state: " + (main.PlayerView?.DebugState() ?? "null"));

                void Phase(string tag, int ms, Action<int> step)
                {
                    main.StartFrameMonitor(ms);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    int n = 0;
                    while (clock.ElapsedMilliseconds < ms)
                    {
                        step(n++);
                        PumpFrames(8);
                    }
                    PumpFrames(150);
                    pageLines.Add($"{tag,-14} {main.DebugFrameStats()}");
                }

                Phase("static", 1600, _ => { });
                {
                    var cpuProcS = System.Diagnostics.Process.GetCurrentProcess();
                    var cpuS0 = cpuProcS.TotalProcessorTime;
                    PumpFrames(1500);
                    var cpuS1 = cpuProcS.TotalProcessorTime;
                    pageLines.Add($"refresh={NativeMethods.DisplayRefreshHz()}Hz " +
                                  $"static cpu={((cpuS1 - cpuS0).TotalMilliseconds / 1500 * 100):0}% of one core");
                }
                if (main.PlayerView != null) pageLines.Add("render: " + main.PlayerView.DebugRenderStats());

                // 把歌词刷新提到「每帧一次」（= 屏幕刷新率），量现在这套渲染能不能撑住
                var pageView = main.PlayerView;
                if (pageView != null)
                {
                    // A/B：同一套界面里切换「烘焙贴图」与「字形轮廓填充」
                    Controls.LyricStackView.UseBakedText = false;
                    pageView.RefreshFromSettings();
                    PumpFrames(250);
                    Phase("fps-geometry", 1400, _ => { });
                    pageLines.Add("draw cost geometry: " + pageView.DebugDrawCost());
                    Controls.LyricStackView.UseBakedText = true;
                    pageView.RefreshFromSettings();
                    PumpFrames(250);
                    Phase("fps-baked", 1400, _ => { });
                    pageLines.Add("draw cost baked   : " + pageView.DebugDrawCost());
                }
                if (pageView != null)
                {
                    var tickClock = System.Diagnostics.Stopwatch.StartNew();
                    System.EventHandler tickEveryFrame = (_, _) =>
                        pageView.DebugAdvance((long)tickClock.Elapsed.TotalMilliseconds);
                    System.Windows.Media.CompositionTarget.Rendering += tickEveryFrame;
                    Phase("tick-every-frame", 1600, _ => { });
                    pageLines.Add("render: " + pageView.DebugRenderStats());
                    System.Windows.Media.CompositionTarget.Rendering -= tickEveryFrame;

                    var swTick = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < 120; i++) pageView.DebugAdvance(20000 + i * 8);
                    swTick.Stop();
                    pageLines.Add($"tick cost: {swTick.Elapsed.TotalMilliseconds / 120:0.00} ms/tick " +
                                  $"(120 ticks, 静止调用)");

                    // 成本归因：逐项关掉，看帧率往哪走
                    pageLines.Add($"render tier: {System.Windows.Media.RenderCapability.Tier >> 16} " +
                                  $"hw={(System.Windows.Media.RenderCapability.IsPixelShaderVersionSupported(2, 0) ? 1 : 0)}");
                    var tickClock2 = System.Diagnostics.Stopwatch.StartNew();
                    System.EventHandler tickAlways = (_, _) =>
                        pageView.DebugAdvance((long)tickClock2.Elapsed.TotalMilliseconds);
                    System.Windows.Media.CompositionTarget.Rendering += tickAlways;

                    pageView.DebugSetPartVisible("backdrop", false);
                    Phase("no-backdrop", 1200, _ => { });
                    pageView.DebugSetPartVisible("backdrop", true);

                    pageView.DebugSetPartVisible("lyrics", false);
                    Phase("no-lyrics", 1200, _ => { });
                    pageView.DebugSetPartVisible("lyrics", true);

                    settings.PlayerGlow = false;
                    pageView.RefreshFromSettings();
                    Phase("no-glow", 1200, _ => { });
                    settings.PlayerGlow = true;
                    pageView.RefreshFromSettings();

                    settings.PlayerPerspective = false;
                    pageView.RefreshFromSettings();
                    Phase("no-perspective", 1200, _ => { });
                    settings.PlayerPerspective = true;
                    pageView.RefreshFromSettings();

                    // 稳定的软件绘制成本（不受机器负载影响），用它做归因
                    pageLines.Add("draw cost         : " + pageView.DebugDrawCost());
                    settings.PlayerGlow = false;
                    pageView.RefreshFromSettings();
                    pageLines.Add("draw cost glowOff : " + pageView.DebugDrawCost());
                    settings.PlayerGlow = true;
                    pageView.RefreshFromSettings();
                    settings.PlayerPerspective = false;
                    pageView.RefreshFromSettings();
                    pageLines.Add("draw cost perspOff: " + pageView.DebugDrawCost());
                    settings.PlayerPerspective = true;
                    pageView.RefreshFromSettings();

                    System.Windows.Media.CompositionTarget.Rendering -= tickAlways;
                }
                double lx = main.Left, ly = main.Top;
                Phase("move", 1600, i =>
                {
                    main.Left = lx + (i % 60) * 6;
                    main.Top = ly + (i % 18);
                });
                main.Left = lx;
                main.Top = ly;
                main.UpdateLayout();
                // 用户真的按住拖动时，窗口进入 move 模态循环（WM_ENTERSIZEMOVE），页面按新逻辑挂起重绘
                var handle = new System.Windows.Interop.WindowInteropHelper(main).Handle;
                string beforeHold = pageView?.DebugRenderStats() ?? "n/a";
                NativeMethods.SendMessage(handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
                PumpFrames(150);
                Phase("move-held", 1600, i =>
                {
                    main.Left = lx + (i % 60) * 6;
                    main.Top = ly + (i % 18);
                });
                pageLines.Add($"held renders: {beforeHold} -> {pageView?.DebugRenderStats()}");
                main.Left = lx;
                main.Top = ly;
                NativeMethods.SendMessage(handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
                Phase("resume", 800, _ => { });
                Phase("resize", 1600, i =>
                {
                    main.Width = 1240 - (i % 50) * 4;
                    main.UpdateLayout();
                });
                main.Width = 1240;
                PumpFrames(200);
                // 修前的状态：页面开着但主题背景还活着（全窗高斯模糊留在渲染树里）
                main.DebugSetThemeBackdrop(true);
                PumpFrames(200);
                Phase("resize+theme", 1600, i =>
                {
                    main.Width = 1240 - (i % 50) * 4;
                    main.UpdateLayout();
                });
                main.Width = 1240;
                main.DebugSetThemeBackdrop(false);

                // 单步 CPU：和上面 playerpage 口径相同，用来对比修前修后
                for (int i = 0; i < 8; i++) PumpFrames(40);
                var proc2 = System.Diagnostics.Process.GetCurrentProcess();
                var cpuA = proc2.TotalProcessorTime;
                var swA = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 40; i++)
                {
                    main.Width = 1180 - i * 12;
                    main.UpdateLayout();
                    PumpFrames(12);
                }
                swA.Stop();
                var cpuB = proc2.TotalProcessorTime;
                pageLines.Add($"resize 40 steps: wall={swA.ElapsedMilliseconds}ms " +
                              $"cpu={(cpuB - cpuA).TotalMilliseconds:0}ms ({((cpuB - cpuA).TotalMilliseconds / 40):0.0} ms/step)");
            }

            File.WriteAllLines(Path.Combine(dir, "page.txt"), pageLines);
        }

        if (args.Contains("--cover-shot"))
        {
            main.DebugUseCoverBackdrop();
            for (int i = 0; i < 8; i++) Pump(app, 60);
            Save(main, Path.Combine(dir, "cover-backdrop.png"));
            File.WriteAllText(Path.Combine(dir, "cover-backdrop.txt"),
                $"current={controller.Current?.DisplayTitle ?? "(none)"} cover={(controller.Current?.Cover != null ? "bitmap" : "none")}");
        }
        if (args.Contains("--theme-blur-shot"))
        {
            // QA: 背景磨砂滑条。必须走 ApplySettings（滑条真正的路径），不是 ApplyAppearance。
            var originalBlur = settings.ThemeBlur;
            var originalImage = settings.ThemeImage;
            string? qaImage = Arg(args, "--image");
            if (qaImage != null && File.Exists(qaImage))
            {
                // 磨砂要有底图才看得见；--image 给一张真图，比封面更可控。
                settings.ThemeImage = qaImage;
                main.ApplyAppearance();
            }
            else
            {
                main.DebugUseCoverBackdrop();   // 需要有底图，否则磨砂没有东西可糊
            }
            for (int i = 0; i < 6; i++) Pump(app, 60);
            var lines = new List<string>();
            foreach (var blur in new[] { 0.0, 20.0, 40.0, 60.0 })
            {
                settings.ThemeBlur = blur;
                main.DebugApplySettings();
                for (int k = 0; k < 6; k++) Pump(app, 60);
                Save(main, Path.Combine(dir, $"blur-{blur:0}.png"));
                lines.Add($"blur={blur:0} effect={main.DebugThemeEffect()} " +
                          $"{main.DebugSaveThemeImage(Path.Combine(dir, $"img-{blur:0}.png"))}");
            }
            settings.ThemeBlur = originalBlur;
            settings.ThemeImage = originalImage;
            main.DebugApplySettings();
            main.ApplyAppearance();
            for (int i = 0; i < 6; i++) Pump(app, 60);
            Save(main, Path.Combine(dir, "blur-restored.png"));
            lines.Add($"restored={originalBlur:0} effect={main.DebugThemeEffect()}");
            File.WriteAllLines(Path.Combine(dir, "theme-blur.txt"), lines);
        }
        if (args.Contains("--tint-shot"))
        {
            // QA: walk the 背景色 from white to black (plus two chromatic picks) in memory only, so the
            // ramp can be checked for continuity. Nothing is written to settings.
            var original = settings.ThemeSurface;
            var sweep = new[] { "FFFFFF", "E8EEF8", "C2C8D4", "8A8F99", "4C5058", "0B0B0C", "7A2B2B", "2B5AA0", "1A0505" };
            var lines = new List<string>();
            for (int i = 0; i < sweep.Length; i++)
            {
                settings.ThemeSurface = sweep[i];
                main.ApplyAppearance();
                for (int k = 0; k < 5; k++) Pump(app, 60);
                Save(main, Path.Combine(dir, $"sweep-{i}-{sweep[i]}.png"));
                lines.Add($"#{sweep[i]} {Services.Palette.Describe()}");
            }
            settings.ThemeSurface = original;
            main.ApplyAppearance();
            for (int i = 0; i < 5; i++) Pump(app, 60);
            Save(main, Path.Combine(dir, "tint-original.png"));
            File.WriteAllLines(Path.Combine(dir, "tint.txt"), lines.Prepend($"original={original}"));
        }
        if (args.Contains("--picker-shot"))
        {
            var pickerWindow = new Window
            {
                Width = 360,
                Height = 320,
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1C, 0x27)),
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 40,
                Top = 40,
                Content = new Controls.ColorPickerPanel("4C8DFF"),
            };
            pickerWindow.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(200);
            Save(pickerWindow, Path.Combine(dir, "picker.png"));
            pickerWindow.Close();
        }
        if (args.Contains("--appearance-shot"))
        {
            var appearance = new AppearanceWindow(settings, _ => { }, () => { });
            appearance.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Save(appearance, Path.Combine(dir, "appearance.png"));
            appearance.Close();
        }
        if (args.Contains("--download-shot"))
        {
            var downloads = new DownloadWindow(settings, _ => { });
            downloads.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Save(downloads, Path.Combine(dir, "downloads.png"));
            foreach (var viewer in Descendants(downloads).OfType<System.Windows.Controls.ScrollViewer>())
                viewer.ScrollToEnd();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Save(downloads, Path.Combine(dir, "downloads-bottom.png"));
            File.WriteAllText(Path.Combine(dir, "downloads.txt"),
                $"folder={settings.ResolvedDownloadFolder} " +
                $"default={settings.DownloadFolder.Length == 0} " +
                $"quality={settings.NeteaseQuality} addToLibrary={settings.AddDownloadedToLibrary}");
            downloads.Close();
        }
        if (args.Contains("--combo-shot"))
        {
            // QA: a ComboBox list is its own popup window, so RenderTargetBitmap cannot see it. Open the
            // dropdown, read the brushes the template actually painted, grab that part of the screen, and
            // report the contrast of the ink against every state colour (light and dark palettes).
            var surfaceOverride = Arg(args, "--surface");
            if (!string.IsNullOrEmpty(surfaceOverride))
            {
                settings.ThemeSurface = surfaceOverride!;
                main.ApplyAppearance();
                for (int i = 0; i < 4; i++) Pump(app, 60);
            }

            var comboWindow = new DownloadWindow(settings, _ => { });
            comboWindow.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(220);

            var lines = new List<string>
            {
                $"surface={settings.ThemeSurface} palette={Services.Palette.Describe()}",
            };
            var combo = Descendants(comboWindow).OfType<System.Windows.Controls.ComboBox>().FirstOrDefault();
            if (combo != null)
            {
                int index = 0;
                foreach (var item in combo.Items.OfType<System.Windows.Controls.ComboBoxItem>())
                    lines.Add($"  item{index++}: {item.Content} selected={item.IsSelected}");

                combo.IsDropDownOpen = true;
                for (int i = 0; i < 8; i++) Pump(app, 60);
                Thread.Sleep(260);

                string RowState(int i)
                {
                    if (combo.ItemContainerGenerator.ContainerFromIndex(i) is not System.Windows.Controls.ComboBoxItem item) return "no-container";
                    var border = item.Template?.FindName("bd", item) as System.Windows.Controls.Border;
                    var bg = (border?.Background as SolidColorBrush)?.Color;
                    var ink = (item.Foreground as SolidColorBrush)?.Color;
                    if (bg is not { } bgc || ink is not { } inkc) return "no-brush";
                    return $"bg={PaletteHex(bgc)} text={PaletteHex(inkc)} contrast={ContrastRatio(bgc, inkc):0.00}";
                }

                for (int i = 0; i < combo.Items.Count; i++) lines.Add($"  row{i}: {RowState(i)}");

                // IsHighlighted only appears when a real mouse is over the row; move it there, read again,
                // then put the pointer back so the machine is left as it was found. The last row is used
                // because the selected row wins the background and would hide the hover colour.
                GetCursorPos(out var restorePoint);
                int hoverIndex = combo.Items.Count - 1;
                if (combo.ItemContainerGenerator.ContainerFromIndex(hoverIndex) is System.Windows.Controls.ComboBoxItem hovered)
                {
                    var centre = hovered.PointToScreen(new Point(hovered.ActualWidth / 2, hovered.ActualHeight / 2));
                    SetCursorPos((int)Math.Round(centre.X), (int)Math.Round(centre.Y));
                    for (int i = 0; i < 6; i++) Pump(app, 60);
                    Thread.Sleep(180);
                    lines.Add($"  hovered row{hoverIndex}: highlighted={hovered.IsHighlighted} {RowState(hoverIndex)}");
                    // A synthetic cursor move does not always reach the popup window in this runner, so the
                    // hover state is also set through the very same dependency property the mouse path uses.
                    if (!hovered.IsHighlighted)
                    {
                        var highlighted = typeof(System.Windows.Controls.ComboBoxItem)
                            .GetProperty("IsHighlighted", System.Reflection.BindingFlags.Instance |
                                                          System.Reflection.BindingFlags.NonPublic |
                                                          System.Reflection.BindingFlags.Public);
                        highlighted?.SetValue(hovered, true);
                        for (int i = 0; i < 4; i++) Pump(app, 60);
                        Thread.Sleep(120);
                        lines.Add($"  forced hover row{hoverIndex}: highlighted={hovered.IsHighlighted} {RowState(hoverIndex)}");
                    }
                }

                double scale = 1.0;
                try { scale = VisualTreeHelper.GetDpi(comboWindow).DpiScaleX; } catch { }
                int wx = (int)Math.Round(comboWindow.Left * scale);
                int wy = (int)Math.Round(comboWindow.Top * scale);
                int ww = Math.Max(1, (int)Math.Round(comboWindow.ActualWidth * scale));
                int wh = Math.Max(1, (int)Math.Round(comboWindow.ActualHeight * scale));
                // The list hangs below the window; keep the capture inside the desktop.
                wh = Math.Min(wh + (int)Math.Round(360 * scale), 1440 - wy);
                try
                {
                    using var bmp = new System.Drawing.Bitmap(ww, Math.Max(1, wh));
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.CopyFromScreen(wx, wy, 0, 0, new System.Drawing.Size(ww, Math.Max(1, wh)));
                    bmp.Save(Path.Combine(dir, $"combo-{settings.ThemeSurface}.png"), System.Drawing.Imaging.ImageFormat.Png);
                    lines.Add($"capture {wx},{wy} {ww}x{wh}");
                }
                catch (Exception e)
                {
                    lines.Add("capture failed: " + e.Message);
                }

                GetCursorPos(out var landed);
                SetCursorPos(restorePoint.X, restorePoint.Y);
                lines.Add($"cursor moved to {landed.X},{landed.Y}, restored to {restorePoint.X},{restorePoint.Y}");
                combo.IsDropDownOpen = false;
            }
            File.WriteAllLines(Path.Combine(dir, $"combo-{settings.ThemeSurface}.txt"), lines);
            comboWindow.Close();
        }
        if (args.Contains("--play-start"))
        {
            // 用户报：还没播过任何歌时，全屏播放页那个三角点了没反应。这里就在那个页面上按一次，
            // 看它有没有从当前列表第一首起播（音量归零，不在测试机上出声）。
            controller.Playback.SetVolume(0);
            main.DebugOpenPlayerPage();
            for (int i = 0; i < 10; i++) Pump(app, 60);
            var lines = new List<string>
            {
                $"before: list={controller.Songs.Count} current={controller.Current?.DisplayTitle ?? "(none)"} " +
                $"playing={controller.PlaybackState} notice={controller.Notice ?? "-"}",
            };
            Save(main, Path.Combine(dir, "play-start-before.png"));
            bool pressed = main.PlayerView?.DebugClickPlay() ?? false;
            for (int i = 0; i < 24; i++) Pump(app, 60);
            lines.Add($"pressed={pressed} after: current={controller.Current?.DisplayTitle ?? "(none)"} " +
                      $"playing={controller.PlaybackState} pos={controller.RawPositionMs:0} " +
                      $"notice={controller.Notice ?? "-"}");
            Save(main, Path.Combine(dir, "play-start-after.png"));
            File.WriteAllLines(Path.Combine(dir, "play-start.txt"), lines);
            main.DebugClosePlayerPage();
        }
        var settingsWindow = new SettingsWindow(settings, _ => { });
        settingsWindow.Show();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Save(settingsWindow, Path.Combine(dir, "settings.png"));

        // Also capture the middle and bottom of the settings list.
        foreach (var viewer in Descendants(settingsWindow).OfType<System.Windows.Controls.ScrollViewer>())
            viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * 0.55);
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Save(settingsWindow, Path.Combine(dir, "settings-mid.png"));

        foreach (var viewer in Descendants(settingsWindow).OfType<System.Windows.Controls.ScrollViewer>())
            viewer.ScrollToEnd();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Save(settingsWindow, Path.Combine(dir, "settings-bottom.png"));

        settingsWindow.DebugOpenTextColorPicker();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Save(settingsWindow, Path.Combine(dir, "settings-color.png"));

        settingsWindow.Close();
        main.Close();
        app.Shutdown();
    }

    /// <summary>Headless playback check: scans a folder, plays the first track and logs the
    /// media clock plus the active lyric cue so sync can be verified without ears.</summary>
    private static void RunSelfTest(Application app, string[] args, AppSettings settings)
    {
        string folder = Arg(args, "--folder") ?? "";
        string report = Arg(args, "--report") ?? "selftest.txt";
        settings.Enabled = false;
        var controller = new PlayerController(settings);
        var lines = new List<string>();

        controller.ShowLocalAsync(folder).GetAwaiter().GetResult();
        var count = controller.Songs.Count;
        lines.Add($"scanned={count}");
        if (count > 0)
        {
            var song = controller.Songs[0];
            lines.Add($"song={song.DisplayTitle} / {song.DisplayArtist} / {song.DurationText}");
            controller.PlayAsync(song).GetAwaiter().GetResult();

            var samples = new List<string>();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            int tick = 0;
            timer.Tick += (_, _) =>
            {
                tick++;
                var (index, line, progress, _) = controller.CurrentCue();
                samples.Add($"t={controller.RawPositionMs:0} line={index} '{(line?.Text ?? "")}' p={progress:0.00} playing={controller.PlaybackState}");
                if (tick >= 9)
                {
                    timer.Stop();
                    lines.Add("lyrics=" + (controller.Lyrics == null ? "none" : $"{controller.Lyrics.Lines.Count} lines, wordTiming={controller.Lyrics.HasWordTiming}"));
                    lines.AddRange(samples);
                    File.WriteAllLines(report, lines);
                    app.Shutdown();
                }
            };
            timer.Start();
            app.Run();
            return;
        }

        File.WriteAllLines(report, lines);
        app.Shutdown();
    }

    private static void Crashing(Exception? error, string origin)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CurrentLyrics");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {origin}: {error}\n\n");
        }
        catch { }
    }

    /// <summary>Runs an async operation to completion while keeping the dispatcher alive;
    /// calling GetResult() on the UI thread would deadlock every continuation.</summary>
    private static void RunBlocking(Application app, Task task)
    {
        while (!task.IsCompleted)
        {
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(30);
        }
    }

    private static string? Arg(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static string PaletteHex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>WCAG contrast ratio, so "is this readable" is a number instead of an opinion.</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        double la = 0.2126 * Channel(a.R) + 0.7152 * Channel(a.G) + 0.0722 * Channel(a.B);
        double lb = 0.2126 * Channel(b.R) + 0.7152 * Channel(b.G) + 0.0722 * Channel(b.B);
        double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static void SaveVisual(Visual visual, int w, int h, string path)    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        int w = (int)Math.Ceiling(window.ActualWidth);
        int h = (int)Math.Ceiling(window.ActualHeight);
        if (w <= 0 || h <= 0) return;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>Borderless window covering the whole virtual desktop in one flat colour.</summary>
    private static Window CreateFlatWindow(string hex)
    {
        hex = (hex ?? "").TrimStart('#');
        if (hex.Length < 6) hex = "000000";
        var colour = Color.FromRgb(
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
        return new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(colour),
            ShowInTaskbar = false,
            Topmost = true,   // nothing (browser, taskbar, notifier) may cover the measurement surface
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.VirtualScreenLeft,
            Top = SystemParameters.VirtualScreenTop,
            Width = SystemParameters.VirtualScreenWidth,
            Height = SystemParameters.VirtualScreenHeight,
        };
    }

    /// <summary>
    /// QA content: a real track from <c>DARIUS_QA_SONG</c> when set (the exact document and cover the
    /// bug was reported with), otherwise the built-in demo cues.
    /// </summary>
    private static (Models.LyricDocument Doc, Models.Song Song) QaContent()
    {
        string? path = Environment.GetEnvironmentVariable("DARIUS_QA_SONG");
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            var song = Services.TagReader.Read(path);
            var doc = Services.LrcParser.LoadFor(path);
            if (doc != null)
                doc = Services.LrcParser.WithOpeningCard(doc, song.DisplayTitle, song.DisplayArtist, song.Album);
            song.Cover ??= Services.TagReader.Read(path).Cover;
            if (doc != null && doc.Lines.Count > 0) return (doc, song);
        }
        return (Services.LrcParser.Demo(), DemoCover());
    }

    private static Models.Song DemoCover()
    {
        var song = new Models.Song { Path = "demo.mp3" };
        song.Title = "让封面、歌词与音乐一起流动";
        song.Artist = "Darius Music";
        var gradient = new LinearGradientBrush(
            Color.FromRgb(0x1C, 0x4B, 0xC8), Color.FromRgb(0x8A, 0x2B, 0xC4), 45);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(gradient, null, new Rect(0, 0, 512, 512));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), null, new Point(160, 150), 210, 210);
        }
        var bitmap = new RenderTargetBitmap(512, 512, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        song.Cover = bitmap;
        return song;
    }
}
