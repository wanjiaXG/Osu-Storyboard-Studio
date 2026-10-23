using SbStudio.Core;
using Silk.NET.OpenGL;

namespace SbStudio.App.Gfx;

/// <summary>
/// The videos of the storyboard as textures: for each, whichever picture belongs to the moment that is
/// drawn. The picture is always the right one, also right after a jump; drawing waits the moment it takes
/// to decode up to it.
/// </summary>
public sealed class VideoPlayer : IDisposable
{
    /// <summary>How many files stay open. A storyboard rarely has more than one video.</summary>
    private const int MostOpen = 4;

    private sealed class Entry
    {
        public VideoReader? Reader;
        public Texture? Texture;
        public long Uploaded = -1;
        public string? Problem;
        public long Used;
    }

    private readonly GL gl;
    private readonly Dictionary<string, Entry> open = new(StringComparer.OrdinalIgnoreCase);
    private long uses;

    public VideoPlayer(GL gl) => this.gl = gl;

    /// <summary>
    /// The entry of a file, opened when it is asked about for the first time. A file that cannot be read
    /// stays an entry with a problem, so that it is not tried again and again.
    /// </summary>
    private Entry Get(string path)
    {
        if (!open.TryGetValue(path, out var entry))
        {
            // The one that has gone unused the longest makes room.
            if (open.Count >= MostOpen) Forget(open.MinBy(e => e.Value.Used).Key);
            entry = new Entry();
            if (!File.Exists(path)) entry.Problem = L.T("video.missing", Path.GetFileName(path));
            else
            {
                entry.Reader = VideoReader.Open(path, out string? error);
                if (entry.Reader == null) entry.Problem = L.T("video.unreadable", Path.GetFileName(path), error ?? "");
            }
            open[path] = entry;
        }
        entry.Used = ++uses;
        return entry;
    }

    /// <summary>What a video file is like: its size and length. Null when it cannot be shown; <see cref="Problem"/> says why.</summary>
    public VideoInfo? Info(string path) => Get(path).Reader?.Info;

    /// <summary>Why a video file is not shown, in the user's language. Null while all is well with it.</summary>
    public string? Problem(string path) => Get(path).Problem;

    /// <summary>The picture of a video file at a time into it, in milliseconds. Null when there is none to show.</summary>
    public Texture? Frame(string path, double ms)
    {
        var entry = Get(path);
        if (entry.Reader == null) return null;
        if (!entry.Reader.Read(ms, out byte[] pixels, out long number))
        {
            if (entry.Reader.Error != null)
            {
                entry.Problem = L.T("video.unreadable", Path.GetFileName(path), entry.Reader.Error);
                entry.Reader.Dispose();
                entry.Reader = null;
            }
            return null;
        }
        if (number != entry.Uploaded)
        {
            entry.Texture ??= new Texture(gl, entry.Reader.Info.Width, entry.Reader.Info.Height);
            entry.Texture.Update(pixels);
            entry.Uploaded = number;
        }
        return entry.Texture;
    }

    /// <summary>
    /// Lets go of a file. It is opened afresh when it is asked about again, which is also the way to try
    /// again after the file has changed or turned up.
    /// </summary>
    public void Forget(string path)
    {
        if (!open.Remove(path, out var entry)) return;
        entry.Reader?.Dispose();
        entry.Texture?.Dispose();
    }

    /// <summary>Lets go of every file.</summary>
    public void Close()
    {
        foreach (string path in open.Keys.ToArray()) Forget(path);
    }

    public void Dispose() => Close();
}
