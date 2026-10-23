using System.Text.Json;
using SbStudio.App.Effects;
using SbStudio.Core;
using SbStudio.Effects;

namespace SbStudio.App;

public sealed partial class EditorApp
{
    private EffectCatalog? effectCatalog;
    private EffectHostPool? effectHosts;
    private EffectPluginSource? effectSource;
    private IDisposable? effectHistoryPins;
    private long nextEffectCollection;
    private Task? effectCollection;
    private CancellationTokenSource effectLifetime = new();
    private void RefreshEffectPins()
    {
        var previous = effectHistoryPins;
        effectHistoryPins = Project == null ? null : EffectResultPins.Pin(Project,
            Doc.Groups.Concat(HistoryStates().SelectMany(s => s.Groups)).Select(g => g.Plugin?.Key));
        previous?.Dispose();
    }
    private void CollectEffectCache()
    {
        if (Project == null || effectCollection is { IsCompleted: false } || Environment.TickCount64 < nextEffectCollection) return;
        nextEffectCollection = Environment.TickCount64 + 60000;
        var project = Project;
        effectCollection = Task.Run(() => EffectResultPins.Collect(project));
    }
    internal EffectCatalogEntry? DraggedEffect;
    internal EffectCatalog EffectsCatalog => effectCatalog ??= new();
    internal EffectHostPool EffectHosts
    {
        get { effectHosts ??= new(concurrency: Settings.EffectWorkerCount); effectHosts.Timeout = TimeSpan.FromSeconds(Math.Clamp(Settings.EffectTimeoutSeconds, 1, 3600)); return effectHosts; }
    }
    internal void ConfigureEffectExecution(int workers, int seconds)
    {
        if (ImportingPlugins != 0) return;
        Settings.EffectWorkerCount = Math.Clamp(workers, 1, 8); Settings.EffectTimeoutSeconds = Math.Clamp(seconds, 1, 3600);
        effectHosts?.Dispose(); effectHosts = null; Settings.Save();
    }
    internal void ConfigureEffectsForTest(string profile, string host)
    { effectHosts?.Dispose(); effectCatalog = new(profile); effectHosts = new(host); }
    public IReadOnlyList<EffectCatalogEntry> EffectLibrary => EffectsCatalog.Entries;
    public void RefreshEffectLibrary() => EffectsCatalog.Refresh(Project);
    public bool IsEffectFavorite(string id) => EffectsCatalog.Favorite(id);
    public void ToggleEffectFavorite(string id) => EffectsCatalog.ToggleFavorite(id);
    public SbObjectGroup? AddEffect(EffectCatalogEntry entry, int? offset = null, SbObjectGroup? parent = null)
    {
        if (Project == null || RejectPluginEdit((parent??OpenGroup)?.PluginRoot != null)) return null;
        if (!entry.Trusted) { SetStatus(L.T("effects.untrusted")); return null; }
        if (entry.SourceKind == "builtin") EffectsCatalog.Trust(entry.Directory, entry.Manifest, false);
        string source = entry.Directory; var manifest = entry.Manifest;
        if (entry.SourceKind != "development")
        {
            string archive = Path.Combine(Path.GetTempPath(), "SbStudio-effect-" + Guid.NewGuid().ToString("N") + ".sbeffect");
            try { PackageFiles.Pack(source, archive); source = PackageFiles.Install(archive, Path.Combine(Project.Folder, "effects", "packages")); }
            finally { if (File.Exists(archive)) File.Delete(archive); }
        }
        CommitEdits();
        var link = new PluginLink { Kind = PluginKind.Effect, Head = "effect", Instance = Doc.Groups.Select(g => g.Plugin?.Instance ?? 0).DefaultIfEmpty().Max() + 1,
            Offset = offset ?? EffectOutputAdapter.Milliseconds(Time), Effect = new() { PackageId = manifest.PackageId, EffectId = entry.Definition.EffectId,
                PackageVersion = manifest.PackageVersion, PackageHash = manifest.ContentHash, SourceKind = entry.SourceKind == "development" ? "development" : "package",
                SourceLocation = StorePluginPath(source), InputDifficulty = Project.Settings.Difficulty, Definition = entry.Definition,
                DurationMs = entry.Definition.TimeMode == TimeMode.AdjustableDuration ? entry.Definition.DefaultDuration : null } };
        foreach (var parameter in entry.Definition.Parameters) EffectBinding.SetParameter(link, parameter.Id, parameter.Default);
        var group = new SbObjectGroup { Name = EffectDisplay.Name(entry.Definition), Parent = parent??OpenGroup, Plugin = link };
        Doc.Groups.Add(group); SelectGroup(group); Touch(); CommitEdits(); SetPluginHistory(HistoryAction.LinkPlugin); SyncPlugins(); return group;
    }
    private SbObjectGroup? editingEffect;
    private void BeginEffectEdit(SbObjectGroup group)
    {
        if (editingEffect == group && PreviewingEdit) return;
        var original = group.Plugin!.Clone();
        BeginPreviewEdit(() => { group.Plugin = original; TouchGroup(group); SyncPlugins(true); });
        editingEffect = group;
    }
    public void FinishEffectEdit()
    {
        if (editingEffect == null) return;
        editingEffect = null; FinishPreviewEdit(true);
    }
    public void SetEffectParameter(SbObjectGroup group, string id, JsonElement value, bool continuous = false)
    {
        var link = group.Plugin; var definition = link?.Effect?.Definition;
        if (link?.Effect?.Supported != true || definition == null) return;
        var parameter = definition.Parameters.Single(p => p.Id == id); ParameterValues.Validate(parameter, value);
        var current = EffectBinding.Parameters(link);
        if (current.TryGetValue(id, out var previous) && JsonElement.DeepEquals(previous, value)) return;
        if (continuous) BeginEffectEdit(group); else CommitEdits();
        EffectBinding.SetParameter(link, id, value); link.Effect.DesiredInputKey = "pending";
        TouchGroup(group); if (!continuous) CommitEdits(); SyncPlugins();
    }
    public void SetEffectDuration(SbObjectGroup group, double duration)
    {
        SetEffectRange(group, group.Plugin?.Offset ?? 0, duration);
    }
    public void SetEffectRange(SbObjectGroup group, int start, double duration, bool continuous = false)
    {
        var effect = group.Plugin?.Effect;
        if (effect?.Definition is not { TimeMode: TimeMode.AdjustableDuration } definition || !double.IsFinite(duration) || duration < definition.MinDuration || duration > definition.MaxDuration) return;
        if (effect.DurationMs == duration && group.Plugin!.Offset == start) return;
        if (continuous) BeginEffectEdit(group); else CommitEdits();
        effect.TimeOffset += Math.Min(0, effect.TimeScale * (effect.DurationMs ?? definition.DefaultDuration)) - Math.Min(0, effect.TimeScale * duration);
        int shift = checked(start - group.Plugin!.Offset);
        if (shift != 0 && plugins.TryGetValue(group.Plugin.Instance, out var runtime) && runtime.Output != null)
        { group.Plugin.Offset = start; PutPluginOutput(group, runtime.Output); }
        group.Plugin.Offset = start; effect.DurationMs = duration; effect.DesiredInputKey = "pending";
        TouchGroup(group); if (!continuous) CommitEdits(); SyncPlugins();
    }
    public void SetEffectOutputDifficulty(SbObjectGroup group, string? difficulty)
    {
        var effect = group.Plugin?.Effect;
        if (effect == null || Project == null || difficulty != null && !Project.Difficulties.Any(d => d.File == difficulty)) return;
        CommitEdits(); effect.OutputDifficulty = difficulty;
        foreach (var sprite in Doc.SpritesIn(group, true)) sprite.Difficulty = difficulty;
        TouchGroup(group); CommitEdits(); SyncPlugins();
    }
    public void SaveEffectPreset(SbObjectGroup group, string file)
    { if (Project != null && group.Plugin?.Effect?.Supported == true) EffectPresets.Save(Project, group.Plugin, file); }
    public AssetReference ImportEffectAsset(string file, bool animation)
    {
        if (Project == null) throw new InvalidOperationException(L.T("effects.projectRequired"));
        if (!animation) return AssetReference.Project(Project.ImportAsset(file));
        string directory = Path.GetDirectoryName(Path.GetFullPath(file))!, extension = Path.GetExtension(file);
        string stem = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(file), "[0-9]+$", "");
        var files = Directory.EnumerateFiles(directory).Where(p => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p), "^" + System.Text.RegularExpressions.Regex.Escape(stem) + "[0-9]+" + System.Text.RegularExpressions.Regex.Escape(extension) + "$"))
            .Order(StringComparer.Ordinal).Take(PackageFiles.MaxEntries + 1).ToArray();
        if (files.Length == 0 || files.Length > PackageFiles.MaxEntries || !files.Any(p => Path.GetFileName(p) == stem + "0" + extension)) throw new IOException("Choose an animation frame from a sequence starting at frame 0.");
        long total = 0;
        foreach (string path in files) { long size = new FileInfo(path).Length; if (size > PackageFiles.MaxFileBytes || (total += size) > PackageFiles.MaxTotalBytes) throw new IOException("Animation assets exceed limits."); }
        string hash = PackageFiles.Hash(System.Text.Encoding.UTF8.GetBytes(string.Join('\n',files.Select(p => Path.GetFileName(p) + ":" + PackageFiles.HashFile(p)))));
        string relative = "generated/effects/" + hash; Directory.CreateDirectory(Path.Combine(Project.AssetsDir, relative));
        foreach (string path in files)
        {
            string destination = PackageFiles.SafePath(Project.AssetsDir,relative + "/" + Path.GetFileName(path));
            if (File.Exists(destination)) { if (PackageFiles.HashFile(destination) != PackageFiles.HashFile(path)) throw new IOException("Immutable animation asset was modified."); }
            else Effects.EffectOutputStore.WriteAtomic(destination, File.ReadAllBytes(path));
        }
        return AssetReference.Project(relative + "/" + stem + extension);
    }
    public void ApplyEffectPreset(SbObjectGroup group, string file)
    {
        if (Project == null || group.Plugin?.Effect?.Supported != true) return;
        var values = EffectPresets.Read(Project, group.Plugin, file);
        CommitEdits(); foreach (var entry in values) EffectBinding.SetParameter(group.Plugin, entry.Key, entry.Value);
        group.Plugin.Effect.DesiredInputKey="pending"; RefreshAssets(); TouchGroup(group); CommitEdits(); SyncPlugins();
    }
    public sealed record EffectUpgrade(SbObjectGroup Group, PluginLink Binding, PluginOutput Output, string OriginalIdentity, string ProjectFolder) : IDisposable
    {
        internal IDisposable? Lease;
        public void Dispose() => Interlocked.Exchange(ref Lease, null)?.Dispose();
    }
    public Task<EffectUpgrade> PrepareEffectUpgradeAsync(SbObjectGroup group, EffectCatalogEntry entry, CancellationToken token = default)
    {
        if (Project == null || group.Plugin?.Effect is not {} old || entry.Definition.EffectId != old.EffectId || !entry.Trusted) throw new InvalidOperationException(L.T("effects.incompatible"));
        var project = Project; string identity = PluginIdentity(group.Plugin); var candidate = group.Plugin.Clone(); var effect = candidate.Effect!;
        var values = EffectBinding.Parameters(candidate);
        foreach (var parameter in entry.Definition.Parameters)
        {
            var previous = old.Definition?.Parameters.FirstOrDefault(p => p.Id == parameter.Id);
            if (previous != null && previous.Type != parameter.Type && values.ContainsKey(parameter.Id)) throw new ArgumentException("Changed parameter type: " + parameter.Id);
            if (values.TryGetValue(parameter.Id,out var value)) ParameterValues.Validate(parameter,value); else EffectBinding.SetParameter(candidate,parameter.Id,parameter.Default);
        }
        string archive = Path.Combine(Path.GetTempPath(), "SbStudio-effect-upgrade-" + Guid.NewGuid().ToString("N") + ".sbeffect"), pinned;
        try { PackageFiles.Pack(entry.Directory,archive); pinned=PackageFiles.Install(archive,Path.Combine(project.Folder,"effects","packages")); }
        finally { if(File.Exists(archive)) File.Delete(archive); }
        effect.SourceKind="package";effect.SourceLocation=StorePluginPath(pinned);effect.PackageVersion=entry.Manifest.PackageVersion;
        effect.PackageHash=entry.Manifest.ContentHash;effect.PackageId=entry.Manifest.PackageId;effect.Definition=entry.Definition;effect.SchemaVersion=entry.Definition.SchemaVersion;
        effect.DurationMs=entry.Definition.TimeMode==TimeMode.AdjustableDuration?effect.DurationMs??entry.Definition.DefaultDuration:null;
        var generate=(effectSource??=new(this)).Prepare(project,candidate);
        var lifetime = effectLifetime.Token;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        return Task.Run(()=>
        {
            using (cancellation)
            {
                var result=generate(cancellation.Token);
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if(result.Output==null || result.Effect==null) throw new InvalidOperationException(result.Error);
                    candidate.Effect=result.Effect;candidate.Key=result.Output.Key;
                    var prepared = new EffectUpgrade(group,candidate,result.Output,identity,project.Folder)
                    { Lease = EffectResultPins.Pin(project, [result.Output.Key], lifetime) };
                    result.OutputLease?.Dispose();
                    return prepared;
                }
                catch { result.OutputLease?.Dispose(); throw; }
            }
        });
    }
    public void ApplyEffectUpgrade(EffectUpgrade prepared)
    {
        using var lease = prepared;
        if(Project?.Folder!=prepared.ProjectFolder || !Doc.Groups.Contains(prepared.Group) || prepared.Group.Plugin==null || PluginIdentity(prepared.Group.Plugin)!=prepared.OriginalIdentity)
            throw new InvalidOperationException("The effect changed while preparing its upgrade; generate the upgrade again.");
        CommitEdits();
        if(plugins.Remove(prepared.Group.Plugin.Instance,out var runtime)) runtime.Dispose();
        prepared.Group.Plugin=prepared.Binding;RefreshAssets();PutPluginOutput(prepared.Group,prepared.Output);TouchGroup(prepared.Group);CommitEdits();SyncPlugins();
    }
    public async Task<IReadOnlyList<EffectCatalogEntry>> LoadDevelopmentEffectAsync(string folder, bool trust, CancellationToken token = default)
    {
        var manifest = PackageFiles.ReadManifest(folder);
        if (trust) EffectsCatalog.Trust(folder, manifest, true);
        if (!EffectsCatalog.Trusted(folder, manifest, true)) throw new InvalidOperationException(L.T("effects.untrusted"));
        using var work = new EffectWorkDirectory(Path.Combine(EffectsCatalog.Profile, "work"));
        var response = await EffectHosts.RunAsync(new() { Operation = "Describe", Development = true, SourceDirectory = folder, WorkDirectory = work.Path,
            BuildDirectory = Path.Combine(EffectsCatalog.Profile, "build") }, token);
        if (!response.Success) throw new InvalidOperationException(string.Join('\n', new[] { response.Error }.Concat(response.Diagnostics.Select(d => $"{d.File}:{d.Line} {d.Message}"))));
        return response.Definitions.Select(d => new EffectCatalogEntry(Path.GetFullPath(folder), manifest, d, "development", true)).ToArray();
    }
    public void AddDevelopmentEntries(IEnumerable<EffectCatalogEntry> entries)
    {
        foreach (var entry in entries)
        {
            EffectsCatalog.Entries.RemoveAll(e => e.Directory.Equals(entry.Directory, StringComparison.OrdinalIgnoreCase) && e.Definition.EffectId == entry.Definition.EffectId);
            EffectsCatalog.Entries.Add(entry);
            EffectsCatalog.RememberDevelopment(entry);
        }
    }
    public void TrustEffect(EffectCatalogEntry entry)
    { EffectsCatalog.Trust(entry.Directory, entry.Manifest, entry.SourceKind == "development"); RefreshEffectLibrary(); }
    public void TrustEffect(SbObjectGroup group)
    {
        if (Project == null || group.Plugin?.Effect == null) return;
        string root = EffectPluginSource.Root(Project, group.Plugin); var manifest = PackageFiles.ReadManifest(root);
        EffectsCatalog.Trust(root, manifest, group.Plugin.Effect.SourceKind == "development"); ReimportPlugin(group);
    }
    public string InstallEffectPackage(string archive)
    { string installed = PackageFiles.Install(archive, EffectsCatalog.Packages); RefreshEffectLibrary(); return installed; }
    public string CreateDevelopmentEffect()
    {
        if (Project == null) throw new InvalidOperationException(L.T("effects.projectRequired"));
        string id = "user.effect." + Guid.NewGuid().ToString("N")[..8], folder = Path.Combine(Project.Folder, "effects", "src", id);
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        File.WriteAllText(Path.Combine(folder, "effect.json"), SbStudio.Effects.EffectJson.Serialize(new PackageManifest { PackageId = id, Effects = [new(id + ".example", "Example")] }));
        File.WriteAllText(Path.Combine(folder, "Example.cs"), """
            using SbStudio.Effects;

            public sealed class Example : IEffectModule
            {
                private ParameterKey<int> count = null!;
                private ParameterKey<ColorValue> color = null!;
                public void Define(EffectDefinitionBuilder d)
                {
                    d.Name("My effect").Category("Custom").AdjustableDuration(5000);
                    count = d.Integer("count").Default(12).Range(1, 1000).Label("Count").Key;
                    color = d.Color("color").Default(ColorValue.White).Label("Color").Key;
                }
                public void Generate(EffectContext c, StoryboardBuilder b)
                {
                    // This fixed speed is intentionally not exposed as a parameter.
                    const double speed = 50;
                    var random = c.Random(123);
                    for (int i = 0; i < c.Parameters.Get(count); i++)
                    {
                        c.Cancellation.ThrowIfCancellationRequested();
                        double x = random.Between(0, 640), y = random.Between(0, 200);
                        b.Animation(AssetReference.Package("assets/particle.png"), 2, 150)
                            .Move(0, c.Duration, x, y, x, y + speed * c.Duration / 1000)
                            .Color(0, c.Parameters.Get(color)).Scale(0, .3)
                            .Loop(0, 4, loop => loop.Fade(0, 250, 0, 1).Fade(250, 500, 1, 0));
                    }
                }
            }
            """);
        string image = Path.Combine(AppContext.BaseDirectory, "effects", "builtin", "assets", "particle.png");
        File.Copy(image, Path.Combine(folder, "assets", "particle0.png")); File.Copy(image, Path.Combine(folder, "assets", "particle1.png"));
        string sdk = System.Security.SecurityElement.Escape(Path.Combine(AppContext.BaseDirectory, "effects", "host", "SbStudio.Effects.Sdk.dll"));
        File.WriteAllText(Path.Combine(folder, "Effect.csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><LangVersion>14.0</LangVersion><Nullable>enable</Nullable></PropertyGroup><ItemGroup><Reference Include=\"SbStudio.Effects.Sdk\"><HintPath>{sdk}</HintPath></Reference></ItemGroup></Project>");
        return folder;
    }
    public void RemoveInstalledEffect(EffectCatalogEntry entry)
    {
        string store = Path.GetFullPath(EffectsCatalog.Packages).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string directory = Path.GetFullPath(entry.Directory);
        if (entry.SourceKind != "installed" || !directory.StartsWith(store, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(L.T("effects.readOnly"));
        PackageFiles.ValidateDirectory(directory); Directory.Delete(directory, true); RefreshEffectLibrary();
    }
    public void CopyEffectSourceIntoProject(SbObjectGroup group)
    {
        if (Project == null || group.Plugin?.Effect is not { SourceKind: "development" } binding) return;
        string source = EffectPluginSource.Root(Project, group.Plugin), target = Path.Combine(Project.Folder, "effects", "src", binding.PackageId + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(target);
        foreach (string file in PackageFiles.EnumerateFiles(source, development: true))
        {
            string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (relative.Split('/').Any(n => n is "bin" or "obj" or ".git" or ".build")) continue;
            string checkedSource = PackageFiles.SafePath(source, relative), destination = PackageFiles.SafePath(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(checkedSource, destination);
        }
        var manifest = PackageFiles.ReadManifest(target);
        if (EffectsCatalog.Trusted(source, manifest, true)) EffectsCatalog.Trust(target, manifest, true);
        CommitEdits(); binding.SourceLocation = StorePluginPath(target); TouchGroup(group); CommitEdits(); SyncPlugins();
    }
    public async Task PackEffectAsync(EffectCatalogEntry entry, string destination, CancellationToken token = default)
    {
        if (!entry.Trusted) throw new InvalidOperationException(L.T("effects.untrusted"));
        if (entry.SourceKind != "development") { PackageFiles.Pack(entry.Directory, destination); return; }
        using var work = new EffectWorkDirectory(Path.Combine(EffectsCatalog.Profile, "work"));
        var response = await EffectHosts.RunAsync(new() { Operation = "Pack", Development = true, SourceDirectory = entry.Directory,
            WorkDirectory = work.Path, BuildDirectory = Path.Combine(EffectsCatalog.Profile, "build"), OutputPackage = destination }, token);
        if (!response.Success) throw new InvalidOperationException(response.Error);
    }
    private void InvalidateEffectRequests()
    {
        foreach (var group in Doc.Groups.Where(g => g.Plugin?.Kind == PluginKind.Effect))
            if (plugins.TryGetValue(group.Plugin!.Instance, out var runtime)) { runtime.Dispose(); plugins.Remove(group.Plugin.Instance); }
    }
    private void UpdateCommittedEffectResult(SbObjectGroup group)
    {
        if (committed == null || group.Plugin?.Effect == null) return;
        var copy = timelineFolds.Clone(committed.Objects, committed.Parked, committed.Groups);
        var previous = copy.Groups.FirstOrDefault(g => g.Plugin?.Effect?.InstanceGuid == group.Plugin.Effect.InstanceGuid);
        if (previous?.Plugin == null || PluginIdentity(previous.Plugin) != PluginIdentity(group.Plugin)) return;
        previous.Plugin.Key = group.Plugin.Key; previous.Plugin.Effect = group.Plugin.Effect.Clone();
        committed = committed with { Objects = copy.Objects, Parked = copy.Parked, Groups = copy.Groups };
        RefreshEffectPins();
    }
}
