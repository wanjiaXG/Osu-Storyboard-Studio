using NAudio.Wave;

namespace SbStudio.App.Audio;

/// <summary>Normalized PCM retained for Timing: seeking and playback never decode or read a file.</summary>
internal sealed class TimingMusic
{
    private const int BlockSamples = 65536;
    private readonly float[][] blocks;
    internal long SampleCount { get; }
    internal long Bytes => SampleCount * sizeof(float);

    internal TimingMusic(ISampleProvider source, Func<bool> cancelled)
    {
        var all = new List<float[]>();
        var scratch = new float[512];
        long samples = 0;
        while (true)
        {
            if (cancelled()) throw new OperationCanceledException();
            var block = new float[BlockSamples];
            int used = 0, read;
            // WDL's upsampling output filter assumes an output offset of zero. Always decode
            // into scratch so later pages cannot re-filter/overwrite earlier cached samples.
            while (used < block.Length && (read = source.Read(scratch, 0, Math.Min(scratch.Length, block.Length - used))) > 0)
            {
                Array.Copy(scratch, 0, block, used, read);
                used += read;
                if (cancelled()) throw new OperationCanceledException();
            }
            if (used == 0) break;
            if (used < block.Length) Array.Resize(ref block, used);
            all.Add(block);
            samples += used;
            if (used < BlockSamples) break;
        }
        blocks = all.ToArray();
        SampleCount = samples;
    }

    internal ISampleProvider At(double timeMs) => new Cursor(this,
        (long)Math.Round(Math.Max(0, timeMs) * AudioEngine.SampleRate / 1000) * 2);

    private sealed class Cursor(TimingMusic music, long position) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioEngine.SampleRate, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            int take = (int)Math.Max(0, Math.Min(count, music.SampleCount - position));
            for (int used = 0; used < take;)
            {
                var block = music.blocks[(int)(position / BlockSamples)];
                int start = (int)(position % BlockSamples), n = Math.Min(take - used, block.Length - start);
                for (int i = 0; i < n; i++) buffer[offset + used + i] = block[start + i];
                position += n;
                used += n;
            }
            return take;
        }
    }
}
