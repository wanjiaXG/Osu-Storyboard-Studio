using System.Collections.Concurrent;
using System.Diagnostics;
using Silk.NET.OpenGL;
using StbImageSharp;

namespace SbStudio.App.Gfx;

/// <summary>
/// Textures of project assets, keyed by asset path. Files are decoded on a worker thread and uploaded on
/// the render thread. A project's images are all asked for with <see cref="Preload"/> when it is opened,
/// behind a progress display, so that nothing is missing once it is shown: the picture is drawn with
/// <see cref="Require"/>, which waits for an image rather than go without it.
/// </summary>
public sealed class TextureCache : IDisposable
{
    private enum State { Loading, Ready, Missing, Failed }

    /// <summary>One file on its way through the decoder.</summary>
    private sealed record Request(string Key, string File, int Generation);

    private sealed class Entry
    {
        public State State;
        public Texture? Texture;
        /// <summary>Set until the file has been through the decoder.</summary>
        public Request? Request;
    }

    private readonly record struct Decoded(Request Request, int Width, int Height, byte[]? Pixels, byte[]? Mask, bool Missing);

    private readonly GL gl;
    private readonly int maxSize;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Request> requests = new();
    private readonly ConcurrentQueue<Decoded> decoded = new();
    private readonly AutoResetEvent arrived = new(false);
    private readonly Thread worker;
    private int generation;
    private int pending;
    public int LoadedCount { get; private set; }
    public int UnavailableCount { get; private set; }
    public long EstimatedBytes { get; private set; }
    public string FullPath(string path) => Resolve?.Invoke(path) ?? path;

    /// <summary>Turns an asset path into a full file path; null when no project is open.</summary>
    public Func<string, string>? Resolve;

    /// <summary>Images asked for that are not ready yet.</summary>
    public int PendingCount => Volatile.Read(ref pending);

    public TextureCache(GL gl)
    {
        this.gl = gl;
        maxSize = gl.GetInteger(GetPName.MaxTextureSize);
        worker = new Thread(DecodeLoop) { IsBackground = true, Name = "texture-decode" };
        worker.Start();
    }

    /// <summary>The texture of an asset if it is ready, null otherwise. Asking starts loading it.</summary>
    public Texture? Get(string assetPath)
    {
        if (assetPath.Length == 0 || Resolve == null) return null;
        if (!entries.TryGetValue(assetPath, out var entry)) entry = Add(assetPath);
        return entry.Texture;
    }

    /// <summary>Starts loading an image that will be shown later.</summary>
    public void Preload(string assetPath)
    {
        if (assetPath.Length > 0 && Resolve != null && !entries.ContainsKey(assetPath)) Add(assetPath);
    }

    /// <summary>
    /// The texture of an asset, waiting for it if it is still being read. Null only when there is no such
    /// image. This is what the picture is drawn with: a late frame is better than a sprite left out.
    /// </summary>
    public Texture? Require(string assetPath)
    {
        var texture = Get(assetPath);
        if (texture != null || !entries.TryGetValue(assetPath, out var entry)) return texture;
        while (entry.Request != null && worker.IsAlive)
        {
            if (decoded.IsEmpty) arrived.WaitOne(20);
            Update(double.PositiveInfinity);
        }
        return entry.Texture;
    }

    private Entry Add(string assetPath)
    {
        var entry = new Entry { State = State.Loading };
        entries[assetPath] = entry;
        Load(entry, assetPath);
        return entry;
    }

    /// <summary>Sends the file of an entry to the decoder. A read that was still under way for it no longer counts.</summary>
    private void Load(Entry entry, string assetPath)
    {
        entry.Request = new Request(assetPath, Resolve!(assetPath), generation);
        Interlocked.Increment(ref pending);
        requests.Add(entry.Request);
    }

