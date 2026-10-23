using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>
/// The list of what is wrong with the storyboard, or likely to be: commands that run at the same time and
/// may not or should not, commands that end before they start, files that are not there. Each line is one
/// place on the timeline, and a click on it goes there. The list follows the storyboard as it is edited.
/// </summary>
public sealed unsafe class ProblemsPanel
{
    public const string WindowId = "###problems";

    /// <summary>
    /// How long the list rests between two looks at a storyboard that keeps changing, in milliseconds.
    /// While something is dragged every frame is an edit. A look only goes into the sprites that were
    /// edited since the last one, but it does pass by every object, which takes a few milliseconds when
    /// there are tens of thousands: four looks a second keep up with the hand well enough.
    /// </summary>
    private const long RestMs = 250;

    private static readonly uint WarningColor = Theme.Rgba(235, 200, 90);

    private readonly EditorApp app;
    /// <summary>The project's asset files, for telling which of the files the storyboard names are missing.</summary>
    private HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Asks <see cref="files"/>. A new one whenever the files are others: what it said is kept for as long as it is the same.</summary>
    private Func<string, bool> fileExists = _ => true;
    private List<Problem> problems = new();
    private List<SourceDiagnostic> sourceDiagnostics = new();
    /// <summary>The places in <see cref="problems"/> of those that are listed with the filter as it is.</summary>
    private readonly List<(bool Source, int Index)> listed = new();
    /// <summary>What the list was made for: it is only made again when one of these changes.</summary>
    private Project? scanned;
    private int scannedRevision = -1, scannedAssets = -1;
    private int scannedDiagnostics = -1, scannedScriptDiagnostics = -1;
    private long scannedAt;
    private int errors, warnings;
    private bool showErrors = true, showWarnings = true;
    private bool sourcesOnly;
    private SourceDiagnostic? currentSource;
    private bool openSource;
    private string sourceText = "";
    private const string SourceDialog = "###sourceDiagnostic";
    /// <summary>The one that was gone to last.</summary>
    private Problem? current;
    private string title = "";
    private (int Count, int Language) titleFor = (-1, -1);

    public bool Open = true;
    /// <summary>The dock node the panel joins the first time it is shown to a user whose layout does not have it yet.</summary>
    public uint DockBeside;
    /// <summary>Goes to a problem: selects what it is about and puts the playhead where it begins.</summary>
    public Action<Problem>? Locate;

    public ProblemsPanel(EditorApp app) => this.app = app;

