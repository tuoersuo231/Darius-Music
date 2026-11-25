using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CurrentLyrics.Services;

/// <summary>
/// 更新检查：清单可以放在一个文件夹里（U 盘 / 网盘同步目录 / 局域网共享），也可以是一个 http 地址。
/// 下载、校验、装版本都由这里完成。装在版本目录布局里时只新建 versions\vX 并原子改指针，
/// 从不覆盖正在运行的文件；老的单目录布局才退回 --apply-update 原地替换。
/// </summary>
public static class UpdateService
{
    public sealed record Manifest(string Version, string Notes, string Package, string Sha256, string? Url, long Size)
    {
        /// <summary>数值版本号（服务端用构建序号，缺失时由 Version 推导）。</summary>
        public int? V { get; init; }

        /// <summary>对 "v|sha256" 的 HMAC-SHA256（hex）。缺失时只验 SHA256。</summary>
        public string? Sig { get; init; }

        /// <summary>用于比较和排序的整数。</summary>
        public int Number
        {
            get
            {
                if (V is { } v) return v;
                var key = VersionStore.VersionKey(Version);
                return key.Item1 * 10000 + key.Item2 * 100 + key.Item3;
            }
        }
    }

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    private static string StagingDir => Path.Combine(AppPaths.DataDir, "updates");

    /// <summary>更新源可以随包发布：编译时用 -p:UpdateFeedDefault=... 烘进程序集，
    /// 也可以在 exe 旁边放一个 update-feed.txt 覆盖（一行地址，# 开头是注释）。
    /// 用户自己在设置里填的地址优先级最高。</summary>
    public static string BakedFeed
    {
        get
        {
            foreach (var attribute in typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (attribute.Key == "UpdateFeed" && !string.IsNullOrWhiteSpace(attribute.Value))
                    return attribute.Value.Trim();
            }
            return "";
        }
    }

    public static string ResolveFeed(string? userFeed)
    {
        var feeds = ResolveFeeds(userFeed);
        return feeds.Count > 0 ? feeds[0] : "";
    }

    /// <summary>按顺序给出候选更新源：设置里填的 &gt; 包内 update-feed.txt 的多行 &gt; 编译时烘进去的默认值。
    /// 多行是为了让同一个包在局域网共享和云同步目录两种机器上都能用（第一个连不通就用下一个）。</summary>
    public static List<string> ResolveFeeds(string? userFeed)
    {
        var chosen = (userFeed ?? "").Trim();
        if (chosen.Length > 0) return new List<string> { chosen };

        var feeds = new List<string>();
        try
        {
            // 先看安装根目录（换版本不会丢），再看 exe 自己所在目录（老布局/绿色版）。
            foreach (var dir in new[] { Layout.Root, AppContext.BaseDirectory })
            {
                var overridePath = Path.Combine(dir, "update-feed.txt");
                if (!File.Exists(overridePath)) continue;
                foreach (var raw in File.ReadAllLines(overridePath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (!feeds.Contains(line, StringComparer.OrdinalIgnoreCase)) feeds.Add(line);
                }
            }
        }
        catch { }
        var baked = BakedFeed;
        if (baked.Length > 0 && !feeds.Contains(baked, StringComparer.OrdinalIgnoreCase)) feeds.Add(baked);
        return feeds;
    }

    /// <summary>依次试每个候选源，返回第一个能读到清单的（同时给出是哪个源命中的）。</summary>
    public static async Task<(Manifest? Manifest, string Feed)> CheckAnyAsync(IEnumerable<string> feeds, CancellationToken token = default)
    {
        foreach (var feed in feeds)
        {
            if (string.IsNullOrWhiteSpace(feed)) continue;
            var manifest = await CheckAsync(feed, token);
            if (manifest != null) return (manifest, feed);
        }
        return (null, "");
    }

    /// <summary>清单签名：HMAC-SHA256(key, "v|sha256")。清单里没有 sig 时视为未签名（只靠 SHA256）。</summary>
    public static bool VerifySignature(Manifest manifest, string? keyText)
    {
        if (string.IsNullOrWhiteSpace(manifest.Sig)) return true;
        if (string.IsNullOrWhiteSpace(keyText)) return false;
        try
        {
            var mac = new HMACSHA256(DecodeKey(keyText));
            var message = Encoding.UTF8.GetBytes(manifest.Number + "|" + manifest.Sha256.ToLowerInvariant());
            var expected = Convert.ToHexString(mac.ComputeHash(message)).ToLowerInvariant();
            var actual = manifest.Sig.Trim().ToLowerInvariant();
            return expected.Length == actual.Length &&
                   CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
        }
        catch
        {
            return false;
        }
    }

    private static byte[] DecodeKey(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length >= 64 && trimmed.Length % 2 == 0 && trimmed.All(Uri.IsHexDigit))
            return Convert.FromHexString(trimmed);
        return Convert.FromBase64String(trimmed);
    }

