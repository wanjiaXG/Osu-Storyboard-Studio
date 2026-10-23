using System.Numerics;
using System.Reflection;
using System.Diagnostics;
using ImGuiNET;
using SbStudio.App.Gfx;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>Main window: menu bar, dock space with the panels, status bar, shortcuts and dialogs.</summary>
public sealed partial class MainUi
{
    private static readonly string AppVersion = typeof(MainUi).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    // Windows and dialogs are told apart by what follows ###, so their titles can change with the language.
    private const string AssetsWindow = "###assets";
    private const string ViewerWindow = "###viewer";
    private const string PropertiesWindow = "###properties";
    private const string ScriptWindow = "###script";
    private const string ProblemsWindow = ProblemsPanel.WindowId;
    private const string TimelineWindow = "###timeline";
    private const string HistoryWindow = HistoryPanel.WindowId;
    private const string PluginsWindow = PluginsPanel.WindowId;

    private const string NewProjectDialog = "###new";
    private const string ExportDialog = "###export";
    private const string SettingsDialog = "###settings";
    private const string TimingListDialog = "###timingList";
    private const string ImportDialog = "###import";
    private const string UnsavedDialog = "###unsaved";
    private const string MessageDialog = "###message";
    private const string ClashDialog = "###clash";
    private const string RecentDialog = "###recent";
    private const string ShortcutsDialog = "###shortcuts";
    private const string AboutDialog = "###about";
    private const string LoadingDialog = "###loading";
    private const string MissingBeatmapDialog = "###missingBeatmap";

    private readonly EditorApp app;
    private readonly AssetsPanel assets;
    private readonly ViewerPanel viewer;
    public void ShowTriggerSimulation() => viewer.ShowTriggerSimulation();
    public object TriggerSimulationUiState => viewer.TriggerSimulationUiState;
    private readonly PropertiesPanel properties;
    private readonly ScriptPanel script;
    private readonly VariablesPanel variables;
    internal void ShowVariables() { variables.Open = true; variables.FocusRequested = true; }
    private readonly ProblemsPanel problems;
    private readonly TimelinePanel timeline;
    private readonly HistoryPanel history;
    private readonly PluginsPanel plugins;
    internal bool EffectsBusy => plugins.EffectsBusy || PropertiesPanel.EffectsBusy(app);
    internal string TimelineViewportState => timeline.ViewportState;
    internal string TimelineHitObjectSelectionState => timeline.HitObjectSelectionState;
    internal string TimelineRecheckState => timeline.RecheckState;
    internal string TimelineRowsState => timeline.RowsState;
    internal Action<string> OsuEditLinkOpener { set => timeline.OpenOsuEditLink = value; }
    internal Action<string> HitObjectClipboardWriter { set => timeline.WriteHitObjectClipboard = value; }
    internal Action<string> TimingClipboardWriter { set => timeline.WriteTimingClipboard = value; }
    internal string CopiedTimingCode => timeline.CopiedTimingCode;
    private readonly VideoExportDialog videoExport;

    private bool resetLayout;
    private string? openPopup;

    // New project dialog
    private string newName = "";
    private string newFolder = "";
    private string newBeatmap = "";
    /// <summary>The beatmap folder that was last looked into, and whether it has a difficulty to take the timing from.</summary>
    private string newBeatmapSeen = "";
    private bool newBeatmapHasOsu;
    /// <summary>
    /// The timing of the beatmap that is about to be imported, read from the difficulty that will be shown
    /// first as soon as its folder is named: listed in the window, and open to changes there. Null while
    /// no folder with a difficulty that can be read is named.
    /// </summary>
    private List<TimingPoint>? newBeatmapTiming;
    /// <summary>The file that timing was read from, how many difficulties the folder has, and whether the list was changed since.</summary>
    private string newBeatmapOsu = "";
    private int newBeatmapOsuCount;
    private bool newBeatmapTimingChanged;
    private int newTimingToShow = -1;
    private bool newDemo;
    private float newBpm = 120;
    private int newOffset;

    // Export dialog
    private readonly ExportOptions export = new();
    private ExportReport? exportReport;
    private string? exportError;
    private (List<string> Files, List<string> Missing)? exportPreview;
    private int exportPreviewRevision = -1;

    // Other dialogs
    private (string Title, string Text)? message;
    private Action? afterConfirm;
    private bool saveRequiredForVideo;
    private string importBeatmapPath = "";
    private Project? missingBeatmapProject;
    private string missingBeatmapSeen = "", missingBeatmapReplacement = "";
    /// <summary>The recent project that is about to be taken off the list once that is confirmed; null for all of them.</summary>
    private string? recentToRemove;

    /// <summary>Set once the user has confirmed that the window may close.</summary>
    public bool QuitRequested { get; private set; }

    public MainUi(EditorApp app, RenderTarget viewerTarget)
    {
        this.app = app;
        assets = new AssetsPanel(app);
        viewer = new ViewerPanel(app, viewerTarget);
        var colourPicker = new ColourPickerPopup(app);
        properties = new PropertiesPanel(app, colourPicker);
        script = new ScriptPanel(app);
        variables = new VariablesPanel(app);
        problems = new ProblemsPanel(app) { Locate = Locate };
        timeline = new TimelinePanel(app, properties.DrawCommandToolbar, colourPicker);
        timeline.TimingListRequested += () => openPopup = TimingListDialog;
        history = new HistoryPanel(app);
        plugins = new PluginsPanel(app) { LinkRequested = RequestLinkPlugin };
        viewer.SimulationLocated += Reveal;
        viewer.FullscreenRequested += SetFullscreen;
        viewer.DebugLocated += Reveal;
        properties.CommandAdded += timeline.RevealAddedCommand;
        properties.CommandAdded += viewer.KeepCommandSelection;
        videoExport = new VideoExportDialog(app, StartVideoExport);
        resetLayout = !Theme.PersistLayout || !File.Exists(AppSettings.LayoutFile);
    }

    public void Draw()
    {
        PollTimelineClipboard();
        PropertiesPanel.UpdateEffects(app);
        PollUpdates();
        // Holding Ctrl sounds the Timing ticks on every snap while the setting for it is off.
        app.HoldTimingSnapSound(ImGui.GetIO().KeyCtrl && !ImGui.GetIO().WantTextInput);
        if (viewer.Fullscreen && (app.Project == null || app.IsLoading || app.Exporting)) SetFullscreen(false);
        HandleShortcuts();
        if (!viewer.Fullscreen) DrawMenuBar();
        DrawDockHost();

        if (viewer.Fullscreen) viewer.DrawFullscreen();
        else if (app.Project == null)
        {
            DrawWelcome();
        }
        else
        {
            using (Perf.Measure("assets")) assets.Draw();
            using (Perf.Measure("viewer")) viewer.Draw();
            using (Perf.Measure("properties")) properties.Draw();
            using (Perf.Measure("script")) script.Draw();
            variables.DockBeside = script.DockId;
            using (Perf.Measure("variables")) variables.Draw();
            // In a layout saved before there was such a panel, it shows up as a tab next to the script.
            problems.DockBeside = script.DockId;
            using (Perf.Measure("problems")) problems.Draw();
            history.DockBeside = script.DockId;
            using (Perf.Measure("history")) history.Draw();
            plugins.DockBeside = script.DockId;
            using (Perf.Measure("plugins")) plugins.Draw();
            using (Perf.Measure("timeline")) timeline.Draw();
        }

        if (!viewer.Fullscreen) DrawStatusBar();
        DrawDialogs();
        if (viewer.Fullscreen) viewer.DrawTriggerSimulationWindow();
    }

    // ---------------------------------------------------------------- project actions

    /// <summary>Runs an action that would discard the open project, asking about unsaved changes first.</summary>
    private void Guard(Action action)
    {
        saveRequiredForVideo = false;
        app.ArchiveScriptDiagnostics();
        if (app.Project != null && app.Dirty)
        {
            afterConfirm = action;
            openPopup = UnsavedDialog;
        }
        else
        {
            action();
        }
    }

    /// <returns>True when the window may close right away.</returns>
    public bool RequestClose()
    {
        if (helperStart != null && !QuitRequested) return false;
        // An export is ended with its own Cancel button, not by the window going away under it.
        if (app.Exporting) return false;
        app.ArchiveScriptDiagnostics();
        if (QuitRequested || app.Project == null || !app.Dirty) return true;
        Guard(() => QuitRequested = true);
        return false;
    }

    /// <summary>Opens a dialog by its id (new, export, settings, shortcuts, about…), as the menu would.</summary>
    public void OpenDialog(string id)
    {
        switch ("###" + id)
        {
            case NewProjectDialog: ShowNewProject(false); break;
            case ExportDialog: ShowExport(); break;
            case VideoExportDialog.Id: ShowVideoExport(); break;
            default: openPopup = "###" + id; break;
        }
    }

    private void OnProjectOpened()
    {
        timeline.RequestDefaultView();
        viewer.ResetView();
    }

    private void ShowNewProject(bool demo)
    {
        newName = demo ? L.T("demo.projectName") : "";
        newFolder = app.Settings.ProjectsFolder;
        newBeatmap = "";
        newBeatmapSeen = "";
        ReadNewBeatmapTiming("");
        newDemo = demo;
        newBpm = 120;
        newOffset = 0;
        openPopup = NewProjectDialog;
    }

    private void OpenProjectDialog()
    {
        string start = Directory.Exists(app.Settings.ProjectsFolder) ? app.Settings.ProjectsFolder : "";
        var result = NativeFileDialogSharp.Dialog.FileOpen("sbproj", start.Length > 0 ? start : null);
        if (result.IsOk && !string.IsNullOrEmpty(result.Path)) OpenProject(result.Path);
    }

    /// <summary>Opens a project as the menu does: without asking about unsaved changes.</summary>
    public void OpenProject(string path) => app.OpenProject(path, OnProjectOpened);

    private void ReloadStoryboard()
    {
        if (app.Project == null || app.IsLoading || app.Exporting) return;
        string folder = app.Project.Folder;
        Guard(() =>
        {
            // Reload the files on disk without resetting the preview or timeline view.
            double time = app.Time;
            app.OpenProject(folder, () => app.Seek(time));
        });
    }

    private void ShowExport()
    {
        if (app.Project == null) return;
        var saved = app.Project.Settings.Export;
        export.FileName = string.IsNullOrWhiteSpace(saved.FileName) ? app.Project.Settings.Name + ".osb" : saved.FileName;
        export.OutputFolder = string.IsNullOrWhiteSpace(saved.Folder) ? Path.Combine(app.Project.Folder, "export") : saved.Folder;
        export.Assets = saved.Assets;
        export.IncludeAudioAndBackground = saved.IncludeAudioAndBackground;
        export.Zip = saved.Zip;
        export.Difficulties = saved.Difficulties;
        exportReport = null;
        exportError = null;
        exportPreview = null;
        openPopup = ExportDialog;
    }

