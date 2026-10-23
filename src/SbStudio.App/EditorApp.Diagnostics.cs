using SbStudio.Core;

namespace SbStudio.App;

public sealed partial class EditorApp
{
    public IReadOnlyList<SourceDiagnostic> ScriptDiagnostics { get; private set; } = Array.Empty<SourceDiagnostic>();
    public int ScriptDiagnosticsRevision { get; private set; }

    public void SetScriptDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        var next = diagnostics.Select(d => new SourceDiagnostic(d, DiagnosticOrigin.Script)).ToArray();
        if (ScriptDiagnostics.SequenceEqual(next)) return;
        ScriptDiagnostics = next;
        ScriptDiagnosticsRevision++;
    }

    /// <summary>When invalid edits are left behind, retain their source rather than silently losing the diagnostics.</summary>
    public void ArchiveScriptDiagnostics()
    {
        if (Project != null && ScriptDiagnostics.Count > 0)
        {
            int previousRevision = Project.DiagnosticsRevision;
            Project.RecordDiagnostics(ScriptDiagnostics.Select(d => d.Diagnostic), DiagnosticOrigin.Script);
            if (Project.DiagnosticsRevision != previousRevision) MarkSettingsChanged();
        }
        SetScriptDiagnostics(Array.Empty<Diagnostic>());
    }
}
