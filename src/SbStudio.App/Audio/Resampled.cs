using FFmpeg.AutoGen;
using NAudio.Wave;
using SbStudio.App.Gfx;

namespace SbStudio.App.Audio;

/// <summary>
/// Stereo sound at another sample rate, by the resampler of the FFmpeg libraries that come with the
/// program, set to be as careful as it can. The readers used for playback bring their own, which is
/// quick and plain and takes some of the treble with it: good enough to listen along to, not for the
/// sound of a file that is kept.
/// </summary>
internal sealed unsafe class Resampled : ISampleProvider, IDisposable
{
    private readonly ISampleProvider source;
    private readonly float[] input = new float[16384];
    private SwrContext* resampler;
    /// <summary>What came out of the resampler and has not been asked for yet.</summary>
    private float[] pending = Array.Empty<float>();
    private int pendingLength, pendingAt;
    /// <summary>The source has run out; the resampler has given all it held back.</summary>
    private bool ended, finished;

    private Resampled(ISampleProvider source, SwrContext* resampler, int sampleRate)
    {
        this.source = source;
        this.resampler = resampler;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
    }

    /// <summary>A stereo source at a sample rate. Null when the libraries, or a resampler for these rates, cannot be had.</summary>
    public static Resampled? Create(ISampleProvider stereo, int sampleRate)
    {
        if (stereo.WaveFormat.Channels != 2 || VideoReader.Load() != null) return null;
        SwrContext* context = null;
        AVChannelLayout layout;
        ffmpeg.av_channel_layout_default(&layout, 2);
        if (ffmpeg.swr_alloc_set_opts2(&context, &layout, AVSampleFormat.AV_SAMPLE_FMT_FLT, sampleRate,
                &layout, AVSampleFormat.AV_SAMPLE_FMT_FLT, stereo.WaveFormat.SampleRate, 0, null) < 0) return null;
        ffmpeg.av_opt_set(context, "resampler", "soxr", 0);
        ffmpeg.av_opt_set_int(context, "precision", 28, 0);
        if (ffmpeg.swr_init(context) < 0)
        {
            // Without that resampler, the built-in one.
            ffmpeg.av_opt_set(context, "resampler", "swr", 0);
            if (ffmpeg.swr_init(context) < 0)
            {
                ffmpeg.swr_free(&context);
                return null;
            }
        }
        return new Resampled(stereo, context, sampleRate);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int done = 0;
        while (done < count)
        {
            if (pendingAt == pendingLength && !Fill()) break;
            int take = Math.Min(count - done, pendingLength - pendingAt);
            for (int i = 0; i < take; i++) buffer[offset + done + i] = pending[pendingAt + i];
            pendingAt += take;
            done += take;
        }
        return done;
    }

    /// <summary>
    /// Puts the next piece of the source through the resampler. Room is made for all that can come out of
    /// it, so nothing waits inside: asking the resampler for what it holds back is how it is told that
    /// the sound has ended, and is only done then.
    /// </summary>
    /// <returns>False when there is no more.</returns>
    private bool Fill()
    {
        pendingAt = pendingLength = 0;
        while (pendingLength == 0)
        {
            if (finished || resampler == null) return false;
            int read = ended ? 0 : source.Read(input, 0, input.Length);
            if (read <= 0) ended = true;
            int frames = Math.Max(0, read) / 2;
            int room = ffmpeg.swr_get_out_samples(resampler, frames);
            if (room <= 0)
            {
                if (ended) finished = true;
                continue;
            }
            if (pending.Length < room * 2) pending = new float[room * 2];
            int got;
            fixed (float* to = pending)
            fixed (float* from = input)
            {
                byte* output = (byte*)to, fed = (byte*)from;
                got = ended ? ffmpeg.swr_convert(resampler, &output, room, null, 0) : ffmpeg.swr_convert(resampler, &output, room, &fed, frames);
            }
            if (got < 0 || (ended && got == 0)) finished = true;
            else pendingLength = got * 2;
        }
        return true;
    }

    public void Dispose()
    {
        fixed (SwrContext** context = &resampler) ffmpeg.swr_free(context);
    }
}