    private void ShowVideoExport()
    {
        if (app.Project == null || app.IsLoading) return;
        SaveBeforeVideoExport(() =>
        {
            videoExport.Prepare();
            openPopup = VideoExportDialog.Id;
        });
    }

    private void SaveBeforeVideoExport(Action ready)
    {
        app.CommitEdits();
        if (app.VideoExportEditProblem is { } problem)
        {
            app.ShowMessage(L.T("dialog.videoExport.title"), problem);
            return;
        }
        if (!app.VideoExportNeedsSave) { ready(); return; }
        app.Pause();
        saveRequiredForVideo = true;
        afterConfirm = ready;
        openPopup = UnsavedDialog;
    }

    private string? StartVideoExport(VideoExportRequest request)
    {
        if(!CheckPluginExport(()=>StartVideoExport(request))) return null;
        if (app.VideoExportEditProblem is { } problem) return problem;
        if (!app.VideoExportNeedsSave) return app.StartVideoExport(request);
        // Check again at the Start button: referenced beatmap changes may have dirtied the project meanwhile.
        SaveBeforeVideoExport(() =>
        {
            if (app.StartVideoExport(request) is { } failure)
                app.ShowMessage(L.T("dialog.videoExport.title"), failure);
        });
        return null;
    }

    private void ImportBeatmapDialog()
    {
        var result = NativeFileDialogSharp.Dialog.FolderPicker(null);
        if (!result.IsOk || string.IsNullOrEmpty(result.Path)) return;
        importBeatmapPath = result.Path;
        openPopup = ImportDialog;
    }

