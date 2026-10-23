namespace SbStudio.Core;

public enum DiagnosticOrigin { Import, Load, Reference, Script, Paste }

/// <summary>A read-time diagnostic and its original source line, retained even when parsing skipped that line.</summary>
public sealed record SourceDiagnostic(Diagnostic Diagnostic, DiagnosticOrigin Origin);

public sealed partial class Project
{
    private HashSet<Diagnostic>? recordedDiagnostics;
    public IReadOnlyList<SourceDiagnostic> SourceDiagnostics => Settings.SourceDiagnostics;
    public int DiagnosticsRevision { get; private set; }

    /// <summary>Keep diagnostic history separate from the editable storyboard and its undo snapshots.</summary>
    public void RecordDiagnostics(IEnumerable<Diagnostic> diagnostics, DiagnosticOrigin origin)
    {
        recordedDiagnostics ??= Settings.SourceDiagnostics.Select(d => d.Diagnostic).ToHashSet();
        foreach (var diagnostic in diagnostics)
        {
            if (!recordedDiagnostics.Add(diagnostic)) continue;
            Settings.SourceDiagnostics.Add(new SourceDiagnostic(diagnostic, origin));
            DiagnosticsRevision++;
        }
    }

    /// <summary>Older projects discarded imported diagnostics. Re-read the original shared file once without replacing any edits.</summary>
    private void RecoverSourceDiagnostics()
    {
        if (Settings.SourceDiagnosticsInitialized || BeatmapDirectory is not { } directory || !Directory.Exists(directory)) return;
        try
        {
            var difficulties = BeatmapImporter.Difficulties(directory)
                .Select(file => (File: Path.GetFileName(file), Osu: OsuFile.Parse(File.ReadAllText(file)))).ToList();
            var osbFiles = Directory.GetFiles(directory, "*.osb").Select(Path.GetFileName).OfType<string>().Order().ToList();
            string? name = BeatmapImporter.MatchOsb(osbFiles, difficulties);
            if (name != null)
            {
                string file = Path.Combine(directory, name);
                RecordDiagnostics(OsbParser.Parse(File.ReadAllText(file), sourceFile: file).Diagnostics, DiagnosticOrigin.Reference);
            }
            Settings.SourceDiagnosticsInitialized = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecordDiagnostics(new[] { new Diagnostic(0, L.T("diagnostics.sourceReadFailed", ex.Message), false, directory) }, DiagnosticOrigin.Reference);
        }
    }
}
