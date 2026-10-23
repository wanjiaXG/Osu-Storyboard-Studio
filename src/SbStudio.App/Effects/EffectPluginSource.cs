using System.Text;
using SbStudio.Core;
using SbStudio.Effects;
using AssetKind = SbStudio.Effects.AssetKind;

namespace SbStudio.App.Effects;

internal sealed class EffectPluginSource(EditorApp app) : IPluginSource
{
    public bool Supported => true;
    internal static string Root(Project project, PluginLink link) => Path.GetFullPath(Path.Combine(project.Folder, link.Effect?.SourceLocation ?? link.File));
    public bool Available(Project project, PluginLink link) => link.Effect?.Supported == true && Directory.Exists(Root(project, link));
    public string? MainFile(Project project, PluginLink link) => Path.Combine(Root(project, link), "effect.json");
    public string Description => L.T("effects.title");
    public IEnumerable<string> WatchFolders(Project project, PluginLink link)
    {
        if (link.Effect?.SourceKind == "development" && Directory.Exists(Root(project, link))) yield return Root(project, link);
        if (Directory.Exists(project.AssetsDir)) yield return project.AssetsDir;
    }
    public string? ComputeKey(Project project, PluginLink link, CancellationToken token) => link.Key;
    public PluginImportResult Generate(Project project, PluginLink link, CancellationToken token) => Prepare(project, link)(token);
    internal Func<CancellationToken, PluginImportResult> Prepare(Project project, PluginLink link,
        EffectRequestIdentity? identity = null, Action<HostResponse>? progress = null)
    {
        var binding = link.Effect?.Clone(); string source = Root(project, link);
        if (binding?.Supported != true) return _ => new(null, L.T("effects.incompatible"));
        if (!Directory.Exists(source)) return _ => new(null, L.T("effects.missing"), Missing: true);
        PackageManifest manifest;
        try { manifest = binding.SourceKind == "development" ? PackageFiles.ReadManifest(source) : PackageFiles.ValidateDirectory(source); }
        catch (Exception ex) { return _ => new(null, ex.Message); }
        if (!app.EffectsCatalog.Trusted(source, manifest, binding.SourceKind == "development")) return _ => new(null, L.T("effects.untrusted"));
        if (binding.SourceKind != "development" && (manifest.ContentHash != binding.PackageHash || manifest.PackageId != binding.PackageId || manifest.PackageVersion != binding.PackageVersion)) return _ => new(null, L.T("effects.incompatible"));
        Dictionary<string, System.Text.Json.JsonElement> parameters;
        BeatmapInput input;
        try { parameters = EffectBinding.Parameters(link); input = Snapshot(project, binding); } catch (Exception ex) { return _ => new(null, ex.Message); }
        var request = new HostRequest { SourceDirectory = source, Development = binding.SourceKind == "development", EffectId = binding.EffectId,
            Identity = identity ?? new(), ProjectAssets = project.AssetsDir, Parameters = parameters, Duration = binding.DurationMs ?? 1000, PlacementTime = link.Offset, Beatmap = input };
        var hosts = app.EffectHosts;
        return token =>
        {
            token.ThrowIfCancellationRequested();
            using var active = EffectResultPins.Pin(project, ["*"]);
            using var work = new EffectWorkDirectory(Path.Combine(project.Folder, "effects", "work"));
            var response = hosts.RunAsync(request with { WorkDirectory = work.Path, BuildDirectory = Path.Combine(project.Folder, "effects", "build") }, token, progress).GetAwaiter().GetResult(); token.ThrowIfCancellationRequested();
            if (!response.Success) return new(null, response.Error, Diagnostics: response.Diagnostics, RequestIdentity: response.Identity);
            var definition = response.Definitions.Single(d => d.EffectId == binding.EffectId);
            binding.Definition = definition; binding.SchemaVersion = definition.SchemaVersion; binding.CodeRevision = response.CodeRevision ?? "";
            var sourceFiles = Directory.Exists(source) ? PackageFiles.EnumerateFiles(source, development: binding.SourceKind == "development").ToList() : [];
            foreach (string key in response.ReadAssets.Keys)
            { var asset = EffectJson.Deserialize<AssetReference>(key); if (asset.Kind == AssetKind.Project) sourceFiles.Add(PackageFiles.SafePath(project.AssetsDir, asset.Path)); }
            var output = EffectOutputStore.Materialize(project, response, work.Path, sourceFiles, token);
            binding.DesiredInputKey = PackageFiles.Hash(Encoding.UTF8.GetBytes(EffectJson.Serialize(new { binding.CodeRevision, definition, parameters, input,
                binding.DurationMs, Offset = definition.Inputs.HasFlag(EffectInputs.AbsoluteTime) ? link.Offset : 0, response.ReadAssets })));
            binding.LastGoodInputKey = binding.DesiredInputKey; binding.LastGoodOutputKey = output.Key;
            binding.LastGoodCodeRevision = binding.CodeRevision; binding.LastGoodParameters = parameters;
            return new(output, Effect: binding, Diagnostics: response.Diagnostics, RequestIdentity: response.Identity,
                OutputLease: EffectResultPins.Pin(project, [output.Key], token));
        };
    }
    internal static string InputSignature(Project project, EffectBinding binding)
    {
        if (!binding.Supported) return "";
        var flags = binding.Definition?.Inputs ?? EffectInputs.None;
        if ((flags & (EffectInputs.Timing | EffectInputs.HitObjects | EffectInputs.Metadata)) == 0) return "";
        var difficulty = project.Difficulties.FirstOrDefault(d => d.File == binding.InputDifficulty);
        var timing = InputTiming(project, binding, difficulty);
        string path = difficulty == null ? "" : project.DifficultyFile(difficulty.File);
        var file = new FileInfo(path.Length == 0 ? Path.Combine(project.Folder, ".no-input") : path);
        return EffectJson.Serialize(new { Timing = flags.HasFlag(EffectInputs.Timing) ? timing : null, Exists = file.Exists, Stamp = file.Exists ? file.LastWriteTimeUtc.Ticks : 0, Size = file.Exists ? file.Length : 0 });
    }
    private static IReadOnlyList<TimingPoint> InputTiming(Project project, EffectBinding binding, DifficultySettings? difficulty) =>
        binding.InputDifficulty == project.Settings.Difficulty ? project.Settings.Timing :
        binding.InputDifficulty.Length == 0 ? project.Settings.Shared?.Timing ?? [] : difficulty?.Timing ?? [];
    internal static BeatmapInput Snapshot(Project project, EffectBinding binding)
    {
        var definition = binding.Definition;
        var difficulty = project.Difficulties.FirstOrDefault(d => d.File == binding.InputDifficulty);
        if (binding.InputDifficulty.Length > 0 && difficulty == null) throw new IOException(L.T("effects.inputMissing"));
        var timing = InputTiming(project, binding, difficulty);
        var points = definition?.Inputs.HasFlag(EffectInputs.Timing) == true ? timing.Select(t => new TimingInput(t.Offset, t.BeatLength, t.Meter, false)).ToArray() : [];
        var hits = Array.Empty<HitObjectInput>(); int mode = 0;
        bool needsFile = (definition?.Inputs & (EffectInputs.Timing | EffectInputs.HitObjects | EffectInputs.Metadata)) is not (null or EffectInputs.None);
        string? text = !needsFile || difficulty == null || !File.Exists(project.DifficultyFile(difficulty.File)) ? null : File.ReadAllText(project.DifficultyFile(difficulty.File));
        if (text != null && definition?.Inputs.HasFlag(EffectInputs.Timing) == true)
        {
            var states = new List<(double Time, bool Kiai)>(); bool timingSection = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith('[')) { timingSection = line == "[TimingPoints]"; continue; }
                if (!timingSection) continue;
                var parts = line.Split(',');
                if (parts.Length >= 8 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double time) && int.TryParse(parts[7], out int flags)) states.Add((time, (flags & 1) != 0));
            }
            var ordered = states.OrderBy(s => s.Time).ToArray();
            points = points.Select(p => p with { Kiai = ordered.LastOrDefault(s => s.Time <= p.Time).Kiai }).ToArray();
        }
        if (definition?.Inputs.HasFlag(EffectInputs.HitObjects) == true && difficulty != null)
        {
            var map = BeatmapReader.Read(project.DifficultyFile(difficulty.File)); mode = map.Mode;
            hits = map.Objects.Select(h => new HitObjectInput(h.StartTime, h.EndTime, new(h.Position.X, h.Position.Y), h.GetType().Name)).ToArray();
        }
        string title = "", artist = "";
        if (text != null)
        { var metadata = OsuFile.Parse(text); mode = metadata.Mode; if (definition?.Inputs.HasFlag(EffectInputs.Metadata) == true) { title = metadata.Title; artist = metadata.Artist; } }
        return new(mode, title, artist, difficulty?.Name ?? "", Array.AsReadOnly(points), Array.AsReadOnly(hits));
    }
    public void DrawProperties(EditorApp app, SbObjectGroup group) => Ui.PropertiesPanel.DrawEffectParameters(app, group);
    public void Reveal(Project project, PluginLink link) => Ui.AssetsPanel.OpenInExplorer(Root(project, link), false);
}
