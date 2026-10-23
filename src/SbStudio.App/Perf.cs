using System.Diagnostics;

namespace SbStudio.App;

/// <summary>Per-frame timings of named sections, for the automated checks.</summary>
public static class Perf
{
    private static readonly Dictionary<string, double> Last = new();

    public readonly struct Scope : IDisposable
    {
        private readonly string name;
        private readonly long start;

        public Scope(string name)
        {
            this.name = name;
            start = Stopwatch.GetTimestamp();
        }

        public void Dispose() => Last[name] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public static Scope Measure(string name) => new(name);

    public static double Read(string name) => Last.GetValueOrDefault(name);

    public static string Report() => string.Join(" ", Last.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value:0.00}"));
}