    /// <summary>这个清单是否比当前版本新（版本目录布局看 current.txt，老布局看程序集版本）。</summary>
    public static bool IsNewerThanCurrent(Manifest manifest)
    {
        if (VersionStore.Enabled && VersionStore.Current() is { } current)
            return manifest.Number > VersionKeyNumber(current);
        return manifest.Number > VersionKeyNumber(CurrentVersion);
    }

    private static int VersionKeyNumber(string version)
    {
        var key = VersionStore.VersionKey(version);
        return key.Item1 * 10000 + key.Item2 * 100 + key.Item3;
    }

    /// <summary>下载 → 校验 SHA256 → 验签 → 装进 versions\<版本> → 原子切指针。返回装好的版本目录名。</summary>
    public static async Task<string> InstallAsync(string feed, Manifest manifest, IProgress<double>? progress = null,
                                                  CancellationToken token = default)
    {
        if (!VersionStore.Enabled)
            throw new InvalidOperationException("当前不是版本目录布局，无法安装新版本");

        if (!VerifySignature(manifest, VersionStore.HmacKey()))
            throw new InvalidDataException("清单签名不对，已放弃更新");

        var zip = await FetchAsync(feed, manifest, progress, token);
        var version = VersionStore.Tag(manifest.Version);
        try
        {
            VersionStore.Install(zip, version);
            VersionStore.WritePointer(version);
        }
        finally
        {
            try { File.Delete(zip); } catch { }
        }
        return version;
    }

