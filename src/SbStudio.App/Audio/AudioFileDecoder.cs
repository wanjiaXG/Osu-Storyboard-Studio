using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SbStudio.App.Gfx;
using SbStudio.Core;

namespace SbStudio.App.Audio;

/// <summary>What an audio file is like, for telling the user what was read.</summary>
/// <param name="Codec">The name FFmpeg has for the way it is stored, such as "flac".</param>
/// <param name="Lossless">Nothing was thrown away when it was stored: FLAC, WAV and the like.</param>
public sealed record AudioFileInfo(string Codec, int SampleRate, int Channels, bool Lossless);

/// <summary>
/// Reads a whole audio file of whatever kind into memory with the FFmpeg libraries that come with the
/// program, as stereo at the sample rate that is asked for. It is for the audio a video export is given
/// in place of the beatmap's own: FLAC, WAV, ALAC, APE and anything else FFmpeg knows, which is more
/// than the readers used for playback do.
/// </summary>
public static unsafe class AudioFileDecoder
{
    /// <summary>The samples of a file, left and right by turns. Null, with what went wrong, when it has no sound that can be read.</summary>
    /// <param name="sampleRate">
    /// The sample rate to bring the sound to. A file that has it already is taken sample for sample; any
    /// other is converted this once, as carefully as the libraries can.
    /// </param>
    public static float[]? Read(string file, int sampleRate, out AudioFileInfo? info, out string? error)
    {
        info = null;
        error = VideoReader.Load();
        if (error != null) return null;

        AVFormatContext* format = null;
        AVCodecContext* codec = null;
        SwrContext* resampler = null;
        AVPacket* packet = null;
        AVFrame* frame = null;
        try
        {
            Check(ffmpeg.avformat_open_input(&format, file, null, null));
            Check(ffmpeg.avformat_find_stream_info(format, null));
            AVCodec* decoder = null;
            int index = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &decoder, 0);
            if (index < 0 || decoder == null) throw new InvalidOperationException(L.T("audiofile.noSound"));

            codec = ffmpeg.avcodec_alloc_context3(decoder);
            Check(ffmpeg.avcodec_parameters_to_context(codec, format->streams[index]->codecpar));
            Check(ffmpeg.avcodec_open2(codec, decoder, null));
            var description = ffmpeg.avcodec_descriptor_get(codec->codec_id);
            bool lossless = description != null && (description->props & ffmpeg.AV_CODEC_PROP_LOSSLESS) != 0 && (description->props & ffmpeg.AV_CODEC_PROP_LOSSY) == 0;
            info = new AudioFileInfo(Marshal.PtrToStringUTF8((nint)decoder->name) ?? "", codec->sample_rate, codec->ch_layout.nb_channels, lossless);

            // As much room as the file says it is long, and more as it turns out to be needed.
            double seconds = format->duration > 0 ? format->duration / (double)ffmpeg.AV_TIME_BASE : 240;
            var samples = new float[Math.Max(4096, (long)((seconds + 1) * sampleRate) * 2)];
            long used = 0;
            (int Rate, int Format, int Channels) resamplerFor = default;

            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            bool draining = false;
            while (true)
            {
                int result = ffmpeg.avcodec_receive_frame(codec, frame);
                if (result == ffmpeg.AVERROR_EOF) break;
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    if (draining) break;
                    // The decoder wants more of the file; at its end it is told so and hands out what it still holds.
                    if (ffmpeg.av_read_frame(format, packet) < 0)
                    {
                        ffmpeg.avcodec_send_packet(codec, null);
                        draining = true;
                        continue;
                    }
                    // A damaged piece is passed over; what follows may be fine.
                    if (packet->stream_index == index) ffmpeg.avcodec_send_packet(codec, packet);
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }
                Check(result);

                // Set up for the sound as the decoder gives it, which a file need not say truthfully up front.
                var now = (frame->sample_rate, frame->format, frame->ch_layout.nb_channels);
                if (resampler == null || now != resamplerFor)
                {
                    if (resampler != null) Convert(resampler, null, 0, ref samples, ref used);
                    ffmpeg.swr_free(&resampler);
                    AVChannelLayout from = frame->ch_layout, stereo;
                    // A file that does not say which channel is which gets the usual arrangement for their number.
                    if (from.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC) ffmpeg.av_channel_layout_default(&from, Math.Max(1, from.nb_channels));
                    ffmpeg.av_channel_layout_default(&stereo, 2);
                    Check(ffmpeg.swr_alloc_set_opts2(&resampler, &stereo, AVSampleFormat.AV_SAMPLE_FMT_FLT, sampleRate,
                        &from, (AVSampleFormat)frame->format, frame->sample_rate, 0, null));
                    // Another sample rate is brought to the one asked for as carefully as the libraries can.
                    if (frame->sample_rate != sampleRate)
                    {
                        ffmpeg.av_opt_set(resampler, "resampler", "soxr", 0);
                        ffmpeg.av_opt_set_int(resampler, "precision", 28, 0);
                    }
                    if (ffmpeg.swr_init(resampler) < 0)
                    {
                        // Without that resampler, the built-in one.
                        ffmpeg.av_opt_set(resampler, "resampler", "swr", 0);
                        Check(ffmpeg.swr_init(resampler));
                    }
                    resamplerFor = now;
                }
                Convert(resampler, frame, frame->nb_samples, ref samples, ref used);
                ffmpeg.av_frame_unref(frame);
            }
            // What the resampler still holds back.
            if (resampler != null) Convert(resampler, null, 0, ref samples, ref used);

            if (used == 0) throw new InvalidOperationException(L.T("audiofile.noSound"));
            Array.Resize(ref samples, (int)(used * 2));
            return samples;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or OverflowException or OutOfMemoryException)
        {
            error = ex is InvalidOperationException ? ex.Message : L.T("audiofile.tooLong");
            return null;
        }
        finally
        {
            ffmpeg.swr_free(&resampler);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avcodec_free_context(&codec);
            ffmpeg.avformat_close_input(&format);
        }
    }

    private static void Check(int result)
    {
        if (result < 0) throw new InvalidOperationException(VideoReader.Describe(result));
    }

    /// <summary>Puts a decoded frame through the resampler, or with no frame gets out what it has left, and adds the result to the samples.</summary>
    /// <param name="used">How many stereo frames the samples hold so far.</param>
    private static void Convert(SwrContext* resampler, AVFrame* frame, int count, ref float[] samples, ref long used)
    {
        while (true)
        {
            int room = ffmpeg.swr_get_out_samples(resampler, count);
            if (room <= 0) return;
            if ((used + room) * 2 > samples.Length)
                Array.Resize(ref samples, checked((int)Math.Max((used + room) * 2, Math.Min(int.MaxValue - 64, samples.Length * 3L / 2))));
            int got;
            fixed (float* start = &samples[used * 2])
            {
                byte* output = (byte*)start;
                got = ffmpeg.swr_convert(resampler, &output, room, frame == null ? null : frame->extended_data, count);
            }
            Check(got);
            used += got;
            // A frame goes in once; after that, and when there was none, only what is left comes out.
            if (frame != null || got == 0) return;
        }
    }
}
