using FFmpeg.AutoGen;
using SbStudio.Core;

namespace SbStudio.App.Gfx;

/// <summary>What a video file is to be like.</summary>
/// <param name="Container">The muxer's name: "mp4", "matroska", "flv" or "avi".</param>
/// <param name="Encoder">The video encoder's name, such as "libx264" or "h264_nvenc".</param>
/// <param name="Bitrate">Bits per second of the picture.</param>
/// <param name="Audio">The file gets a sound track.</param>
/// <param name="AudioBitrate">Bits per second of the sound.</param>
/// <param name="AudioSampleRate">Samples per second of the sound, which is stereo.</param>
public sealed record VideoWriterOptions(string Path, string Container, string Encoder, int Width, int Height, int Fps, long Bitrate, bool Audio,
    long AudioBitrate = 192_000, int AudioSampleRate = 44100);

/// <summary>
/// Writes a video file with the FFmpeg libraries that come with the program: pictures as the renderer
/// reads them back, and sound as the mixer gives it. Pictures are numbered as they come, one for each
/// frame of the frame rate, and the sound is cut into the pieces its encoder wants.
/// </summary>
public sealed unsafe class VideoWriter : IDisposable
{
    /// <summary>The sound of this kind of file is MP3, which players expect in an AVI; in every other kind it is AAC.</summary>
    public static bool Mp3Sound(string extension) => ContainerOf(extension) == "avi";

    private AVFormatContext* format;
    private AVCodecContext* video, audio;
    private AVStream* videoStream, audioStream;
    private AVFrame* picture, sound;
    private AVPacket* packet;
    private SwsContext* scaler;
    private readonly byte*[] source = new byte*[4];
    private readonly int[] sourceStride = new int[4];
    private long pictures, samples;
    private float[] pending = Array.Empty<float>();
    private int pendingFrames;
    private bool finished;
    /// <summary>The file has been created, or emptied if it was there: from here on it is this writer's.</summary>
    private bool fileOpened;

    private VideoWriter()
    {
    }

