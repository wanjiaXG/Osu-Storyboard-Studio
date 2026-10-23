using NAudio.Wave;

namespace SbStudio.App.Audio;

/// <summary>Runs the first processing pass before the device starts, without inserting output delay.</summary>
internal sealed class PrimedSource(ISampleProvider source, int frames) : ISampleProvider
{
    private readonly float[] first = new float[frames * source.WaveFormat.Channels];
    private int available, position;
    public WaveFormat WaveFormat => source.WaveFormat;

    internal void Prime() => available = source.Read(first, 0, first.Length);

    public int Read(float[] buffer, int offset, int count)
    {
        int take = Math.Min(count, available - position);
        for (int i = 0; i < take; i++) buffer[offset + i] = first[position + i];
        position += take;
        return take + (take < count ? source.Read(buffer, offset + take, count - take) : 0);
    }
}
