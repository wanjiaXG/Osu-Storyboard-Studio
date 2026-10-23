using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SbStudio.Core;

namespace SbStudio.App.Gfx;

/// <summary>
/// Reads the pictures of a video file with the FFmpeg libraries that come with the program. A thread of
/// its own decodes a few pictures ahead of the one being shown, so playing costs the drawing no more than
/// handing a picture over. A jump has it start again at the key picture before the wanted time and decode
/// up to it, which is what makes the picture after a jump the right one and not merely a near one.
///
/// As in osu!, the picture for a time is the last one that has begun by then, counted from the start of
/// the video's own stream.
/// </summary>
public sealed unsafe class VideoReader : IDisposable
{
    /// <summary>How many pictures are decoded ahead of the one being shown.</summary>
    private const int Ahead = 4;
    /// <summary>How long a picture may take before the decoding counts as stuck.</summary>
    private const int TimeoutMs = 10_000;
    /// <summary>A time up to this far ahead is reached by decoding on; further ahead by a jump. In milliseconds.</summary>
    private const double ReachMs = 1000;
    /// <summary>Tolerance when comparing times, in milliseconds: far below the length of any picture.</summary>
    private const double Slack = 1e-6;

    private sealed class Picture
    {
        public byte[] Pixels = Array.Empty<byte>();
        /// <summary>When it begins, in milliseconds into the video.</summary>
        public double Time;
        public long Serial;
        /// <summary>The jump it was decoded for; pictures of an earlier one are thrown away.</summary>
        public int Jump;
        /// <summary>Not a picture: the file has no more.</summary>
        public bool End;
        /// <summary>Not a picture: decoding failed, and why.</summary>
        public string? Error;
    }

    // What belongs to FFmpeg. After opening, only the decoding thread touches it.
    private AVFormatContext* format;
    private AVStream* stream;
    private AVCodecContext* codec;
    private AVPacket* packet;
    private AVFrame* decoded, held;
    private SwsContext* scaler;
    private (int Width, int Height, int Format, AVColorSpace Space, AVColorRange Range) scalerFor;
    private bool scalerAlpha;
    private readonly byte*[] target = new byte*[4];
    private readonly int[] targetStride = new int[4];
    private double timeBase, lastTime;
    private long startTime, serial;

    private Thread? thread;
    private readonly object gate = new();
    private readonly BlockingCollection<Picture> ready = new(Ahead);
    private readonly ConcurrentBag<byte[]> spare = new();
    private volatile bool stopping;
    private volatile int jump;
    private double jumpTo;
    /// <summary>Why the decoding thread gave up, when it did.</summary>
    private volatile string? failure;

    // What the caller's side keeps.
    private Picture? current, next;
    /// <summary><see cref="current"/> is the first picture since the last jump: there is none before it to go back to.</summary>
    private bool first;
    private bool ended, nothing;
    private double jumpedTo;

    public VideoInfo Info { get; private set; } = new(0, 0, 0);
    /// <summary>Why reading stopped for good, when it did.</summary>
    public string? Error { get; private set; }

    private VideoReader()
    {
    }

    // ---------------------------------------------------------------- the libraries

    private static readonly object setup = new();
    private static bool loaded;
    private static string? unavailable;

