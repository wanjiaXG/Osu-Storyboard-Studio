using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using SbStudio.App.Audio;
using SbStudio.App.Gfx;
using SbStudio.Core;
using Silk.NET.OpenGL;

namespace SbStudio.App;

/// <summary>What is to be exported as a video.</summary>
/// <param name="Path">The file to write; its extension says what kind of file.</param>
/// <param name="Encoder">The video encoder's name in FFmpeg, such as "libx264" or "h264_nvenc".</param>
/// <param name="Widescreen">A 16:9 picture; otherwise 4:3.</param>
/// <param name="Bitrate">Bits per second of the picture.</param>
/// <param name="Start">The time of the first frame, in milliseconds.</param>
/// <param name="End">The time the video ends at.</param>
/// <param name="AudioBitrate">Bits per second of the sound.</param>
/// <param name="AudioSampleRate">Samples per second of the sound.</param>
/// <param name="Music">Music to use in place of the project's main audio, as stereo at that sample rate; null for the main audio itself.</param>
/// <param name="MusicDelayMs">How much later than time 0 that music begins.</param>
public sealed record VideoExportRequest(string Path, string Encoder, bool Widescreen, int Height, int Fps, long Bitrate, bool Audio, double Start, double End,
    long AudioBitrate = 192_000, int AudioSampleRate = 44100, float[]? Music = null, double MusicDelayMs = 0);

/// <summary>
/// A video export under way. The storyboard is drawn frame by frame with independent export options, at the
/// size of the video and at the exact time of each frame. Drawing needs the OpenGL context, so that part
/// is driven from the main loop a few frames at a time, which also keeps the window alive to show how
/// far it has got. Each picture then goes to a thread of its own that encodes it and writes it to the
/// file together with the sound for that stretch: drawing and encoding take about as long as each other,
/// and side by side the export is nearly twice as fast.
/// </summary>
public sealed class VideoExportJob : IDisposable
{
    /// <summary>How many pictures may wait for the encoder; drawing pauses when it gets that far ahead.</summary>
    private const int Waiting = 4;

    private readonly EditorApp app;
    private readonly Project project;
    private readonly IDisposable effectPins;
    private readonly StoryboardIndex exportIndex = new();
    private readonly RenderTarget target;
    private readonly ViewTransform view;
    private readonly RenderOptions renderOptions = RenderOptions.ForVideoExport();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly BlockingCollection<byte[]> drawn = new(Waiting);
    private readonly ConcurrentBag<byte[]> spare = new();
    private readonly float[] sound;
    /// <summary>
    /// The file is written under this name and gets its own once it is complete. So a file of that name
    /// that was there before is only replaced by a whole video, never lost to an export that was
    /// cancelled or failed.
    /// </summary>
    private readonly string partPath;
    private VideoWriter? writer;
    private AudioEngine.Mixdown? mix;
    private Thread? encoder;
    private int rendered;
    private volatile int written;
    private volatile bool stopping, complete;
    private volatile string? failure;

    public VideoExportRequest Request { get; }
    public int Width { get; }
    public int Height => Request.Height;
    public int FrameCount { get; }
    /// <summary>How many frames have been written.</summary>
    public int Frame => written;
    /// <summary>The file is complete.</summary>
    public bool Finished { get; private set; }
    /// <summary>Why the export stopped, when it failed.</summary>
    public string? Error { get; private set; }
    public bool Running => !Finished && Error == null;
    /// <summary>The frame that was drawn last, for showing while the export runs.</summary>
    public nint Picture => target.ImGuiId;
    /// <summary>How long the export has taken; stands still once it is over.</summary>
    public double Seconds => clock.Elapsed.TotalSeconds;
    /// <summary>The size of the finished file in bytes.</summary>
    public long FileSize { get; private set; }

    private VideoExportJob(EditorApp app, GL gl, VideoExportRequest request)
    {
        this.app = app;
        project = app.Project!.ExportSnapshot();
        effectPins = Effects.EffectResultPins.Pin(project, project.Storyboard.Groups.Select(g => g.Plugin?.Key));
        exportIndex.Build(project.Storyboard);
        Request = request;
        Width = EditorApp.FrameWidth(request.Height, request.Widescreen);
        FrameCount = Math.Max(1, (int)Math.Ceiling((request.End - request.Start) * request.Fps / 1000 - 1e-6));
        float scale = request.Height / 480f;
        // The 640 wide playfield sits in the middle; a widescreen picture shows more on both sides.
        view = new ViewTransform { Scale = scale, Offset = new Vector2((Width - 640 * scale) / 2, 0) };
        target = new RenderTarget(gl);
        target.Resize(Width, request.Height);
        sound = new float[(request.AudioSampleRate / request.Fps + 2) * 2];
        partPath = request.Path + ".part";
    }

