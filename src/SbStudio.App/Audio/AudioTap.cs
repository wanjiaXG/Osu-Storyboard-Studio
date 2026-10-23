using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SbStudio.App.Audio;

/// <summary>Optional callback capture. Only the editor thread opens and drains files.</summary>
internal static class AudioTap
{
    private static readonly List<BassOutput> outputs = new();
    private static Session? session;
    internal static void Register(BassOutput output)
    {
        outputs.Add(output);
        if (session != null) output.Tap = session.Add(output.StreamId);
    }
    internal static void Unregister(BassOutput output)
    {
        Event(output.StreamId, "Dispose");
        outputs.Remove(output);
    }
    internal static void Start(string? file)
    {
        foreach (var output in outputs) output.Tap = null;
        session?.Dispose();
        session = file == null ? null : new Session(file);
        if (session != null)
            foreach (var output in outputs) output.Tap = session.Add(output.StreamId);
    }
    internal static void Stream(int id, object state) => session?.Stream(id, state);
    internal static void Flush() => session?.Flush();
    internal static void Event(int stream, string name) => session?.Event(stream, name);
    internal static void Command(int frame, string command, string argument) => session?.Command(frame, command, argument);

    internal sealed class Ring(int stream)
    {
        private const int Samples = 1 << 21, Rows = 8192;
        private readonly float[] audio = new float[Samples];
        private readonly float[] preGain = new float[Samples];
        private readonly long[] ticks = new long[Rows], positions = new long[Rows];
        private readonly int[] counts = new int[Rows];
        private long written, consumed, sampleWrite, sampleRead;
        private long dropped;
        internal long Dropped => Interlocked.Read(ref dropped);
        private int preCount, writing;
        private bool closed;
        internal void Close()
        {
            Volatile.Write(ref closed, true);
            SpinWait.SpinUntil(() => Volatile.Read(ref writing) == 0);
        }
        internal void BeforeGain(float[] data, int offset, int count)
        {
            preCount = 0;
            if (count > Samples || sampleWrite - Volatile.Read(ref sampleRead) + count > Samples) return;
            CopyIn(preGain, data, offset, count);
            preCount = count;
        }
        private void CopyIn(float[] target, float[] data, int offset, int count)
        {
            int at = (int)(sampleWrite & (Samples - 1));
            int first = Math.Min(count, Samples - at);
            Array.Copy(data, offset, target, at, first);
            if (first < count) Array.Copy(data, offset + first, target, 0, count - first);
        }
        internal void Record(long qpc, long position, float[] data, int count)
        {
            Interlocked.Increment(ref writing);
            try
            {
            if (Volatile.Read(ref closed)) return;
            long row = written;
            if (row - Volatile.Read(ref consumed) >= Rows || count > Samples || sampleWrite - Volatile.Read(ref sampleRead) + count > Samples)
            {
                Interlocked.Increment(ref dropped);
                preCount = 0;
                return;
            }
            CopyIn(audio, data, 0, count);
            if (preCount != count) CopyIn(preGain, data, 0, count);
            preCount = 0;
            int slot = (int)(row & (Rows - 1));
            ticks[slot] = qpc;
            positions[slot] = position;
            counts[slot] = count;
            sampleWrite += count;
            Volatile.Write(ref written, row + 1);
            }
            finally { Interlocked.Decrement(ref writing); }
        }
        internal void Drain(Session target)
        {
            long to = Volatile.Read(ref written);
            while (consumed < to)
            {
                int slot = (int)(consumed & (Rows - 1)), count = counts[slot];
                long offset = target.Audio.Position / sizeof(float);
                int at = (int)(sampleRead & (Samples - 1)), first = Math.Min(count, Samples - at);
                target.Audio.Write(MemoryMarshal.AsBytes(audio.AsSpan(at, first)));
                target.PreGain.Write(MemoryMarshal.AsBytes(preGain.AsSpan(at, first)));
                if (first < count)
                {
                    target.Audio.Write(MemoryMarshal.AsBytes(audio.AsSpan(0, count - first)));
                    target.PreGain.Write(MemoryMarshal.AsBytes(preGain.AsSpan(0, count - first)));
                }
                target.Csv.WriteLine(FormattableString.Invariant($"{ticks[slot]},{stream},{count / 2},{positions[slot]},{offset}"));
                Volatile.Write(ref sampleRead, sampleRead + count);
                Volatile.Write(ref consumed, consumed + 1);
            }
        }
    }
    internal sealed class Session : IDisposable
    {
        internal readonly FileStream Audio, PreGain;
        internal readonly StreamWriter Csv;
        private readonly StreamWriter events, commands, streams;
        private readonly string file;
        private readonly List<Ring> rings = new();
        internal Session(string file)
        {
            this.file = file;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            Audio = File.Create(file + ".f32");
            PreGain = File.Create(file + ".pregain.f32");
            Csv = new(file + ".csv");
            events = new(file + ".events.csv");
            commands = new(file + ".commands.csv");
            streams = new(file + ".streams.jsonl");
            Csv.WriteLine("qpc,stream,frames,played_bytes,sample_offset");
            events.WriteLine("qpc,stream,event");
            commands.WriteLine("qpc,frame,command,argument");
            File.WriteAllText(file + ".frequency.txt", Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture));
        }
        internal Ring Add(int stream) { var ring = new Ring(stream); rings.Add(ring); return ring; }
        internal void Stream(int id, object state) => streams.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Stream = id, State = state }));
        internal void Event(int stream, string name) => events.WriteLine($"{Stopwatch.GetTimestamp()},{stream},{name}");
        internal void Command(int frame, string command, string argument) =>
            commands.WriteLine($"{Stopwatch.GetTimestamp()},{frame},{command},\"{argument.Replace("\"", "\"\"")}\"");
        internal void Flush()
        {
            foreach (var ring in rings) ring.Drain(this);
            Audio.Flush(); PreGain.Flush(); Csv.Flush(); events.Flush(); commands.Flush(); streams.Flush();
        }
        public void Dispose()
        {
            foreach (var ring in rings) ring.Close();
            Flush();
            File.WriteAllText(file + ".dropped.txt", rings.Sum(r => r.Dropped).ToString(CultureInfo.InvariantCulture));
            Audio.Dispose(); PreGain.Dispose(); Csv.Dispose(); events.Dispose(); commands.Dispose(); streams.Dispose();
        }
    }
}
