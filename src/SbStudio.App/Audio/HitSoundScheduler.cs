using SbStudio.App.Gfx;
using SbStudio.Core;

namespace SbStudio.App.Audio;

internal static class HitSoundScheduler
{
    public static OsuPreviewSkin Skin(SkinLibrary library, string id)
    {
        return library.CreateSkin(id, sounds: true);
    }

    public static IEnumerable<ScheduledSample> Schedule(OsuBeatmap map, OsuPreviewSkin skin) => Build(map, skin).Playback;

    public static HitSoundEventIndex Build(OsuBeatmap map, OsuPreviewSkin skin, string? beatmapFolder = null)
    {
        var result = new List<HitSoundEvent>();
        HitSoundOrigin origin = HitSoundOrigin.Circle;
        HitSample currentSample = new();
        int objectIndex = -1;
        (int Normal, int Addition, float Volume, int Index) Settings(double time, HitSample sample) => HitSampleResolver.Resolve(map, time, sample);
        void Add(double time, string name, float volume, double duration = 0, bool loop = false, int index = 1)
        {
            string? file = skin.SampleSound(name, index);
            var kind = name.Split('-').Last() switch
            {
                "hitwhistle" => HitSoundKind.Whistle, "hitfinish" => HitSoundKind.Finish, "hitclap" => HitSoundKind.Clap,
                "slidertick" => HitSoundKind.SliderTick, "sliderslide" => HitSoundKind.SliderSlide, "sliderwhistle" => HitSoundKind.SliderWhistle,
                "spinnerspin" => HitSoundKind.SpinnerSpin, "metronomelow" => HitSoundKind.Banana, _ => HitSoundKind.Normal
            };
            int bank = name.Contains("soft-") ? 2 : name.Contains("drum-") ? 3 : 1;
            bool explicitBank = kind is HitSoundKind.Whistle or HitSoundKind.Finish or HitSoundKind.Clap or HitSoundKind.SliderWhistle
                ? currentSample.AdditionSet != 0 || currentSample.NormalSet != 0 : currentSample.NormalSet != 0;
            result.Add(new(time, duration, kind, bank, index, volume, explicitBank, name, file, skin.SourceOf(file), origin, objectIndex, loop));
        }
        string Bank(int bank) => (map.Mode == 1 ? "taiko-" : "") + (bank switch { 2 => "soft", 3 => "drum", _ => "normal" });
        void Hit(double time, int sound, HitSample sample)
        {
            currentSample = sample;
            var settings = Settings(time, sample);
            if (sample.Filename.Length > 0)
            {
                // ConvertHitObjectParser: an overriding filename replaces all bank/flag samples.
                string? file = null;
                if (!string.IsNullOrEmpty(beatmapFolder) && !Path.IsPathRooted(sample.Filename) && !sample.Filename.Contains(':'))
                {
                    string root = Path.GetFullPath(beatmapFolder);
                    string path = Path.GetFullPath(Path.Combine(root, sample.Filename));
                    if (path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(path)) file = path;
                }
                bool fromBeatmap = file != null;
                file ??= skin.Sound(sample.Filename);
                result.Add(new(time, 0, HitSoundKind.Custom, settings.Normal, settings.Index, settings.Volume, sample.NormalSet != 0,
                    sample.Filename, file, fromBeatmap ? HitSoundSource.Beatmap : skin.SourceOf(file), origin, objectIndex, false));
                return;
            }
            bool beatmapProvided = skin.BeatmapSample(Bank(settings.Normal) + "-hitnormal", settings.Index) != null
                || (sound & 2) != 0 && skin.BeatmapSample(Bank(settings.Addition) + "-hitwhistle", settings.Index) != null
                || (sound & 4) != 0 && skin.BeatmapSample(Bank(settings.Addition) + "-hitfinish", settings.Index) != null
                || (sound & 8) != 0 && skin.BeatmapSample(Bank(settings.Addition) + "-hitclap", settings.Index) != null;
            if (map.Mode == 1 && skin.ArgonSounds && sample.Filename.Length == 0 && !beatmapProvided)
            {
                // VolumeAwareHitSampleInfo: Argon taiko selects softness from sample volume,
                // with a separate bank for strong notes. Whistle/clap both signify rim hits.
                string bank = (sound & 4) != 0 ? "strong" : settings.Volume >= 0.9f ? "drum" : settings.Volume >= 0.6f ? "normal" : "soft";
                Add(time, "taiko-" + bank + ((sound & 10) != 0 ? "-hitclap" : "-hitnormal"), settings.Volume);
                // Recheck describes the .osu flags, not Argon's combined sample name/bank.
                // Keep one scheduled drum sound, with auditionable annotations for every addition.
                var combined = result[^1];
                result[^1] = combined with { Kind = HitSoundKind.Normal, Bank = settings.Normal,
                    CustomIndex = settings.Index, ExplicitBank = sample.NormalSet != 0 };
                foreach (var addition in new[] { (Flag: 2, Kind: HitSoundKind.Whistle), (Flag: 4, Kind: HitSoundKind.Finish), (Flag: 8, Kind: HitSoundKind.Clap) })
                    if ((sound & addition.Flag) != 0)
                        result.Add(combined with { Kind = addition.Kind, Bank = settings.Addition, CustomIndex = settings.Index,
                            ExplicitBank = sample.AdditionSet != 0 || sample.NormalSet != 0, TimelineOnly = true });
                return;
            }
            if (map.Mode != 3 && skin.LayeredHitSounds || (sound & 14) == 0 || (sound & 1) != 0)
                Add(time, Bank(settings.Normal) + "-hitnormal", settings.Volume, index: settings.Index);
            if ((sound & 2) != 0) Add(time, Bank(settings.Addition) + "-hitwhistle", settings.Volume, index: settings.Index);
            if ((sound & 4) != 0) Add(time, Bank(settings.Addition) + "-hitfinish", settings.Volume, index: settings.Index);
            if ((sound & 8) != 0) Add(time, Bank(settings.Addition) + "-hitclap", settings.Volume, index: settings.Index);
        }
        if (map.Mode == 2)
        {
            foreach (var obj in CatchPreviewObjects.Build(map))
            {
                objectIndex = obj.Index;
                currentSample = obj.Sample;
                origin = obj.Kind switch { CatchObjectKind.Droplet => HitSoundOrigin.CatchDroplet, CatchObjectKind.Banana => HitSoundOrigin.CatchBanana, _ => HitSoundOrigin.CatchFruit };
                if (obj.Kind == CatchObjectKind.TinyDroplet) continue;
                if (obj.Kind == CatchObjectKind.Droplet)
                {
                    var settings = Settings(obj.Time, obj.Sample);
                    Add(obj.Time, Bank(settings.Normal) + "-slidertick", settings.Volume, index: settings.Index);
                }
                else if (obj.Kind == CatchObjectKind.Banana && skin.Sound("metronomelow") != null)
                    Add(obj.Time, "metronomelow", Settings(obj.Time, obj.Sample).Volume);
                else Hit(obj.Time, obj.HitSound, obj.Sample);
            }
            return new(result);
        }
        foreach (var obj in map.Objects)
        {
            objectIndex++;
            currentSample = obj.Sample;
            origin = obj is HoldNote ? HitSoundOrigin.HoldHead : map.Mode == 3 ? HitSoundOrigin.ManiaNote : HitSoundOrigin.Circle;
            if (map.Mode == 1)
            {
                if (obj is Slider roll)
                {
                    origin = HitSoundOrigin.TaikoRoll;
                    double spacing = map.ScrollAt(roll.StartTime).BeatLength / (map.SliderTickRate == 3 ? 3 : 4);
                    for (double t = roll.StartTime; t < roll.EndTime + spacing / 2; t += spacing) Hit(t, roll.HitSound, roll.Sample);
                }
                else if (obj is Spinner swell)
                {
                    origin = HitSoundOrigin.TaikoSwell;
                    for (double t = swell.StartTime; t <= swell.EndTime; t += 100) Hit(t, 0, swell.Sample);
                }
                else Hit(obj.StartTime, obj.HitSound, obj.Sample);
                continue;
            }
            if (obj is Slider slider)
            {
                for (int edge = 0; edge <= slider.SpanCount; edge++)
                {
                    origin = edge == 0 ? HitSoundOrigin.SliderHead : edge == slider.SpanCount ? HitSoundOrigin.SliderTail : HitSoundOrigin.SliderRepeat;
                    Hit(slider.StartTime + edge * slider.SpanDuration,
                        edge < slider.EdgeSounds.Count ? slider.EdgeSounds[edge] : slider.HitSound,
                        edge < slider.EdgeSamples.Count ? slider.EdgeSamples[edge] : slider.Sample);
                }
                origin = HitSoundOrigin.SliderTick;
                currentSample = slider.Sample;
                foreach (var tick in slider.Ticks)
                {
                    var settings = Settings(tick.Time, slider.Sample);
                    Add(tick.Time, Bank(settings.Normal) + "-slidertick", settings.Volume, index: settings.Index);
                }
                // Split sustained samples at control points so slider volume/bank changes are audible.
                var times = new[] { slider.StartTime }.Concat(map.SamplePoints.Select(p => p.Time - 5)
                    .Where(t => t > slider.StartTime && t < slider.EndTime)).Append(slider.EndTime).Distinct().Order().ToArray();
                for (int i = 0; i < times.Length - 1; i++)
                {
                    origin = HitSoundOrigin.SliderBody;
                    var settings = Settings(times[i], slider.Sample);
                    Add(times[i], Bank(settings.Normal) + "-sliderslide", settings.Volume, times[i + 1] - times[i], true, settings.Index);
                    if ((slider.HitSound & 2) != 0)
                        Add(times[i], Bank(settings.Addition) + "-sliderwhistle", settings.Volume, times[i + 1] - times[i], true, settings.Index);
                }
            }
            else if (obj is Spinner spinner)
            {
                origin = HitSoundOrigin.SpinnerBody;
                var settings = Settings(spinner.StartTime, spinner.Sample);
                if (spinner.Duration > 0) Add(spinner.StartTime, "spinnerspin", settings.Volume, spinner.Duration, true);
                origin = HitSoundOrigin.SpinnerTail;
                Hit(spinner.EndTime, spinner.HitSound, spinner.Sample);
            }
            else Hit(obj.StartTime, obj.HitSound, obj.Sample);
        }
        return new(result);
    }
}
