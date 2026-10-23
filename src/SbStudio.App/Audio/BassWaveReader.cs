using ManagedBass;
using NAudio.Wave;
using WaveFormat = NAudio.Wave.WaveFormat;
using System.Runtime.InteropServices;
using SbStudio.Resources;

namespace SbStudio.App.Audio;

/// <summary>
/// Native-rate float PCM from the same BASS decoder used by osu!lazer. BASS handles each file's
/// encoder delay, padding and exact MP3 seeks; the application adds no decoder-delay constants.
/// </summary>
internal sealed unsafe class BassWaveReader : WaveStream
{
    private int handle;
    private readonly long length;
    private long position;
    private GCHandle resourceMemory;

    public BassWaveReader(string file)
    {
        BassRuntime.PrepareDecoding();
        try
        {
            const BassFlags flags = BassFlags.Decode | BassFlags.Float | BassFlags.Prescan;
            if (ResourceAssets.IsEmbedded(file))
            {
                byte[] data = ResourceAssets.ReadAllBytes(file);
                resourceMemory = GCHandle.Alloc(data, GCHandleType.Pinned);
                handle = Bass.CreateStream(resourceMemory.AddrOfPinnedObject(), 0, data.LongLength, flags);
            }
            else handle = Bass.CreateStream(file, 0, 0, flags);
            if (handle == 0) throw BassRuntime.Failure();
            if (!Bass.ChannelGetInfo(handle, out ChannelInfo info)) throw BassRuntime.Failure();
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(info.Frequency, info.Channels);
            length = Bass.ChannelGetLength(handle);
            if (length < 0) throw BassRuntime.Failure();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public override WaveFormat WaveFormat { get; }
    public override long Length => length;
    public override long Position
    {
        get => position;
        set
        {
            ObjectDisposedException.ThrowIf(handle == 0, this);
            long target = Math.Clamp(value, 0, length) / WaveFormat.BlockAlign * WaveFormat.BlockAlign;
            // BASS does not seek to the very end. Represent EOF here, and seek normally when
            // a later request moves back into the stream.
            if (target < length && !Bass.ChannelSetPosition(handle, target)) throw BassRuntime.Failure();
            position = target;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(handle == 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException(nameof(count));
        count = (int)Math.Min(count, length - position);
        if (count == 0) return 0;
        int read;
        fixed (byte* start = buffer) read = Bass.ChannelGetData(handle, (IntPtr)(start + offset), count);
        if (read < 0)
        {
            if (Bass.LastError == Errors.Ended) return 0;
            throw BassRuntime.Failure();
        }
        position += read;
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (handle != 0) Bass.StreamFree(handle);
        handle = 0;
        if (resourceMemory.IsAllocated) resourceMemory.Free();
        base.Dispose(disposing);
    }
}
