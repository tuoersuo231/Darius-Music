using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using CurrentLyrics.Models;

namespace CurrentLyrics.Services;

/// <summary>Downloads and caches remote artwork (playlist covers, album art) for the library.</summary>
public static class ImageCache
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(4);
    private static readonly string DiskDir = Path.Combine(AppPaths.DataDir, "covers");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.163.com/");
        return client;
    }

    public static async Task<BitmapSource?> LoadAsync(string? url, int decodeWidth = 300)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        lock (Cache)
            if (Cache.TryGetValue(url, out var cached)) return cached;

        await Gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(DiskDir);
            var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(url)))[..24];
            var file = Path.Combine(DiskDir, key + ".img");

            byte[]? bytes = File.Exists(file) ? await File.ReadAllBytesAsync(file) : null;
            if (bytes == null)
            {
                bytes = await Http.GetByteArrayAsync(url);
                if (bytes.Length < 64) return null;
                await File.WriteAllBytesAsync(file, bytes);
            }

            var image = Decode(bytes, decodeWidth);
            if (image != null) lock (Cache) Cache[url] = image;
            return image;
        }
        catch
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static BitmapSource? Decode(byte[] bytes, int decodeWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
