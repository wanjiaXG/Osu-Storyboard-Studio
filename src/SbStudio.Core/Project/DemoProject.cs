using System.Globalization;
using System.Text;

namespace SbStudio.Core;

/// <summary>Fills a project with generated images, a click track and a storyboard that exercises every command.</summary>
public static class DemoProject
{
    private const int Bpm = 120;
    private const int Beat = 60000 / Bpm;
    private const int LengthMs = 24000;

    public static void Populate(Project project)
    {
        string dir = project.AssetsDir;

        WriteBackground(Path.Combine(dir, "bg.png"));
        WriteShape(Path.Combine(dir, "sb", "glow.png"), 128, (x, y) => Math.Pow(Math.Clamp(1 - Math.Sqrt(x * x + y * y), 0, 1), 2));
        WriteShape(Path.Combine(dir, "sb", "dot.png"), 32, (x, y) => x * x + y * y <= 0.85 * 0.85 ? 1 : 0);
        WriteShape(Path.Combine(dir, "sb", "ring.png"), 256, (x, y) =>
        {
            double r = Math.Sqrt(x * x + y * y);
            return r is >= 0.86 and <= 0.96 ? 1 : 0;
        });
        WriteShape(Path.Combine(dir, "sb", "star.png"), 96, (x, y) => InStar(x, y) ? 1 : 0);
        WriteShape(Path.Combine(dir, "sb", "pixel.png"), 8, (_, _) => 1);
        for (int i = 0; i < 8; i++)
        {
            double head = i * Math.PI * 2 / 8;
            WriteShape(Path.Combine(dir, "sb", $"spinner{i}.png"), 64, (x, y) =>
            {
                double r = Math.Sqrt(x * x + y * y);
                if (r is < 0.55 or > 0.9) return 0;
                double a = (Math.Atan2(y, x) - head) % (Math.PI * 2);
                if (a < 0) a += Math.PI * 2;
                double tail = a / (Math.PI * 2);
                return tail * tail;
            });
        }

        WriteClickTrack(Path.Combine(dir, "audio.wav"));
        WriteBlip(Path.Combine(dir, "sfx", "blip.wav"));

        var settings = project.Settings;
        settings.Audio = "audio.wav";
        settings.Background = "bg.png";
        settings.Widescreen = true;
        settings.Timing = new List<TimingPoint> { new() { Offset = 0, Bpm = Bpm, Meter = 4 } };
        if (string.IsNullOrEmpty(settings.Export.FileName)) settings.Export.FileName = "Demo Artist - Demo Song (Studio).osb";

        project.Storyboard = OsbParser.Parse(BuildScript()).Storyboard;
    }

