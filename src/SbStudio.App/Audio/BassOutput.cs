using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Fx;
using NAudio.Wave;

namespace SbStudio.App.Audio;

/// <summary>BASS playback and its audible-position clock for the mixed storyboard track.</summary>
internal sealed class BassOutput : IDisposable
{
    private static int nextStream;
    private static readonly object bufferedGate = new();
    private static int bufferedOutputs, previousUpdatePeriod;
    private bool usesBufferedUpdates;
    internal readonly int StreamId = Interlocked.Increment(ref nextStream);
    internal AudioTap.Ring? Tap;
    private readonly ISampleProvider source;
    private readonly BassTempo? tempo;
    private readonly StreamProcedure procedure;
    private float[] buffer = new float[16384];
    private int handle;
    internal bool NativeTempo { get; }
    internal int Device => Bass.ChannelGetDevice(handle);
    internal double TrackPositionMilliseconds => Bass.ChannelBytes2Seconds(handle, Position) * 1000;
    private long peakCallbackTicks;
    public Exception? Error { get; private set; }
    internal double? BufferMilliseconds => Bass.ChannelGetAttribute(handle, ChannelAttribute.Buffer, out float seconds) ? seconds * 1000 : null;
    internal double PeakCallbackMilliseconds => Interlocked.Read(ref peakCallbackTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    internal double? DeviceLatencyMilliseconds { get; private set; }
    public bool IsActive => Bass.ChannelIsActive(handle) is ManagedBass.PlaybackState.Playing or ManagedBass.PlaybackState.Stalled;

    public BassOutput(ISampleProvider source, double rate = 1, int device = -1, bool nativeTempo = false)
    {
        this.source = source;
        NativeTempo = nativeTempo;
        procedure = Fill;
        try
        {
            if (!nativeTempo && Math.Abs(rate - 1) > 1e-6) this.source = tempo = new BassTempo(source, rate);
            BassRuntime.PreparePlayback(device);
            if (nativeTempo)
            {
                lock (bufferedGate)
                {
                    if (bufferedOutputs == 0)
                    {
                        previousUpdatePeriod = Bass.UpdatePeriod;
                        if (!Bass.Configure(Configuration.UpdatePeriod, 10)) throw BassRuntime.Failure();
                    }
                    bufferedOutputs++;
                    usesBufferedUpdates = true;
                }
            }
            // Ordinary playback counts mixed output samples; the trial method plays Tempo directly
            // and deliberately uses that stream's native source-position reporting instead.
            handle = Bass.CreateStream(source.WaveFormat.SampleRate, source.WaveFormat.Channels,
                BassFlags.Float | (nativeTempo ? BassFlags.Decode : 0), procedure);
            if (handle == 0) throw BassRuntime.Failure();
            if (nativeTempo)
            {
                // A playing Tempo stream reports its own source position.
                // Keep this isolated from the ordinary mixed-output clock and marker processing.
                int stretched = BassFx.TempoCreate(handle, BassFlags.Float | BassFlags.FxFreeSource);
                if (stretched == 0) throw BassRuntime.Failure();
                handle = stretched;
                if (!Bass.ChannelSetAttribute(handle, ChannelAttribute.TempoUseQuickAlgorithm, 1)
                    || !Bass.ChannelSetAttribute(handle, ChannelAttribute.TempoSequenceMilliseconds, 30)
                    || !Bass.ChannelSetAttribute(handle, ChannelAttribute.TempoOverlapMilliseconds, 4)
                    || !Bass.ChannelSetAttribute(handle, ChannelAttribute.Tempo, (float)((rate - 1) * 100))) throw BassRuntime.Failure();
            }
            // Keep the existing unbuffered shared mix, or a 100 ms music buffer for the trial method.
            if (!Bass.ChannelSetAttribute(handle, ChannelAttribute.Buffer, nativeTempo ? 0.1f : 0)) throw BassRuntime.Failure();
            if (Bass.GetInfo(out BassInfo info) && info.Latency > 0) DeviceLatencyMilliseconds = info.Latency;
            AudioTap.Register(this);
            if (source is TempoMix mix) mix.BeforeGain = CaptureBeforeGain;
        }
        catch
        {
            if (handle != 0) Bass.StreamFree(handle);
            tempo?.Dispose();
            ReleaseBufferedUpdates();
            throw;
        }
    }

    public void Play()
    {
        if (!Bass.ChannelPlay(handle)) throw BassRuntime.Failure();
        AudioTap.Event(StreamId, "ChannelPlay");
    }

    public void SwitchDevice(int device)
    {
        if (Bass.ChannelGetDevice(handle) == device) return;
        int target = BassRuntime.PreparePlayback(device);
        if (!Bass.ChannelSetDevice(handle, target)) throw BassRuntime.Failure();
        DeviceLatencyMilliseconds = Bass.GetInfo(out BassInfo info) && info.Latency > 0 ? info.Latency : null;
        if (!Bass.ChannelPlay(handle)) throw BassRuntime.Failure();
    }

    public long Position
    {
        get
        {
            long position = Bass.ChannelGetPosition(handle);
            if (position < 0)
            {
                Error ??= BassRuntime.Failure();
                return 0;
            }
            return position;
        }
    }

    private void CaptureBeforeGain(float[] data, int offset, int count) => Volatile.Read(ref Tap)?.BeforeGain(data, offset, count);

    private int Fill(int stream, IntPtr destination, int bytes, IntPtr user)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            int count = bytes / sizeof(float);
            if (buffer.Length < count) buffer = new float[count];
            int read = source.Read(buffer, 0, count);
            Marshal.Copy(buffer, 0, destination, read);
            Volatile.Read(ref Tap)?.Record(started, Bass.ChannelGetPosition(stream), buffer, read);
            return read * sizeof(float);
        }
        catch (Exception ex)
        {
            // An exception must never escape a native callback. Report it on the editor thread.
            Error = ex;
            return (int)StreamProcedureType.End;
        }
        finally
        {
            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            long previous;
            do { previous = Interlocked.Read(ref peakCallbackTicks); if (elapsed <= previous) break; }
            while (Interlocked.CompareExchange(ref peakCallbackTicks, elapsed, previous) != previous);
        }
    }

    public void Dispose()
    {
        AudioTap.Unregister(this);
        if (handle != 0)
        {
            Bass.ChannelStop(handle);
            Bass.StreamFree(handle);
            handle = 0;
        }
        tempo?.Dispose();
        ReleaseBufferedUpdates();
        GC.KeepAlive(procedure);
    }

    private void ReleaseBufferedUpdates()
    {
        lock (bufferedGate)
        {
            if (!usesBufferedUpdates) return;
            usesBufferedUpdates = false;
            if (--bufferedOutputs == 0) Bass.UpdatePeriod = previousUpdatePeriod;
        }
    }
}