    /// <summary>读清单。feed 可以是目录、latest.json 的完整路径，或 http(s) 地址。</summary>
    public static async Task<Manifest?> CheckAsync(string feed, CancellationToken token = default)
    {
        feed = (feed ?? "").Trim();
        LastFailure = null;
        if (feed.Length == 0) return null;

        string json;
        if (feed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // The public feed is served through a tunnel on a home uplink, so a check can queue
                // behind other traffic; 20 s plus one retry rides that out instead of reporting a
                // missing manifest that is really a slow connection.
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var url = feed.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? feed : feed.TrimEnd('/') + "/latest.json";
                try
                {
                    json = await http.GetStringAsync(url, token);
                }
                catch (Exception first) when (!token.IsCancellationRequested)
                {
                    Log($"check retry {feed}: {first.GetType().Name}");
                    await Task.Delay(600, token);
                    json = await http.GetStringAsync(url, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                LastFailure = "连接超时（更新源太慢或正在占用带宽）";
                Log($"check failed {feed}: timeout");
                return null;
            }
            catch (HttpRequestException ex)
            {
                LastFailure = ex.StatusCode is { } code ? $"HTTP {(int)code}" : "连不上更新源";
                Log($"check failed {feed}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                // 服务器 404 / 断网 / 证书问题都只是“这个地址没有清单”，不该让界面崩一段异常文本。
                LastFailure = ex.GetType().Name;
                Log($"check failed {feed}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
        else
        {
            var path = Directory.Exists(feed) ? Path.Combine(feed, "latest.json") : feed;
            if (!File.Exists(path))
            {
                LastFailure = "目录里没有 latest.json";
                return null;
            }
            json = await File.ReadAllTextAsync(path, token);
        }

        var manifest = JsonSerializer.Deserialize<Manifest>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        if (manifest is { Version.Length: > 0, Package.Length: > 0 }) return manifest;
        LastFailure = "latest.json 内容不完整";
        return null;
    }

    /// <summary>Why the last <see cref="CheckAsync"/> produced no manifest (null when it succeeded).</summary>
    public static string? LastFailure { get; private set; }

    private static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "update.log"), $"{DateTime.Now:HH:mm:ss} {text}\n");
        }
        catch { }
    }

    public static bool IsNewer(string candidate, string current)
    {
        if (!Version.TryParse(candidate, out var a) || !Version.TryParse(current, out var b)) return false;
        return a > b;
    }

    /// <summary>把新版本下载到 data\updates，返回本地 zip 路径；哈希不符就删掉并报错。</summary>
    public static async Task<string> FetchAsync(string feed, Manifest manifest, IProgress<double>? progress = null,
                                                CancellationToken token = default)
    {
        Directory.CreateDirectory(StagingDir);
        var target = Path.Combine(StagingDir, manifest.Package);
        if (File.Exists(target) && HashOf(target).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            return target;

        var source = manifest.Url;
        if (string.IsNullOrWhiteSpace(source))
        {
            source = source is null && Directory.Exists(feed)
                ? Path.Combine(feed, manifest.Package)
                : feed.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? feed.TrimEnd('/') + "/" + Uri.EscapeDataString(manifest.Package)
                    : Path.Combine(Path.GetDirectoryName(Directory.Exists(feed) ? Path.Combine(feed, "x") : feed) ?? "",
                        manifest.Package);
        }

        if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? manifest.Size;
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = File.Create(target);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        else
        {
            File.Copy(source, target, true);
            progress?.Report(1);
        }

        var actual = HashOf(target);
        if (!actual.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(target); } catch { }
            throw new InvalidDataException($"SHA256 不符：期望 {manifest.Sha256[..Math.Min(12, manifest.Sha256.Length)]}…，实际 {actual[..12]}…");
        }
        return target;
    }

    /// <summary>
    /// 把**整个程序目录**（不含 data）复制到临时目录，用那里的 exe 以 --apply-update 重新启动。
    /// 只复制 exe 是不够的：自包含包缺 runtime、框架依赖包缺 dll，单独一个 exe 根本起不来，
    /// 更新就会静默失败。复制完再让调用方退出。
    /// </summary>
    public static void ApplyAndRestart(string zipPath)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("找不到自身路径");
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var helperDir = Path.Combine(Path.GetTempPath(), "DariusUpdate");
        try { if (Directory.Exists(helperDir)) Directory.Delete(helperDir, true); } catch { }
        Directory.CreateDirectory(helperDir);
        CopyTree(dir, helperDir);

        var helper = Path.Combine(helperDir, "Darius Music.exe");
        var args = $"--apply-update \"{zipPath}\" \"{dir}\" {Environment.ProcessId}";
        Process.Start(new ProcessStartInfo(helper, args) { UseShellExecute = false, WorkingDirectory = helperDir });
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            try { File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true); }
            catch { }   // 正在被占用的文件不该让整个更新失败
        }
        foreach (var folder in Directory.GetDirectories(from))
        {
            var name = Path.GetFileName(folder);
            if (name.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".old", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".update", StringComparison.OrdinalIgnoreCase)) continue;
            CopyTree(folder, Path.Combine(to, name));
        }
    }

    /// <summary>更新器模式：等主程序退出 → 解压到 .update → 旧目录改名 .old → 搬入新文件 → 重启 → 清理。</summary>
    public static int RunApply(string[] args)
    {
        string zip = args[1], dir = args[2];
        int pid = args.Length > 3 && int.TryParse(args[3], out var p) ? p : 0;
        string logPath = Path.Combine(dir, "data", "update.log");
        void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} {text}\n");
            }
            catch { }
        }

        Log($"apply start zip={Path.GetFileName(zip)} dir={dir} pid={pid}");
        try { if (pid > 0) Process.GetProcessById(pid).WaitForExit(30000); } catch { }

        var staging = Path.Combine(dir, ".update");
        var old = Path.Combine(dir, ".old");
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            if (Directory.Exists(old)) Directory.Delete(old, true);
            Directory.CreateDirectory(old);          // must exist before the moves below, or the first File.Move throws
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, staging, true);

            // zip 里通常带一层顶层文件夹，先剥掉
            var entries = Directory.GetFileSystemEntries(staging);
            if (entries.Length == 1 && Directory.Exists(entries[0])) staging = entries[0];

            var keep = new[] { "data", ".update", ".old" };
            foreach (var file in Directory.GetFiles(dir))
            {
                try { File.Move(file, Path.Combine(old, Path.GetFileName(file)), true); }
                catch (Exception ex) { Log("move-out failed " + Path.GetFileName(file) + ": " + ex.Message); }
            }
            foreach (var folder in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(folder);
                if (keep.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                try { Directory.Move(folder, Path.Combine(old, name)); }
                catch (Exception ex) { Log("move-out failed " + name + "/: " + ex.Message); }
            }

            int copied = 0, failed = 0;
            foreach (var entry in Directory.GetFileSystemEntries(staging))
            {
                var target = Path.Combine(dir, Path.GetFileName(entry));
                try
                {
                    if (Directory.Exists(entry)) Directory.Move(entry, target);
                    else File.Copy(entry, target, true);
                    copied++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Log("copy-in failed " + Path.GetFileName(entry) + ": " + ex.Message);
                }
            }
            Log($"apply done copied={copied} failed={failed}");
            Process.Start(new ProcessStartInfo(Path.Combine(dir, "Darius Music.exe")) { WorkingDirectory = dir });
        }
        catch (Exception ex)
        {
            Log("apply failed: " + ex);
            try { Process.Start(new ProcessStartInfo(Path.Combine(dir, "Darius Music.exe")) { WorkingDirectory = dir }); } catch { }
            return 2;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch { }
            // 只删暂存副本：万一 zip 是用户自己的更新源文件（U 盘/共享目录），不能把源包删掉。
            try
            {
                var stagingDir = Path.Combine(dir, "data") + Path.DirectorySeparatorChar;
                if (File.Exists(zip) && zip.StartsWith(stagingDir, StringComparison.OrdinalIgnoreCase)) File.Delete(zip);
            }
            catch { }
            // 更新器自己是从 %TEMP%\DariusUpdate 里跑的，退出后删掉这份副本（~200 MB）。
            try
            {
                var self = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                if (self.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 >nul & rd /s /q \"{self}\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = Path.GetTempPath(),   // 不在被删目录里当 CWD，否则 rd 删不掉自己
                    });
                }
            }
            catch { }
        }
        return 0;
    }

    private static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