    /// <summary>Finds and loads the FFmpeg libraries, once. Null when they are there; else what is wrong.</summary>
    internal static string? Load()
    {
        lock (setup)
        {
            if (loaded || unavailable != null) return unavailable;
            try
            {
                // Next to the program when it was published for one system, in the system's folder otherwise.
                string? folder = new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native") }
                    .FirstOrDefault(f => Directory.Exists(f) && Directory.EnumerateFiles(f, "avcodec-*.dll").Any());
                if (folder == null || !OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                    return unavailable = L.T("video.noDecoder");
                ffmpeg.RootPath = folder;
                DynamicallyLoadedBindings.Initialize();
                // Nothing is written to a console that is not there. This is also the first call into the libraries.
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
                loaded = true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or NotSupportedException or EntryPointNotFoundException or TypeInitializationException)
            {
                unavailable = L.T("video.noDecoder");
            }
            return unavailable;
        }
    }

    internal static string Describe(int error)
    {
        const int size = 256;
        byte* text = stackalloc byte[size];
        ffmpeg.av_strerror(error, text, size);
        return Marshal.PtrToStringUTF8((nint)text) ?? error.ToString();
    }

    // ---------------------------------------------------------------- opening

    /// <summary>Opens a video file. Null, with what went wrong, when there is no video in it that can be read.</summary>
    public static VideoReader? Open(string file, out string? error)
    {
        error = Load();
        if (error != null) return null;
        var reader = new VideoReader();
        try
        {
            error = reader.OpenFile(file);
        }
        catch (NotSupportedException ex)
        {
            // A function the libraries do not have.
            error = ex.Message;
        }
        if (error == null) return reader;
        reader.Dispose();
        return null;
    }

    private string? OpenFile(string file)
    {
        AVFormatContext* opened = null;
        int result = ffmpeg.avformat_open_input(&opened, file, null, null);
        if (result < 0) return Describe(result);
        format = opened;
        if ((result = ffmpeg.avformat_find_stream_info(format, null)) < 0) return Describe(result);

        AVCodec* decoder = null;
        int index = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
        if (index < 0 || decoder == null) return L.T("video.noPicture");
        stream = format->streams[index];
        // Sound and whatever else the file holds is passed over unread.
        for (int i = 0; i < format->nb_streams; i++)
            if (i != index) format->streams[i]->discard = AVDiscard.AVDISCARD_ALL;

        codec = ffmpeg.avcodec_alloc_context3(decoder);
        if ((result = ffmpeg.avcodec_parameters_to_context(codec, stream->codecpar)) < 0) return Describe(result);
        // As many threads as the machine has to offer.
        codec->thread_count = 0;
        if ((result = ffmpeg.avcodec_open2(codec, decoder, null)) < 0) return Describe(result);
        if (codec->width <= 0 || codec->height <= 0 || codec->width > 8192 || codec->height > 8192) return L.T("video.noPicture");

        timeBase = ffmpeg.av_q2d(stream->time_base);
        startTime = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time;
        // The length of the pictures themselves, as osu! takes it; that of the whole file when they do not say.
        double duration = stream->duration > 0 ? stream->duration * timeBase * 1000
            : format->duration > 0 ? format->duration * 1000.0 / ffmpeg.AV_TIME_BASE
            : double.PositiveInfinity;
        // Turned and stretched pictures stay as they are stored, which is how osu! shows them.
        Info = new VideoInfo(codec->width, codec->height, duration);

        packet = ffmpeg.av_packet_alloc();
        decoded = ffmpeg.av_frame_alloc();
        held = ffmpeg.av_frame_alloc();
        thread = new Thread(Work) { IsBackground = true, Name = "video-decode" };
        thread.Start();
        return null;
    }

    // ---------------------------------------------------------------- the caller's side

    /// <summary>
    /// The picture to show at a time into the video: the last one that has begun by then, which past the
    /// end of the file is its last one. Waits until it is decoded. The pixels stay valid until the next call.
    /// </summary>
    /// <param name="ms">Milliseconds from the start of the video.</param>
    /// <param name="number">Tells pictures apart: the same picture has the same number every time.</param>
    /// <returns>False when there is nothing to show: the file has no pictures, or reading failed (see <see cref="Error"/>).</returns>
    public bool Read(double ms, out byte[] picture, out long number)
    {
        picture = Array.Empty<byte>();
        number = -1;
        if (Error != null || nothing) return false;
        ms = Math.Max(0, ms);

        // Going back needs a jump, and so does going further ahead than is worth decoding through.
        if (current == null
            || (ms < current.Time - Slack && (!first || ms < jumpedTo))
            || (!ended && ms > (next ?? current).Time + ReachMs))
            Jump(ms);

        // How far before the wanted time the jump goes, once a jump to the time itself found nothing.
        double back = 0;
        while (true)
        {
            if (current == null)
            {
                var found = Take();
                if (found == null) return false;
                if (found.End)
                {
                    // Nothing at or before that time, which a file can do when it ends earlier than it says.
                    // Its last picture is then looked for further and further back.
                    if (ms - back <= 0)
                    {
                        nothing = true;
                        return false;
                    }
                    back = back == 0 ? 1000 : back * 2;
                    Jump(Math.Max(0, ms - back));
                    continue;
                }
                current = found;
                first = true;
                continue;
            }

            if (next == null && !ended)
            {
                var found = Take();
                if (found == null) return false;
                if (found.End) ended = true;
                else next = found;
            }
            if (next == null || next.Time > ms + Slack) break;
            spare.Add(current.Pixels);
            current = next;
            next = null;
            first = false;
        }

        picture = current.Pixels;
        number = current.Serial;
        return true;
    }

    private void Jump(double ms)
    {
        if (current != null) spare.Add(current.Pixels);
        if (next != null) spare.Add(next.Pixels);
        current = next = null;
        ended = first = false;
        jumpedTo = ms;
        lock (gate)
        {
            jumpTo = ms;
            jump++;
            Monitor.PulseAll(gate);
        }
    }

    /// <summary>The next picture of the jump in force. Null when decoding failed or got stuck; <see cref="Error"/> says which.</summary>
    private Picture? Take()
    {
        long waited = Environment.TickCount64;
        while (true)
        {
            if (ready.TryTake(out Picture? picture, 100))
            {
                if (picture.Jump != jump)
                {
                    if (picture.Pixels.Length > 0) spare.Add(picture.Pixels);
                    continue;
                }
                if (picture.Error == null) return picture;
                Error = picture.Error;
                return null;
            }
            if (failure != null) Error = failure;
            else if (Environment.TickCount64 - waited > TimeoutMs) Error = L.T("video.stuck");
            if (Error != null) return null;
        }
    }

    // ---------------------------------------------------------------- the decoding thread

    private void Work()
    {
        int serving = 0;
        // Nothing to do until the next jump: before the first one, at the end of the file, after a failure.
        bool idle = true;
        // Still on the way to the time the jump asked for.
        bool seeking = false, holding = false;
        // The file has been gone through from its very beginning for this jump.
        bool fromStart = false;
        double wanted = 0, heldTime = 0;
        try
        {
            while (true)
            {
                bool jumped = false;
                lock (gate)
                {
                    while (!stopping && idle && jump == serving) Monitor.Wait(gate);
                    if (stopping) return;
                    if (jump != serving)
                    {
                        serving = jump;
                        wanted = jumpTo;
                        jumped = true;
                    }
                }
                if (jumped)
                {
                    idle = false;
                    seeking = true;
                    holding = fromStart = false;
                    ffmpeg.av_frame_unref(held);
                    string? problem = Seek(wanted);
                    if (problem != null)
                    {
                        Hand(new Picture { Error = problem }, serving);
                        idle = true;
                        continue;
                    }
                }

                int result = Decode();
                if (result == ffmpeg.AVERROR_EOF)
                {
                    // The last picture before the wanted time is the last of the file.
                    if (holding) Hand(Convert(held, heldTime), serving);
                    holding = false;
                    ffmpeg.av_frame_unref(held);
                    Hand(new Picture { End = true }, serving);
                    idle = true;
                    continue;
                }
                if (result < 0)
                {
                    Hand(new Picture { Error = Describe(result) }, serving);
                    idle = true;
                    continue;
                }

                double time = lastTime = TimeOf(decoded);
                if (seeking)
                {
                    if (time <= wanted + Slack)
                    {
                        // Not past the wanted time yet: kept, as it may turn out to be the last one before it.
                        // Only that one is ever made into pixels.
                        ffmpeg.av_frame_unref(held);
                        ffmpeg.av_frame_move_ref(held, decoded);
                        heldTime = time;
                        holding = true;
                        continue;
                    }
                    // The jump came out after the wanted time, which a file with a poor index can do. Going
                    // through it from the beginning finds the picture all the same.
                    if (!holding && !fromStart && wanted > 0)
                    {
                        fromStart = true;
                        ffmpeg.av_frame_unref(decoded);
                        string? problem = Seek(0);
                        if (problem == null) continue;
                        Hand(new Picture { Error = problem }, serving);
                        idle = true;
                        continue;
                    }
                    if (holding) Hand(Convert(held, heldTime), serving);
                    holding = seeking = false;
                    ffmpeg.av_frame_unref(held);
                }
                Hand(Convert(decoded, time), serving);
                ffmpeg.av_frame_unref(decoded);
            }
        }
        catch (Exception ex)
        {
            // Whatever goes wrong in here ends the video, not the program.
            failure = ex.Message;
        }
    }

    /// <summary>Puts a picture where the caller finds it, waiting while enough lie ready. A jump or the end makes it moot.</summary>
    private void Hand(Picture picture, int serving)
    {
        picture.Jump = serving;
        while (!ready.TryAdd(picture, 20))
        {
            if (!stopping && jump == serving) continue;
            if (picture.Pixels.Length > 0) spare.Add(picture.Pixels);
            return;
        }
    }

    /// <summary>Goes to the key picture at or before a time, from where decoding reaches the time itself.</summary>
    private string? Seek(double ms)
    {
        long timestamp = startTime + (long)Math.Floor(ms / 1000 / timeBase);
        int result = ffmpeg.av_seek_frame(format, stream->index, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);
        // A file that cannot go there is read from its beginning: slower, and as right.
        if (result < 0) result = ffmpeg.av_seek_frame(format, stream->index, startTime, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (result < 0) result = ffmpeg.av_seek_frame(format, -1, 0, ffmpeg.AVSEEK_FLAG_BYTE);
        if (result < 0) return Describe(result);
        ffmpeg.avcodec_flush_buffers(codec);
        lastTime = 0;
        return null;
    }

    /// <summary>Decodes the next picture into <see cref="decoded"/>. Zero when it did, or FFmpeg's word for the end of the file.</summary>
    private int Decode()
    {
        while (true)
        {
            int result = ffmpeg.avcodec_receive_frame(codec, decoded);
            if (result != ffmpeg.AVERROR(ffmpeg.EAGAIN)) return result < 0 ? result : 0;

            // The decoder wants more of the file.
            if (ffmpeg.av_read_frame(format, packet) < 0)
            {
                // No more: it hands out what it still holds, then says so itself.
                ffmpeg.avcodec_send_packet(codec, null);
                continue;
            }
            // A damaged piece is passed over; the pictures after it may be fine.
            if (packet->stream_index == stream->index) ffmpeg.avcodec_send_packet(codec, packet);
            ffmpeg.av_packet_unref(packet);
        }
    }

    /// <summary>When a picture begins, in milliseconds from the start of the video.</summary>
    private double TimeOf(AVFrame* frame)
    {
        long stamp = frame->best_effort_timestamp;
        if (stamp == ffmpeg.AV_NOPTS_VALUE) stamp = frame->pts;
        if (stamp == ffmpeg.AV_NOPTS_VALUE) stamp = frame->pkt_dts;
        // A picture that does not say follows the one before it.
        if (stamp == ffmpeg.AV_NOPTS_VALUE)
        {
            double rate = ffmpeg.av_q2d(stream->avg_frame_rate);
            return lastTime + 1000 / (rate > 0 ? rate : 30);
        }
        return (stamp - startTime) * timeBase * 1000;
    }

    /// <summary>Makes a decoded picture into RGBA pixels of the video's size.</summary>
    /// <param name="time">When the picture begins, in milliseconds from the start of the video.</param>
    private Picture Convert(AVFrame* frame, double time)
    {
        var wanted = (frame->width, frame->height, frame->format, frame->colorspace, frame->color_range);
        if (scaler == null || wanted != scalerFor)
        {
            ffmpeg.sws_freeContext(scaler);
            scaler = ffmpeg.sws_getContext(frame->width, frame->height, (AVPixelFormat)frame->format,
                Info.Width, Info.Height, AVPixelFormat.AV_PIX_FMT_RGBA, ffmpeg.SWS_BILINEAR, null, null, null);
            if (scaler == null) throw new InvalidOperationException(L.T("video.noPicture"));
            scalerFor = wanted;
            var description = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)frame->format);
            scalerAlpha = description != null && (description->flags & ffmpeg.AV_PIX_FMT_FLAG_ALPHA) != 0;

            // The colours the file says its pictures are in; a file that says nothing gets the usual ones.
            int space = frame->colorspace switch
            {
                AVColorSpace.AVCOL_SPC_BT709 => ffmpeg.SWS_CS_ITU709,
                AVColorSpace.AVCOL_SPC_FCC => ffmpeg.SWS_CS_FCC,
                AVColorSpace.AVCOL_SPC_SMPTE240M => ffmpeg.SWS_CS_SMPTE240M,
                AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => ffmpeg.SWS_CS_BT2020,
                _ => ffmpeg.SWS_CS_DEFAULT,
            };
            var from = Coefficients(space);
            var to = Coefficients(ffmpeg.SWS_CS_DEFAULT);
            int fullRange = frame->color_range == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0;
            // Refused for pictures that are not in such colours to begin with, which is fine.
            ffmpeg.sws_setColorspaceDetails(scaler, in from, fullRange, in to, 1, 0, 1 << 16, 1 << 16);
        }

        if (!spare.TryTake(out byte[]? pixels)) pixels = GC.AllocateUninitializedArray<byte>(Info.Width * Info.Height * 4);
        fixed (byte* start = pixels)
        {
            target[0] = start;
            targetStride[0] = Info.Width * 4;
            ffmpeg.sws_scale(scaler, frame->data, frame->linesize, 0, frame->height, target, targetStride);
            target[0] = null;
        }
        // A video is opaque, whatever it says: a see-through one would not be drawn the way osu! draws it anyway.
        if (scalerAlpha)
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        return new Picture { Pixels = pixels, Time = time, Serial = ++serial };
    }

    private static int_array4 Coefficients(int space)
    {
        int* table = ffmpeg.sws_getCoefficients(space);
        var result = new int_array4();
        for (uint i = 0; i < 4; i++) result[i] = table[i];
        return result;
    }

    // ---------------------------------------------------------------- closing

    public void Dispose()
    {
        lock (gate)
        {
            stopping = true;
            Monitor.PulseAll(gate);
        }
        // What FFmpeg holds is only let go of once nothing works with it any more.
        if (thread != null && !thread.Join(5000)) return;

        ffmpeg.sws_freeContext(scaler);
        scaler = null;
        fixed (AVFrame** frame = &decoded) ffmpeg.av_frame_free(frame);
        fixed (AVFrame** frame = &held) ffmpeg.av_frame_free(frame);
        fixed (AVPacket** pointer = &packet) ffmpeg.av_packet_free(pointer);
        fixed (AVCodecContext** context = &codec) ffmpeg.avcodec_free_context(context);
        fixed (AVFormatContext** context = &format) ffmpeg.avformat_close_input(context);
    }
}