    /// <summary>The names FFmpeg knows the kinds of file by, for the extensions a video can be saved with.</summary>
    public static string ContainerOf(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    {
        "mkv" => "matroska",
        "flv" => "flv",
        "avi" => "avi",
        _ => "mp4",
    };

    private static int Check(int result)
    {
        if (result < 0) throw new InvalidOperationException(VideoReader.Describe(result));
        return result;
    }

    // ---------------------------------------------------------------- opening

    /// <summary>Begins a file. Null, with what went wrong, when it cannot be written like that.</summary>
    public static VideoWriter? Open(VideoWriterOptions options, out string? error)
    {
        error = VideoReader.Load();
        if (error != null) return null;
        var writer = new VideoWriter();
        try
        {
            writer.OpenFile(options);
            return writer;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            error = ex.Message;
            writer.Dispose();
            // A file that was there before and has not been touched stays; one that was begun is of no use.
            if (writer.fileOpened) Delete(options.Path);
            return null;
        }
    }

    /// <summary>Removes a file that did not get to its end. One that cannot be removed is left where it is.</summary>
    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Whether an encoder can be started on this machine. One that belongs to a graphics card is there in
    /// the libraries on every machine, but works only with the card and its driver.
    /// </summary>
    public static bool CanEncode(string encoder)
    {
        if (VideoReader.Load() != null) return false;
        try
        {
            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(encoder);
            if (codec == null) return false;
            AVCodecContext* context = ffmpeg.avcodec_alloc_context3(codec);
            SetUpVideo(context, encoder, 1280, 720, 30, 4_000_000);
            bool works = ffmpeg.avcodec_open2(context, codec, null) >= 0;
            ffmpeg.avcodec_free_context(&context);
            return works;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>What a card's encoder takes its pictures in: Intel's wants the colour planes interleaved.</summary>
    private static AVPixelFormat PixelFormatOf(string encoder) =>
        encoder.EndsWith("_qsv", StringComparison.Ordinal) ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_YUV420P;

    private static void SetUpVideo(AVCodecContext* context, string encoder, int width, int height, int fps, long bitrate)
    {
        context->width = width;
        context->height = height;
        context->time_base = new AVRational { num = 1, den = fps };
        context->framerate = new AVRational { num = fps, den = 1 };
        context->pix_fmt = PixelFormatOf(encoder);
        context->bit_rate = bitrate;
        // The bit rate is an average; no stretch gets more than twice of it.
        context->rc_max_rate = bitrate * 2;
        context->rc_buffer_size = (int)Math.Min(int.MaxValue, bitrate * 2);
        // A key picture every two seconds, so that a player gets to any moment quickly.
        context->gop_size = fps * 2;
        context->max_b_frames = 2;
        // The colours of high-definition video, which is what players take a file to have when it does not say.
        context->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        context->color_range = AVColorRange.AVCOL_RANGE_MPEG;

        if (encoder is "libx264" or "libx265") ffmpeg.av_opt_set(context->priv_data, "preset", "medium", 0);
        // x265 writes about its work to a console of its own accord; there is none to write to.
        if (encoder == "libx265") ffmpeg.av_opt_set(context->priv_data, "x265-params", "log-level=error", 0);
        else if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            ffmpeg.av_opt_set(context->priv_data, "preset", "p5", 0);
            ffmpeg.av_opt_set(context->priv_data, "rc", "vbr", 0);
        }
        else if (encoder.EndsWith("_qsv", StringComparison.Ordinal)) ffmpeg.av_opt_set(context->priv_data, "preset", "medium", 0);
        else if (encoder.EndsWith("_amf", StringComparison.Ordinal))
        {
            ffmpeg.av_opt_set(context->priv_data, "quality", "balanced", 0);
            ffmpeg.av_opt_set(context->priv_data, "rc", "vbr_peak", 0);
        }
    }

    private void OpenFile(VideoWriterOptions options)
    {
        AVFormatContext* opened = null;
        Check(ffmpeg.avformat_alloc_output_context2(&opened, null, options.Container, options.Path));
        format = opened;
        bool globalHeader = (format->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0;

        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(options.Encoder);
        if (codec == null) throw new InvalidOperationException(L.T("videoexport.noEncoder", options.Encoder));
        video = ffmpeg.avcodec_alloc_context3(codec);
        SetUpVideo(video, options.Encoder, options.Width, options.Height, options.Fps, options.Bitrate);
        // An AVI file has no way of saying that pictures are stored out of order.
        if (options.Container == "avi") video->max_b_frames = 0;
        // What Apple's players want an H.265 picture in an MP4 file to be called.
        if (options.Container == "mp4" && codec->id == AVCodecID.AV_CODEC_ID_HEVC) video->codec_tag = 'h' | 'v' << 8 | 'c' << 16 | (uint)'1' << 24;
        if (globalHeader) video->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        Check(ffmpeg.avcodec_open2(video, codec, null));

        videoStream = ffmpeg.avformat_new_stream(format, null);
        videoStream->time_base = video->time_base;
        videoStream->avg_frame_rate = video->framerate;
        Check(ffmpeg.avcodec_parameters_from_context(videoStream->codecpar, video));

        if (options.Audio)
        {
            // AAC everywhere but in AVI, where players expect MP3.
            AVCodec* audioCodec = options.Container == "avi" ? ffmpeg.avcodec_find_encoder_by_name("libmp3lame") : ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
            if (audioCodec == null) audioCodec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
            if (audioCodec == null) throw new InvalidOperationException(L.T("videoexport.noEncoder", "aac"));
            audio = ffmpeg.avcodec_alloc_context3(audioCodec);
            audio->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            audio->sample_rate = options.AudioSampleRate;
            audio->bit_rate = options.AudioBitrate;
            audio->time_base = new AVRational { num = 1, den = options.AudioSampleRate };
            ffmpeg.av_channel_layout_default(&audio->ch_layout, 2);
            if (globalHeader) audio->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            Check(ffmpeg.avcodec_open2(audio, audioCodec, null));

            audioStream = ffmpeg.avformat_new_stream(format, null);
            audioStream->time_base = audio->time_base;
            Check(ffmpeg.avcodec_parameters_from_context(audioStream->codecpar, audio));

            sound = ffmpeg.av_frame_alloc();
            sound->nb_samples = audio->frame_size > 0 ? audio->frame_size : 1024;
            sound->format = (int)audio->sample_fmt;
            sound->sample_rate = audio->sample_rate;
            Check(ffmpeg.av_channel_layout_copy(&sound->ch_layout, &audio->ch_layout));
            Check(ffmpeg.av_frame_get_buffer(sound, 0));
            pending = new float[sound->nb_samples * 2 * 4];
        }

        picture = ffmpeg.av_frame_alloc();
        picture->format = (int)video->pix_fmt;
        picture->width = video->width;
        picture->height = video->height;
        picture->colorspace = video->colorspace;
        picture->color_range = video->color_range;
        Check(ffmpeg.av_frame_get_buffer(picture, 0));
        packet = ffmpeg.av_packet_alloc();

        // Exact rounding, and colour taken from every pixel: the quick way comes out a shade darker all over.
        scaler = ffmpeg.sws_getContext(options.Width, options.Height, AVPixelFormat.AV_PIX_FMT_RGBA,
            options.Width, options.Height, video->pix_fmt, ffmpeg.SWS_BILINEAR | ffmpeg.SWS_ACCURATE_RND | ffmpeg.SWS_FULL_CHR_H_INP, null, null, null);
        if (scaler == null) throw new InvalidOperationException(L.T("videoexport.noEncoder", options.Encoder));
        // From the full range of the screen's colours to the narrower one video is stored in.
        var from = Coefficients(ffmpeg.SWS_CS_DEFAULT);
        var to = Coefficients(ffmpeg.SWS_CS_ITU709);
        ffmpeg.sws_setColorspaceDetails(scaler, in from, 1, in to, 0, 0, 1 << 16, 1 << 16);

        if ((format->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0) Check(ffmpeg.avio_open(&format->pb, options.Path, ffmpeg.AVIO_FLAG_WRITE));
        fileOpened = true;
        AVDictionary* settings = null;
        // The index at the head of the file, so that a player or a website can begin before it has all of it.
        if (options.Container == "mp4") ffmpeg.av_dict_set(&settings, "movflags", "+faststart", 0);
        int result = ffmpeg.avformat_write_header(format, &settings);
        ffmpeg.av_dict_free(&settings);
        Check(result);
    }

    private static int_array4 Coefficients(int space)
    {
        int* table = ffmpeg.sws_getCoefficients(space);
        var result = new int_array4();
        for (uint i = 0; i < 4; i++) result[i] = table[i];
        return result;
    }

    // ---------------------------------------------------------------- writing

    /// <summary>Adds the next picture. The pixels are RGBA rows of the video's size, the bottom row first, as OpenGL hands them over.</summary>
    public void WritePicture(byte[] rgba)
    {
        Check(ffmpeg.av_frame_make_writable(picture));
        int stride = video->width * 4;
        fixed (byte* pixels = rgba)
        {
            // Read from the last row up: that turns the picture the right way round on the way.
            source[0] = pixels + (long)(video->height - 1) * stride;
            sourceStride[0] = -stride;
            ffmpeg.sws_scale(scaler, source, sourceStride, 0, video->height, picture->data, picture->linesize);
            source[0] = null;
        }
        picture->pts = pictures++;
        Encode(video, videoStream, picture);
    }

    /// <summary>Adds sound: stereo, left and right sample by turns, at the sample rate the file was opened with. Ignored by a file without sound.</summary>
    public void WriteSound(float[] interleaved, int frames)
    {
        if (audio == null) return;
        if ((pendingFrames + frames) * 2 > pending.Length) Array.Resize(ref pending, Math.Max(pending.Length * 2, (pendingFrames + frames) * 2));
        Array.Copy(interleaved, 0, pending, pendingFrames * 2, frames * 2);
        pendingFrames += frames;

        int size = sound->nb_samples, used = 0;
        while (pendingFrames - used >= size)
        {
            SendSound(used, size);
            used += size;
        }
        if (used == 0) return;
        Array.Copy(pending, used * 2, pending, 0, (pendingFrames - used) * 2);
        pendingFrames -= used;
    }

    /// <summary>Hands one piece of the waiting sound to the encoder, each channel on its own as the encoder wants it.</summary>
    private void SendSound(int from, int count)
    {
        Check(ffmpeg.av_frame_make_writable(sound));
        float* left = (float*)sound->data[0], right = (float*)sound->data[1];
        for (int i = 0; i < sound->nb_samples; i++)
        {
            // What is missing from the last piece is silence.
            bool have = i < count;
            left[i] = have ? pending[(from + i) * 2] : 0;
            right[i] = have ? pending[(from + i) * 2 + 1] : 0;
        }
        sound->pts = samples;
        samples += sound->nb_samples;
        Encode(audio, audioStream, sound);
    }

    /// <summary>Gives the encoder a frame, or null for "no more", and writes whatever it has ready into the file.</summary>
    private void Encode(AVCodecContext* context, AVStream* stream, AVFrame* frame)
    {
        Check(ffmpeg.avcodec_send_frame(context, frame));
        while (true)
        {
            int result = ffmpeg.avcodec_receive_packet(context, packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return;
            Check(result);
            ffmpeg.av_packet_rescale_ts(packet, context->time_base, stream->time_base);
            packet->stream_index = stream->index;
            // Sorts sound and pictures into the order of their times, and lets go of the packet.
            Check(ffmpeg.av_interleaved_write_frame(format, packet));
        }
    }

    /// <summary>Writes what the encoders still hold, and the end of the file. Without this the file cannot be played.</summary>
    public void Finish()
    {
        if (finished) return;
        finished = true;
        if (audio != null)
        {
            if (pendingFrames > 0) SendSound(0, pendingFrames);
            pendingFrames = 0;
            Encode(audio, audioStream, null);
        }
        Encode(video, videoStream, null);
        Check(ffmpeg.av_write_trailer(format));
        Close();
    }

    private void Close()
    {
        if (format != null && format->pb != null && (format->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0) ffmpeg.avio_closep(&format->pb);
    }

    public void Dispose()
    {
        ffmpeg.sws_freeContext(scaler);
        scaler = null;
        fixed (AVFrame** frame = &picture) ffmpeg.av_frame_free(frame);
        fixed (AVFrame** frame = &sound) ffmpeg.av_frame_free(frame);
        fixed (AVPacket** pointer = &packet) ffmpeg.av_packet_free(pointer);
        fixed (AVCodecContext** context = &video) ffmpeg.avcodec_free_context(context);
        fixed (AVCodecContext** context = &audio) ffmpeg.avcodec_free_context(context);
        if (format == null) return;
        Close();
        ffmpeg.avformat_free_context(format);
        format = null;
    }
}
