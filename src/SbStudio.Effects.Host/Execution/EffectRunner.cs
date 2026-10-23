using System.Diagnostics;

namespace SbStudio.Effects.Host;

internal static class EffectRunner
{
    internal static HostResponse Run(HostRequest request, CancellationToken token, Action<HostResponse>? report = null)
    {
        var response = RunCore(request, token, message => report?.Invoke(message with { RequestId = request.RequestId, Identity = request.Identity }));
        return response with { Identity = request.Identity, Kind = response.Success ? HostMessageKind.Completed : HostMessageKind.Failed };
    }
    private static HostResponse RunCore(HostRequest request, CancellationToken token, Action<HostResponse> report)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            if (request.ProtocolVersion != 1) throw new NotSupportedException("Incompatible effect protocol.");
            Directory.CreateDirectory(request.WorkDirectory);
            string root = Path.GetFullPath(request.SourceDirectory), revision;
            IReadOnlyList<EffectDiagnostic> diagnostics = [];
            if (request.Development)
            {
                report(new() { Kind = HostMessageKind.Progress, Stage = "compile", Progress = .1 });
                var compiled = EffectCompiler.Compile(root, request.WorkDirectory, token, request.Identity.CodeRevision, request.BuildDirectory); root = compiled.Root; revision = compiled.Revision; diagnostics = compiled.Diagnostics;
            }
            else revision = request.Operation == "Prepare" ? "build" : PackageFiles.ValidateDirectory(root).ContentHash;
            if (request.Identity.CodeRevision.Length > 0 && request.Identity.CodeRevision != revision)
                throw new IOException("Effect package changed after this request was queued.");
            token.ThrowIfCancellationRequested();
            report(new() { Kind = HostMessageKind.Progress, Stage = "describe", Progress = .35 });
            using var modules = new ModuleSession(root);
            var entries = modules.Manifest.Effects.Select(e => modules.Create(e)).ToArray();
            var definitions = entries.Select(e => e.Definition).ToArray();
            if (!request.Development && request.Operation != "Prepare")
            {
                var expected = EffectJson.Deserialize<EffectDefinition[]>(File.ReadAllText(PackageFiles.SafePath(root, "schema.json")));
                if (EffectJson.Serialize(expected) != EffectJson.Serialize(definitions)) throw new IOException("Effect definition differs from the installed schema.");
            }
            string? outputFile = null, outputHash = null;
            var reads = new Dictionary<string, string>();
            foreach (var diagnostic in diagnostics.Take(100))
                report(new() { Kind = HostMessageKind.Diagnostic, Diagnostics = [diagnostic] });
            if (request.Operation is "Pack" or "Prepare")
            {
                string destination = Path.Combine(request.WorkDirectory, "package"); Directory.CreateDirectory(destination);
                var m = modules.Manifest;
                foreach (string relative in new[] { m.EntryAssembly }.Concat(m.Dependencies).Concat(new[] { "assets", "presets" }.Where(name => Directory.Exists(Path.Combine(root, name)))
                    .SelectMany(name => PackageFiles.EnumerateFiles(Path.Combine(root, name))).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))))
                {
                    string source = PackageFiles.SafePath(root, relative), target = PackageFiles.SafePath(destination, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, true);
                }
                File.WriteAllText(Path.Combine(destination, "schema.json"), EffectJson.Serialize(definitions));
                m = PackageFiles.SealDirectory(destination, m);
                if (request.Operation == "Pack") PackageFiles.Pack(destination, request.OutputPackage);
                return new() { RequestId = request.RequestId, Success = true, CodeRevision = revision, PackageDirectory = destination, Definitions = definitions, Diagnostics = diagnostics, ElapsedMs = timer.Elapsed.TotalMilliseconds };
            }
            if (request.Operation == "Generate")
            {
                report(new() { Kind = HostMessageKind.Progress, Stage = "generate", Progress = .5 });
                var selected = entries.SingleOrDefault(e => e.Definition.EffectId == request.EffectId);
                if (selected.Module == null) throw new ArgumentException("Effect is not declared by this package.");
                var definition = selected.Definition;
                double duration = definition.TimeMode == TimeMode.AdjustableDuration ? request.Duration : definition.DefaultDuration;
                if (!double.IsFinite(duration) || duration < definition.MinDuration || duration > definition.MaxDuration) throw new ArgumentOutOfRangeException(nameof(request.Duration));
                var assets = new EffectAssets(request.ProjectAssets, root, Path.Combine(request.WorkDirectory, "generated"));
                var values = new ParameterValues(definition, request.Parameters);
                var builder = new StoryboardBuilder();
                var messages = diagnostics.Take(100).ToList(); int logSize = 0;
                selected.Module.Generate(new(values, duration, request.PlacementTime, request.Beatmap, assets, token, text =>
                {
                    if (logSize >= 16384 || messages.Count >= 200) return;
                    string bounded = text[..Math.Min(text.Length, Math.Min(2048, 16384 - logSize))]; logSize += bounded.Length;
                    messages.Add(new("info", bounded));
                    report(new() { Kind = HostMessageKind.Diagnostic, Diagnostics = [new("info", bounded)] });
                    if (logSize == 16384 || messages.Count == 200) messages.Add(new("info", "Further effect log messages were truncated."));
                }), builder);
                diagnostics = messages;
                token.ThrowIfCancellationRequested();
                report(new() { Kind = HostMessageKind.Progress, Stage = "output", Progress = .9 });
                var output = builder.Build(); outputFile = Path.Combine(request.WorkDirectory, "result.json");
                File.WriteAllText(outputFile, EffectJson.Serialize(output)); outputHash = PackageFiles.HashFile(outputFile); reads = assets.Reads;
            }
            else if (request.Operation is not ("Describe" or "Compile")) throw new ArgumentException("Unknown host operation.");
            return new() { RequestId = request.RequestId, Success = true, CodeRevision = revision, PackageDirectory = root, Definitions = definitions,
                Diagnostics = diagnostics, OutputFile = outputFile, OutputHash = outputHash, ReadAssets = reads, ElapsedMs = timer.Elapsed.TotalMilliseconds };
        }
        catch (Exception ex)
        {
            while (ex is System.Reflection.TargetInvocationException { InnerException: {} inner }) ex = inner;
            string detail = ex.ToString();
            return new() { RequestId = request.RequestId, Error = ex.Message, Diagnostics = ex is CompilationFailure failure ? failure.Diagnostics : [new("Error", detail[..Math.Min(detail.Length,8192)])], ElapsedMs = timer.Elapsed.TotalMilliseconds };
        }
    }
}
