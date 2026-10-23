using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Fx;
using NAudio.Wave;
using WaveFormat = NAudio.Wave.WaveFormat;

namespace SbStudio.App.Audio;

/// <summary>Pitch-preserving tempo adjustment of the mixed track, using osu!framework's BASS_FX settings.</summary>
internal sealed class BassTempo : ISampleProvider, IDisposable
{
    private readonly ISampleProvider source;
    private readonly StreamProcedure procedure;
    private float[] input = new float[16384], output = new float[16384];
    private int handle;
    public Exception? Error { get; private set; }
    public WaveFormat WaveFormat => source.WaveFormat;

    public BassTempo(ISampleProvider source, double rate, bool timingPrecision = false)
    {
        this.source = source;
        procedure = Fill;
        BassRuntime.PrepareDecoding();
        int decoding = Bass.CreateStream(WaveFormat.SampleRate, WaveFormat.Channels, BassFlags.Float | BassFlags.Decode, procedure);
        if (decoding == 0) throw BassRuntime.Failure();
        try
        {
            handle = BassFx.TempoCreate(decoding, BassFlags.Decode | BassFlags.FxFreeSource);
            if (handle == 0) throw BassRuntime.Failure();
            Set(ChannelAttribute.TempoUseQuickAlgorithm, 1);
            Set(ChannelAttribute.TempoOverlapMilliseconds, timingPrecision ? 2 : 4);
            Set(ChannelAttribute.TempoSequenceMilliseconds, timingPrecision ? 16 : 30);
            Set(ChannelAttribute.Tempo, (rate - 1) * 100);
        }
        catch
        {
            if (handle != 0) Bass.StreamFree(handle);
            else Bass.StreamFree(decoding);
            handle = 0;
            throw;
        }
    }

    private void Set(ChannelAttribute attribute, double value)
    {
        if (!Bass.ChannelSetAttribute(handle, attribute, value)) throw BassRuntime.Failure();
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (output.Length < count) output = new float[count];
        int bytes = Bass.ChannelGetData(handle, output, count * sizeof(float));
        if (Error != null) throw new InvalidOperationException("Audio source failed", Error);
        if (bytes < 0)
        {
            if (Bass.LastError == Errors.Ended) return 0;
            throw BassRuntime.Failure();
        }
        int read = bytes / sizeof(float);
        // Sample providers may hand over a byte buffer viewed as floats. Copy values individually,
        // rather than relying on the managed array's physical element type.
        for (int i = 0; i < read; i++) buffer[offset + i] = output[i];
        return read;
    }

    private int Fill(int stream, IntPtr destination, int bytes, IntPtr user)
    {
        try
        {
            int count = bytes / sizeof(float);
            if (input.Length < count) input = new float[count];
            int read = source.Read(input, 0, count);
            Marshal.Copy(input, 0, destination, read);
            return read == 0 ? (int)StreamProcedureType.End : read * sizeof(float);
        }
        catch (Exception ex)
        {
            Error = ex;
            return (int)StreamProcedureType.End;
        }
    }

    public void Dispose()
    {
        if (handle != 0)
        {
            Bass.StreamFree(handle); // FxFreeSource also releases the callback stream.
            handle = 0;
        }
        GC.KeepAlive(procedure);
    }
}