    /// <summary>True once loading finished without producing a texture.</summary>
    public bool IsUnavailable(string assetPath) =>
        entries.TryGetValue(assetPath, out var entry) && entry.State is State.Missing or State.Failed;

    private void DecodeLoop()
    {
        foreach (var request in requests.GetConsumingEnumerable())
        {
            if (request.Generation != Volatile.Read(ref generation))
            {
                Interlocked.Decrement(ref pending);
                continue;
            }
            decoded.Enqueue(Decode(request));
            arrived.Set();
        }
    }

    private static Decoded Decode(Request request)
    {
        try
        {
            if (!File.Exists(request.File)) return new Decoded(request, 0, 0, null, null, true);
            var image = ImageResult.FromMemory(File.ReadAllBytes(request.File), ColorComponents.RedGreenBlueAlpha);
            byte[] data = image.Data;
            // osu! uploads straight RGBA. Filtering must happen before multiplying by alpha,
            // otherwise transparent edges and narrow storyboard slices blend differently.
            // Reads every pixel, so it is done here rather than on the render thread.
            byte[] mask = Texture.BuildMask(image.Width, image.Height, data);
            return new Decoded(request, image.Width, image.Height, data, mask, false);
        }
        catch (Exception)
        {
            return new Decoded(request, 0, 0, null, null, false);
        }
    }

    /// <summary>
    /// Uploads finished images. Call once per frame on the GL thread.
    /// </summary>
    /// <param name="budgetMs">
    /// How long to go on for. A small image takes a fraction of a millisecond and a large one several; the
    /// one that crosses this line is the last of the call.
    /// </param>
    public void Update(double budgetMs)
    {
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < budgetMs && decoded.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref pending);
            // The asset may have been forgotten, or asked for again, while its file was being read.
            if (!entries.TryGetValue(item.Request.Key, out var entry) || entry.Request != item.Request) continue;
            entry.Request = null;
            // What was shown until now, when the file changed and has been read again.
            if (entry.Texture is { } previous)
            {
                LoadedCount--;
                EstimatedBytes -= (long)previous.Width * previous.Height * 4;
                previous.Dispose();
            }
            if (entry.State is State.Missing or State.Failed) UnavailableCount--;
            entry.Texture = null;

            if (item.Pixels == null)
            {
                entry.State = item.Missing ? State.Missing : State.Failed;
            }
            else if (item.Width > maxSize || item.Height > maxSize)
            {
                entry.State = State.Failed;
            }
            else
            {
                // osu-framework sh_Texture.fs samples with -0.9 LOD bias and limits mipmaps to level 3.
                entry.Texture = new Texture(gl, item.Width, item.Height, item.Pixels, true, item.Mask,
                    premultiplied: false, lodBias: -0.9f, maxMipmapLevel: 3);
                entry.State = State.Ready;
                LoadedCount++;
                EstimatedBytes += (long)item.Width * item.Height * 4;
            }
            if (entry.State is State.Missing or State.Failed) UnavailableCount++;
        }
    }

    /// <summary>
    /// Reads one asset from disk again, as its file has changed. The version in use stays on screen until
    /// the new one is ready.
    /// </summary>
    public void Invalidate(string assetPath)
    {
        if (Resolve == null) return;
        if (entries.TryGetValue(assetPath, out var entry)) Load(entry, assetPath);
        // The changed file may be used by a script name without the appended extension. Re-resolve
        // that name too: adding a PNG can take precedence over the JPG previously shown.
        if (assetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || assetPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            string alias = assetPath[..^4];
            if (entries.TryGetValue(alias, out var aliased)) Load(aliased, alias);
        }
    }

    public void Clear()
    {
        Interlocked.Increment(ref generation);
        foreach (var entry in entries.Values) entry.Texture?.Dispose();
        entries.Clear();
        LoadedCount = UnavailableCount = 0;
        EstimatedBytes = 0;
    }

    public void Dispose()
    {
        requests.CompleteAdding();
        Clear();
    }
}