    public static string BuildScript()
    {
        var sb = new StringBuilder();
        int measure = Beat * 4;

        sb.AppendLine("//@name=" + L.T("demo.background"));
        sb.AppendLine("Sprite,Background,Centre,\"bg.png\",320,240");
        sb.AppendLine($" F,0,0,{measure},0,1");
        sb.AppendLine($" F,0,{LengthMs - measure},{LengthMs},1,0");
        sb.AppendLine($" S,0,0,{LengthMs},1,1.15");

        sb.AppendLine("//@name=" + L.T("demo.beatGlow"));
        sb.AppendLine("Sprite,Background,Centre,\"sb/glow.png\",320,240");
        sb.AppendLine(" P,0,0,,A");
        sb.AppendLine($" F,0,0,{measure},0,0.55");
        sb.AppendLine($" F,0,{LengthMs - measure},{LengthMs},0.55,0");
        sb.AppendLine($" C,0,0,{LengthMs / 2},255,120,80,80,160,255,255,200,120");
        sb.AppendLine($" L,0,{LengthMs / Beat}");
        sb.AppendLine($"  S,1,0,{Beat},4.6,3.6");

        sb.AppendLine("//@name=" + L.T("demo.pulseRing"));
        sb.AppendLine("Sprite,Foreground,Centre,\"sb/ring.png\",320,240");
        sb.AppendLine($" L,{measure},{LengthMs / measure - 2}");
        sb.AppendLine($"  S,19,0,{measure},0.1,2.2");
        sb.AppendLine($"  F,0,0,{measure},0.8,0");

        // One dot per easing, all doing the same move.
        int[] easings = { 0, 4, 7, 16, 19, 25, 30, 33 };
        for (int i = 0; i < easings.Length; i++)
        {
            int y = 100 + i * 40;
            var (r, g, b) = Hue(i / (double)easings.Length);
            sb.AppendLine("//@name=" + L.T("demo.easing", SbInfo.EasingNames[easings[i]]));
            sb.AppendLine($"Sprite,Foreground,Centre,\"sb/dot.png\",120,{y}");
            sb.AppendLine($" C,0,{measure * 2},,{r},{g},{b}");
            sb.AppendLine($" L,{measure * 2},2");
            sb.AppendLine("  F,0,0,200,0,1");
            sb.AppendLine($"  MX,{easings[i]},0,1600,120,520");
            sb.AppendLine("  F,0,1600,2000,1,0");
        }

        // Stars on a circle: SineInOut on X and a quarter-phase Sine pair on Y trace exact circular motion.
        const int stars = 6;
        const int period = 4000;
        int orbitStart = measure * 4, orbitEnd = measure * 8;
        for (int i = 0; i < stars; i++)
        {
            int phase = orbitStart - period + i * period / stars;
            var (r, g, b) = Hue(0.1 + i / (double)stars);
            sb.AppendLine("//@name=" + L.T("demo.star", i + 1));
            sb.AppendLine("Sprite,Foreground,Centre,\"sb/star.png\",320,240");
            sb.AppendLine($" F,0,{orbitStart},{orbitStart + Beat * 2},0,1");
            sb.AppendLine($" F,0,{orbitEnd - Beat * 2},{orbitEnd},1,0");
            sb.AppendLine($" S,0,{orbitStart},{orbitEnd},0.45");
            sb.AppendLine($" R,0,{orbitStart},{orbitEnd},0,{Num(Math.PI * 4)}");
            sb.AppendLine($" C,0,{orbitStart},,{r},{g},{b}");
            sb.AppendLine($" L,{phase},4");
            sb.AppendLine($"  MX,17,0,{period / 2},170,470,170");
            sb.AppendLine($"  MY,16,0,{period / 4},240,390");
            sb.AppendLine($"  MY,15,{period / 4},{period / 2},390,240");
            sb.AppendLine($"  MY,16,{period / 2},{period * 3 / 4},240,90");
            sb.AppendLine($"  MY,15,{period * 3 / 4},{period},90,240");
        }

        sb.AppendLine("//@name=" + L.T("demo.spinner"));
        sb.AppendLine("Animation,Foreground,Centre,\"sb/spinner.png\",320,240,8,60,LoopForever");
        sb.AppendLine($" MY,33,{measure * 8},{measure * 8 + 1200},-60,240");
        sb.AppendLine($" S,30,{measure * 8},{measure * 8 + 800},0,2");
        sb.AppendLine($" F,0,{measure * 8},{measure * 11},1");
        sb.AppendLine($" F,0,{measure * 11},{measure * 11 + Beat * 2},1,0");

        // Rising additive particles from a fixed seed.
        var random = new Random(7);
        for (int i = 0; i < 48; i++)
        {
            int start = measure * 8 + random.Next(0, measure * 3);
            int duration = 1500 + random.Next(0, 2000);
            int x = -80 + random.Next(0, 800);
            int drift = random.Next(-60, 60);
            double scale = 0.15 + random.NextDouble() * 0.45;
            var (r, g, b) = Hue(0.5 + random.NextDouble() * 0.2);
            sb.AppendLine("Sprite,Foreground,Centre,\"sb/glow.png\",320,240");
            sb.AppendLine($" P,0,{start},,A");
            sb.AppendLine($" M,1,{start},{start + duration},{x},500,{x + drift},{random.Next(-40, 200)}");
            sb.AppendLine($" F,0,{start},{start + duration / 4},0,0.9");
            sb.AppendLine($" F,0,{start + duration / 4},{start + duration},0.9,0");
            sb.AppendLine($" S,0,{start},,{Num(scale)}");
            sb.AppendLine($" C,0,{start},,{r},{g},{b}");
        }

        sb.AppendLine("//@name=" + L.T("demo.flash"));
        sb.AppendLine("Sprite,Foreground,Centre,\"sb/pixel.png\",320,240");
        sb.AppendLine($" V,0,{measure * 8},,110,62");
        sb.AppendLine($" F,1,{measure * 8},{measure * 8 + 900},0.9,0");

        sb.AppendLine("//@name=" + L.T("demo.barTop"));
        sb.AppendLine("Sprite,Overlay,TopCentre,\"sb/pixel.png\",320,0");
        sb.AppendLine(" C,0,0,,0,0,0");
        sb.AppendLine($" V,7,0,{Beat * 3},110,0,110,6");
        sb.AppendLine($" V,6,{LengthMs - Beat * 3},{LengthMs},110,6,110,0");
        sb.AppendLine("//@name=" + L.T("demo.barBottom"));
        sb.AppendLine("Sprite,Overlay,BottomCentre,\"sb/pixel.png\",320,480");
        sb.AppendLine(" C,0,0,,0,0,0");
        sb.AppendLine($" V,7,0,{Beat * 3},110,0,110,6");
        sb.AppendLine($" V,6,{LengthMs - Beat * 3},{LengthMs},110,6,110,0");

        sb.AppendLine("//@name=" + L.T("demo.clapFlash"));
        sb.AppendLine("Sprite,Foreground,Centre,\"sb/glow.png\",320,240");
        sb.AppendLine($" T,HitSoundClap,0,{LengthMs}");
        sb.AppendLine("  F,0,0,300,1,0");
        sb.AppendLine("  S,0,0,300,2,3");

        sb.AppendLine($"Sample,{measure * 8},0,\"sfx/blip.wav\",70");
        return sb.ToString();
    }

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static (int R, int G, int B) Hue(double h)
    {
        h = (h % 1 + 1) % 1 * 6;
        double x = 1 - Math.Abs(h % 2 - 1);
        var (r, g, b) = (int)h switch
        {
            0 => (1.0, x, 0.0),
            1 => (x, 1.0, 0.0),
            2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0),
            4 => (x, 0.0, 1.0),
            _ => (1.0, 0.0, x),
        };
        // Pastel: mix with white.
        return ((int)(120 + r * 135), (int)(120 + g * 135), (int)(120 + b * 135));
    }

    private static bool InStar(double x, double y)
    {
        const double outer = 0.95, inner = 0.4;
        double r = Math.Sqrt(x * x + y * y);
        double sector = Math.PI * 2 / 5;
        double a = (Math.Atan2(y, x) + Math.PI / 2) % sector;
        if (a < 0) a += sector;
        a = Math.Min(a, sector - a);
        double px = r * Math.Cos(a), py = r * Math.Sin(a);
        double bx = inner * Math.Cos(sector / 2) - outer, by = inner * Math.Sin(sector / 2);
        // Same side of the tip-to-notch edge as the centre.
        double side = bx * py - by * (px - outer);
        double centre = bx * 0 - by * (0 - outer);
        return side * centre >= 0;
    }

    /// <summary>White shape with antialiased alpha; the callback gets coordinates in -1..1.</summary>
    private static void WriteShape(string path, int size, Func<double, double, double> coverage)
    {
        const int ss = 3;
        var pixels = new byte[size * size * 4];
        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                double sum = 0;
                for (int sy = 0; sy < ss; sy++)
                {
                    for (int sx = 0; sx < ss; sx++)
                    {
                        double x = (px + (sx + 0.5) / ss) / size * 2 - 1;
                        double y = (py + (sy + 0.5) / ss) / size * 2 - 1;
                        sum += coverage(x, y);
                    }
                }
                int o = (py * size + px) * 4;
                pixels[o] = pixels[o + 1] = pixels[o + 2] = 255;
                pixels[o + 3] = (byte)Math.Round(Math.Clamp(sum / (ss * ss), 0, 1) * 255);
            }
        }
        PngWriter.Write(path, size, size, pixels);
    }

    private static void WriteBackground(string path)
    {
        const int w = 854, h = 480;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double t = y / (double)(h - 1);
                double dx = (x - w / 2.0) / (w / 2.0), dy = (y - h / 2.0) / (h / 2.0);
                double vignette = 1 - 0.45 * Math.Min(1, dx * dx + dy * dy);
                int o = (y * w + x) * 4;
                pixels[o] = (byte)((18 + 60 * t) * vignette);
                pixels[o + 1] = (byte)((22 + 14 * t) * vignette);
                pixels[o + 2] = (byte)((52 + 50 * t) * vignette);
                pixels[o + 3] = 255;
            }
        }
        PngWriter.Write(path, w, h, pixels);
    }

    private static void WriteClickTrack(string path)
    {
        const int rate = 44100;
        var samples = new float[rate * (LengthMs + 1000) / 1000];
        int beats = LengthMs / Beat;
        for (int beat = 0; beat < beats; beat++)
        {
            int at = (int)((long)beat * Beat * rate / 1000);
            double freq = beat % 4 == 0 ? 1320 : 880;
            for (int i = 0; i < rate / 8 && at + i < samples.Length; i++)
            {
                double t = i / (double)rate;
                samples[at + i] += (float)(Math.Sin(t * freq * Math.PI * 2) * Math.Exp(-t * 45) * 0.6);
            }
        }
        // A quiet moving bass note so the waveform is not only spikes.
        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / (double)rate;
            double freq = ((int)(t / 2) % 4) switch { 0 => 110, 1 => 130.81, 2 => 98, _ => 146.83 };
            double fade = Math.Min(1, Math.Min(t / 2, (LengthMs / 1000.0 - t) / 2));
            samples[i] += (float)(Math.Sin(t * freq * Math.PI * 2) * 0.14 * Math.Max(0, fade));
        }
        WriteWav(path, samples, rate);
    }

    private static void WriteBlip(string path)
    {
        const int rate = 44100;
        var samples = new float[rate / 3];
        double phase = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / (double)samples.Length;
            phase += (500 + 1500 * t) * Math.PI * 2 / rate;
            samples[i] = (float)(Math.Sin(phase) * Math.Exp(-t * 5) * 0.5);
        }
        WriteWav(path, samples, rate);
    }

    private static void WriteWav(string path, float[] samples, int rate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new BinaryWriter(File.Create(path));
        int dataBytes = samples.Length * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);      // PCM
        writer.Write((short)1);      // mono
        writer.Write(rate);
        writer.Write(rate * 2);      // bytes per second
        writer.Write((short)2);      // block align
        writer.Write((short)16);     // bits
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        foreach (float s in samples)
            writer.Write((short)Math.Round(Math.Clamp(s, -1f, 1f) * short.MaxValue));
    }
}