    public void Draw()
    {
        if (!Open) return;
        // Also while the panel is a tab behind another: its title says how many there are.
        Refresh();
        int total = problems.Count + sourceDiagnostics.Count;
        if (titleFor != (total, L.Generation))
        {
            titleFor = (total, L.Generation);
            title = (total > 0 ? L.T("panel.problems.count", total) : L.T("panel.problems")) + WindowId;
        }

        ImGui.SetNextWindowSize(new Vector2(Theme.Px(560), Theme.Px(300)), ImGuiCond.FirstUseEver);
        if (DockBeside != 0) ImGui.SetNextWindowDockID(DockBeside, ImGuiCond.FirstUseEver);
        // It does not come to the front by itself: it is there for when it is looked for.
        if (!ImGui.Begin(title, ref Open, ImGuiWindowFlags.NoFocusOnAppearing) || app.Project == null)
        {
            ImGui.End();
            return;
        }

        bool errorsWere = showErrors, warningsWere = showWarnings, sourcesWere = sourcesOnly;
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
        ImGui.Checkbox(L.T("problems.errors", errors) + "###errors", ref showErrors);
        ImGui.PopStyleColor();
        Theme.Tooltip(L.T("problems.errors.tip"));
        ImGui.SameLine(0, Theme.Px(14));
        ImGui.PushStyleColor(ImGuiCol.Text, WarningColor);
        ImGui.Checkbox(L.T("problems.warnings", warnings) + "###warnings", ref showWarnings);
        ImGui.PopStyleColor();
        Theme.Tooltip(L.T("problems.warnings.tip"));
        if (sourceDiagnostics.Count > 0 || sourcesOnly)
        {
            ImGui.Checkbox(L.T("diagnostics.onlySources", sourceDiagnostics.Count) + "###sourcesOnly", ref sourcesOnly);
            Theme.Tooltip(L.T("diagnostics.historyNote"));
        }
        if (errorsWere != showErrors || warningsWere != showWarnings || sourcesWere != sourcesOnly) List();

        if (listed.Count == 0)
        {
            ImGui.Spacing();
            Theme.TextDimmed(total == 0 ? L.T("problems.none") : L.T("problems.noneListed"));
            DrawSourceDetails();
            ImGui.End();
            return;
        }

        var flags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit;
        if (ImGui.BeginTable("problems", 4, flags))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(L.T("problems.column.time"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(82));
            ImGui.TableSetupColumn(L.T("problems.column.what"), ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn(L.T("problems.column.object"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn(L.T("problems.column.detail"), ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableHeadersRow();

            // Only the lines in view are drawn: a storyboard made by a program may have thousands.
            var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin(listed.Count);
            while (clipper.Step())
                for (int line = clipper.DisplayStart; line < clipper.DisplayEnd; line++) DrawLine(line);
            clipper.End();
            clipper.Destroy();
            ImGui.EndTable();
        }
        DrawSourceDetails();
        ImGui.End();
    }

    private void DrawLine(int line)
    {
        var entry = listed[line];
        if (entry.Source)
        {
            DrawSourceLine(line, sourceDiagnostics[entry.Index]);
            return;
        }
        var problem = problems[entry.Index];
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.PushID(line);
        if (ImGui.Selectable(Theme.FormatTime(problem.Start), problem == current, ImGuiSelectableFlags.SpanAllColumns))
        {
            current = problem;
            currentSource = null;
            Locate?.Invoke(problem);
        }
        ImGui.PopID();
        // The whole line once more, for a panel too narrow to show all of it, and what such a problem is.
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
            Theme.SetTooltip($"{What(problem)}\n{NameOf(problem)}    {Theme.FormatTime(problem.Start)}    {Detail(problem)}\n\n{Explain(problem)}");

        // Written as text of their own rather than as the label: a name may have "##" in it.
        ImGui.TableNextColumn();
        ImGui.PushStyleColor(ImGuiCol.Text, problem.IsError ? Theme.TextError : WarningColor);
        ImGui.TextUnformatted(What(problem));
        ImGui.PopStyleColor();

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(NameOf(problem));

        ImGui.TableNextColumn();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.TextUnformatted(Detail(problem));
        ImGui.PopStyleColor();
    }

    private static string OriginName(DiagnosticOrigin origin) => origin switch
    {
        DiagnosticOrigin.Import => L.T("diagnostics.origin.import"),
        DiagnosticOrigin.Load => L.T("diagnostics.origin.load"),
        DiagnosticOrigin.Reference => L.T("diagnostics.origin.reference"),
        DiagnosticOrigin.Paste => L.T("diagnostics.origin.paste"),
        _ => L.T("diagnostics.origin.script"),
    };

    private void DrawSourceLine(int line, SourceDiagnostic entry)
    {
        var diagnostic = entry.Diagnostic;
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.PushID(line);
        string location = diagnostic.Line > 0 ? L.T("diagnostics.line", diagnostic.Line) : L.T("common.none");
        if (ImGui.Selectable(location, entry == currentSource, ImGuiSelectableFlags.SpanAllColumns))
        {
            currentSource = entry;
            current = null;
            sourceText = diagnostic.SourceText;
            openSource = true;
        }
        ImGui.PopID();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
            Theme.SetTooltip($"{diagnostic.SourceFile}\n{location}: {diagnostic.Message}\n{diagnostic.SourceText}\n\n{L.T("diagnostics.click")}");
        ImGui.TableNextColumn();
        ImGui.PushStyleColor(ImGuiCol.Text, diagnostic.IsError ? Theme.TextError : WarningColor);
        ImGui.TextUnformatted(OriginName(entry.Origin));
        ImGui.PopStyleColor();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(diagnostic.SourceFile.Length > 0 ? Path.GetFileName(diagnostic.SourceFile) : OriginName(entry.Origin));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(diagnostic.Message);
    }

    private void DrawSourceDetails()
    {
        if (openSource) { ImGui.OpenPopup(SourceDialog); openSource = false; }
        if (!MainUi.BeginModal(L.T("diagnostics.details") + SourceDialog, ImGui.GetMainViewport().GetCenter(), 720)) return;
        if (currentSource is { } entry)
        {
            var diagnostic = entry.Diagnostic;
            ImGui.PushStyleColor(ImGuiCol.Text, diagnostic.IsError ? Theme.TextError : WarningColor);
            Theme.TextWrapped(diagnostic.Message);
            ImGui.PopStyleColor();
            Theme.TextWrapped(L.T("diagnostics.source", diagnostic.SourceFile.Length > 0 ? diagnostic.SourceFile : OriginName(entry.Origin)));
            if (diagnostic.Line > 0) ImGui.TextUnformatted(L.T("diagnostics.line", diagnostic.Line));
            Theme.TextDimmed(OriginName(entry.Origin));
            Theme.TextWrapped(L.T("diagnostics.historyNote"));
            if (sourceText.Length > 0)
            {
                ImGui.SeparatorText(L.T("diagnostics.original"));
                uint capacity = (uint)Math.Max(1024, System.Text.Encoding.UTF8.GetByteCount(sourceText) + 1);
                ImGui.InputTextMultiline("##sourceLine", ref sourceText, capacity, new Vector2(-1, Theme.Px(110)), ImGuiInputTextFlags.ReadOnly);
            }
            if (Theme.Button(L.T("diagnostics.copy"), 130))
                ImGui.SetClipboardText($"{diagnostic.SourceFile}:{diagnostic.Line}\n{diagnostic.Message}\n{diagnostic.SourceText}");
            ImGui.SameLine();
        }
        if (Theme.Button(L.T("common.close"), 100)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /// <summary>Looks through the storyboard again when it, or the files of the project, changed since the last time.</summary>
    private void Refresh()
    {
        var project = app.Project;
        if (project == null)
        {
            if (scanned == null) return;
            scanned = null;
            problems = new List<Problem>();
            sourceDiagnostics.Clear();
            current = null;
            currentSource = null;
            List();
            return;
        }
        if (scanned == project && scannedRevision == app.DocRevision && scannedAssets == app.AssetsRevision &&
            scannedDiagnostics == project.DiagnosticsRevision && scannedScriptDiagnostics == app.ScriptDiagnosticsRevision) return;
        long now = Environment.TickCount64;
        if (scanned == project && now - scannedAt < RestMs) return;

        if (scanned != project || scannedAssets != app.AssetsRevision)
        {
            files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in app.Assets) files.Add(asset.Path);
            fileExists = files.Contains;
        }
        scanned = project;
        scannedRevision = app.DocRevision;
        scannedAssets = app.AssetsRevision;
        scannedAt = now;
        scannedDiagnostics = project.DiagnosticsRevision;
        scannedScriptDiagnostics = app.ScriptDiagnosticsRevision;
        problems = Problems.Find(app.Doc, fileExists);
        problems.AddRange(app.PluginProblems());
        // Reading diagnostics are listed before timeline warnings so a large imported storyboard cannot bury them.
        sourceDiagnostics = project.SourceDiagnostics.Concat(app.ScriptDiagnostics)
            .DistinctBy(d => d.Diagnostic).ToList();
        List();
    }

    /// <summary>Counts the problems and picks out those the filter lets through.</summary>
    private void List()
    {
        listed.Clear();
        errors = warnings = 0;
        for (int i = 0; i < sourceDiagnostics.Count; i++)
        {
            bool error = sourceDiagnostics[i].Diagnostic.IsError;
            if (error) errors++; else warnings++;
            if (error ? showErrors : showWarnings) listed.Add((true, i));
        }
        for (int i = 0; i < problems.Count; i++)
        {
            bool error = problems[i].IsError;
            if (error) errors++;
            else warnings++;
            if (!sourcesOnly && (error ? showErrors : showWarnings)) listed.Add((false, i));
        }
    }

    /// <summary>The name of a kind of command in a sentence; in the timeline it is spaced for standing in a column.</summary>
    private static string KindName(CommandKind kind) => Theme.KindLabel(kind).Replace("  ", " ");

    private static string What(in Problem problem)
    {
        string text=WhatCore(problem);
        var root=(problem.Object as SbSprite)?.Group?.PluginRoot??problem.Group?.Parent?.PluginRoot;
        return root==null?text:L.T("problem.pluginGenerated",root.Name)+text;
    }
    private static string WhatCore(in Problem problem) => problem.Kind switch
    {
        ProblemKind.PluginFailed => L.T("plugin.failed"),
        ProblemKind.PluginLinkMissing => L.T("plugin.missing"),
        ProblemKind.PluginUnsupported => L.T("plugin.unsupported"),
        ProblemKind.PluginWarnings => L.T("plugin.warnings",problem.Files),
        ProblemKind.Clash => L.T("problems.clash", problem.Command == CommandKind.M ? L.T("clash.pair.move") : L.T("clash.pair.scale")),
        ProblemKind.Overlap => L.T("problems.overlap", KindName(problem.Command)),
        ProblemKind.Reversed => L.T("problems.reversed", KindName(problem.Command)),
        ProblemKind.SpaceComposition => L.T("problem.spaceComposition"),
        ProblemKind.SpaceLoopTooLong => L.T("bake.loopTooLong",problem.Object?.DisplayName ?? ""),
        _ => L.T("problems.missing"),
    };

    private string NameOf(in Problem problem)
    {
        if (problem.Object is not { } obj) return problem.Group!.Name.Length > 0 ? problem.Group.Name : L.T("group.unnamed");
        string name = obj.DisplayName.Length > 0 ? obj.DisplayName : L.T("common.none");
        // One that belongs to a difficulty which is not shown says which.
        return obj.Difficulty != null && obj.Difficulty != app.Project!.Settings.Difficulty ? name + "    [" + obj.Difficulty + "]" : name;
    }

    /// <summary>How long it lasts, or which file it is; ahead of that, that it is in a loop when it is.</summary>
    private static string Detail(in Problem problem) => problem.Loop != null ? L.T("problems.detail.inLoop", Extent(problem)) : Extent(problem);

    private static string Extent(in Problem problem) => problem.Kind switch
    {
        ProblemKind.PluginFailed or ProblemKind.PluginLinkMissing or ProblemKind.PluginUnsupported or ProblemKind.PluginWarnings=>problem.File,
        ProblemKind.MissingFile => problem.Files > 1 ? L.T("problems.detail.files", problem.File, problem.Files) : problem.File,
        ProblemKind.Reversed => L.T("problems.detail.reversed", Theme.FormatTime(problem.End)),
        _ => problem.End > problem.Start
            ? L.T("problems.detail.until", Theme.FormatTime(problem.End), problem.End - problem.Start)
            : L.T("problems.detail.moment"),
    };

    private static string Explain(in Problem problem) => problem.Kind switch
    {
        ProblemKind.Clash => L.T("problems.clash.tip"),
        ProblemKind.Overlap => L.T("problems.overlap.tip"),
        ProblemKind.Reversed => L.T("problems.reversed.tip"),
        ProblemKind.SpaceComposition => L.T("problem.spaceComposition"),
        ProblemKind.SpaceLoopTooLong => L.T("bake.loopTooLong",problem.Object?.DisplayName ?? ""),
        _ => L.T("problems.missing.tip"),
    } + "\n\n" + L.T("problems.locate.tip");
}
