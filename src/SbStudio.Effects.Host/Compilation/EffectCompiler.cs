using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using SbStudio.Effects;

namespace SbStudio.Effects.Host;

internal sealed record CompilationResult(string Root, string Revision, IReadOnlyList<EffectDiagnostic> Diagnostics);
internal static class EffectCompiler
{
    internal static CompilationResult Compile(string source, string work, CancellationToken token, string expectedRevision = "", string cache = "")
    {
        var captured = DevelopmentSnapshot.Capture(source, token);
        var snapshot = captured.Files;
        var manifest = EffectJson.Deserialize<PackageManifest>(Encoding.UTF8.GetString(snapshot["effect.json"]));
        PackageFiles.ValidateManifest(manifest);
        string revision = captured.Revision;
        if (expectedRevision.Length > 0 && expectedRevision != revision)
            throw new IOException("Development files changed after this request was queued; regenerate the latest version.");
        string root = Path.Combine(work, "compiled", revision); Directory.CreateDirectory(root);
        foreach (var p in snapshot) { string file = PackageFiles.SafePath(root, p.Key); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, p.Value); }
        string assembly = PackageFiles.SafePath(root, manifest.EntryAssembly); Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
        if (CompilationCache.Restore(cache, revision, assembly, out var cachedDiagnostics))
            return new(root, revision, cachedDiagnostics);
        var options = new CSharpParseOptions(LanguageVersion.CSharp14);
        var trees = snapshot.Where(p => p.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(p => CSharpSyntaxTree.ParseText(SourceText.From(Encoding.UTF8.GetString(p.Value), Encoding.UTF8), options, p.Key, token)).ToArray();
        if (trees.Length == 0) throw new IOException("No C# source files found.");
        string refs = Path.Combine(AppContext.BaseDirectory, "refs");
        if (!Directory.Exists(refs)) throw new IOException("The effect compiler reference assemblies are missing.");
        var references = Directory.GetFiles(refs, "*.dll").Select(p => MetadataReference.CreateFromFile(p)).ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(IEffectModule).Assembly.Location));
        foreach (string dependency in manifest.Dependencies)
        {
            if (Path.GetFileName(dependency).Equals("SbStudio.Effects.Sdk.dll", StringComparison.OrdinalIgnoreCase)) throw new IOException("Packages must use the host SDK identity.");
            references.Add(MetadataReference.CreateFromFile(PackageFiles.SafePath(root, dependency)));
        }
        var compilation = CSharpCompilation.Create("Effect_" + revision[..16], trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Enable, deterministic: true, allowUnsafe: false));
        using var dll = new MemoryStream(); using var pdb = new MemoryStream();
        var result = compilation.Emit(dll, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb), cancellationToken: token);
        var diagnostics = result.Diagnostics.Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning).Select(d =>
        {
            var pos = d.Location.GetLineSpan(); return new EffectDiagnostic(d.Severity.ToString(), d.GetMessage(), pos.Path, pos.StartLinePosition.Line + 1, pos.StartLinePosition.Character + 1);
        }).ToArray();
        if (!result.Success) throw new CompilationFailure(diagnostics);
        File.WriteAllBytes(assembly, dll.ToArray()); File.WriteAllBytes(Path.ChangeExtension(assembly, ".pdb"), pdb.ToArray());
        CompilationCache.Save(cache, revision, dll.ToArray(), pdb.ToArray(), diagnostics);
        return new(root, revision, diagnostics);
    }

}
internal sealed class CompilationFailure(IReadOnlyList<EffectDiagnostic> diagnostics) : Exception("Effect compilation failed.")
{ internal IReadOnlyList<EffectDiagnostic> Diagnostics => diagnostics; }