    /// <summary>Begins an export. Null, with what went wrong, when the file cannot be written as asked.</summary>
    public static VideoExportJob? Start(EditorApp app, GL gl, VideoExportRequest request, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.Path))!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return null;
        }

        var job = new VideoExportJob(app, gl, request);
        string container = VideoWriter.ContainerOf(Path.GetExtension(request.Path));
        job.writer = VideoWriter.Open(new VideoWriterOptions(job.partPath, container, request.Encoder, job.Width, request.Height, request.Fps, request.Bitrate, request.Audio,
            request.AudioBitrate, request.AudioSampleRate), out error);
        if (job.writer == null)
        {
            job.target.Dispose();
            return null;
        }
        if (request.Audio) job.mix = app.Audio.OpenMixdown(request.Start, app.ScheduledSamples(forExport: true), request.AudioSampleRate, request.Music, request.MusicDelayMs,
            usePlaybackGains: false);
        job.encoder = new Thread(job.Encode) { IsBackground = true, Name = "video-encode" };
        job.encoder.Start();
        return job;
    }

    /// <summary>Draws frames for about as long as given, then returns so that the window can show the progress.</summary>
    public void Step(double budgetMs)
    {
        if (!Running) return;
        long began = Stopwatch.GetTimestamp();
        while (rendered < FrameCount && failure == null && Stopwatch.GetElapsedTime(began).TotalMilliseconds < budgetMs)
        {
            double time = Request.Start + rendered * 1000.0 / Request.Fps;
            app.Renderer.Render(project, exportIndex, time, target, view, renderOptions, widescreen: Request.Widescreen);
            if (!spare.TryTake(out byte[]? pixels)) pixels = GC.AllocateUninitializedArray<byte>(Width * Height * 4);
            target.ReadPixelsRaw(pixels);
            // Waits while the encoder has enough before it; it gets through a picture in a few milliseconds.
            while (!drawn.TryAdd(pixels, 20))
                if (failure != null) break;
            rendered++;
        }
        if (rendered == FrameCount && !drawn.IsAddingCompleted) drawn.CompleteAdding();

        if (failure != null)
        {
            Error = failure;
            clock.Stop();
            Release();
            Discard();
        }
        else if (complete)
        {
            Release();
            clock.Stop();
            try
            {
                File.Move(partPath, Request.Path, true);
                FileSize = new FileInfo(Request.Path).Length;
                Finished = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file of that name is open somewhere, most likely in a player.
                Error = ex.Message;
                Discard();
            }
        }
    }

    /// <summary>Normal Pass storyboard audio, including editor-hidden samples; never beatmap hit sounds.</summary>
    internal static List<ScheduledSample> StoryboardSamples(Project project) => project.Storyboard.Samples
        .Where(sample => sample.Layer != SbLayer.Fail)
        .Select(sample => new ScheduledSample(sample.Time, project.AssetFullPath(sample.Path), (float)Math.Clamp(sample.Volume / 100, 0, 1)))
        .ToList();

    /// <summary>The other thread: takes the pictures in the order they were drawn and writes each with its sound.</summary>
    private void Encode()
    {
        long soundFrames = 0;
        try
        {
            foreach (byte[] pixels in drawn.GetConsumingEnumerable())
            {
                if (stopping) return;
                writer!.WritePicture(pixels);
                spare.Add(pixels);
                int count = written + 1;

                if (mix != null)
                {
                    // The sound up to the end of this frame.
                    long upTo = (long)Math.Round(count * (double)Request.AudioSampleRate / Request.Fps);
                    int frames = (int)Math.Min(sound.Length / 2, upTo - soundFrames);
                    if (frames > 0)
                    {
                        mix.Read(sound, frames);
                        writer.WriteSound(sound, frames);
                        soundFrames += frames;
                    }
                }
                written = count;
            }
            if (stopping) return;
            writer!.Finish();
            complete = true;
        }
        catch (Exception ex)
        {
            // Whatever goes wrong in here ends the export, not the program.
            failure = ex.Message;
        }
    }

    /// <summary>Stops the encoding thread and lets go of the encoder and the sound; the last picture stays to be looked at.</summary>
    private void Release()
    {
        stopping = true;
        if (!drawn.IsAddingCompleted) drawn.CompleteAdding();
        // What FFmpeg holds is only let go of once nothing works with it any more.
        if (encoder != null && !encoder.Join(10_000)) return;
        encoder = null;
        writer?.Dispose();
        writer = null;
        mix?.Dispose();
        mix = null;
        effectPins.Dispose();
    }

    /// <summary>Removes the file of an export that did not get to its end: half a video is of no use to anyone.</summary>
    private void Discard() => VideoWriter.Delete(partPath);

    public void Dispose()
    {
        bool unfinished = Running;
        Release();
        if (unfinished) Discard();
        target.Dispose();
    }
}
