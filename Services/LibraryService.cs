using System.Collections.ObjectModel;
using System.IO;
using CurrentLyrics.Models;

namespace CurrentLyrics.Services;

/// <summary>Local folder library: recursive scan, tag + cover read on a worker thread.</summary>
public sealed class LibraryService
{
    public ObservableCollection<Song> Songs { get; } = new();
    public string Folder { get; private set; } = "";

    public async Task<int> ScanAsync(string folder, ICollection<string>? exclude = null,
                                     IProgress<(int done, int total)>? progress = null)
    {
        Folder = folder;
        if (!Directory.Exists(folder)) return 0;
        var skip = exclude == null
            ? null
            : new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        var files = await Task.Run(() => Directory
            .EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(TagReader.IsAudio)
            .Where(p => skip == null || !skip.Contains(p))
            .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)
            .ToList());

        var songs = new List<Song>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            songs.Add(TagReader.Read(files[i]));
            if (i % 8 == 0) progress?.Report((i, files.Count));
        }
        progress?.Report((files.Count, files.Count));

        Songs.Clear();
        foreach (var song in songs) Songs.Add(song);
        return songs.Count;
    }

    public Song? Next(Song? current, int direction)
    {
        if (Songs.Count == 0) return null;
        int index = current == null ? -1 : Songs.IndexOf(current);
        if (index < 0) return Songs[0];
        int next = (index + direction + Songs.Count) % Songs.Count;
        return Songs[next];
    }
}