    public void OnFilesDropped(string[] paths)
    {
        if (helperStart != null) return;
        if (paths.Length == 0 || app.IsLoading) return;
        var skinFiles = paths.Where(p => p.EndsWith(".osk", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (skinFiles.Length > 0)
        {
            foreach (string file in skinFiles) viewer.ImportSkin(file);
            return;
        }
        string? projectFile = paths.FirstOrDefault(p =>
            p.EndsWith(".sbproj", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(p, Project.ProjectFileName)));
        if (projectFile != null)
        {
            Guard(() => OpenProject(projectFile));
            return;
        }

        if (app.Project == null)
        {
            app.ShowMessage(L.T("msg.noProject.title"), L.T("msg.noProject.text"));
            return;
        }

        // A beatmap folder, or one of the files that make a folder one.
        string? beatmap = paths.FirstOrDefault(p =>
            p.EndsWith(".osu", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".osb", StringComparison.OrdinalIgnoreCase) ||
            HasBeatmapFiles(p, "*.osu") || HasBeatmapFiles(p, "*.osb"));
        if (beatmap != null)
        {
            importBeatmapPath = beatmap;
            openPopup = ImportDialog;
            return;
        }
        assets.Reveal(app.ImportFiles(paths));
    }

    /// <summary>Whether a path is a folder with files of a kind in it. Never throws: the path may be anything a user typed.</summary>
    private static bool HasBeatmapFiles(string path, string pattern)
    {
        try
        {
            return Directory.Exists(path) && Directory.EnumerateFiles(path, pattern).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The group menu commands act on: the selected one, or with nothing selected the one that is open.</summary>
    private SbObjectGroup? ActiveGroup() =>
        app.Project == null ? null : app.SelectedGroup ?? (app.Selection.Count == 0 && app.SelectedGroups.Count == 0 ? app.OpenGroup : null);

    // ---------------------------------------------------------------- shortcuts

    private void HandleShortcuts()
    {
        if (helperStart != null) return;
        if (app.IsLoading) return;
        var io = ImGui.GetIO();
        bool ctrl = io.KeyCtrl, shift = io.KeyShift;
        bool modal = ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel);

        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.S, false)) SaveProject();
        if (modal)
        {
            // Keyboard navigation is disabled, so dismiss non-modal popups explicitly before handling full-screen exit.
            if (viewer.Fullscreen && !io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Escape, false)) igClosePopupsExceptModals();
            return;
        }

        bool noModifiers = !ctrl && !shift && !io.KeyAlt && !io.KeySuper;
        if (!io.WantTextInput && app.Project != null && !app.Exporting && noModifiers && ImGui.IsKeyPressed(ImGuiKey.F11, false))
        { SetFullscreen(!viewer.Fullscreen); return; }
        if (viewer.Fullscreen && !io.WantTextInput && noModifiers)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false)) { SetFullscreen(false); return; }
            if (ImGui.IsKeyPressed(ImGuiKey.Tab, false)) viewer.SetDebugPanel(!app.Settings.FullscreenDebugPanel);
        }
        if (!viewer.Fullscreen && ctrl && ImGui.IsKeyPressed(ImGuiKey.N, false)) Guard(() => ShowNewProject(false));
        if (!viewer.Fullscreen && ctrl && ImGui.IsKeyPressed(ImGuiKey.O, false)) Guard(OpenProjectDialog);
        if (app.Project == null || io.WantTextInput) return;

        if (!viewer.Fullscreen && !ctrl && !shift && !io.KeyAlt && app.Settings.ShowTimingRecheck && ImGui.IsKeyPressed(ImGuiKey.F6, false))
        {
            openPopup = TimingListDialog;
            return;
        }
        if (!viewer.Fullscreen && !ctrl && !shift && !io.KeyAlt && app.Settings.ShowTimingRecheck && ImGui.IsKeyPressed(ImGuiKey.R, false))
        {
            timeline.ToggleTimingOffset();
            return;
        }
        if (!viewer.Fullscreen && !ctrl && !shift && !io.KeyAlt && app.Settings.ShowTimingRecheck && ImGui.IsKeyPressed(ImGuiKey.C, false))
        {
            timeline.OpenTimingCalculator();
            return;
        }

        if (!viewer.Fullscreen && ctrl && ImGui.IsKeyPressed(ImGuiKey.E, false)) ShowExport();
        if (!viewer.Fullscreen && ctrl && ImGui.IsKeyPressed(ImGuiKey.L, false)) ReloadStoryboard();
        if (!viewer.Fullscreen || app.Settings.FullscreenEditing)
        {
            if (ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.Z, true)) app.Undo();
            if (ctrl && (ImGui.IsKeyPressed(ImGuiKey.Y, true) || (shift && ImGui.IsKeyPressed(ImGuiKey.Z, true)))) app.Redo();
            if (!viewer.Fullscreen && timeline.HandleHitObjectShortcuts(io)) return;
            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.A, false)) app.SelectAll();
            bool selected = app.Selection.Count > 0 || app.SelectedGroups.Count > 0;
            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.C, false) && selected) ImGui.SetClipboardText(app.CopyScript());
            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.X, false) && selected)
            {
                ImGui.SetClipboardText(app.CopyScript());
                app.ClearKeySelection();
                app.DeleteSelection();
            }
            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.V, false)) app.PasteScript(ImGui.GetClipboardText(), shift);
            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.D, false)) app.DuplicateSelection();
            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.G, false))
            {
                if (!shift) app.GroupSelection();
                else if (ActiveGroup() is { } group) app.Ungroup(group, true);
            }
            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.RightBracket, true))
            {
                if (shift) app.MoveSelectionToEdge(true);
                else app.MoveSelection(1);
            }
            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.LeftBracket, true))
            {
                if (shift) app.MoveSelectionToEdge(false);
                else app.MoveSelection(-1);
            }
            if (ImGui.IsKeyPressed(ImGuiKey.Delete, false)) app.DeleteSelection();
            if (!viewer.Fullscreen && ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            {
                // First what is selected is let go of; with nothing selected, one step out of the group that is open.
                if (selected || app.SelectedKeys.Count > 0 || app.SelectedCommands.Count > 0 || app.ActiveTrack != null || app.SelectedBlock != null) app.ClearSelection();
                else app.LeaveGroup();
            }
        }

        // From keyframe to keyframe, of the property that is picked or of what is selected.
        if (ctrl && !shift)
        {
            int toKey = (ImGui.IsKeyPressed(ImGuiKey.RightArrow, true) ? 1 : 0) - (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, true) ? 1 : 0);
            if (toKey != 0) app.JumpKey(toKey);
        }

        if (ctrl) return;
        if (ImGui.IsKeyPressed(ImGuiKey.Space, false)) app.TogglePlay();
        if (ImGui.IsKeyPressed(ImGuiKey.Home, false)) app.SeekToStart();
        if (ImGui.IsKeyPressed(ImGuiKey.End, false)) app.SeekSnapped(app.TimeBounds.End);

        // Arrow keys move the playhead from snap tick to snap tick (Shift: four at a time), unless the
        // preview has focus and uses them to nudge sprites.
        if (!viewer.Focused || (viewer.Fullscreen && (!app.Settings.FullscreenEditing || (app.Selection.Count == 0 && app.SelectedGroups.Count == 0))))
        {
            int direction = (ImGui.IsKeyPressed(ImGuiKey.RightArrow, true) ? 1 : 0) - (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, true) ? 1 : 0);
            if (direction != 0) app.StepTime(direction * (shift ? 4 : 1));
        }
    }

    // ---------------------------------------------------------------- menu bar

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMainMenuBar()) return;
        bool has = app.Project != null;

        if (UiScriptTargets.Item("menu.file", ImGui.BeginMenu(L.T("menu.file"))))
        {
            if (ImGui.MenuItem(L.T("menu.file.new"), "Ctrl+N")) Guard(() => ShowNewProject(false));
            if (ImGui.MenuItem(L.T("menu.file.newDemo"))) Guard(() => ShowNewProject(true));
            if (ImGui.MenuItem(L.T("menu.file.open"), "Ctrl+O")) Guard(OpenProjectDialog);
            if (UiScriptTargets.Item("menu.file.recent", ImGui.BeginMenu(L.T("menu.file.recent"), app.Settings.RecentProjects.Count > 0)))
            {
                foreach (string recent in app.Settings.RecentProjects.ToArray())
                    if (UiScriptTargets.Item("recent.menu." + recent, ImGui.MenuItem(recent))) Guard(() => OpenProject(recent));
                ImGui.Separator();
                if (ImGui.MenuItem(L.T("recent.clearList"))) AskRemoveRecent(null);
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem(L.T("common.save"), "Ctrl+S", false, has)) SaveProject();
            if (ImGui.MenuItem(L.T("menu.file.restoreAutosave"), null, false, has && app.HasAutosave)) app.RestoreAutosave();
            Theme.Tooltip(L.T("menu.file.restoreAutosave.tip"));
            ImGui.Separator();
            if (ImGui.MenuItem(L.T("menu.file.import"), null, false, has)) ImportBeatmapDialog();
            if (ImGui.MenuItem(L.T("menu.file.export"), "Ctrl+E", false, has)) ShowExport();
            if (ImGui.MenuItem(L.T("menu.file.exportVideo"), null, false, has)) ShowVideoExport();
            ImGui.Separator();
            if (ImGui.MenuItem(L.T("menu.file.settings"), null, false, has)) openPopup = SettingsDialog;
            if (ImGui.MenuItem(L.T("menu.file.openFolder"), null, false, has)) AssetsPanel.OpenInExplorer(app.Project!.Folder, false);
            if (ImGui.MenuItem(L.T("menu.file.close"), null, false, has)) Guard(app.CloseProject);
            ImGui.Separator();
            if (ImGui.MenuItem(L.T("menu.file.exit"))) Guard(() => QuitRequested = true);
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu(L.T("menu.edit"), has))
        {
            if (ImGui.MenuItem(L.T("menu.edit.undo"), "Ctrl+Z", false, app.CanUndo)) app.Undo();
            if (ImGui.MenuItem(L.T("menu.edit.redo"), "Ctrl+Y", false, app.CanRedo)) app.Redo();
            ImGui.Separator();
            Menus.ObjectItems(app);
            ImGui.Separator();
            if (ImGui.MenuItem(L.T("menu.edit.selectAll"), "Ctrl+A")) app.SelectAll();
            if (ImGui.MenuItem(L.T("menu.edit.deselect"), "Esc")) app.ClearSelection();
            ImGui.Separator();
            if (ImGui.MenuItem(L.T("menu.edit.reloadStoryboard"), "Ctrl+L", false, !app.IsLoading && !app.Exporting)) ReloadStoryboard();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu(L.T("menu.object"), has))
        {
            if (ImGui.MenuItem(L.T("menu.object.newSprite"))) app.AddSprite("", 320, 240);
            Theme.Tooltip(L.T("menu.object.newSprite.tip"));
            if (ImGui.MenuItem(L.T("menu.object.newSample"))) app.AddSample("");
            ImGui.Separator();
            if (ImGui.MenuItem(L.T("edit.group"), "Ctrl+G", false, Menus.CanGroup(app))) app.GroupSelection();
            Theme.Tooltip(L.T("edit.group.tip"));
            var activeGroup = ActiveGroup();
            if(activeGroup?.Plugin!=null && ImGui.MenuItem(L.T("plugin.detach"))) app.DetachPlugin(activeGroup);
            if (activeGroup?.Plugin==null && ImGui.MenuItem(L.T("group.ungroupKeep"), "Ctrl+Shift+G", false, activeGroup != null)) app.Ungroup(activeGroup!, true);
            if (activeGroup?.Plugin==null && ImGui.MenuItem(L.T("group.ungroupDiscard"), null, false, activeGroup != null)) app.Ungroup(activeGroup!, false);
            ImGui.Separator();
            bool anySprite = app.Selection.Any(o => o is SbSprite);
            if (ImGui.MenuItem(L.T("menu.object.expand"), null, false, anySprite))
            {
                foreach (var obj in app.Selection) obj.Expanded = true;
                app.TouchFolds();
            }
            if (ImGui.MenuItem(L.T("menu.object.collapseAll")))
            {
                foreach (var obj in app.Doc.Objects) obj.Expanded = false;
                app.TouchFolds();
            }
            ImGui.EndMenu();
        }

        if (UiScriptTargets.Item("menu.view", ImGui.BeginMenu(L.T("menu.view"))))
        {
            ImGui.MenuItem(L.T("panel.assets"), null, ref assets.Open);
            ImGui.MenuItem(L.T("panel.viewer"), null, ref viewer.Open);
            ImGui.MenuItem(L.T("panel.properties"), null, ref properties.Open);
            ImGui.MenuItem(L.T("panel.script"), null, ref script.Open);
            if (UiScriptTargets.Item("menu.variables", ImGui.MenuItem(L.T("panel.variables"), null, ref variables.Open)) && variables.Open)
                variables.FocusRequested = true;
            ImGui.MenuItem(L.T("panel.problems"), null, ref problems.Open);
            ImGui.MenuItem(L.T("panel.timeline"), null, ref timeline.Open);
            ImGui.MenuItem(L.T("panel.history"), null, ref history.Open);
            ImGui.MenuItem(L.T("panel.plugins"), null, ref plugins.Open);
            ImGui.Separator();
            if (ImGui.MenuItem(L.T("menu.view.resetLayout")))
            {
                assets.Open = viewer.Open = properties.Open = script.Open = problems.Open = timeline.Open = history.Open = plugins.Open = true;
                variables.Open = true;
                resetLayout = true;
            }
            if (ImGui.MenuItem(L.T("menu.view.fitPreview"), null, false, has)) viewer.ResetView();
            if (ImGui.MenuItem(L.T("menu.view.fitTimeline"), null, false, has)) timeline.RequestFit();
            ImGui.Separator();
            bool vsync = app.Settings.VSync;
            if (ImGui.MenuItem(L.T("menu.view.vsync"), null, ref vsync))
            {
                app.Settings.VSync = vsync;
                app.Settings.Save();
            }
            Theme.Tooltip(L.T("menu.view.vsync.tip"));
            bool video = app.Settings.ShowVideo;
            if (ImGui.MenuItem(L.T("menu.view.video"), null, ref video))
            {
                app.Settings.ShowVideo = app.RenderOptions.ShowVideo = video;
                app.Settings.Save();
            }
            Theme.Tooltip(L.T("menu.view.video.tip"));
            if (ImGui.BeginMenu(L.T("menu.view.uiScale")))
            {
                foreach (float scale in new[] { 0.85f, 1f, 1.15f, 1.25f, 1.5f, 1.75f, 2f })
                {
                    if (ImGui.MenuItem($"{scale * 100:0}%", null, Math.Abs(app.Settings.UiScale - scale) < 0.01f))
                    {
                        SetUiScale(scale);
                    }
                }
                ImGui.EndMenu();
            }
            Theme.Tooltip(L.T("menu.view.uiScale.note"));
            if (ImGui.BeginMenu(L.T("menu.view.language")))
            {
                string system = L.Languages.First(l => l.Code == UiLanguage.SystemCode).Name;
                if (ImGui.MenuItem(L.T("menu.view.languageSystem", system), null, app.Settings.Language.Length == 0)) SetLanguage("");
                ImGui.Separator();
                foreach (var language in L.Languages)
                    if (ImGui.MenuItem(language.Name + "##" + language.Code, null, L.Match(app.Settings.Language) == language.Code)) SetLanguage(language.Code);
                ImGui.EndMenu();
            }
            ImGui.EndMenu();
        }

        if (UiScriptTargets.Item("menu.preferences", ImGui.BeginMenu(L.T("menu.preferences"))))
        {
            if (ImGui.MenuItem(L.T("menu.preferences.seamlessAudio"), null, app.Settings.SeamlessPlaybackRefresh))
            {
                app.SetSeamlessPlaybackRefresh(!app.Settings.SeamlessPlaybackRefresh);
                app.Settings.Save();
            }
            Theme.Tooltip(L.T("menu.preferences.seamlessAudio.tip"));
            if (UiScriptTargets.Item("preferences.soundOffset", ImGui.MenuItem(L.T("menu.preferences.soundOffset")))) ShowSoundOffset();
            if (ImGui.MenuItem(L.T("menu.preferences.freeMode"), null, app.Settings.FreeMode))
            {
                app.Settings.FreeMode = !app.Settings.FreeMode;
                app.Settings.Save();
            }
            Theme.Tooltip(L.T("menu.preferences.freeMode.tip"));
            if (ImGui.MenuItem(L.T("menu.preferences.autoScaleImages"), null, app.Settings.AutoScaleAddedImages))
            {
                app.Settings.AutoScaleAddedImages = !app.Settings.AutoScaleAddedImages;
                app.Settings.Save();
            }
            Theme.Tooltip(L.T("menu.preferences.autoScaleImages.tip"));
            if (UiScriptTargets.Item("preferences.outlineScope", ImGui.BeginMenu(L.T("menu.preferences.outlineScope"))))
            {
                if (UiScriptTargets.Item("preferences.outlineScope.group", ImGui.MenuItem(L.T("menu.preferences.outlineScope.group"), null,
                    app.Settings.OutlineScope == GroupOutlineScope.Group)))
                {
                    app.Settings.OutlineScope = GroupOutlineScope.Group;
                    app.Settings.Save();
                }
                if (UiScriptTargets.Item("preferences.outlineScope.members", ImGui.MenuItem(L.T("menu.preferences.outlineScope.members"), null,
                    app.Settings.OutlineScope == GroupOutlineScope.Members)))
                {
                    app.Settings.OutlineScope = GroupOutlineScope.Members;
                    app.Settings.Save();
                }
                ImGui.EndMenu();
            }
            if (UiScriptTargets.Item("preferences.timelineAutoJump", ImGui.MenuItem(L.T("menu.preferences.timelineAutoJump"), null, app.Settings.TimelineAutoJump)))
            {
                app.Settings.TimelineAutoJump = !app.Settings.TimelineAutoJump;
                ResetTimelineClipboard();
                app.Settings.Save();
            }
            Theme.Tooltip(L.T("menu.preferences.timelineAutoJump.tip"));
            if (OperatingSystem.IsWindows() && ImGui.MenuItem(L.T("menu.preferences.openProjectFiles"), null, app.Settings.OpenProjectFiles))
            {
                app.Settings.OpenProjectFiles = !app.Settings.OpenProjectFiles;
                app.Settings.Save();
                Program.ApplyFileAssociation(app.Settings);
            }
            Theme.Tooltip(L.T("menu.preferences.openProjectFiles.tip"));
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu(L.T("menu.help")))
        {
            if (ImGui.MenuItem(L.T("menu.help.shortcuts"))) openPopup = ShortcutsDialog;
            if (ImGui.MenuItem(L.T("update.title"))) ShowUpdates();
            if (ImGui.MenuItem(L.T("menu.help.about"))) openPopup = AboutDialog;
            ImGui.EndMenu();
        }

        ImGui.EndMainMenuBar();
    }

    /// <param name="code">A language code, or empty to follow the system.</param>
    private void SetLanguage(string code)
    {
        app.Settings.Language = code;
        app.Settings.Save();
        UiLanguage.Apply(code);
    }

    public void SetUiScale(float scale)
    {
        if (!float.IsFinite(scale)) return;
        scale = Math.Clamp(scale, 0.75f, 2.5f);
        if (Math.Abs(app.Settings.UiScale - scale) < 0.001f) return;
        app.Settings.UiScale = scale;
        app.Settings.Save();
    }

    // ---------------------------------------------------------------- dock space

    private void DrawDockHost()
    {
        var viewport = ImGui.GetMainViewport();
        float statusHeight = ImGui.GetFrameHeight() + 2;
        var size = new Vector2(viewport.WorkSize.X, viewport.WorkSize.Y - statusHeight);
        if (!viewer.Fullscreen) { dockPosition = viewport.WorkPos; dockSize = size; }
        else size = dockSize;

        ImGui.SetNextWindowPos(dockPosition);
        ImGui.SetNextWindowSize(size);
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.Begin("##dockhost", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize |
                                  ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus |
                                  ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings);
        ImGui.PopStyleVar(3);

        uint dockId = ImGui.GetID("MainDockSpace");
        if (!viewer.Fullscreen && resetLayout && size.X > 100 && size.Y > 100)
        {
            resetLayout = false;
            DockBuilder.Reset(dockId, size);
            uint centre = dockId;
            uint bottom = DockBuilder.Split(centre, ImGuiDir.Down, 0.38f, out centre);
            uint left = DockBuilder.Split(centre, ImGuiDir.Left, 0.19f, out centre);
            uint right = DockBuilder.Split(centre, ImGuiDir.Right, 0.32f, out centre);
            uint rightBottom = DockBuilder.Split(right, ImGuiDir.Down, 0.42f, out right);
            DockBuilder.Dock(AssetsWindow, left);
            DockBuilder.Dock(ViewerWindow, centre);
            DockBuilder.Dock(PropertiesWindow, right);
            DockBuilder.Dock(ScriptWindow, rightBottom);
            DockBuilder.Dock(VariablesPanel.WindowId, rightBottom);
            DockBuilder.Dock(ProblemsWindow, rightBottom);
            DockBuilder.Dock(HistoryWindow, rightBottom);
            DockBuilder.Dock(PluginsWindow, rightBottom);
            DockBuilder.Dock(TimelineWindow, bottom);
            DockBuilder.Finish(dockId);
        }
        ImGui.DockSpace(dockId, Vector2.Zero, viewer.Fullscreen ? ImGuiDockNodeFlags.KeepAliveOnly : ImGuiDockNodeFlags.None);
        ImGui.End();
    }

    private void DrawWelcome()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(Theme.Px(560), 0));
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.Begin("##welcome", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking);

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
        ImGui.TextUnformatted("osu! Storyboard Studio");
        ImGui.PopStyleColor();
        Theme.TextDimmed(L.T("welcome.intro"));
        ImGui.Spacing();

        var button = new Vector2(Theme.Px(170), Theme.Px(34));
        if (ImGui.Button(L.T("menu.file.new"), button)) ShowNewProject(false);
        ImGui.SameLine();
        if (ImGui.Button(L.T("menu.file.open"), button)) OpenProjectDialog();
        ImGui.SameLine();
        if (ImGui.Button(L.T("menu.file.newDemo"), button)) ShowNewProject(true);

        if (app.Settings.RecentProjects.Count > 0)
        {
            ImGui.Spacing();
            float right = ImGui.GetWindowContentRegionMax().X;
            var style = ImGui.GetStyle();
            string clear = L.T("recent.clearList");
            float clearWidth = Theme.ButtonWidth(clear, 90);
            float headerY = ImGui.GetCursorScreenPos().Y + ImGui.GetFrameHeight() / 2;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(L.T("welcome.recent"));
            ImGui.SameLine();
            float dividerEnd = ImGui.GetWindowPos().X + right - clearWidth - style.ItemSpacing.X;
            if (dividerEnd > ImGui.GetCursorScreenPos().X)
                ImGui.GetWindowDrawList().AddLine(new Vector2(ImGui.GetCursorScreenPos().X, headerY),
                    new Vector2(dividerEnd, headerY), ImGui.GetColorU32(ImGuiCol.Separator));
            ImGui.SetCursorPosX(right - clearWidth);
            if (UiScriptTargets.Item("recent.clear", Theme.Button(clear, 90))) AskRemoveRecent(null);
            Theme.Tooltip(L.T("recent.clearList.tip"));

            float remove = ImGui.CalcTextSize("×").X + style.FramePadding.X * 2;
            float rowHeight = ImGui.GetFrameHeight();
            float availableHeight = MathF.Max(rowHeight, viewport.WorkSize.Y - Theme.Px(32) - ImGui.GetCursorPosY() - style.WindowPadding.Y);
            float listHeight = MathF.Min(app.Settings.RecentProjects.Count * (rowHeight + style.ItemSpacing.Y) - style.ItemSpacing.Y, availableHeight);
            ImGui.BeginChild("##recentProjects", new Vector2(0, listHeight));
            foreach (string recent in app.Settings.RecentProjects.ToArray())
            {
                ImGui.PushID(recent);
                var position = ImGui.GetCursorScreenPos();
                float rowWidth = ImGui.GetContentRegionAvail().X - remove - style.ItemSpacing.X;
                if (UiScriptTargets.Item("recent.open." + recent, ImGui.Selectable("##open", false, ImGuiSelectableFlags.None, new Vector2(rowWidth, rowHeight)))) OpenProject(recent);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                {
                    float tooltipWidth = MathF.Min(Theme.Px(600), viewport.Size.X - Theme.Px(32));
                    var mouse = ImGui.GetMousePos();
                    float tooltipRight = viewport.Pos.X + viewport.Size.X - tooltipWidth - style.WindowPadding.X * 2 - Theme.Px(8);
                    if (mouse.X + Theme.Px(16) > tooltipRight)
                        ImGui.SetNextWindowPos(new Vector2(MathF.Max(viewport.Pos.X + Theme.Px(8), tooltipRight), mouse.Y + Theme.Px(24)));
                    ImGui.BeginTooltip();
                    ImGui.PushTextWrapPos(tooltipWidth);
                    ImGui.TextUnformatted(recent);
                    ImGui.PopTextWrapPos();
                    ImGui.EndTooltip();
                }
                float textWidth = MathF.Max(0, rowWidth - Theme.Px(4));
                string name = EllipsizeRecentText(Path.GetFileName(recent), textWidth * 0.55f);
                float pathX = ImGui.CalcTextSize(name).X + style.ItemSpacing.X;
                string path = EllipsizeRecentText(recent, textWidth - pathX);
                var drawList = ImGui.GetWindowDrawList();
                drawList.PushClipRect(position, position + new Vector2(rowWidth, rowHeight), true);
                float textY = position.Y + (rowHeight - ImGui.GetTextLineHeight()) / 2;
                drawList.AddText(new Vector2(position.X, textY), ImGui.GetColorU32(ImGuiCol.Text), name);
                drawList.AddText(new Vector2(position.X + pathX, textY), Theme.TextDim, path);
                drawList.PopClipRect();
                ImGui.SameLine();
                if (UiScriptTargets.Item("recent.remove." + recent, ImGui.Button("×", new Vector2(remove, rowHeight)))) AskRemoveRecent(recent);
                Theme.Tooltip(L.T("recent.remove.tip"));
                ImGui.PopID();
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private static string EllipsizeRecentText(string text, float width)
    {
        if (width <= 0) return "";
        if (ImGui.CalcTextSize(text).X <= width) return text;
        const string ellipsis = "...";
        float available = width - ImGui.CalcTextSize(ellipsis).X;
        if (available < 0) return "";
        int low = 0, high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..middle]).X <= available) low = middle;
            else high = middle - 1;
        }
        if (low > 0 && char.IsHighSurrogate(text[low - 1])) low--;
        return text[..low] + ellipsis;
    }

    /// <summary>Asks before a project, or with null every project, is taken off the list of recent ones.</summary>
    private void AskRemoveRecent(string? project)
    {
        recentToRemove = project;
        openPopup = RecentDialog;
    }

    private void DrawRecentDialog(Vector2 centre)
    {
        if (!BeginModal(L.T("dialog.recent.title") + RecentDialog, centre, 500)) return;
        var recents = app.Settings.RecentProjects;
        if (recentToRemove != null)
        {
            Theme.TextWrapped(L.T("recent.removeOne"));
            Theme.TextDimmed(recentToRemove);
        }
        else
        {
            Theme.TextWrapped(L.T("recent.removeAll", recents.Count));
        }
        ImGui.Spacing();
        Theme.TextDimmed(L.T("recent.note"));
        ImGui.Spacing();

        if (UiScriptTargets.Item("recent.confirm", Theme.Button(L.T(recentToRemove != null ? "recent.remove" : "recent.clear"), 120)))
        {
            if (recentToRemove != null) recents.RemoveAll(p => string.Equals(p, recentToRemove, StringComparison.OrdinalIgnoreCase));
            else recents.Clear();
            app.Settings.Save();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (UiScriptTargets.Item("recent.cancel", Theme.Button(L.T("common.cancel"), 120))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawStatusBar()
    {
        var viewport = ImGui.GetMainViewport();
        float height = ImGui.GetFrameHeight() + 2;
        ImGui.SetNextWindowPos(new Vector2(viewport.WorkPos.X, viewport.WorkPos.Y + viewport.WorkSize.Y - height));
        ImGui.SetNextWindowSize(new Vector2(viewport.WorkSize.X, height));
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 3));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.PanelDark);
        ImGui.Begin("##status", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
                                ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);

        bool recent = app.Status.Length > 0 && (DateTime.Now - app.StatusAt).TotalSeconds < 8;
        if (recent) ImGui.TextUnformatted(app.Status);
        else if (app.Audio.Error != null) ImGui.TextColored(new Vector4(1, 0.45f, 0.45f, 1), Theme.EscapeFormat(app.Audio.Error));
        else Theme.TextDimmed(app.Project == null
            ? L.T("status.ready")
            : L.T("status.hint"));

        if (app.Project != null)
        {
            string right = L.T("status.summary", app.Doc.Objects.Count, L.T(app.Dirty ? "status.unsaved" : "status.saved"));
            ImGui.SameLine(ImGui.GetWindowWidth() - ImGui.CalcTextSize(right).X - 12);
            Theme.TextDimmed(right);
        }
        ImGui.End();
    }

    // ---------------------------------------------------------------- dialogs

    private void DrawDialogs()
    {
        if (app.Project is { BeatmapReferenceMissing: true } linked && !app.IsLoading && openPopup == null &&
            !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel) &&
            (missingBeatmapProject != linked || missingBeatmapSeen != linked.Settings.BeatmapFolder))
        {
            missingBeatmapProject = linked;
            missingBeatmapSeen = linked.Settings.BeatmapFolder;
            missingBeatmapReplacement = "";
            openPopup = MissingBeatmapDialog;
        }
        else if (app.Project is not { BeatmapReferenceMissing: true }) missingBeatmapProject = null;
        // What opening a project has to report waits until the loading screen is gone.
        if (app.PendingMessage is { } pending && openPopup == null && !app.IsLoading && !ImGui.IsPopupOpen(MessageDialog))
        {
            message = pending;
            app.PendingMessage = null;
            openPopup = MessageDialog;
        }

        if (openPopup != null)
        {
            ImGui.OpenPopup(openPopup);
            openPopup = null;
        }

        var centre = ImGui.GetMainViewport().GetCenter();

        DrawPluginExportDialog(centre);
        DrawLinkPluginDialog(centre);
        DrawNewProjectDialog(centre);
        DrawExportDialog(centre);
        videoExport.Draw(centre);
        DrawSoundOffsetDialog(centre);
        DrawProjectSettingsDialog(centre);
        DrawTimingListDialog(centre);
        DrawImportBeatmapDialog(centre);
        DrawMissingBeatmapDialog(centre);
        DrawConfirmDialog(centre);
        DrawClashDialog(centre);
        DrawRecentDialog(centre);
        DrawInfoDialogs(centre);
        DrawUpdateDialog(centre);
        DrawLoading(centre);
    }

    /// <summary>Shown from the moment a project is opened until its storyboard and all its images are in.</summary>
    private void DrawLoading(Vector2 centre)
    {
        var loading = app.Loading;
        if (loading != null && !ImGui.IsPopupOpen(LoadingDialog)) ImGui.OpenPopup(LoadingDialog);

        ImGui.SetNextWindowPos(centre, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(Theme.Px(460), 0));
        // No close button: there is nothing to do but wait.
        if (!ImGui.BeginPopupModal(L.T("dialog.loading.title") + LoadingDialog)) return;
        if (loading is { } state)
        {
            ImGui.TextUnformatted(state.Name);
            Theme.TextDimmed(state.Operation ?? (state.Images ? L.T("loading.images", state.Done, state.Total) : L.T("loading.storyboard")));
            ImGui.ProgressBar(state.Fraction, new Vector2(-1, 0), $"{state.Fraction * 100:0}%");
        }
        else
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    internal static bool BeginModal(string name, Vector2 centre, float width, bool canClose = true)
    {
        // Fixed width, height follows the content (wrapped paths and error lists change it while open).
        float w = Theme.Px(width);
        var viewport = ImGui.GetMainViewport();
        float maxHeight = viewport.WorkSize.Y * 0.92f;
        float top = centre.Y - maxHeight * 0.36f;
        // Whatever it comes to hold, it ends where the screen does: past that it scrolls.
        maxHeight = MathF.Max(Theme.Px(200), MathF.Min(maxHeight, ScreenBottom(viewport) - top - Theme.Px(8)));
        ImGui.SetNextWindowPos(new Vector2(centre.X, top), ImGuiCond.Appearing, new Vector2(0.5f, 0f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(w, 0), new Vector2(w, maxHeight));
        bool open = true;
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize;
        return canClose ? ImGui.BeginPopupModal(name, ref open, flags) : ImGui.BeginPopupModal(name, flags);
    }

    /// <summary>
    /// Where the room for a dialog ends below. A dialog is a window of its own and may reach beyond the
    /// main window, so that is the end of the work area of the screen the main window is on.
    /// </summary>
    private static float ScreenBottom(ImGuiViewportPtr viewport)
    {
        float bottom = viewport.WorkPos.Y + viewport.WorkSize.Y;
        if ((ImGui.GetIO().ConfigFlags & ImGuiConfigFlags.ViewportsEnable) == 0) return bottom;
        Vector2 middle = viewport.GetCenter();
        var monitors = ImGui.GetPlatformIO().Monitors;
        for (int i = 0; i < monitors.Size; i++)
        {
            var monitor = monitors[i];
            if (middle.X >= monitor.MainPos.X && middle.X < monitor.MainPos.X + monitor.MainSize.X &&
                middle.Y >= monitor.MainPos.Y && middle.Y < monitor.MainPos.Y + monitor.MainSize.Y)
                return MathF.Max(bottom, monitor.WorkPos.Y + monitor.WorkSize.Y);
        }
        return bottom;
    }

    internal static bool FolderField(string label, ref string folder)
    {
        bool changed = false;
        string browse = L.T("common.browse");
        float button = Theme.ButtonWidth(browse, 70);
        ImGui.SetNextItemWidth(-button - ImGui.GetStyle().ItemSpacing.X);
        if (ImGui.InputText("##" + label, ref folder, 512)) changed = true;
        ImGui.SameLine();
        if (ImGui.Button(browse + "##" + label, new Vector2(button, 0)))
        {
            var result = NativeFileDialogSharp.Dialog.FolderPicker(Directory.Exists(folder) ? folder : null);
            if (result.IsOk && !string.IsNullOrEmpty(result.Path))
            {
                folder = result.Path;
                changed = true;
            }
        }
        return changed;
    }

    private void DrawNewProjectDialog(Vector2 centre)
    {
        if (!BeginModal(L.T("dialog.new.title") + NewProjectDialog, centre, 560)) return;

        ImGui.TextUnformatted(L.T("project.name"));
        ImGui.SetNextItemWidth(-1);
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.InputText("##name", ref newName, 128);

        ImGui.TextUnformatted(L.T("new.location"));
        FolderField("folder", ref newFolder);
        string target = newName.Trim().Length > 0 ? Path.Combine(newFolder, newName.Trim()) : newFolder;
        Theme.TextDimmed(L.T("new.willCreate", target));

        ImGui.Spacing();
        ImGui.Checkbox(L.T("new.demo"), ref newDemo);

        ImGui.Spacing();
        ImGui.TextUnformatted(L.T("new.fromBeatmap"));
        string browse = L.T("common.browse");
        float button = Theme.ButtonWidth(browse, 70);
        ImGui.SetNextItemWidth(-button - ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputTextWithHint("##beatmap", L.T("new.beatmapHint"), ref newBeatmap, 512);
        ImGui.SameLine();
        if (ImGui.Button(browse + "##beatmap", new Vector2(button, 0)))
        {
            var result = NativeFileDialogSharp.Dialog.FolderPicker(Directory.Exists(newBeatmap) ? newBeatmap : null);
            if (result.IsOk && !string.IsNullOrEmpty(result.Path)) newBeatmap = result.Path;
        }
        Theme.TextDimmed(L.T("new.beatmapNote"));

        // The snap grid needs a BPM; a demo or an imported beatmap brings its own. A typed path may also
        // name a file of the folder.
        if (newBeatmap != newBeatmapSeen)
        {
            newBeatmapSeen = newBeatmap;
            ReadNewBeatmapTiming(newBeatmap.Trim());
        }
        ImGui.Spacing();
        ImGui.TextUnformatted(L.T("new.timing"));
        if (newBeatmapTiming is { } imported)
        {
            // The timing of the beatmap, there to be seen and changed the moment its folder is named.
            bool edited = TimingTable("newtiming", imported, NewTimingRowsInView, ref newTimingToShow);
            if (ImGui.Button(L.T("settings.addSection")))
            {
                // After the last one, four bars on, with its tempo: a place to start from.
                var last = imported.Count > 0 ? imported[^1] : null;
                var added = new TimingPoint { Offset = last == null ? 0 : Math.Round(last.Offset + last.BeatLength * last.Meter * 4), BeatLength = last?.BeatLength ?? 500, Meter = last?.Meter ?? 4 };
                imported.Add(added);
                newTimingToShow = imported.Count - 1;
                edited = true;
            }
            ImGui.SameLine(0, Theme.Px(12));
            ImGui.AlignTextToFramePadding();
            Theme.TextDimmed(L.T("settings.timingCount", imported.Count));
            if (edited)
            {
                newBeatmapTimingChanged = true;
                // Kept in order once the user lets go, so that dragging an offset does not reshuffle rows mid-drag.
                if (!ImGui.IsAnyItemActive()) imported.Sort((x, y) => x.Offset.CompareTo(y.Offset));
            }
            Theme.TextDimmed(newBeatmapOsuCount > 1
                ? L.T("new.timingFromBeatmap", newBeatmapOsu) + L.T("new.timingOtherDifficulties", newBeatmapOsuCount)
                : L.T("new.timingFromBeatmap", newBeatmapOsu));
        }
        else
        {
            bool ownTiming = newDemo || newBeatmapHasOsu;
            ImGui.BeginDisabled(ownTiming);
            ImGui.SetNextItemWidth(Theme.Px(150));
            ImGui.DragFloat("##bpm", ref newBpm, 0.1f, 1, 1000, "%.3f BPM");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Theme.Px(150));
            ImGui.DragInt("##offset", ref newOffset, 1f, -600000, 600000, Theme.NumberFormat("timing.offsetFormat", "%d"));
            ImGui.EndDisabled();
            Theme.TextDimmed(ownTiming
                ? L.T("new.timingFromSource")
                : L.T("new.timingNote"));
        }

        ImGui.Spacing();
        ImGui.Separator();
        bool valid = newName.Trim().Length > 0 && newFolder.Trim().Length > 0;
        ImGui.BeginDisabled(!valid);
        if (Theme.Button(L.T("new.create"), 110))
        {
            app.Settings.ProjectsFolder = newFolder.Trim();
            app.Settings.Save();
            // The field is a float showing three decimals: without the rounding 174.3 would become 174.3000030518.
            var timing = new TimingPoint { Offset = newOffset, Bpm = Math.Round(Math.Clamp(newBpm, 1, 1000), 3) };
            // The list is only handed on when it was changed: untouched, the files say the same.
            if (app.NewProject(newFolder.Trim(), newName, newDemo, newBeatmap.Trim().Length > 0 ? newBeatmap.Trim() : null, timing,
                    newBeatmapTimingChanged ? newBeatmapTiming : null))
                OnProjectOpened();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (Theme.Button(L.T("common.cancel"), 110)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /// <summary>How many timing sections the new project window shows at a time; it has more to show besides them.</summary>
    private const int NewTimingRowsInView = 6;

    /// <summary>
    /// Looks into the beatmap folder that is named in the new project window: whether it has a difficulty,
    /// and what the timing of the one that will be shown first is. Never throws: the path may be anything
    /// a user typed, and a file that cannot be read only means there is no list to show.
    /// </summary>
    private void ReadNewBeatmapTiming(string path)
    {
        newBeatmapHasOsu = false;
        newBeatmapTiming = null;
        newBeatmapTimingChanged = false;
        newTimingToShow = -1;
        if (path.Length == 0) return;
        try
        {
            var difficulties = BeatmapImporter.Difficulties(path);
            newBeatmapHasOsu = difficulties.Count > 0;
            if (difficulties.Count == 0) return;
            newBeatmapOsu = Path.GetFileName(difficulties[0]);
            newBeatmapOsuCount = difficulties.Count;
            newBeatmapTiming = OsuFile.Read(difficulties[0]).Timing;
        }
        catch (Exception)
        {
            newBeatmapTiming = null;
        }
    }

    /// <summary>
    /// Timing sections as a list of its own that scrolls: a beatmap may have hundreds of them, and the
    /// window it is in has to stay on the screen with its buttons in reach. Each section can be changed
    /// and taken away. Nothing is drawn for a list without sections.
    /// </summary>
    /// <param name="rowsInView">How many sections are shown at a time.</param>
    /// <param name="show">The section to scroll to, such as one that was just added; -1 once that is done.</param>
    /// <returns>True when a section was changed or taken away.</returns>
    /// <param name="picked">When given, every row has a check box for choosing red lines to delete together.</param>
    /// <summary>The red line whose last typed beats per measure was refused, to say why on hover.</summary>
    private static TimingPoint? meterRefused;
    /// <summary>The red line whose beats per measure is being typed, and whether its box still needs focus.</summary>
    private static TimingPoint? meterEditing;
    private static bool meterFocus;

    private static unsafe bool TimingTable(string id, List<TimingPoint> timing, int rowsInView, ref int show,
        TimingOffsetPopup? offsetPopup = null, TimingPoint? current = null, HashSet<TimingPoint>? picked = null)
    {
        if (timing.Count == 0) return false;
        bool changed = false;
        int remove = -1;
        float rowHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().CellPadding.Y * 2;
        float headHeight = ImGui.GetTextLineHeight() + ImGui.GetStyle().CellPadding.Y * 2;
        float listHeight = Math.Min(timing.Count, rowsInView) * rowHeight;
        var flags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV;
        if (ImGui.BeginTable(id, 5, flags, new Vector2(0, headHeight + listHeight + Theme.Px(6))))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, Theme.Px(picked != null ? 64 : 34));
            ImGui.TableSetupColumn(L.T("settings.timing.offset"), ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("BPM", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn(L.T("settings.timing.meter"), ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
            ImGui.TableHeadersRow();
            if (show >= 0)
            {
                // In the middle of what is in view.
                ImGui.SetScrollY(MathF.Max(0, show * rowHeight - (listHeight - rowHeight) / 2));
                show = -1;
            }

            var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin(timing.Count, rowHeight);
            while (clipper.Step())
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var point = timing[i];
                    ImGui.TableNextRow();
                    // The red line in effect at the playhead, in the Timing lane's colour.
                    if (point == current)
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, Theme.Rgba(235, 180, 80, 70));
                    ImGui.PushID(i);
                    ImGui.TableNextColumn();
                    if (picked != null)
                    {
                        bool on = picked.Contains(point);
                        if (ImGui.Checkbox("##pick", ref on))
                        {
                            if (on) picked.Add(point);
                            else picked.Remove(point);
                        }
                        ImGui.SameLine();
                    }
                    ImGui.AlignTextToFramePadding();
                    ImGui.PushStyleColor(ImGuiCol.Text, point == current ? Theme.Rgba(235, 180, 80) : Theme.TextDim);
                    ImGui.TextUnformatted((i + 1).ToString());
                    ImGui.PopStyleColor();

                    float offset = (float)point.Offset, bpm = (float)point.Bpm;
                    int meter = point.Meter;
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.DragFloat("##offset", ref offset, 1f, 0, 0, "%.0f ms"))
                    {
                        point.Offset = Math.Round(offset);
                        changed = true;
                    }
                    offsetPopup?.Track(point);
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    // The field is a float showing three decimals: without the rounding 174.3 would become 174.3000030518.
                    if (ImGui.DragFloat("##bpm", ref bpm, 0.1f, 1, 1000, "%.3f"))
                    {
                        point.Bpm = Math.Round(Math.Clamp(bpm, 1, 1000), 3);
                        changed = true;
                    }
                    ImGui.TableNextColumn();
                    // Shows "N/4"; a click turns it into a box for the beats per measure. Out of range is refused.
                    if (meterEditing == point)
                    {
                        ImGui.SetNextItemWidth(-1);
                        if (meterFocus) { ImGui.SetKeyboardFocusHere(); meterFocus = false; }
                        if (ImGui.InputInt("##meter", ref meter, 0, 0, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
                        {
                            if (meter is >= 1 and <= 16) { point.Meter = meter; changed = true; meterRefused = null; }
                            else meterRefused = point;
                            meterEditing = null;
                        }
                        else if (ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.IsItemDeactivated()) meterEditing = null;
                    }
                    else if (ImGui.Button(L.T("timing.meterFormat", meter) + "##meterButton", new Vector2(-1, 0)))
                    {
                        meterEditing = point;
                        meterFocus = true;
                    }
                    if (ImGui.IsItemHovered())
                        Theme.SetTooltip(meterRefused == point
                            ? L.T("timing.meter.tip") + "\n\n" + L.T("timing.invalidMeter") : L.T("timing.meter.tip"));
                    ImGui.TableNextColumn();
                    if (ImGui.Button("×", new Vector2(ImGui.GetFrameHeight(), 0))) remove = i;
                    ImGui.PopID();
                }
            }
            clipper.End();
            clipper.Destroy();
            ImGui.EndTable();
        }
        if (remove >= 0)
        {
            picked?.Remove(timing[remove]);
            timing.RemoveAt(remove);
            changed = true;
        }
        return changed;
    }

    private void DrawExportDialog(Vector2 centre)
    {
        if (!BeginModal(L.T("dialog.export.title") + ExportDialog, centre, 620)) return;
        var project = app.Project;
        if (project == null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        bool changed = false;
        ImGui.TextUnformatted(L.T("export.folder"));
        changed |= FolderField("exportfolder", ref export.OutputFolder);
        Theme.TextDimmed(L.T("export.folderNote"));

        ImGui.Spacing();
        ImGui.TextUnformatted(L.T("export.fileName"));
        ImGui.SetNextItemWidth(-1);
        changed |= ImGui.InputTextWithHint("##filename", "Artist - Title (Creator).osb", ref export.FileName, 256);
        Theme.TextDimmed(L.T("export.fileNameNote"));

        ImGui.Spacing();
        ImGui.TextUnformatted(L.T("export.assets"));
        int mode = (int)export.Assets;
        changed |= ImGui.RadioButton(L.T("export.assetsNone"), ref mode, (int)ExportAssetMode.None);
        changed |= ImGui.RadioButton(L.T("export.assetsUsed"), ref mode, (int)ExportAssetMode.Used);
        changed |= ImGui.RadioButton(L.T("export.assetsAll"), ref mode, (int)ExportAssetMode.All);
        export.Assets = (ExportAssetMode)mode;
        changed |= ImGui.Checkbox(L.T("export.includeMain"), ref export.IncludeAudioAndBackground);
        changed |= ImGui.Checkbox(L.T("export.zip"), ref export.Zip);
        if (project.Difficulties.Count > 0)
        {
            ImGui.Spacing();
            changed |= ImGui.Checkbox(L.T("export.difficulties", project.Difficulties.Count), ref export.Difficulties);
            Theme.TextDimmed(L.T("export.difficultiesNote"));
        }

        if (changed || exportPreview == null || exportPreviewRevision != app.DocRevision)
        {
            exportPreview = AssetPathPolicy.InvalidPath(project, export) is { } invalidPath
                ? (new List<string>(), new List<string> { invalidPath })
                : Exporter.CollectAssets(project, export);
            exportPreviewRevision = app.DocRevision;
            if (changed) exportReport = null;
        }

        ImGui.Spacing();
        ImGui.Separator();
        var (files, missing) = exportPreview.Value;
        long bytes = 0;
        foreach (string file in files)
        {
            var info = new FileInfo(project.AssetFullPath(file));
            if (info.Exists) bytes += info.Length;
        }
        ImGui.TextUnformatted(L.T("export.summary", files.Count, Theme.FormatSize(bytes)));
        int grouped = project.Storyboard.Sprites.Count(s => s.Group != null);
        if (grouped > 0)
            Theme.TextDimmed(L.T("export.groupsNote", project.Storyboard.Groups.Count, grouped));
        if (missing.Count > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            ImGui.TextUnformatted(L.T("export.missing", missing.Count));
            ImGui.PopStyleColor();
            ImGui.BeginChild("missing", new Vector2(0, Theme.Px(Math.Min(missing.Count, 5) * 20 + 6)));
            foreach (string file in missing) Theme.TextDimmed("  " + file);
            ImGui.EndChild();
            Theme.TextDimmed(L.T("export.missingNote"));
        }

        if (exportError != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(L.T("export.failed", exportError));
            ImGui.PopStyleColor();
        }
        if (exportReport != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Rgba(110, 210, 130));
            Theme.TextWrapped(L.T("export.done", exportReport.OutputPath, exportReport.FilesCopied));
            if (exportReport.OsuFiles.Count > 0) Theme.TextWrapped(L.T("export.osuDone", exportReport.OsuFiles.Count));
            ImGui.PopStyleColor();
            foreach (string warning in exportReport.Warnings.Take(6))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Rgba(235, 200, 90));
                Theme.TextWrapped(warning);
                ImGui.PopStyleColor();
            }
        }

        ImGui.Spacing();
        bool valid = export.OutputFolder.Trim().Length > 0 && export.FileName.Trim().Length > 0;
        ImGui.BeginDisabled(!valid);
        if (Theme.Button(L.T("export.run"), 110))
        {
            // The same check as before saving: what is exported is what osu! will play.
            clashes = CommandClash.Find(app.Doc);
            if (clashes.Count > 0) ImGui.OpenPopup(ClashDialog);
            else RunExport(project);
        }
        ImGui.EndDisabled();

        // The question comes up on top of this dialog, which stays as it is underneath.
        bool leave = false;
        if (BeginModal(L.T("dialog.clash.exportTitle") + ClashDialog, centre, 600))
        {
            switch (DrawClashList(L.T("clash.export.anyway"), out var target))
            {
                case ClashAnswer.Proceed:
                    ImGui.CloseCurrentPopup();
                    RunExport(project);
                    break;
                case ClashAnswer.Cancel:
                    ImGui.CloseCurrentPopup();
                    break;
                case ClashAnswer.Locate:
                    // To look at it, both dialogs get out of the way; nothing is exported.
                    ImGui.CloseCurrentPopup();
                    leave = true;
                    Locate(target);
                    break;
            }
            ImGui.EndPopup();
        }
        if (exportReport != null)
        {
            ImGui.SameLine();
            if (ImGui.Button(L.T("export.openLocation")))
                AssetsPanel.OpenInExplorer(exportReport.OsbPath, true);
        }
        ImGui.SameLine();
        if (Theme.Button(L.T("common.close"), 110) || leave) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /// <summary>Exports with the settings of the dialog, which then shows what was written, or why it could not be.</summary>
    private void RunExport(Project project)
    {
        if(!CheckPluginExport(()=>RunExport(project))) return;
        try
        {
            exportError = null;
            if (AssetPathPolicy.InvalidPath(project, export) is { } invalidPath)
                throw new InvalidOperationException(AssetPathPolicy.Message(invalidPath));
            export.OutputFolder = export.OutputFolder.Trim();
            exportReport = Exporter.Export(project, export);
            var saved = project.Settings.Export;
            saved.FileName = Exporter.NormalizeFileName(export.FileName);
            saved.Folder = export.OutputFolder;
            saved.Assets = export.Assets;
            saved.IncludeAudioAndBackground = export.IncludeAudioAndBackground;
            saved.Zip = export.Zip;
            saved.Difficulties = export.Difficulties;
            app.MarkSettingsChanged();
            app.SetStatus(L.T("export.status", exportReport.OsbPath));
        }
        catch (Exception ex)
        {
            exportReport = null;
            exportError = ex.Message;
        }
    }

    private ProjectSettingsEdit? settingsEdit;

    private void DrawProjectSettingsDialog(Vector2 centre)
    {
        if (!BeginModal(L.T("dialog.settings.title") + SettingsDialog, centre, 560))
        {
            settingsEdit = null;
            return;
        }
        var project = app.Project;
        if (project == null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        if (settingsEdit == null || settingsEdit.Project != project || ImGui.IsWindowAppearing())
        {
            settingsEdit = new ProjectSettingsEdit(project);
        }
        var settings = settingsEdit.Settings;
        ImGui.PushItemWidth(-Theme.LabelWidth(90, L.T("project.name"), L.T("settings.audio"), L.T("settings.background")));

        string name = settings.Name;
        if (ImGui.InputText(L.T("project.name"), ref name, 128))
        {
            settings.Name = name;
        }

        if (AssetPicker(L.T("settings.audio"), settings.Audio, AssetKind.Audio, out string audio))
        {
            settings.Audio = audio;
        }
        if (AssetPicker(L.T("settings.background"), settings.Background, AssetKind.Image, out string background))
        {
            settings.Background = background;
        }
        Theme.TextDimmed(L.T("settings.backgroundNote"));

        bool wide = settings.Widescreen;
        if (ImGui.Checkbox(L.T("settings.widescreen"), ref wide))
        {
            settings.Widescreen = wide;
        }
        ImGui.PopItemWidth();

        ImGui.SeparatorText(L.T("reference.folder"));
        Theme.TextWrapped(settingsEdit.BeatmapDirectory ?? L.T("common.none"));
        Theme.TextDimmed(L.T("reference.note"));
        if (ImGui.Button(L.T("reference.choose")))
        {
            var result = NativeFileDialogSharp.Dialog.FolderPicker(settingsEdit.BeatmapDirectory is { } folder && Directory.Exists(folder) ? folder : null);
            if (result.IsOk && !string.IsNullOrEmpty(result.Path)) settingsEdit.BeatmapDirectory = result.Path;
        }
        if (settingsEdit.BeatmapDirectory != null)
        {
            ImGui.SameLine();
            if (ImGui.Button(L.T("reference.unlink"))) settingsEdit.BeatmapDirectory = null;
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (Theme.Button(L.T("common.ok"), 110))
        {
            if (settingsEdit.ReferenceChanged && settingsEdit.BeatmapDirectory is { } directory && !Directory.Exists(directory))
                app.ShowMessage(L.T("reference.failed"), L.T("import.noFolder", directory));
            else
            {
                string previousAudio = project.Settings.Audio;
                // Apply first so reference refresh and unlinking retain the confirmed difficulty settings.
                bool wasPlaying = app.Playing;
                if (settingsEdit.Apply()) app.MarkSettingsChanged();
                if (previousAudio != project.Settings.Audio)
                {
                    if (wasPlaying) app.Pause();
                    app.ReloadMusic();
                    if (wasPlaying) app.Play();
                }
                if (settingsEdit.ReferenceChanged)
                {
                    if (settingsEdit.BeatmapDirectory is { } folder) app.ReferenceBeatmap(folder);
                    else app.UnlinkBeatmap();
                }
                settingsEdit = null;
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (Theme.Button(L.T("common.cancel"), 110) || ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            settingsEdit = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private bool AssetPicker(string label, string current, AssetKind kind, out string picked)
    {
        picked = current;
        bool changed = false;
        if (ImGui.BeginCombo(label, current.Length > 0 ? current : L.T("common.none"), ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.Selectable(L.T("common.none"), current.Length == 0))
            {
                picked = "";
                changed = true;
            }
            foreach (var asset in app.Assets)
            {
                if (asset.Kind != kind) continue;
                if (ImGui.Selectable(asset.Path, asset.Path.Equals(current, StringComparison.OrdinalIgnoreCase)))
                {
                    picked = asset.Path;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        return changed;
    }

    private void DrawImportBeatmapDialog(Vector2 centre)
    {
        if (!BeginModal(L.T("dialog.import.title") + ImportDialog, centre, 560)) return;
        Theme.TextWrapped(importBeatmapPath);
        ImGui.Spacing();
        Theme.TextWrapped(L.T("import.note"));
        ImGui.Spacing();
        if (Theme.Button(L.T("import.replace"), 160))
        {
            app.ImportBeatmap(importBeatmapPath, true);
            OnProjectOpened();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (Theme.Button(L.T("import.append"), 160))
        {
            app.ImportBeatmap(importBeatmapPath, false);
            OnProjectOpened();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (Theme.Button(L.T("common.cancel"), 100)) ImGui.CloseCurrentPopup();
        Theme.TextDimmed(L.T("import.undoNote"));
        ImGui.EndPopup();
    }

    private void DrawMissingBeatmapDialog(Vector2 centre)
    {
        if (!BeginModal(L.T("reference.missingTitle") + MissingBeatmapDialog, centre, 560)) return;
        if (app.Project is not { BeatmapReferenceMissing: true } project)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        Theme.TextWrapped(L.T("reference.missingText"));
        Theme.TextWrapped(project.BeatmapDirectory!);
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-Theme.Px(90));
        ImGui.InputTextWithHint("##replacementBeatmap", L.T("new.beatmapHint"), ref missingBeatmapReplacement, 2048);
        ImGui.SameLine();
        if (ImGui.Button(L.T("common.browse")))
        {
            var result = NativeFileDialogSharp.Dialog.FolderPicker(null);
            if (result.IsOk && !string.IsNullOrEmpty(result.Path)) missingBeatmapReplacement = result.Path;
        }
        ImGui.Spacing();
        if (Theme.Button(L.T("reference.relink"), 150) && app.ReferenceBeatmap(missingBeatmapReplacement.Trim())) ImGui.CloseCurrentPopup();
        ImGui.SameLine();
        if (Theme.Button(L.T("reference.unlink"), 150))
        {
            app.UnlinkBeatmap();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawConfirmDialog(Vector2 centre)
    {
        bool required = saveRequiredForVideo;
        if (!BeginModal(L.T(required ? "videoexport.saveRequired.title" : "dialog.unsaved.title") + UnsavedDialog, centre, 440)) return;
        Theme.TextWrapped(L.T(required ? "videoexport.saveRequired.text" : "unsaved.text"));
        ImGui.Spacing();
        if (Theme.Button(L.T(required ? "videoexport.saveAndContinue" : "common.save"), 120))
        {
            clashes = CommandClash.Find(app.Doc);
            if (clashes.Count > 0)
            {
                // The check asks first. What was waiting for the project to be saved goes on from there.
                ImGui.CloseCurrentPopup();
                openPopup = ClashDialog;
            }
            else if (app.Save())
            {
                ImGui.CloseCurrentPopup();
                RunAfterConfirm();
            }
        }
        if (!required)
        {
            ImGui.SameLine();
            if (Theme.Button(L.T("unsaved.discard"), 120))
            {
                ImGui.CloseCurrentPopup();
                RunAfterConfirm();
            }
        }
        ImGui.SameLine();
        if (Theme.Button(L.T("common.cancel"), 120))
        {
            afterConfirm = null;
            saveRequiredForVideo = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    /// <summary>What the check before saving found: objects with commands that run together and may not.</summary>
    private List<CommandClash.Clash> clashes = new();

    /// <summary>
    /// Saves the project, after looking for commands that may not run at the same time. A storyboard that
    /// came with such commands is opened and shown as it is; saving is where they are pointed out, and the
    /// user decides whether it is saved like that.
    /// </summary>
    private void SaveProject()
    {
        if (app.Project == null) return;
        // This save was asked for by itself; nothing left over from a dialog that was closed waits for it.
        afterConfirm = null;
        saveRequiredForVideo = false;
        clashes = CommandClash.Find(app.Doc);
        if (clashes.Count > 0) openPopup = ClashDialog;
        else app.Save();
    }

    private void DrawClashDialog(Vector2 centre)
    {
        if (!BeginModal(L.T("dialog.clash.title") + ClashDialog, centre, 600)) return;
        switch (DrawClashList(L.T("clash.save.anyway"), out var target))
        {
            case ClashAnswer.Proceed:
                ImGui.CloseCurrentPopup();
                if (app.Save()) RunAfterConfirm();
                else afterConfirm = null;
                break;
            case ClashAnswer.Cancel:
                afterConfirm = null;
                ImGui.CloseCurrentPopup();
                break;
            case ClashAnswer.Locate:
                // Going to look at it is not saving: whatever waited for the save is off.
                afterConfirm = null;
                ImGui.CloseCurrentPopup();
                Locate(target);
                break;
        }
        ImGui.EndPopup();
    }

    private enum ClashAnswer { None, Proceed, Cancel, Locate }

    /// <summary>
    /// What the check found before the storyboard is written, to the project or to an export: the objects
    /// with commands that run together and may not, and the choice of what to do about it.
    /// </summary>
    /// <param name="proceed">What the button says that writes it all the same.</param>
    /// <param name="target">The one to go and look at, when that is the answer.</param>
    private ClashAnswer DrawClashList(string proceed, out CommandClash.Clash target)
    {
        target = default;
        var answer = ClashAnswer.None;
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
        Theme.TextWrapped(L.T("clash.save.count", clashes.Count));
        ImGui.PopStyleColor();
        Theme.TextWrapped(L.T("clash.save.text"));
        ImGui.Spacing();

        // The first of them; a click on a line goes to that one.
        const int shown = 12;
        for (int i = 0; i < clashes.Count && i < shown; i++)
        {
            var clash = clashes[i];
            string name = clash.Sprite?.DisplayName ?? (clash.Group!.Name.Length > 0 ? clash.Group.Name : L.T("group.unnamed"));
            // One that belongs to a difficulty which is not shown says which.
            if (clash.Sprite is { Difficulty: { } difficulty } parked && !app.Doc.Objects.Contains(parked)) name += "    [" + difficulty + "]";
            ImGui.PushID(i);
            Vector2 pos = ImGui.GetCursorScreenPos();
            if (ImGui.Selectable("##clash", false))
            {
                target = clash;
                answer = ClashAnswer.Locate;
            }
            // Drawn rather than given as the label: a name may have "##" in it.
            ImGui.GetWindowDrawList().AddText(pos, ImGui.GetColorU32(ImGuiCol.Text),
                Theme.FormatTime(clash.Time) + "    " + (clash.Kind == CommandKind.M ? L.T("clash.pair.move") : L.T("clash.pair.scale")) + "    " + name);
            ImGui.PopID();
        }
        if (clashes.Count > shown) Theme.TextDimmed(L.T("clash.save.more", clashes.Count - shown));
        Theme.TextDimmed(L.T("clash.save.hint"));
        ImGui.Spacing();

        if (Theme.Button(L.T("clash.save.locate"), 150))
        {
            target = clashes[0];
            answer = ClashAnswer.Locate;
        }
        ImGui.SameLine();
        if (Theme.Button(proceed, 120)) answer = ClashAnswer.Proceed;
        ImGui.SameLine();
        if (Theme.Button(L.T("common.cancel"), 100)) answer = ClashAnswer.Cancel;
        return answer;
    }

    /// <summary>
    /// Shows where commands run together that may not, as the check before saving found it: the same as
    /// going to that problem from the list of problems.
    /// </summary>
    private void Locate(CommandClash.Clash clash)
    {
        var found = new List<Problem>();
        if (clash.Sprite != null) found.AddRange(clash.Sprite.CommandProblems);
        else if (clash.Group != null) Problems.Find(clash.Group, found);
        int at = found.FindIndex(p => p.Kind == ProblemKind.Clash && p.Command == clash.Kind && p.Start == clash.Time);
        if (at >= 0) Locate(found[at]);
        else if (Select(clash.Sprite, clash.Group)) Show(clash.Time);
    }

    /// <summary>
    /// Goes to a problem, down to the commands it is about. The object or the group of sprites is selected
    /// and unfolded, and the property those commands belong to is picked, in the loop they are in when
    /// they are in one: its row comes into view in the timeline, the properties panel shows it, and the
    /// commands themselves are selected, as keyframes where the property has keyframes and as commands
    /// where its commands run at the same time. A loop that as a whole runs together with another loop is
    /// selected as a whole. The playhead goes to where the problem begins.
    /// </summary>
    private void Locate(Problem problem)
    {
        var plugin=(problem.Object as SbSprite)?.Group?.PluginRoot??problem.Group?.PluginRoot;
        if(plugin!=null) {app.OpenContainer(plugin.Parent);app.SelectGroup(plugin);app.Seek(problem.Start);Reveal();return;}
        if (!Select(problem.Object, problem.Group)) return;
        // The playhead goes there before anything is picked: with a property picked, the one keyframe it
        // comes to stand on would be selected, and here all the commands of the problem are to be.
        app.Seek(problem.Start);
        var commands = Problems.Commands(problem);
        if (commands.Count > 0)
        {
            app.SelectTrack(commands[0].Nodes, commands[0].Command.Kind);
            foreach (var (nodes, command) in commands)
            {
                var keys = KeyTrack.Read(nodes, command.Kind);
                if (keys == null)
                {
                    app.SelectedCommands.Add(command);
                    continue;
                }
                for (int i = 0; i < keys.Count; i++)
                {
                    var key = new KeyRef(nodes, command.Kind, i);
                    if ((keys[i].In == command || keys[i].Out == command || keys[i].Point == command) && !app.SelectedKeys.Contains(key)) app.SelectedKeys.Add(key);
                }
            }
        }
        else if (problem.Loop != null && problem.Object is SbSprite owner)
        {
            app.SelectBlock(owner, problem.Loop);
        }
        Reveal();
    }

    /// <summary>Selects and unfolds an object or a group of sprites to go to something in it. False when it is no longer there.</summary>
    private bool Select(SbObject? obj, SbObjectGroup? group)
    {
        if (obj != null)
        {
            // It may belong to a difficulty that is not the one shown: that one is shown first.
            if (obj.Difficulty is { } difficulty && !app.Doc.Objects.Contains(obj)) app.SelectDifficulty(difficulty);
            // Gone since it was listed.
            if (!app.Doc.Objects.Contains(obj)) return false;
            // One that is in a group can only be selected in there.
            app.OpenContainerOf(obj);
            app.SelectOnly(obj);
            // Back to the object as a whole; what is picked in it comes next.
            app.ClearKeySelection();
            app.ActiveTrack = null;
            obj.Expanded = true;
            return true;
        }
        if (group == null || !app.Doc.Groups.Contains(group)) return false;
        // To where the group is one thing: the storyboard, or the group it is in.
        app.OpenContainer(group.Parent);
        app.SelectGroup(group);
        app.ClearKeySelection();
        app.ActiveTrack = null;
        group.Expanded = true;
        return true;
    }

    /// <summary>Puts the playhead at a time and brings what is selected into view in the timeline.</summary>
    private void Show(int time)
    {
        app.Seek(time);
        Reveal();
    }

    /// <summary>Brings what is selected, and what is picked in it, into view in the timeline.</summary>
    private void Reveal()
    {
        app.TouchFolds();
        timeline.RevealSelection();
    }

    private void RunAfterConfirm()
    {
        var action = afterConfirm;
        afterConfirm = null;
        saveRequiredForVideo = false;
        action?.Invoke();
    }

    private void DrawInfoDialogs(Vector2 centre)
    {
        if (BeginModal(L.T("dialog.message.title") + MessageDialog, centre, 520))
        {
            if (message is { } m)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
                ImGui.TextUnformatted(m.Title);
                ImGui.PopStyleColor();
                ImGui.Separator();
                Theme.TextWrapped(m.Text);
            }
            ImGui.Spacing();
            // The Enter that confirmed a typed value may be what brought this message up: it must not also
            // dismiss it in the very frame it appears.
            bool dismissKey = !ImGui.IsWindowAppearing() &&
                              (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false) || ImGui.IsKeyPressed(ImGuiKey.Escape, false));
            if (Theme.Button(L.T("common.ok"), 110) || dismissKey) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (BeginModal(L.T("dialog.shortcuts.title") + ShortcutsDialog, centre, 680))
        {
            (string Keys, string What)[] items =
            {
                (L.T("shortcut.space.keys"), L.T("shortcut.play")),
                ("F11", L.T("shortcut.fullscreen")),
                (L.T("shortcut.snapSoundHold.keys"), L.T("shortcut.snapSoundHold")),
                ("Tab", L.T("shortcut.fullscreenDebug")),
                ("F6", L.T("shortcut.timingList")),
                ("R", L.T("shortcut.timingToggle")),
                ("C", L.T("shortcut.timingCalculator")),
                ("Home / End", L.T("shortcut.homeEnd")),
                ("← / →", L.T("shortcut.step")),
                ("Ctrl + ← / →", L.T("shortcut.keyJump")),
                (L.T("shortcut.nudge.keys"), L.T("shortcut.nudge")),
                (L.T("shortcut.bar.keys"), L.T("shortcut.bar")),
                (L.T("shortcut.wheelTracks.keys"), L.T("shortcut.wheelTracks")),
                (L.T("shortcut.zoom.keys"), L.T("shortcut.zoom")),
                (L.T("shortcut.shiftWheel.keys"), L.T("shortcut.scrollLayers")),
                (L.T("shortcut.wheelList.keys"), L.T("shortcut.scrollLayers")),
                (L.T("shortcut.viewer.keys"), L.T("shortcut.viewer")),
                (L.T("shortcut.cycle.keys"), L.T("shortcut.cycle")),
                (L.T("shortcut.pick.keys"), L.T("shortcut.pick")),
                ("Ctrl+Z / Ctrl+Y", L.T("shortcut.undo")),
                ("Ctrl+C / X / V", L.T("shortcut.clipboard")),
                ("Ctrl+Shift+V", L.T("edit.pasteAtTime")),
                ("Ctrl+D", L.T("shortcut.duplicate")),
                ("Ctrl+G / Ctrl+Shift+G", L.T("shortcut.group")),
                ("Delete", L.T("shortcut.delete")),
                ("Ctrl+] / Ctrl+[", L.T("shortcut.order")),
                ("Ctrl+S / Ctrl+E", L.T("shortcut.saveExport")),
                ("Ctrl+L", L.T("menu.edit.reloadStoryboard")),
                (L.T("shortcut.clickField.keys"), L.T("shortcut.clickField")),
                (L.T("shortcut.dragField.keys"), L.T("shortcut.dragField")),
            };
            if (ImGui.BeginTable("shortcuts", 2, ImGuiTableFlags.RowBg))
            {
                // The keys take the width they need; the descriptions wrap in what is left.
                ImGui.TableSetupColumn("keys", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("what", ImGuiTableColumnFlags.WidthStretch);
                foreach (var (keys, what) in items)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(keys);
                    ImGui.TableNextColumn();
                    Theme.TextDimmed(what);
                }
                ImGui.EndTable();
            }
            ImGui.Spacing();
            if (Theme.Button(L.T("common.close"), 110)) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (BeginModal(L.T("dialog.about.title") + AboutDialog, centre, 460))
        {
            AboutLink("osu! Storyboard Studio", "https://github.com/wanjiaXG/Osu-Storyboard-Studio");
            ImGui.SameLine(0, Theme.Px(6));
            ImGui.TextUnformatted("by");
            ImGui.SameLine(0, Theme.Px(4));
            AboutLink("wanjia", "https://osu.ppy.sh/users/1780106");
            Theme.TextDimmed(L.T("about.version", AppVersion));
            Theme.TextWrapped(L.T("about.text"));
            ImGui.Spacing();
            Theme.TextDimmed(L.T("about.layout"));
            Theme.TextDimmed(L.T("about.sbproj"));
            Theme.TextDimmed(L.T("about.osb"));
            Theme.TextDimmed(L.T("about.assets"));
            ImGui.Spacing();
            ImGui.SeparatorText(L.T("about.thanks"));
            AboutThanks(
                ("gust", "https://osu.ppy.sh/users/1244312"),
                ("Mihato Kano", "https://osu.ppy.sh/users/9633734"),
                ("Hajime", "https://osu.ppy.sh/users/1844862"));
            ImGui.Spacing();
            if (Theme.Button(L.T("common.close"), 110)) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    /// <summary>Contributors share a wrapping row of links separated by white commas and spaces.</summary>
    private void AboutThanks(params (string Name, string Url)[] contributors)
    {
        float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        for (int i = 0; i < contributors.Length; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine(0, 0);
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 1, 1, 1));
                ImGui.TextUnformatted(", ");
                ImGui.PopStyleColor();
                if (ImGui.GetItemRectMax().X + ImGui.CalcTextSize(contributors[i].Name).X <= right)
                    ImGui.SameLine(0, 0);
            }
            AboutLink(contributors[i].Name, contributors[i].Url);
        }
    }

    private void AboutLink(string label, string url)
    {
        Vector2 pos = ImGui.GetCursorScreenPos(), size = ImGui.CalcTextSize(label);
        bool clicked = ImGui.InvisibleButton(url, size);
        var dl = ImGui.GetWindowDrawList();
        dl.AddText(pos, Theme.Accent, label);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            dl.AddLine(pos + new Vector2(0, size.Y - 1), pos + new Vector2(size.X, size.Y - 1), Theme.Accent);
            Theme.SetTooltip(url);
        }
        if (clicked)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { app.ShowMessage(L.T("msg.openFailed"), ex.Message); }
        }
    }
}
