using System.Numerics;
using System.Text;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>
/// Live osb script editor. Shows the script of the selected object (or of the whole storyboard); every
/// keystroke is parsed and, when it is valid, applied to the storyboard so the preview follows the text.
/// </summary>
public sealed class ScriptPanel
{
    private static readonly OsbWriteOptions WriteOptions = new() { IncludeEditorMeta = true, SafeVariableReferences = true };

    private readonly EditorApp app;
    private bool whole;
    private string text = "";
    private int syncedRevision = -1;
    private SbObject? syncedObject;
    private bool syncedWhole;
    private bool pendingErrors;
    private List<Diagnostic> diagnostics = new();

    private static readonly Localized<string> WindowTitle = new(() => L.T("panel.script") + "###script");

    public bool Open = true;
    /// <summary>The dock node the panel sits in, 0 while it floats or is closed. A panel that is new to a saved layout joins it there.</summary>
    public uint DockId { get; private set; }

    public ScriptPanel(EditorApp app) => this.app = app;

    private bool ReadOnly => app.EmptyBeatmapBackgroundSelected || app.Selection.Any(app.IsProtectedBeatmapBackground);

    public void Draw()
    {
        DockId = 0;
        if (!Open) return;
        bool visible = ImGui.Begin(WindowTitle.Value, ref Open);
        DockId = ImGui.GetWindowDockID();
        if (!visible || app.Project == null)
        {
            ImGui.End();
            return;
        }

        if (ImGui.RadioButton(L.T("script.selected"), !whole)) whole = false;
        ImGui.SameLine();
        if (ImGui.RadioButton(L.T("script.whole"), whole)) whole = true;
        bool readOnly = ReadOnly;
        string note = readOnly ? L.T("script.readOnlyBeatmapBg")
            : pendingErrors ? L.T("script.errors", diagnostics.Count(d => d.IsError)) : L.T("script.live");
        ImGui.SameLine();
        // Next to the buttons when it fits there, which depends on the language; otherwise on its own line.
        if (ImGui.GetContentRegionAvail().X < ImGui.CalcTextSize(note).X) ImGui.NewLine();
        if (pendingErrors && !readOnly)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            ImGui.TextUnformatted(note);
            ImGui.PopStyleColor();
        }
        else
        {
            Theme.TextDimmed(note);
        }

        var target = whole ? null : app.Primary;
        if (!whole && target == null)
        {
            app.ArchiveScriptDiagnostics();
            Theme.TextDimmed(app.SelectedGroup != null
                ? L.T("script.groupNote")
                : app.EmptyBeatmapBackgroundSelected ? L.T("common.none") : L.T("script.noSelection"));
            syncedObject = null;
            ImGui.End();
            return;
        }

        // A newly selected protected object must replace any active editor buffer immediately.
        if (readOnly && (syncedRevision != app.DocRevision || syncedObject != target || syncedWhole != whole)) Sync(target);

        float diagHeight = diagnostics.Count > 0
            ? Math.Min(diagnostics.Count, 5) * ImGui.GetTextLineHeightWithSpacing() + Theme.Px(8)
            : 0;
        var size = ImGui.GetContentRegionAvail();
        size.Y = MathF.Max(Theme.Px(60), size.Y - diagHeight);

        // The buffer has to be large enough for what the user may type next.
        uint capacity = (uint)Math.Max(1 << 16, Encoding.UTF8.GetByteCount(text) * 2 + (1 << 14));
        ImGui.PushFont(Theme.MonoFont);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Theme.PanelDark);
        bool edited = ImGui.InputTextMultiline("##script", ref text, capacity, size,
            ImGuiInputTextFlags.AllowTabInput | (readOnly ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None));
        bool editing = ImGui.IsItemActive();
        ImGui.PopStyleColor();
        ImGui.PopFont();

        if (edited && !readOnly)
        {
            Apply(target);
        }
        else if (!editing && (syncedRevision != app.DocRevision || syncedObject != target || syncedWhole != whole))
        {
            Sync(target);
        }

        if (diagnostics.Count > 0)
        {
            ImGui.BeginChild("diagnostics", new Vector2(0, 0));
            foreach (var d in diagnostics)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, d.IsError ? Theme.TextError : Theme.Rgba(235, 200, 90));
                ImGui.TextUnformatted(L.T("common.line", d.Line, d.Message));
                ImGui.PopStyleColor();
            }
            ImGui.EndChild();
        }

        ImGui.End();
    }

    private void MarkSynced(SbObject? target)
    {
        syncedRevision = app.DocRevision;
        syncedObject = target;
        syncedWhole = whole;
    }

    private void Sync(SbObject? target)
    {
        app.ArchiveScriptDiagnostics();
        text = whole
            ? OsbWriter.WriteFile(app.Doc, new OsbWriteOptions {IncludeEditorMeta=true,SkipGenerated=true,SafeVariableReferences=true})
            : OsbWriter.WriteObjects(new[] { target! }, WriteOptions, app.Doc.Groups);
        diagnostics = new List<Diagnostic>();
        pendingErrors = false;
        MarkSynced(target);
    }

    private void Apply(SbObject? target)
    {
        if (ReadOnly) return;
        var parsed = OsbParser.Parse(text, externalGroups: whole ? null : app.Doc.Groups, variables: whole ? null : app.Doc.Variables,
            sourceFile: whole ? L.T("script.whole") : L.T("diagnostics.objectScript", target?.DisplayName ?? ""));
        diagnostics = parsed.Diagnostics;
        var objects = parsed.Storyboard.Objects;
        if (AssetPathPolicy.InvalidPath(app.Project!, parsed.Storyboard) is { } invalidPath)
            diagnostics.Add(new Diagnostic(1, AssetPathPolicy.Message(invalidPath), true));

        if (!whole && objects.Count == 0 && !parsed.HasErrors)
            diagnostics.Add(new Diagnostic(1, L.T("script.needDeclaration"), true));
        if (whole && !app.Project!.PreservesBackgroundPlaceholders(parsed.Storyboard))
            diagnostics.Add(new Diagnostic(1, L.T("props.beatmapBg.pathOnly"), true));

        pendingErrors = diagnostics.Any(d => d.IsError);
        app.SetScriptDiagnostics(diagnostics);
        if (pendingErrors) return;

        if (whole)
        {
            app.ReplaceAll(parsed.Storyboard);
            MarkSynced(null);
        }
        else
        {
            app.ReplaceObject(target!, parsed);
            MarkSynced(app.Primary);
        }
    }
}
