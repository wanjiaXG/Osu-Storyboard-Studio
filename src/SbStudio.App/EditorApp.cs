using System.Collections.Concurrent;
using System.Numerics;
using SbStudio.App.Audio;
using SbStudio.App.Gfx;
using SbStudio.Core;
using Silk.NET.OpenGL;

namespace SbStudio.App;

/// <summary>
/// A keyframe picked out for editing: the list its commands are in (those of a sprite, a loop, a trigger
/// or a group of sprites), the kind of command, and its place among the keyframes of that kind.
/// </summary>
public readonly record struct KeyRef(System.Collections.IList Nodes, CommandKind Kind, int Index);

/// <summary>A property picked out for adjusting in the preview: the list its commands are in, and their kind.</summary>
public readonly record struct TrackRef(System.Collections.IList Nodes, CommandKind Kind, char Parameter = '\0');

/// <summary>Editor state shared by all panels: the open project, selection, clock, undo history and commands.</summary>
public sealed partial class EditorApp : IDisposable
{
    /// <param name="Parked">The objects of the difficulties that were not shown.</param>
    /// <param name="Difficulty">The difficulty that was shown.</param>
    /// <param name="SelectedGroups">The places of the selected groups among the groups.</param>
    /// <param name="OpenGroup">The place of the group that was open, or -1 for the storyboard itself.</param>
    private sealed record Snapshot(List<SbObject> Objects, List<SbObject> Parked, List<SbObjectGroup> Groups, string Difficulty,
        int[] Selected, int[] SelectedGroups, int OpenGroup, int[]? PreviousObjectOrder = null, HistoryLabel? HistoryLabel = null)
    {
        // Preserved by record copies that only update history labels or selection ordering.
        public object StateToken { get; } = new();
        public SimulationHistory Simulation { get; init; } = new(Array.Empty<TriggerSimulationRule>());
        public TimingHistoryState? Timing { get; init; }
        public IReadOnlyList<StoryboardVariable> Variables { get; init; } = Array.Empty<StoryboardVariable>();
    }

    private readonly GL gl;
    public readonly AppSettings Settings;
    public readonly TextureCache Textures;
    public readonly SpriteBatch Batch;
    public readonly StoryboardRenderer Renderer;
    public readonly HitObjectRenderer HitObjects;
    public readonly VideoPlayer Videos;
    public readonly AudioEngine Audio = new();
    public readonly RenderOptions RenderOptions = new();

    public Project? Project { get; private set; }
    public Storyboard Doc => Project!.Storyboard;
    public bool Dirty => Project != null && (pendingCommit || settingsDirty || pluginDirty || !ReferenceEquals(committed?.StateToken, savedStateToken));
    /// <summary>Changes on every edit; panels compare it to refresh derived data.</summary>
    public int DocRevision { get; private set; }
    /// <summary>Changes when a timeline row is folded or unfolded. That is not an edit, so <see cref="DocRevision"/> stays.</summary>
    public int FoldRevision { get; private set; }

    private readonly StoryboardIndex index = new();
    private int indexRevision = -1;

    /// <summary>Per-layer sprite lists and totals of the open storyboard, collected again after every edit.</summary>
    public StoryboardIndex Index
    {
        get
        {
            if (indexRevision != DocRevision)
            {
                index.Build(Project?.Storyboard);
                indexRevision = DocRevision;
            }
            return index;
        }
    }

    private OsuBeatmap? beatmap;
    private string? beatmapProblem;
    /// <summary>The difficulty file <see cref="beatmap"/> was read from; null when it has to be read again.</summary>
    private string? beatmapFile;

    /// <summary>
    /// The hit objects of the difficulty that is shown, read directly from the referenced directory when
    /// they are first asked for. Null when there are none to show; <see cref="BeatmapProblem"/> says why.
    /// </summary>
    public OsuBeatmap? Beatmap
    {
        get
        {
            ReadBeatmap();
            return beatmap;
        }
    }

    /// <summary>Why no hit objects can be shown, in the user's language; null when they can.</summary>
    public string? BeatmapProblem
    {
        get
        {
            ReadBeatmap();
            return beatmapProblem;
        }
    }

    private void ReadBeatmap()
    {
        string file = Project?.CurrentDifficulty is { } difficulty ? Project.DifficultyFile(difficulty.File) : "";
        if (file == beatmapFile) return;
        beatmapFile = file;
        beatmap = null;
        beatmapProblem = null;
        if (file.Length == 0)
        {
            HitObjects.SetBeatmapSkin(null, null);
            beatmapProblem = L.T(Project == null || Project.Difficulties.Count == 0 ? "hitobjects.noDifficulties" : "hitobjects.noneSelected");
            return;
        }
        try
        {
            var read = BeatmapReader.Read(file);
            if (read.Mode is < 0 or > 3) beatmapProblem = L.T("hitobjects.otherMode");
            else if (read.Objects.Count == 0) beatmapProblem = L.T("hitobjects.empty");
            else beatmap = read;
        }
        catch (Exception ex)
        {
            // Whatever a difficulty file may have in it, it is no reason for the editor to stop.
            beatmapProblem = L.T("hitobjects.unreadable", Path.GetFileName(file), ex.Message);
        }
        HitObjects.SetBeatmapSkin(file, beatmap);
    }

    /// <summary>The video export that is under way, or that has ended and has not been looked at yet.</summary>
    public VideoExportJob? VideoExport { get; private set; }

    /// <summary>A video is being exported; the preview stands still meanwhile.</summary>
    public bool Exporting => VideoExport is { Running: true };

    public bool VideoExportNeedsSave => Project != null &&
        (Dirty || !File.Exists(Project.ProjectFile) || !File.Exists(Project.StoryboardFile));

    public string? VideoExportEditProblem => ScriptDiagnostics.Any(d => d.Diagnostic.IsError)
        ? L.T("videoexport.unappliedScript")
        : PreviewingEdit ? L.T("videoexport.pendingEdit") : null;

    /// <summary>Begins exporting a video. Null when it has begun; otherwise why it could not.</summary>
    public string? StartVideoExport(VideoExportRequest request)
    {
        if(ImportingPlugins>0) return L.T("export.pluginImporting");
        if (Project == null || IsLoading) return "";
        Pause();
        CommitEdits();
        if (VideoExportEditProblem is { } editProblem) return editProblem;
        if (VideoExportNeedsSave) return L.T("videoexport.mustSave");
        ClearVideoExport();
        VideoExport = VideoExportJob.Start(this, gl, request, out string? error);
        return VideoExport == null ? error ?? "" : null;
    }

    /// <summary>Lets go of the export: one that is under way is stopped and its unfinished file removed.</summary>
    public void ClearVideoExport()
    {
        VideoExport?.Dispose();
        VideoExport = null;
    }

    /// <summary>How long a stretch of exporting may take before the window is drawn again, in milliseconds.</summary>
    private const double ExportStepMs = 50;

    /// <summary>What is drawn of the beatmap with the options as they are: its hit objects when they are switched on.</summary>
    public OsuBeatmap? ShownBeatmap => RenderOptions.ShowHitObjects ? Beatmap : null;

    public double Time { get; private set; }
    public double Rate { get; private set; } = 1;
    public bool Playing => Audio.IsPlaying;

    public string Status { get; private set; } = "";
    public DateTime StatusAt { get; private set; }
    /// <summary>Title and text of a message the UI should show in a dialog.</summary>
    public (string Title, string Text)? PendingMessage;

    private readonly List<SbObject> selection = new();
    private readonly HashSet<SbObject> selectionSet = new();
    private SbLayer insertionLayer = SbLayer.Foreground;
    /// <summary>Layer for new images: the primary sprite's layer, or the layer explicitly selected in the list.</summary>
    public SbLayer InsertionLayer => Primary is SbSprite sprite ? sprite.Layer : insertionLayer;

    public void SelectLayer(SbLayer layer)
    {
        if ((int)layer < 0 || (int)layer >= SbInfo.LayerCount) return;
        SelectTimelineSection((TimelineSection)layer);
    }
    /// <summary>
    /// Commands selected as they are written: the P commands, and those of a property whose commands run
    /// at the same time and so cannot be shown as keyframes.
    /// </summary>
    public readonly HashSet<SbCommand> SelectedCommands = new();
    /// <summary>The selected keyframes. They belong to the selected objects or to the selected group.</summary>
    public readonly List<KeyRef> SelectedKeys = new();
    /// <summary>
    /// The property that dragging in the preview adjusts, the way the Design mode of the osu! editor has its
    /// Move, Scale, Fade and Rotate tools: with opacity picked, dragging a sprite up and down changes how
    /// opaque it is at the playhead. It is a property of a selected sprite, of one of its loops or of the
    /// selected group, and is null while dragging moves objects as a whole.
    /// </summary>
    public TrackRef? ActiveTrack
    {
        get => activeTrack;
        set
        {
            activeTrack = value;
            // A property and a loop or trigger as a whole are picked one at a time.
            if (value != null) SelectedBlock = null;
        }
    }

    private TrackRef? activeTrack;

    /// <summary>
    /// The loop or trigger of a selected sprite that is selected as a whole: the timeline and the
    /// properties mark it, and Delete takes it away with the commands in it rather than the sprite.
    /// </summary>
    public SbGroup? SelectedBlock { get; set; }

    /// <summary>Selects a loop or trigger of a sprite as a whole, and the sprite with it.</summary>
    public void SelectBlock(SbSprite sprite, SbGroup block)
    {
        if(PluginLink.IsGenerated(sprite)) {SelectOnly(sprite);return;}
        if (!IsSelected(sprite) || selection.Count > 1) SelectOnly(sprite);
        ClearKeySelection();
        ActiveTrack = null;
        SelectedBlock = block;
    }

    /// <summary>
    /// The time the commands of a loop or trigger count from at the playhead. For a loop that is the start
    /// of the pass the playhead is in, so that a moment of any pass is the same moment of the loop. For a
    /// trigger it is the simulated firing the playhead is in (see <see cref="TriggerAnchor"/>), as in osu!lazer
    /// its commands count from the moment it fires.
    /// </summary>
    public int BlockOffset(SbGroup? block) => block switch
    {
        SbLoop loop => loop.StartAt(Time),
        SbTrigger trigger => (int)Math.Round(TriggerAnchor(trigger).Time),
        _ => 0,
    };

    /// <summary>The firing a trigger's commands are edited and shown at.</summary>
    /// <param name="Index">Which firing it is, from 0; -1 when nothing simulated fires the trigger.</param>
    public readonly record struct TriggerFiring(double Time, int Index, int Count, bool Simulated);

    /// <summary>
    /// The simulated firing of a trigger at the playhead: the last one at or before it, or the first one
    /// while the playhead is still before that. Without a firing it is the start of the trigger's window,
    /// as it was before there was a simulation.
    /// </summary>
    public TriggerFiring TriggerAnchor(SbTrigger trigger, SbSprite? owner = null)
    {
        owner ??= OwnerOf(trigger);
        if (owner != null && RenderOptions.Simulation is { } simulation)
        {
            var fires = simulation.FiresOf(owner, trigger);
            int index = TriggerSimulation.FiringAt(fires, Time);
            if (index >= 0) return new(fires[index].Time, index, fires.Count, true);
        }
        return new(trigger.EditOffset, -1, 0, false);
    }

    /// <summary>The sprite a trigger belongs to: looked for among the selected ones first.</summary>
    public SbSprite? OwnerOf(SbTrigger trigger)
    {
        foreach (var obj in selection)
            if (obj is SbSprite sprite && sprite.Nodes.Contains(trigger)) return sprite;
        foreach (var sprite in Doc.Sprites)
            if (sprite.Nodes.Contains(trigger)) return sprite;
        return null;
    }

    private readonly List<Snapshot> undo = new();
    private readonly List<Snapshot> redo = new();
    private Snapshot? committed;
    private object? savedStateToken;
    // Project settings and diagnostics are saved with the storyboard but are not restored by undo.
    private bool settingsDirty;
    private bool pendingCommit;
    private Action? restorePreviewEdit;
    public bool PreviewingEdit => restorePreviewEdit != null;

    private List<AssetInfo> assets = new();
    private List<string> assetFolders = new();
    private Dictionary<string, int>? usage;
    private int usageRevision = -1;
    private FileSystemWatcher? watcher;
    private readonly ConcurrentQueue<string> changedFiles = new();
    private long assetsChangedTicks;

    internal EditorApp(Project project)
    {
        gl=null!;Textures=null!;Batch=null!;Renderer=null!;HitObjects=null!;Videos=null!;
        Settings=new AppSettings();Project=project;beatmapFile="";ResetHistory();SyncPlugins();
    }

    public EditorApp(GL gl, AppSettings settings)
    {
        this.gl = gl;
        Settings = settings;
        if (settings.ShowTimingRecheck) settings.ShowHitSoundRecheck = false;
        Textures = new TextureCache(gl);
        Batch = new SpriteBatch(gl);
        Videos = new VideoPlayer(gl);
        HitObjects = new HitObjectRenderer(gl, Batch);
        HitObjects.SkinLibrary.BeatmapFallbackId = settings.PreviewBeatmapSkinFallback == "beatmap" ? "lazer" : settings.PreviewBeatmapSkinFallback;
        HitObjects.SelectSkin(settings.PreviewSkin);
        Renderer = new StoryboardRenderer(Batch, Textures, Videos, HitObjects);
        RenderOptions.ShowVideo = settings.ShowVideo;
        RenderOptions.ShowHitObjects = settings.ShowHitObjects;
        Audio.Volume = settings.Volume;
        Audio.HitSoundVolume = settings.HitSoundVolume;
        Audio.MusicVolume = settings.MusicVolume;
        ApplyPlaybackGates();
        Audio.SetOutputDevice(settings.AudioOutputDevice);
    }

    // ---------------------------------------------------------------- status

    public void SetStatus(string text)
    {
        Status = text;
        StatusAt = DateTime.Now;
    }

    public void ShowMessage(string title, string text) => PendingMessage = (title, text);

    /// <summary>The program's version. This is the one place to change it for a new release.</summary>
    public static readonly string Version = typeof(EditorApp).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;

    /// <summary>What the title bar says after the name of the open project: program, version and author.</summary>
    public static readonly string WindowTitle = $"osu!Storyboard Studio V{Version} by wanjia";

    public string Title
    {
        get
        {
            if (Project == null) return WindowTitle;
            string name = string.IsNullOrEmpty(Project.Settings.Name) ? Path.GetFileName(Project.Folder) : Project.Settings.Name;
            return $"{name}{(Dirty ? " *" : "")} - {WindowTitle}";
        }
    }

    // ---------------------------------------------------------------- selection

    public IReadOnlyList<SbObject> Selection => selection;
    public SbObject? Primary => selection.Count > 0 ? selection[^1] : null;
    public bool EmptyBeatmapBackgroundSelected { get; private set; }

    public void SelectEmptyBeatmapBackground()
    {
        if (Project == null) return;
        ClearSelection();
        EmptyBeatmapBackgroundSelected = true;
    }
    public bool IsSelected(SbObject obj) => selectionSet.Contains(obj);

    private readonly List<SbObjectGroup> selectedGroups = new();

    /// <summary>
    /// The selected groups. A group is one thing in the timeline, like a sprite, and is selected like one:
    /// by itself, or together with sprites and other groups of the place that is open.
    /// </summary>
    public IReadOnlyList<SbObjectGroup> SelectedGroups => selectedGroups;

    public bool IsSelected(SbObjectGroup group) => selectedGroups.Contains(group);

    /// <summary>
    /// The group being edited: the one that is selected with nothing else beside it. Its properties are
    /// what the properties panel shows, and dragging in the preview moves it.
    /// </summary>
    public SbObjectGroup? SelectedGroup => selectedGroups.Count == 1 && selection.Count == 0 ? selectedGroups[0] : null;

    /// <summary>
    /// The group that has been gone into, like a folder: the timeline lists what is in it, the preview
    /// selects among that, and what is added is added to it. Null for the storyboard itself.
    /// </summary>
    public SbObjectGroup? OpenGroup { get; private set; }

    /// <summary>Goes into a group, or with null back out to the storyboard. Nothing is selected in there at first.</summary>
    public void OpenContainer(SbObjectGroup? group)
    {
        if(RejectPluginEdit(group?.PluginRoot!=null)) return;
        if (group != null && (Project == null || !Doc.Groups.Contains(group))) return;
        if (OpenGroup == group) return;
        ClearSelection();
        OpenGroup = group;
        TouchFolds();
    }

    /// <summary>Goes out of the group that is open, to where that group is one thing again, and selects it there.</summary>
    public void LeaveGroup()
    {
        if (OpenGroup is not { } left) return;
        ClearSelection();
        OpenGroup = left.Parent;
        selectedGroups.Add(left);
        TouchFolds();
    }

    /// <summary>
    /// What stands for a sprite in the place that is open: the sprite itself when it is a member of the
    /// open group, or the group of that place it is in, however deep.
    /// </summary>
    /// <returns>False for a sprite that is not in the open group at all.</returns>
    public bool ItemOf(SbSprite sprite, out SbObjectGroup? group)
    {
        group = null;
        if (sprite.Group == OpenGroup) return true;
        int steps = 0;
        for (var g = sprite.Group; g != null && steps < SbObjectGroup.MaxDepth; g = g.Parent, steps++)
        {
            if (g.Parent != OpenGroup) continue;
            group = g;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Goes into the group an object is a member of, so that it can be selected: for going to an object
    /// from elsewhere, such as the list of problems.
    /// </summary>
    public void OpenContainerOf(SbObject obj) => OpenContainer((obj as SbSprite)?.Group?.PluginRoot?.Parent ?? (PluginLink.IsGenerated(obj) ? null : (obj as SbSprite)?.Group));

    /// <summary>Forgets groups that are gone: among the selected ones, and the open one, which is left for the nearest one that is still there.</summary>
    private void DropStaleGroups()
    {
        selectedGroups.RemoveAll(g => !Doc.Groups.Contains(g));
        int steps = 0;
        while (OpenGroup != null && !Doc.Groups.Contains(OpenGroup) && steps++ < SbObjectGroup.MaxDepth) OpenGroup = OpenGroup.Parent;
        if (OpenGroup != null && !Doc.Groups.Contains(OpenGroup)) OpenGroup = null;
    }

    /// <summary>Takes the groups away that nothing is in any more.</summary>
    private void PruneGroups()
    {
        Doc.PruneGroups();
        DropStaleGroups();
    }

    public void ClearSelection()
    {
        SelectedTimelineSection = null;
        EmptyBeatmapBackgroundSelected = false;
        // Keep the latest layer even if the selected sprite's layer was edited or reordered.
        if (Primary is SbSprite sprite) insertionLayer = sprite.Layer;
        selection.Clear();
        selectionSet.Clear();
        SelectedCommands.Clear();
        SelectedKeys.Clear();
        ActiveTrack = null;
        SelectedBlock = null;
        selectedGroups.Clear();
    }

    /// <summary>Deselects keyframes and commands, and a loop or trigger that was selected as a whole; the selected objects stay selected.</summary>
    public void ClearKeySelection()
    {
        SelectedCommands.Clear();
        SelectedKeys.Clear();
        SelectedBlock = null;
    }

    /// <summary>
    /// Makes one keyframe the only thing selected among keyframes and commands. Its property becomes the one
    /// the preview adjusts.
    /// </summary>
    public void SelectKey(KeyRef key)
    {
        SelectedCommands.Clear();
        SelectedKeys.Clear();
        SelectedKeys.Add(key);
        ActiveTrack = new TrackRef(key.Nodes, key.Kind);
    }

    /// <summary>Picks a property for adjusting in the preview; no keyframe of it is selected.</summary>
    public void SelectTrack(System.Collections.IList nodes, CommandKind kind, char parameter = '\0')
    {
        ClearKeySelection();
        ActiveTrack = new TrackRef(nodes, kind, parameter);
    }

    public bool IsActiveTrack(System.Collections.IList nodes, CommandKind kind) =>
        ActiveTrack is { } track && track.Nodes == nodes && track.Kind == kind;

    /// <summary>The loop or trigger of a selected sprite whose commands a list is; null for a sprite's own list and for that of a group.</summary>
    private SbGroup? BlockOf(System.Collections.IList nodes)
    {
        foreach (var obj in selection)
        {
            if (obj is not SbSprite sprite || sprite.Nodes == nodes) continue;
            foreach (var node in sprite.Nodes)
                if (node is SbGroup block && block.Commands == nodes) return block;
        }
        return null;
    }

    /// <summary>
    /// With a property picked, the keyframe of it that the playhead has come to stand on becomes the
    /// selected one, in place of whichever was selected before: it is the one the preview and the fields
    /// of the properties panel change from now on, and the one both of them should mark. Where the
    /// playhead stands on none, what is selected stays as it is.
    ///
    /// A property whose commands run at the same time has no keyframes to select; there the commands that
    /// meet at the playhead are selected, as they are after a change made in the preview.
    /// </summary>
    private void SelectAtPlayhead()
    {
        if (ActiveTrack is not { } track || track.Kind == CommandKind.P) return;
        int now = (int)Math.Round(Time) - BlockOffset(BlockOf(track.Nodes));
        var keys = KeyTrack.Read(track.Nodes, track.Kind);
        if (keys != null)
        {
            int index = keys.FindIndex(k => k.Time == now);
            if (index < 0) return;
            SelectedCommands.Clear();
            SelectedKeys.Clear();
            SelectedKeys.Add(new KeyRef(track.Nodes, track.Kind, index));
        }
        else if (KeyTrack.At(track.Nodes, track.Kind, now) is { } met)
        {
            SelectedKeys.Clear();
            SelectedCommands.Clear();
            foreach (var command in new[] { met.In, met.Out, met.Point })
                if (command != null) SelectedCommands.Add(command);
        }
    }

    public void SelectGroup(SbObjectGroup group)
    {
        if(group.Parent?.PluginRoot is {} plugin) {OpenContainer(plugin.Parent);group=plugin;}
        if (SelectedGroup == group) return;
        ClearSelection();
        selectedGroups.Add(group);
    }

    /// <summary>Selects a group in addition to what is selected, or makes it the last one selected.</summary>
    public void AddToSelection(SbObjectGroup group)
    {
        if(group.Parent?.PluginRoot is {} plugin) {OpenContainer(plugin.Parent);group=plugin;}
        SelectedTimelineSection = null;
        EmptyBeatmapBackgroundSelected = false;
        selectedGroups.Remove(group);
        selectedGroups.Add(group);
    }

    public void ToggleSelection(SbObjectGroup group)
    {
        if(group.Parent?.PluginRoot is {} plugin) {OpenContainer(plugin.Parent);group=plugin;}
        SelectedTimelineSection = null;
        EmptyBeatmapBackgroundSelected = false;
        if (!selectedGroups.Remove(group)) selectedGroups.Add(group);
    }

    public void SelectOnly(SbObject obj)
    {
        if(obj is SbSprite {Group.PluginRoot: {} plugin}) {OpenContainer(plugin.Parent);SelectGroup(plugin);return;}
        if (selection.Count == 1 && selection[0] == obj) return;
        ClearSelection();
        AddToSelection(obj);
    }

    public void AddToSelection(SbObject obj)
    {
        if(obj is SbSprite {Group.PluginRoot: {} plugin}) {OpenContainer(plugin.Parent);SelectGroup(plugin);return;}
        SelectedTimelineSection = null;
        EmptyBeatmapBackgroundSelected = false;
        if (obj is SbSprite sprite) insertionLayer = sprite.Layer;
        if (selectionSet.Add(obj)) selection.Add(obj);
        else
        {
            // Make it the primary one.
            selection.Remove(obj);
            selection.Add(obj);
        }
    }

    public void ToggleSelection(SbObject obj)
    {
        if(obj is SbSprite {Group.PluginRoot: {} plugin}) {OpenContainer(plugin.Parent);SelectGroup(plugin);return;}
        SelectedTimelineSection = null;
        if (selectionSet.Remove(obj)) selection.Remove(obj);
        else AddToSelection(obj);
    }

    /// <summary>Selects everything of the place that is open: its sprites and its groups, and in the storyboard itself the sounds and videos.</summary>
    public void SelectAll()
    {
        ClearSelection();
        foreach (var obj in Doc.Objects)
            if (obj is SbSprite sprite ? sprite.Group == OpenGroup : OpenGroup == null) AddToSelection(obj);
        foreach (var group in Doc.Groups)
            if (group.Parent == OpenGroup) selectedGroups.Add(group);
    }

    // ---------------------------------------------------------------- editing and undo

    /// <summary>Call after changing an object in place.</summary>
    public void Touch(SbObject? obj = null)
    {
        System.Diagnostics.Debug.Assert(obj==null || !PluginLink.IsGenerated(obj));
        if (obj is SbSprite sprite) sprite.Touch();
        DocRevision++;
        pendingCommit = true;
    }

    /// <summary>Call after changing a group in place.</summary>
    public void TouchGroup(SbObjectGroup? group)
    {
        System.Diagnostics.Debug.Assert(group==null || !PluginLink.IsGenerated(group));
        group?.Touch();
        DocRevision++;
        pendingCommit = true;
    }

    /// <summary>Call after changing <see cref="SbObject.Expanded"/> of an object or of a group.</summary>
    public void TouchFolds()
    {
        if (Project != null) timelineFolds.Remember(Doc);
        FoldRevision++;
    }

    public bool CanUndo => undo.Count > 0 || pendingCommit;
    public bool CanRedo => redo.Count > 0 && !pendingCommit;

    private int[] SelectedIndices()
    {
        var objects = HistoryObjects.ToList();
        var selected = new List<int>();
        for (int i = 0; i < objects.Count; i++)
            if (selectionSet.Contains(objects[i])) selected.Add(i);
        return selected.ToArray();
    }

    private readonly TimelineFoldState timelineFolds = new();

    private Snapshot Capture()
    {
        timelineFolds.Remember(Doc);
        var (objects, parked, groups) = timelineFolds.Clone(Doc.Objects, Doc.Parked, Doc.Groups, skipGenerated: true);
        return new Snapshot(objects, parked, groups, Project!.Settings.Difficulty, SelectedIndices(),
            selectedGroups.Select(g => HistoryGroups.IndexOf(g)).ToArray(), OpenGroup == null ? -1 : HistoryGroups.IndexOf(OpenGroup))
        { Simulation = CaptureSimulation(Project.Settings), Timing = TimingHistoryState.Capture(Project), Variables = Doc.Variables.ToArray() };
    }

    private void Restore(Snapshot snapshot, int[]? reorderedSelection = null)
    {
        // Keep the same things selected when the step did not add or remove any.
        int[] selected = reorderedSelection ?? snapshot.Selected;
        int[] groupsSelected = snapshot.SelectedGroups;
        int open = snapshot.OpenGroup;
        if (snapshot.Objects.Count == HistoryObjects.Count() && snapshot.Groups.Count == HistoryGroups.Count)
        {
            // The same place stays open, too.
            open = OpenGroup == null ? -1 : HistoryGroups.IndexOf(OpenGroup);
            if (selection.Count > 0 || selectedGroups.Count > 0)
            {
                selected = reorderedSelection ?? SelectedIndices();
                groupsSelected = selectedGroups.Select(g => HistoryGroups.IndexOf(g)).ToArray();
            }
        }

        // The snapshot itself stays untouched so it can be restored again.
        timelineFolds.Remember(Doc);
        InvalidateEffectRequests();
        (Doc.Objects, Doc.Parked, Doc.Groups) = timelineFolds.Clone(snapshot.Objects, snapshot.Parked, snapshot.Groups, restore: true);
        Doc.Variables = snapshot.Variables.ToList();
        Project!.Settings.TriggerSimulationEnabled = true;
        Project.Settings.TriggerSimulationRules = snapshot.Simulation.Rules.Select(r => r.Clone()).ToList();
        RefreshTriggerSimulation();
        RefreshPlayback();
        // A step that crosses a change of difficulty goes back to the one that was shown then.
        if (snapshot.Difficulty != Project!.Settings.Difficulty)
        {
            string audio = Project.Settings.Audio;
            if (Project.UseDifficulty(snapshot.Difficulty)) AfterDifficultyChange(audio);
        }
        // A reference may have lost a difficulty since this undo step was captured.
        if (Project.ArrangeStoryboard())
        {
            selected = Array.Empty<int>();
            MarkSettingsChanged();
        }
        if (snapshot.Timing?.Restore(Project) == true) RefreshPlayback();
        ClearSelection();
        OpenGroup = open >= 0 && open < Doc.Groups.Count ? Doc.Groups[open] : null;
        foreach (int i in selected)
            if (i < Doc.Objects.Count) AddToSelection(Doc.Objects[i]);
        foreach (int i in groupsSelected)
            if (i >= 0 && i < Doc.Groups.Count) selectedGroups.Add(Doc.Groups[i]);
        SyncPlugins(true);Arrange();
        DocRevision++;
    }

    /// <summary>Turns the edits made since the last commit into one undo step.</summary>
    public void CommitEdits()
    {
        if (PreviewingEdit || !pendingCommit || Project == null) return;
        pendingCommit = false;
        Arrange();
        if (committed != null) undo.Add(committed);
        var next = Capture();
        committed = next with { HistoryLabel = DescribeHistory(committed, next) };
        redo.Clear();
        TrimHistory();
        RefreshEffectPins();
        HistoryRevision++;
    }

    /// <summary>Previews changes as one edit; the caller supplies how to restore the original values.</summary>
    public void BeginPreviewEdit(Action restore)
    {
        FinishPreviewEdit(false);
        CommitEdits();
        restorePreviewEdit = restore;
    }

    public void FinishPreviewEdit(bool confirm)
    {
        if (restorePreviewEdit == null) return;
        var restore = restorePreviewEdit;
        restorePreviewEdit = null;
        if (confirm) CommitEdits();
        else
        {
            restore();
            pendingCommit = false;
        }
    }

    public void Undo()
    {
        if (Project == null || PreviewingEdit) return;
        CommitEdits();
        if (undo.Count == 0 || committed == null) return;
        int[]? selected = committed.PreviousObjectOrder is { } order && order.Length == HistoryObjects.Count()
            ? SelectedIndices().Select(i => order[i]).ToArray() : null;
        redo.Add(committed);
        committed = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        Restore(committed, selected);
        HistoryRevision++;
        SetStatus(L.T("status.undone"));
    }

    public void Redo()
    {
        if (Project == null || PreviewingEdit) return;
        CommitEdits();
        if (redo.Count == 0 || committed == null) return;
        var shown = HistoryObjects.ToList();
        int[]? selected = redo[^1].PreviousObjectOrder is { } order && order.Length == shown.Count
            ? order.Select((oldIndex, newIndex) => (oldIndex, newIndex)).Where(p => selectionSet.Contains(shown[p.oldIndex])).Select(p => p.newIndex).ToArray()
            : null;
        undo.Add(committed);
        committed = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        Restore(committed, selected);
        HistoryRevision++;
        SetStatus(L.T("status.redone"));
    }

    /// <param name="initial">The state to start from, when it has been captured already.</param>
    private void ResetHistory(Snapshot? initial = null)
    {
        restorePreviewEdit = null;
        undo.Clear();
        redo.Clear();
        pendingCommit = false;
        committed = initial ?? (Project != null ? Capture() : null);
        savedStateToken = committed?.StateToken;
        settingsDirty = false;
        HistoryRevision++;
        RefreshEffectPins();
    }

    // ---------------------------------------------------------------- clock

    /// <summary>Earliest and latest time worth showing: audio and storyboard combined.</summary>
    public (double Start, double End) TimeBounds
    {
        get
        {
            double start = 0, end = Math.Max(1000, Audio.DurationMs);
            if (Project != null && Index.TimeRange is { } range)
            {
                start = Math.Min(start, range.Start);
                end = Math.Max(end, range.End);
            }
            return (start, end);
        }
    }

    /// <summary>
    /// What the preview's progress bar runs over, the way a video of the map would: from the first thing
    /// the storyboard shows, or the start of the music if that comes first, to the end of the music.
    /// Without music it ends where the storyboard does.
    /// </summary>
    public (double Start, double End) PlayRange
    {
        get
        {
            var bounds = TimeBounds;
            return (bounds.Start, Audio.DurationMs > 0 ? Audio.DurationMs : bounds.End);
        }
    }

    /// <summary>
    /// Jumps to an exact time. Only for values the user typed and for jumping to an existing object;
    /// everything done with the mouse or the arrow keys goes through <see cref="SeekSnapped"/>.
    ///
    /// Where the playhead comes to stand on a keyframe of the property that is picked, that keyframe is
    /// selected (<see cref="SelectAtPlayhead"/>). Whoever wants something else selected there selects it
    /// after the jump.
    /// </summary>
    public void Seek(double time)
    {
        bool wasPlaying = Playing;
        if (wasPlaying) Audio.Stop();
        Time = time;
        if (wasPlaying) Play();
        else SelectAtPlayhead();
    }

    /// <summary>Jumps to the snap tick nearest to <paramref name="time"/>.</summary>
    public void SeekSnapped(double time) => Seek(SnapMs(time));

    /// <summary>
    /// Jumps to the very first moment: the earliest time of anything in the storyboard, or the start of
    /// the music when nothing comes before it. Exactly there, on the grid or not: the nearest snap tick
    /// may be later, and playing from it would leave out what begins right at the start. A sample only
    /// sounds when playback passes its beginning.
    /// </summary>
    public void SeekToStart() => Seek(TimeBounds.Start);

    /// <summary>Moves the playhead by a number of snap ticks (negative = backwards).</summary>
    public void StepTime(int ticks)
    {
        if (Project == null || ticks == 0) return;
        var timing = Project.Settings.Timing;
        if (timing.Count == 0)
        {
            Seek(Math.Round(Time) + ticks * 100);
            return;
        }
        double time = Time;
        for (int i = 0; i < Math.Abs(ticks); i++)
            time = ticks > 0 ? TimingUtil.NextTick(timing, time, Divisor) : TimingUtil.PreviousTick(timing, time, Divisor);
        Seek(Math.Round(time));
    }

    public void Play()
    {
        if (Project == null) return;
        var bounds = TimeBounds;
        if (Time >= bounds.End) Time = bounds.Start;
        Audio.SetTimingMode(Settings.ShowTimingRecheck, Settings.TimingSoundFollowsTempo, Settings.TimingSlowPlayback);
        if (Settings.SeamlessPlaybackRefresh)
        {
            ApplyPlaybackGates();
            var samples = ScheduledSamples();
            Audio.Play(Time, Rate, samples);
            return;
        }
        Audio.Play(Time, Rate, ScheduledSamples());
    }

    /// <summary>Preview sounds with the user offset; export retains the original storyboard times.</summary>
    public List<ScheduledSample> ScheduledSamples(bool forExport = false)
    {
        var samples = new List<ScheduledSample>();
        if (Project == null) return samples;
        if (forExport) return VideoExportJob.StoryboardSamples(Project);
        EnsureTriggerSimulation();
        foreach (var sample in Doc.Samples)
        {
            if (sample.Hidden) continue;
            bool failing = TriggerSimulation.Stream.HasStateRules ? TriggerSimulation.FailStateAt(sample.Time) : RenderOptions.FailState;
            if (sample.Layer == SbLayer.Fail && !failing) continue;
            if (sample.Layer == SbLayer.Pass && failing) continue;
            samples.Add(new ScheduledSample(sample.Time, Project.AssetFullPath(sample.Path), (float)Math.Clamp(sample.Volume / 100, 0, 1)));
        }
        if (Settings.ShowTimingRecheck)
        {
            scheduledTimingIndex = TimingRecheckEvents;
            if (Settings.SeamlessPlaybackRefresh || Settings.TimingAssistSound && !TimingLaneHidden)
                samples.AddRange(Settings.TimingSlowPlayback == TimingSlowPlaybackStyle.PlaybackClock && Rate < 1
                    ? scheduledTimingIndex.PrecisePlayback(Project.Settings.Timing, TimingSnapSoundActive ? Divisor : 1)
                    : scheduledTimingIndex.Playback);
        }
        else if ((RenderOptions.ShowHitObjects || Settings.ShowHitSoundRecheck && HitSoundRecheckSupported) && Beatmap != null)
            samples.AddRange(Settings.SeamlessPlaybackRefresh ? HitSoundEvents.PlaybackWithLaneMasks : HitSoundEvents.PlaybackWithMutedLanes(hiddenHitSoundLanes));
        // Schedules use map milliseconds; scale the offset so its real duration stays the same at every rate.
        double offset = Settings.SoundOffsetMs * Rate;
        if (offset != 0)
            for (int i = 0; i < samples.Count; i++)
                samples[i] = samples[i] with { TimeMs = samples[i].TimeMs + offset };
        return samples;
    }

    public void SetSoundOffset(int milliseconds)
    {
        if (Settings.SoundOffsetMs == milliseconds) return;
        Settings.SoundOffsetMs = milliseconds;
        if (Playing && !Settings.SeamlessPlaybackRefresh)
        {
            // The delayed tail can lie past TimeBounds.End; preserve it instead of Play() wrapping to the start.
            var samples = ScheduledSamples();
            Audio.Play(Audio.CurrentTimeMs, Rate, samples);
        }
        else RefreshPlayback();
    }

    // Let delayed sounds at the end play without changing the editor's visible time range.
    internal double PlaybackEndMs => TimeBounds.End + 500 + Math.Max(0, Settings.SoundOffsetMs) * Rate;

    private void ApplyPlaybackGates()
    {
        Audio.SeamlessRefresh = Settings.SeamlessPlaybackRefresh;
        Audio.ClickGain = !Settings.SeamlessPlaybackRefresh || Settings.TimingAssistSound ? 1 : 0;
        Audio.MutedLanes = Settings.SeamlessPlaybackRefresh ? hiddenHitSoundLanes : 0;
    }

    public void SetSeamlessPlaybackRefresh(bool enabled)
    {
        if (Settings.SeamlessPlaybackRefresh == enabled) return;
        Settings.SeamlessPlaybackRefresh = enabled;
        ApplyPlaybackGates();
        hitSoundMap = null;
        if (!Playing) return;
        Audio.SetTimingMode(Settings.ShowTimingRecheck, Settings.TimingSoundFollowsTempo, Settings.TimingSlowPlayback);
        var samples = ScheduledSamples();
        Audio.Play(Audio.CurrentTimeMs, Rate, samples);
    }

    public void RefreshPlayback()
    {
        if (Settings.SeamlessPlaybackRefresh)
        {
            ApplyPlaybackGates();
            Audio.SetTimingMode(Settings.ShowTimingRecheck, Settings.TimingSoundFollowsTempo, Settings.TimingSlowPlayback);
            if (!Playing) return;
            var samples = ScheduledSamples();
            if (!Audio.TryUpdateSamples(samples, Rate)) Audio.Play(Audio.CurrentTimeMs, Rate, samples);
            return;
        }
        hitSoundMap = null;
        Audio.ClearSampleCache();
        if (!Playing) return;
        Time = Audio.CurrentTimeMs;
        Play();
    }

    /// <summary>Stops playback. The playhead settles on the nearest snap tick so whatever is placed next is on the grid.</summary>
    public void Pause()
    {
        if (!Playing) return;
        double time = Audio.CurrentTimeMs;
        Audio.Stop();
        Time = SnapMs(time);
        SelectAtPlayhead();
    }

    public void TogglePlay()
    {
        if (Playing) Pause();
        else Play();
    }

    public void SetRate(double rate)
    {
        bool wasPlaying = Playing;
        if (wasPlaying)
        {
            Time = Audio.CurrentTimeMs;
            Audio.Stop();
        }
        Rate = rate;
        if (wasPlaying) Play();
    }

    // ---------------------------------------------------------------- beat snap

    public bool HasTiming => Project != null && Project.Settings.Timing.Count > 0;

    /// <summary>Beat snap divisor: ticks are 1/Divisor of a beat apart.</summary>
    public int Divisor => Math.Max(1, Project?.Settings.BeatDivisor ?? 4);

    /// <summary>
    /// Whether what is dragged, placed or sought lands on the ticks of the snap grid
    /// (<see cref="AppSettings.Snap"/>). Switched off, every time is taken as it is, to the millisecond.
    /// </summary>
    public bool Snapping => Settings.Snap;

    /// <summary>Nearest tick of the snap grid, whether snapping is on or not; the time itself when the project has no timing.</summary>
    public double GridTime(double time) =>
        Project == null ? time : TimingUtil.Snap(Project.Settings.Timing, time, Divisor);

    /// <summary>Nearest tick of the snap grid as a whole millisecond.</summary>
    public int GridMs(double time) => (int)Math.Round(GridTime(time));

    /// <summary>Where a time lands: on the nearest tick of the snap grid while snapping is on, and where it is otherwise.</summary>
    public double SnapTime(double time) => Snapping ? GridTime(time) : time;

    /// <summary>The same as a whole millisecond, the precision times are stored in.</summary>
    public int SnapMs(double time) => (int)Math.Round(SnapTime(time));

    /// <summary>The playhead position new things are created at: on the grid while snapping is on.</summary>
    public int SnappedTime => SnapMs(Time);

    public double BeatLengthAt(double time)
    {
        var point = Project == null ? null : TimingUtil.At(Project.Settings.Timing, time);
        return point?.BeatLength ?? 500;
    }

    public double BeatLength => BeatLengthAt(Time);

    /// <summary>
    /// The nearest place after a time that something can be put at: the first snap tick after it, in whole
    /// milliseconds; one millisecond later without timing, and while snapping is off.
    /// </summary>
    public int NextTickMs(double time) =>
        HasTiming && Snapping ? (int)Math.Round(TimingUtil.NextTick(Project!.Settings.Timing, time, Divisor)) : (int)Math.Round(time) + 1;

    /// <summary>The same before a time: the last snap tick before it, or one millisecond earlier.</summary>
    public int PreviousTickMs(double time) =>
        HasTiming && Snapping ? (int)Math.Round(TimingUtil.PreviousTick(Project!.Settings.Timing, time, Divisor)) : (int)Math.Round(time) - 1;

    // ---------------------------------------------------------------- from keyframe to keyframe

    /// <summary>
    /// The moments the playhead goes to with the buttons for the previous and the next keyframe: the
    /// keyframes of the property that is picked; with none picked, those of every property of the selected
    /// sprites, their loops included, and of the selected groups. A loop has its keyframes in every pass.
    /// </summary>
    private SortedSet<int> KeyTimes()
    {
        var times = new SortedSet<int>();
        void Add(System.Collections.IList nodes, CommandKind? only, SbGroup? block)
        {
            int origin = block switch { SbLoop loop => loop.Start, SbTrigger trigger => BlockOffset(trigger), _ => 0 };
            times.UnionWith(KeyNavigation.Times(nodes, only, origin, block as SbLoop));
        }

        if (ActiveTrack is { } track)
        {
            var nodes = track.Parameter == '\0' ? track.Nodes : ParameterTrack.Commands(track.Nodes, track.Parameter);
            Add(nodes, track.Kind, BlockOf(track.Nodes));
            return times;
        }
        foreach (var obj in selection)
        {
            if (obj is not SbSprite sprite) continue;
            Add(sprite.Nodes, null, null);
            // A trigger's keyframes are those of the simulated firing at the playhead; unsimulated, it plays nowhere.
            foreach (var node in sprite.Nodes)
                if (node is SbLoop loop) Add(loop.Commands, null, loop);
                else if (node is SbTrigger trigger && TriggerAnchor(trigger, sprite).Simulated) Add(trigger.Commands, null, trigger);
        }
        foreach (var group in selectedGroups) Add(group.Commands, null, null);
        return times;
    }

    /// <summary>
    /// Puts the playhead on the next keyframe (+1) or the one before (-1), exactly, on the grid or not:
    /// see <see cref="KeyTimes"/> for which keyframes those are. With a property picked, the keyframe the
    /// playhead comes to stand on is selected, as with any other way of getting there.
    /// </summary>
    /// <returns>False when there is none that way; the status line says why.</returns>
    public bool JumpKey(int direction)
    {
        if (Project == null) return false;
        var times = KeyTimes();
        if (times.Count == 0)
        {
            bool nothing = ActiveTrack == null && selection.Count == 0 && selectedGroups.Count == 0;
            SetStatus(nothing ? L.T("status.keyJumpNothing") : L.T("status.keyJumpNone"));
            return false;
        }
        int? target = null;
        foreach (int time in direction > 0 ? times : times.Reverse())
        {
            if (direction > 0 ? time <= Time + 0.001 : time >= Time - 0.001) continue;
            target = time;
            break;
        }
        if (target == null)
        {
            SetStatus(L.T("status.keyJumpEnd"));
            return false;
        }
        Seek(target.Value);
        return true;
    }

    // ---------------------------------------------------------------- keyframes

    /// <summary>What a command of a kind has to say for a sprite to be in a given state.</summary>
    public static double[] StateValues(CommandKind kind, in SpriteState st, bool shown) => kind switch
    {
        // A sprite without commands is not shown at all; its opacity is that of one that is.
        CommandKind.F => new[] { shown ? Math.Round(st.Opacity, 3) : 1 },
        CommandKind.M => new[] { Math.Round(st.X, 2), Math.Round(st.Y, 2) },
        CommandKind.MX => new[] { Math.Round(st.X, 2) },
        CommandKind.MY => new[] { Math.Round(st.Y, 2) },
        CommandKind.S => new[] { Math.Round(st.Scale, 4) },
        CommandKind.V => new[] { Math.Round(st.ScaleX, 4), Math.Round(st.ScaleY, 4) },
        CommandKind.R => new[] { Math.Round(st.Rotation, 5) },
        CommandKind.C => new[] { Math.Round(st.R), Math.Round(st.G), Math.Round(st.B) },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>What a command of a kind has to say for a group of sprites to be in a given state.</summary>
    public static double[] StateValues(CommandKind kind, in GroupState state) => kind switch
    {
        CommandKind.M => new[] { Math.Round(state.X, 2), Math.Round(state.Y, 2) },
        CommandKind.MX => new[] { Math.Round(state.X, 2) },
        CommandKind.MY => new[] { Math.Round(state.Y, 2) },
        CommandKind.S => new[] { Math.Round(state.Scale, 4) },
        CommandKind.R => new[] { Math.Round(state.Rotation, 5) },
        CommandKind.Z => new[] {state.Z}, CommandKind.RX => new[] {state.RX}, CommandKind.RY => new[] {state.RY},
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// The place among the keyframes of a list where the playhead stands, or -1 when it is on none of them.
    /// </summary>
    /// <param name="offset">Start of the loop or trigger the commands belong to; their times count from there.</param>
    public int KeyAtPlayhead(System.Collections.IList nodes, CommandKind kind, int offset = 0)
    {
        int now = (int)Math.Round(Time) - offset;
        return KeyTrack.Read(nodes, kind)?.FindIndex(k => k.Time == now) ?? -1;
    }

    /// <summary>
    /// The keyframe of a list at the playhead, or null when it stands on none. For commands that run at
    /// the same time, which have no keyframes to count, it is the place where the one that has its way
    /// gives the value (<see cref="KeyTrack.At(System.Collections.IList, CommandKind, int)"/>).
    /// </summary>
    public Keyframe? PlayheadKey(System.Collections.IList nodes, CommandKind kind, int offset = 0)
    {
        int now = (int)Math.Round(Time) - offset;
        var keys = KeyTrack.Read(nodes, kind);
        return keys != null ? keys.Find(k => k.Time == now) : KeyTrack.At(nodes, kind, now);
    }

    /// <summary>
    /// The keyframe at the playhead for a property whose commands run at the same time: the command that
    /// has its way there gets to give its value at that moment, see <see cref="KeyTrack.Pin"/>. The
    /// property becomes the one the preview adjusts, and the commands that meet there are selected, so
    /// that it can be seen which of them a change goes to.
    /// </summary>
    /// <param name="time">Where a new one goes, counted from <paramref name="offset"/>: on the grid.</param>
    /// <param name="changed">Commands were added or cut; the caller says so to whoever owns them.</param>
    private KeyResult PinKey(System.Collections.IList nodes, CommandKind kind, ref int time, double[] value, int offset, out bool changed)
    {
        changed = false;
        // The playhead may stand on one already that is not on the grid.
        int now = (int)Math.Round(Time) - offset;
        if (KeyTrack.At(nodes, kind, now) != null) time = now;

        // What is added may not run together with a kind it is at odds with, any more than a keyframe may.
        if (CommandClash.Rivals(kind).Length > 0 && CommandClash.Overlaps(nodes, kind).Count == 0)
        {
            var trial = TrialOf(nodes, kind);
            if (KeyTrack.Pin(trial, kind, time, value, out var tried) != null && tried == KeyTrack.Pinned.Added && ReportClash(trial, kind, offset))
                return KeyResult.Refused;
        }

        var key = KeyTrack.Pin(nodes, kind, time, value, out var outcome);
        if (key == null)
        {
            if (outcome != KeyTrack.Pinned.Blocked) return KeyResult.NotKeyframes;
            ShowMessage(L.T("clash.title"), L.T("track.blocked", Ui.Theme.KindLabel(kind).Replace("  ", " "), Ui.Theme.FormatTime(offset + time)));
            return KeyResult.Refused;
        }
        changed = outcome == KeyTrack.Pinned.Added;
        SelectTrack(nodes, kind);
        foreach (var command in new[] { key.In, key.Out, key.Point })
            if (command != null) SelectedCommands.Add(command);
        return KeyResult.Done;
    }

    /// <summary>
    /// Adds a keyframe at the playhead to a property of a sprite, or of one of its loops and triggers. It
    /// holds what the property is at that moment, so that the picture stays as it is until the value of the
    /// keyframe is changed. The keyframe is selected; one that is there already is only selected.
    ///
    /// In a loop the playhead stands for the moment of the pass it is in, and the keyframe is that moment
    /// in every pass. Past the end of the loop it counts on in the last pass, so a keyframe there makes
    /// every pass longer: that is how a loop is given its length.
    /// </summary>
    public KeyResult AddKey(SbSprite sprite, SbGroup? group, CommandKind kind)
    {
        if (IsProtectedBeatmapBackground(sprite)) return KeyResult.Refused;
        bool depth = SbInfo.DepthKinds.Contains(kind);
        if(depth && (sprite.Group==null || group!=null)) return KeyResult.Refused;
        System.Collections.IList nodes = depth ? sprite.DepthCommands : group == null ? sprite.Nodes : group.Commands;
        int offset = BlockOffset(group);
        int index = KeyAtPlayhead(nodes, kind, offset);
        if (index < 0)
        {
            // New things stand on the grid. In a loop the grid may lie just outside of it where the
            // playhead is still inside: there the end of the loop is as near as it gets.
            int snapped = SnappedTime;
            var loop = group as SbLoop;
            if (loop != null && Time >= loop.AbsoluteStart && Time <= loop.AbsoluteEnd) snapped = Math.Clamp(snapped, loop.AbsoluteStart, loop.AbsoluteEnd);
            int pass = loop?.IterationAt(snapped) ?? 0;
            if (loop != null) offset = loop.StartAt(snapped);
            // Times inside a loop or trigger count from its start and do not go back beyond it.
            int time = Math.Max(group == null ? int.MinValue : 0, snapped - offset);
            var timeline = sprite.Timeline;
            // In a trigger the moment is held as the preview shows it, with what the trigger does there.
            var state = group is SbTrigger ? PreviewState(sprite, offset + time) : timeline.Evaluate(offset + time, sprite);
            double[] value = depth ? new[]{GroupSpace.Value(sprite.DepthCommands,kind,offset+time,0)} : StateValues(kind, state, timeline.HasCommands);
            if (RefusesKey(nodes, kind, time, value, offset)) return KeyResult.Refused;
            index = KeyTrack.Insert(nodes, kind, time, value);
            var result = KeyResult.Done;
            bool changed = true;
            // Commands that run at the same time have no keyframes to add one to.
            if (index < 0) result = PinKey(nodes, kind, ref time, value, offset, out changed);
            if (changed) Touch(sprite);
            if (result != KeyResult.Done) return result;
            // The playhead stands on what was just added, so that the preview shows and edits that moment.
            // A keyframe past the end of a loop has made its passes longer, and the pass begins elsewhere now.
            if (loop != null) offset = loop.Start + pass * loop.IterationLength;
            if (Time != offset + time) Seek(offset + time);
            if (index < 0) return result;
        }
        SelectKey(new KeyRef(nodes, kind, index));
        return KeyResult.Done;
    }

    /// <summary>The same for a property of a group of sprites.</summary>
    public KeyResult AddKey(SbObjectGroup group, CommandKind kind)
    {
        if(SbInfo.CameraKinds.Contains(kind)) return AddCameraKey(group,kind);
        int index = KeyAtPlayhead(group.Commands, kind);
        if (index < 0)
        {
            int time = SnappedTime;
            double[] value = StateValues(kind, group.Timeline.Evaluate(time, group));
            if (RefusesKey(group.Commands, kind, time, value, 0)) return KeyResult.Refused;
            index = KeyTrack.Insert(group.Commands, kind, time, value);
            if (index < 0)
            {
                var pinned = PinKey(group.Commands, kind, ref time, value, 0, out bool changed);
                if (changed) TouchGroup(group);
                if (pinned == KeyResult.Done)
                {
                    group.Expanded = true;
                    if (Time != time) Seek(time);
                }
                return pinned;
            }
            group.Expanded = true;
            TouchGroup(group);
            if (Time != time) Seek(time);
        }
        SelectKey(new KeyRef(group.Commands, kind, index));
        return KeyResult.Done;
    }

    /// <summary>Record a sample at music time, without snapping or seeking the playback clock.</summary>
    public bool RecordFreehandKey(System.Collections.IList nodes, CommandKind kind, int time, double[] value, int offset, int? previous)
    {
        if (RefusesKey(nodes, kind, time, value, offset)) return false;
        int index = KeyTrack.Insert(nodes, kind, time, value);
        if (index < 0) return false;
        if (previous is { } from)
        {
            // Use straight interpolation between hand-recorded samples, retaining the gesture's timing.
            var keys = KeyTrack.Read(nodes, kind)!;
            for (int i = 0; i + 1 < keys.Count; i++)
                if (keys[i].Time >= from && keys[i].Time < time) KeyTrack.SetEasing(nodes, kind, i, 0);
        }
        SelectedKeys.Clear();
        SelectedCommands.Clear();
        return true;
    }

    /// <summary>What became of a request for a keyframe.</summary>
    public enum KeyResult
    {
        /// <summary>It is there and selected.</summary>
        Done,
        /// <summary>The commands of that kind cannot be read as keyframes, and no value could be set among them either.</summary>
        NotKeyframes,
        /// <summary>It would run together with commands it may not run with; a message says so.</summary>
        Refused,
    }

    /// <summary>
    /// Whether a new keyframe would make the commands of its kind run at the same time as commands they may
    /// not run with: a move with MX or MY, the scale with the vector scale. If so, a message says why and
    /// the keyframe is not to be added. Imported conflicts can still be edited, but a new command must
    /// never share a rival's exact start, even when another conflict already exists in the list.
    /// </summary>
    private bool RefusesKey(System.Collections.IList nodes, CommandKind kind, int time, double[] value, int offset)
    {
        if (CommandClash.HasRivalStart(nodes, kind, time)) return ReportClash(kind, offset + time);
        if (CommandClash.Rivals(kind).Length == 0 || CommandClash.Overlaps(nodes, kind).Count > 0) return false;
        var trial = TrialOf(nodes, kind);
        return KeyTrack.Insert(trial, kind, time, value) >= 0 && ReportClash(trial, kind, offset);
    }

    /// <summary>The same check for a command that is added as it is.</summary>
    public bool RefusesCommand(System.Collections.IList nodes, SbCommand added, int offset = 0)
    {
        if (CommandClash.HasRivalStart(nodes, added.Kind, added.Start)) return ReportClash(added.Kind, offset + added.Start);
        if (CommandClash.Rivals(added.Kind).Length == 0 || CommandClash.Overlaps(nodes, added.Kind).Count > 0) return false;
        var trial = TrialOf(nodes, added.Kind);
        trial.Add(added);
        return ReportClash(trial, added.Kind, offset);
    }

    /// <summary>The same check for a transition where a keyframe only held its value until the next one.</summary>
    public bool RefusesEasing(System.Collections.IList nodes, CommandKind kind, int index, int easing, int offset = 0)
    {
        if (easing == Keyframe.Hold || CommandClash.Rivals(kind).Length == 0 || CommandClash.Overlaps(nodes, kind).Count > 0) return false;
        var trial = TrialOf(nodes, kind);
        return KeyTrack.SetEasing(trial, kind, index, easing) && ReportClash(trial, kind, offset);
    }

    /// <summary>The commands of a list, those of one kind as copies, for trying out a change of that kind.</summary>
    private static List<SbNode> TrialOf(System.Collections.IList nodes, CommandKind kind)
    {
        var trial = new List<SbNode>();
        foreach (object? node in nodes)
            if (node is SbCommand c) trial.Add(c.Kind == kind ? c.CloneCommand() : c);
        return trial;
    }

    private bool ReportClash(List<SbNode> trial, CommandKind kind, int offset)
    {
        var overlaps = CommandClash.Overlaps(trial, kind);
        if (overlaps.Count == 0) return false;
        return ReportClash(kind, offset + overlaps[0].Start);
    }

    private bool ReportClash(CommandKind kind, int time)
    {
        // The names of the kinds are spaced for standing in a column; in a sentence one space will do.
        string Name(CommandKind k) => Ui.Theme.KindLabel(k).Replace("  ", " ");
        string others = string.Join(" / ", CommandClash.Rivals(kind).Select(Name));
        ShowMessage(L.T("clash.title"), L.T("clash.add", Name(kind), others, Ui.Theme.FormatTime(time)));
        return true;
    }

    /// <summary>
    /// The times a keyframe may be moved to: between its neighbours, and a snap tick away from each where
    /// there is room for that. Two keyframes at one time are a jump in the value, which is not what moving
    /// one along the timeline is meant to make.
    /// </summary>
    /// <param name="offset">Start of the loop or trigger the keyframes belong to; their times count from there.</param>
    public (int Min, int Max) KeyTimeRange(IReadOnlyList<Keyframe> keys, int index, int offset = 0)
    {
        int time = keys[index].Time, min = int.MinValue, max = int.MaxValue;
        if (index > 0)
        {
            int before = keys[index - 1].Time;
            min = NextTickMs(offset + before) - offset;
            // Nearer to it than a tick already: then any millisecond between the two will do.
            if (min > time) min = Math.Min(before + 1, time);
        }
        if (index + 1 < keys.Count)
        {
            int after = keys[index + 1].Time;
            max = PreviousTickMs(offset + after) - offset;
            if (max < time) max = Math.Max(after - 1, time);
        }
        return (min, max);
    }

    /// <summary>The snap tick a number of beats after <paramref name="start"/>.</summary>
    public int AfterBeats(int start, double beats) => Math.Max(start, SnapMs(start + BeatLengthAt(start) * beats));

    /// <summary>
    /// Time reached by dragging a time field sideways: one snap tick every ten pixels, so the result is
    /// always on the grid no matter how slowly the mouse moves.
    /// </summary>
    public int DragTime(double start, double pixels)
    {
        if (!HasTiming) return (int)Math.Round(start + pixels * 5);
        // With snapping off, a millisecond for every pixel: fine enough to set any time.
        if (!Snapping) return (int)Math.Round(start + pixels);
        return SnapMs(start + pixels / 10 * BeatLengthAt(start) / Divisor);
    }

    // ---------------------------------------------------------------- autosave

    private const long AutosaveIntervalMs = 120_000;
    private long lastAutosave = Environment.TickCount64;

    /// <summary>Copy of the unsaved storyboard, written every couple of minutes and when the program crashes.</summary>
    public string? AutosaveFile => Project == null ? null : Path.Combine(Project.Folder, "storyboard.autosave.osb");

    public bool HasAutosave => AutosaveFile != null && File.Exists(AutosaveFile);

    public void WriteAutosave()
    {
        if (Project == null || AppSettings.ReadOnly) return;
        try
        {
            File.WriteAllText(AutosaveFile!, OsbWriter.WriteFile(Doc, new OsbWriteOptions { IncludeEditorMeta = true, ConcreteVariableReferences = true }));
        }
        catch (IOException ex)
        {
            AppSettings.Log(L.T("log.autosaveFailed", ex.Message));
        }
        lastAutosave = Environment.TickCount64;
    }

    /// <summary>Replaces the storyboard with the autosaved one. Can be undone.</summary>
    public void RestoreAutosave()
    {
        if (!HasAutosave) return;
        try
        {
            var parsed = OsbParser.Parse(File.ReadAllText(AutosaveFile!));
            ReplaceAll(parsed.Storyboard);
            SetStatus(L.T("status.autosaveRestored", parsed.Storyboard.Objects.Count));
        }
        catch (Exception ex)
        {
            ShowMessage(L.T("msg.restoreFailed"), ex.Message);
        }
    }

    // ---------------------------------------------------------------- per-frame

    public void Update()
    {
        UpdatePlugins();
        PrepareSimulationFrame();
        Audio.UpdateDevices();
        if (Settings.AudioOutputDevice != Audio.OutputDeviceId)
        {
            Settings.AudioOutputDevice = Audio.OutputDeviceId;
            Settings.Save();
        }
        if (opening is { IsCompleted: true }) FinishOpening();
        UpdateBeatmapReference();
        UpdateTimingPlayback();
        Audio.UpdateTimingPlayback();
        // Uploads are kept short for the sake of the picture; behind the loading screen there is none to keep smooth.
        Textures.Update(imagesToLoad > 0 ? 12 : 2);
        if (Textures.PendingCount == 0) imagesToLoad = 0;

        if (Dirty && !PreviewingEdit && !pendingCommit && Environment.TickCount64 - lastAutosave > AutosaveIntervalMs) WriteAutosave();

        if (Playing)
        {
            Time = Audio.CurrentTimeMs;
            if (Time > PlaybackEndMs) Pause();
        }

        if (VideoExport is { Running: true } export)
        {
            export.Step(ExportStepMs);
            if (export.Finished) SetStatus(L.T("videoexport.status", export.Request.Path));
        }

        long changed = Interlocked.Read(ref assetsChangedTicks);
        if (changed != 0 && Environment.TickCount64 - changed > 300 && Project != null)
        {
            Interlocked.Exchange(ref assetsChangedTicks, 0);
            bool musicChanged = false;
            while (changedFiles.TryDequeue(out string? file))
            {
                string? asset = Project.ToAssetPath(file);
                if (asset == null) continue;
                Textures.Invalidate(asset);
                imageSizes.Clear();
                // Listed first: each Touch makes the next UsesDepth walk its whole tree again.
                var depthMembers = Doc.AllObjects.OfType<SbSprite>().Where(s => GroupSpace.UsesDepth(s.Group)).ToList();
                foreach (var sprite in depthMembers) sprite.Touch();
                // A video that was added or replaced is read anew, also one that was missing so far.
                Videos.Forget(Project.AssetFullPath(asset));
                if (asset.Equals(Project.Settings.Audio, StringComparison.OrdinalIgnoreCase)) musicChanged = true;
            }
            Audio.ClearSampleCache();
            RefreshAssets();
            if (musicChanged) ReloadMusic();
        }
    }

    /// <summary>Call at the end of a frame; <paramref name="interacting"/> is true while a drag or text edit is in progress.</summary>
    public void EndFrame(bool interacting)
    {
        if (!interacting) CommitEdits();
    }

    // ---------------------------------------------------------------- assets

    public IReadOnlyList<AssetInfo> Assets => assets;
    /// <summary>The folders below the assets folder, the empty ones among them.</summary>
    public IReadOnlyList<string> AssetFolders => assetFolders;
    public int AssetsRevision { get; private set; }

    public void RefreshAssets()
    {
        assets = Project?.ScanAssets() ?? new List<AssetInfo>();
        assetFolders = Project?.ScanAssetFolders() ?? new List<string>();
        if (Project != null)
        {
            assets.RemoveAll(asset => !AssetPathPolicy.Allows(Project, asset.Path));
            assetFolders.RemoveAll(folder => !AssetPathPolicy.Allows(Project, folder));
        }
        usage = null;
        AssetsRevision++;
    }

    /// <summary>Number of storyboard objects that use a file.</summary>
    /// <param name="recount">
    /// False answers from the last count even if the storyboard was edited since. Counting walks every
    /// object, so a caller passes false while the edits cannot have changed which files are used.
    /// </param>
    public int UsageOf(string assetPath, bool recount = true)
    {
        if (Project == null) return 0;
        if (usage == null || (recount && usageRevision != DocRevision))
        {
            var files = new HashSet<string>(assets.Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
            usage = AssetUsage.CountByFile(Doc, files.Contains);
            usageRevision = DocRevision;
        }
        return usage.GetValueOrDefault(assetPath);
    }

    private void WatchAssets()
    {
        watcher?.Dispose();
        watcher = null;
        if (Project == null) return;
        try
        {
            watcher = new FileSystemWatcher(Project.AssetsDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            void OnChanged(object sender, FileSystemEventArgs e)
            {
                changedFiles.Enqueue(e.FullPath);
                Interlocked.Exchange(ref assetsChangedTicks, Environment.TickCount64);
            }
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += (s, e) =>
            {
                changedFiles.Enqueue(e.OldFullPath);
                OnChanged(s, e);
            };
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            AppSettings.Log(L.T("log.watchFailed", ex.Message));
        }
    }

    public void ReloadMusic()
    {
        if (Project == null)
        {
            Audio.SetMusic(null);
            return;
        }
        string audio = Project.Settings.Audio;
        Audio.SetMusic(audio.Length > 0 ? Project.AssetFullPath(audio) : null);
        if (Audio.Error != null) SetStatus(Audio.Error);
    }

    /// <summary>Copies files into the project and returns their asset paths.</summary>
    /// <param name="folder">
    /// The folder below assets to copy into. Without one, images go to "sb" and everything else to the
    /// assets root.
    /// </param>
    public List<string> ImportFiles(IEnumerable<string> files, string? folder = null)
    {
        var imported = new List<string>();
        if (Project == null) return imported;
        if (folder != null && !AssetPathPolicy.Allows(Project, folder))
        {
            SetStatus(AssetPathPolicy.Message(folder));
            return imported;
        }
        foreach (string file in files)
        {
            try
            {
                if (Directory.Exists(file))
                {
                    imported.AddRange(ImportFiles(Directory.EnumerateFiles(file), folder));
                    continue;
                }
                if (!File.Exists(file)) continue;
                var kind = Project.KindOf(file);
                string asset = Project.ImportAsset(file, folder ?? (kind == AssetKind.Image ? "sb" : ""));
                imported.Add(asset);

                string ext = Path.GetExtension(asset).ToLowerInvariant();
                if (Project.Settings.Audio.Length == 0 && (ext == ".mp3" || ext == ".ogg"))
                {
                    Project.Settings.Audio = asset;
                    MarkSettingsChanged();
                    ReloadMusic();
                }
            }
            catch (Exception ex)
            {
                SetStatus(L.T("status.importFailed", Path.GetFileName(file), ex.Message));
            }
        }
        if (imported.Count > 0)
        {
            RefreshAssets();
            SetStatus(L.T("status.imported", imported.Count));
        }
        return imported;
    }

    // ---------------------------------------------------------------- project files

    /// <summary>What the loading screen shows about the project that is being opened.</summary>
    /// <param name="Images">False while the storyboard is being read, true while the images are being loaded.</param>
    /// <param name="Fraction">How far that step has got, from 0 to 1.</param>
    public readonly record struct LoadState(string Name, bool Images, float Fraction, int Done, int Total, string? Operation = null);

    private Task<(Project Project, Snapshot Initial)>? opening;
    private string openingPath = "", loadingName = "";
    private Action? openingDone;
    private volatile float openingFraction;
    /// <summary>How many images were asked for when loading began; above zero until they are all in.</summary>
    private int imagesToLoad;

    /// <summary>
    /// A project is being opened: its storyboard is read, then all its images are loaded. The preview shows
    /// nothing of it before both are done, so nothing is ever shown half loaded.
    /// </summary>
    public bool IsLoading => pluginsLoading || opening != null || beatmapRefresh != null || imagesToLoad > 0 || SimulationPreparing;

    public LoadState? Loading
    {
        get
        {
            if (SimulationPreparing) return new LoadState(L.T("simulation.title"), false, (float)simulationPrepared / simulationPreparing.Length, 0, 0, L.T("simulation.preparing"));
            if(pluginsLoading) return new LoadState(L.T("menu.plugins"),false,0,0,0,L.T("plugin.importing"));
            if (opening != null) return new LoadState(loadingName, false, openingFraction, 0, 0);
            if (beatmapRefresh != null) return new LoadState(loadingName, false, 0, 0, 0);
            if (imagesToLoad == 0) return null;
            int done = Math.Max(0, imagesToLoad - Textures.PendingCount);
            return new LoadState(loadingName, true, (float)done / imagesToLoad, done, imagesToLoad);
        }
    }

    /// <summary>Waits for a project that is being opened, for when no frames are drawn to show the progress.</summary>
    public void FinishLoading()
    {
        while (IsLoading)
        {
            Update();
            if (IsLoading) Thread.Sleep(1);
        }
    }

    private void Attach(Project project, Snapshot? initial = null)
    {
        SetScriptDiagnostics(Array.Empty<Diagnostic>());
        Audio.Stop();
        ReleasePlugins();
        Project = project;
        ResetBeatmapReference();
        beatmapFile = null;
        RefreshTriggerSimulation();
        Textures.Clear();
        Videos.Close();
        Textures.Resolve = project.ImageFullPath;
        imageSizes.Clear(); BakeOptions.DefaultImageSize=ImageSize;
        Audio.ClearSampleCache();
        ClearSelection();
        OpenGroup = null;
        insertionLayer = SbLayer.Foreground;
        DocRevision++;
        Time = project.Settings.EditorTime;
        ResetHistory(initial);
        RefreshAssets();
        WatchAssets();
        SyncPlugins();pluginsLoading=ImportingPlugins>0;cleanupPlugins=true;
        ReloadMusic();
        Settings.AddRecent(project.Folder);
        LoadImages();
    }

    /// <summary>
    /// Asks for every image of the project: what the storyboard shows, and whatever else is in the assets
    /// folder, as any of it may be put to use. <see cref="IsLoading"/> holds until they are all in.
    /// </summary>
    private void LoadImages()
    {
        if (Project == null || Textures==null) return;
        loadingName = string.IsNullOrEmpty(Project.Settings.Name) ? Path.GetFileName(Project.Folder) : Project.Settings.Name;
        Textures.Preload(Project.Settings.Background);
        foreach (var sprite in Doc.Sprites)
            foreach (string file in AssetUsage.FilesOf(sprite)) Textures.Preload(file);
        foreach (var asset in assets)
            if (asset.Kind == AssetKind.Image) Textures.Preload(asset.Path);
        imagesToLoad = Textures.PendingCount;
    }

    /// <param name="timing">BPM and offset for the snap grid; a demo or an imported beatmap brings its own.</param>
    /// <param name="beatmapFolder">A beatmap folder to import, or any file in it.</param>
    /// <param name="beatmapTiming">
    /// The timing of the beatmap as the user changed it before making the project. It takes the place of
    /// what the difficulty that is shown first has in its file; null to leave that as it is.
    /// </param>
    public bool NewProject(string parentFolder, string name, bool demo, string? beatmapFolder, TimingPoint? timing = null, List<TimingPoint>? beatmapTiming = null)
    {
        try
        {
            string safeName = name.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c.ToString(), "");
            if (safeName.Length == 0) throw new ArgumentException(L.T("project.emptyName"));

            var project = Project.Create(Path.Combine(parentFolder, safeName), name.Trim());
            if (demo) DemoProject.Populate(project);
            string? note = null;
            if (!string.IsNullOrWhiteSpace(beatmapFolder))
            {
                var report = BeatmapImporter.Import(project, beatmapFolder!, true);
                note = DescribeImport(report);
                // Saving writes it to the difficulty that is shown, which is the one it was read from.
                if (beatmapTiming != null && report.OsuFiles.Count > 0)
                    project.Settings.Timing = beatmapTiming.OrderBy(t => t.Offset).Select(t => new TimingPoint { Offset = t.Offset, BeatLength = t.BeatLength, Meter = t.Meter }).ToList();
            }
            if (timing != null && project.Settings.Timing.Count == 0) project.Settings.Timing.Add(timing);
            project.Save();
            Attach(project);
            SetStatus(L.T("status.created", project.Folder));
            if (note != null) ShowMessage(L.T("msg.importBeatmap"), note);
            return true;
        }
        catch (Exception ex)
        {
            ShowMessage(L.T("msg.newFailed"), ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Opens a project. Reading a large storyboard takes seconds, so that is done off the UI thread while
    /// <see cref="Loading"/> tells how far it is. <paramref name="opened"/> runs once the project is in place.
    /// </summary>
    public void OpenProject(string path, Action? opened = null)
    {
        if (IsLoading) return;
        string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path)) ?? path;
        loadingName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        openingPath = path;
        openingDone = opened;
        openingFraction = 0;
        opening = Task.Run(() =>
        {
            // Parsing is about half of the work on a large storyboard. The rest would otherwise hold up the
            // first frames: the timelines every panel asks for, and the copy the undo history starts from.
            var project = Project.Load(path, read => openingFraction = (float)read * 0.5f);
            var all = project.Storyboard.Objects;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] is SbSprite sprite) _ = sprite.Timeline;
                if ((i & 0x3FF) == 0) openingFraction = 0.5f + 0.4f * i / all.Count;
            }
            openingFraction = 0.9f;
            var document = project.Storyboard;
            var (objects, parked, groups) = timelineFolds.Clone(document.Objects, document.Parked, document.Groups, skipGenerated: true);
            return (project, new Snapshot(objects, parked, groups, project.Settings.Difficulty, Array.Empty<int>(), Array.Empty<int>(), -1)
            { Simulation = CaptureSimulation(project.Settings), Timing = TimingHistoryState.Capture(project), Variables = document.Variables.ToArray() });
        });
    }

    private void FinishOpening()
    {
        var task = opening!;
        var opened = openingDone;
        opening = null;
        openingDone = null;
        try
        {
            // Rethrows whatever reading the project failed with.
            var (project, initial) = task.GetAwaiter().GetResult();
            Attach(project, initial);
            if (project.LoadDiagnostics.Count > 0)
            {
                var lines = project.LoadDiagnostics.Take(15).Select(d => L.T("common.line", d.Line, d.Message));
                ShowMessage(L.T("msg.unrecognised.title"), string.Join("\n", lines) +
                    (project.LoadDiagnostics.Count > 15 ? "\n" + L.T("msg.unrecognised.more", project.LoadDiagnostics.Count) : "") +
                    "\n\n" + L.T("msg.unrecognised.note"));
            }
            else if (HasAutosave && File.GetLastWriteTimeUtc(AutosaveFile!) > File.GetLastWriteTimeUtc(project.StoryboardFile))
            {
                ShowMessage(L.T("msg.autosaveFound.title"), L.T("msg.autosaveFound.text"));
            }
            SetStatus(L.T("status.opened", project.Folder));
            opened?.Invoke();
        }
        catch (Exception ex)
        {
            Settings.RecentProjects.RemoveAll(p => string.Equals(p, openingPath, StringComparison.OrdinalIgnoreCase));
            Settings.Save();
            ShowMessage(L.T("msg.openFailed"), ex.Message);
        }
    }

    public bool Save()
    {
        if (Project == null || PreviewingEdit) return false;
        try
        {
            CommitEdits();
            Project.RecordDiagnostics(ScriptDiagnostics.Select(d => d.Diagnostic), DiagnosticOrigin.Script);
            Project.Settings.EditorTime = Math.Round(Time);
            Project.Save();
            savedStateToken = committed?.StateToken;
            settingsDirty = false;pluginDirty=false;
            // The saved file is now the newest copy.
            if (HasAutosave) File.Delete(AutosaveFile!);
            SetStatus(L.T("status.savedAt", DateTime.Now.ToString("HH:mm:ss")));
            return true;
        }
        catch (Exception ex)
        {
            ShowMessage(L.T("msg.saveFailed"), ex.Message);
            return false;
        }
    }

    public void CloseProject()
    {
        ReleasePlugins();
        SetScriptDiagnostics(Array.Empty<Diagnostic>());
        Audio.Stop();
        Audio.SetMusic(null);
        watcher?.Dispose();
        watcher = null;
        ClearVideoExport();
        Project = null;
        RefreshTriggerSimulation();
        ResetBeatmapReference();
        beatmapFile = null;
        DocRevision++;
        Textures.Clear();
        Videos.Close();
        Textures.Resolve = null;
        imagesToLoad = 0;
        ClearSelection();
        assets = new List<AssetInfo>();
        assetFolders = new List<string>();
        AssetsRevision++;
        ResetHistory();
    }

    /// <summary>Marks project settings (not the storyboard) as changed.</summary>
    public void MarkSettingsChanged() => settingsDirty = true;

    /// <param name="path">The beatmap folder, or any file in it.</param>
    public void ImportBeatmap(string path, bool replace)
    {
        if (Project == null) return;
        try
        {
            CommitEdits();
            var report = BeatmapImporter.Import(Project, path, replace);
            ResetBeatmapReference();
            // The difficulty files are now read directly from this reference.
            beatmapFile = null;
            ClearSelection();
            Touch();
            RefreshAssets();
            ReloadMusic();
            Videos.Close();
            LoadImages();
            ShowMessage(L.T("msg.importBeatmap"), DescribeImport(report));
        }
        catch (Exception ex)
        {
            ShowMessage(L.T("msg.importBeatmapFailed"), ex.Message);
        }
    }

    private static string DescribeImport(BeatmapImportReport report)
    {
        var lines = new List<string>
        {
            L.T("import.summary", report.Objects, report.FilesCopied),
        };
        if (report.OsuFiles.Count > 0) lines.Add(L.T("import.osuFiles", report.OsuFiles.Count, report.OsuFiles[0]));
        if (report.OsbFile != null) lines.Add(L.T("import.osbFile", Path.GetFileName(report.OsbFile)));
        // Only the .osb that goes with the difficulties by its name is read, as in osu!; the others are named.
        if (report.OsbExpected != null) lines.Add(L.T("import.osbUnmatched", report.OsbExpected));
        if (report.OsbSkipped.Count > 0)
            lines.Add(L.T("import.osbSkipped", report.OsbSkipped.Count) + "\n  " + string.Join("\n  ", report.OsbSkipped.Take(8)) +
                      (report.OsbSkipped.Count > 8 ? "\n  \u2026" : ""));
        if (report.Audio != null) lines.Add(L.T("import.audioFile", report.Audio));
        if (report.MissingFiles.Count > 0)
            lines.Add("\n" + L.T("import.missing", report.MissingFiles.Count) + "\n  " + string.Join("\n  ", report.MissingFiles.Take(12)));
        if (report.Diagnostics.Count > 0)
            lines.Add("\n" + L.T("import.unparsed", report.Diagnostics.Count) + "\n  " + string.Join("\n  ", report.Diagnostics.Take(12).Select(d => L.T("common.line", d.Line, d.Message))));
        return string.Join("\n", lines);
    }

    // ---------------------------------------------------------------- object commands

    public double? ImageFitScale(SbSprite sprite, bool waitForTexture = false)
    {
        string path = sprite.IsAnimation ? SbSprite.FramePath(sprite.Path, 0) : sprite.Path;
        var texture = waitForTexture ? Textures.Require(path) : Textures.Get(path);
        if (texture == null || Project == null) return null;
        return Math.Max((Project.Settings.Widescreen ? 854d : 640d) / texture.Width, 480d / texture.Height);
    }

    public SbSprite AddSprite(string assetPath, double x, double y, int frameCount = 0, SbLayer? layer = null)
    {
        int start = SnappedTime;
        var sprite = new SbSprite
        {
            Layer = layer ?? InsertionLayer,
            Path = assetPath,
            Origin = SbOrigin.Centre,
            X = Math.Round(x),
            Y = Math.Round(y),
            IsAnimation = frameCount > 1,
            FrameCount = Math.Max(1, frameCount),
            Expanded = true,
            // What is added while a group is open is added to that group.
            Group = OpenGroup,
        };
        sprite.Nodes.Add(new SbCommand(CommandKind.F, 0, start, AfterBeats(start, 4), new[] { 1.0 }));
        if (Settings.AutoScaleAddedImages && ImageFitScale(sprite, waitForTexture: true) is { } scale)
            sprite.Nodes.Add(new SbCommand(CommandKind.S, 0, start, start, new[] { scale }));
        Doc.Objects.Add(sprite);
        SelectOnly(sprite);
        Touch(sprite);
        return sprite;
    }

    public SbSample AddSample(string assetPath)
    {
        var sample = new SbSample { Path = assetPath, Time = SnappedTime, Layer = SbLayer.Background };
        Doc.Objects.Add(sample);
        SelectOnly(sample);
        Touch();
        return sample;
    }

    /// <summary>Adds a video that begins at the playhead. It belongs to every difficulty until it is given to one.</summary>
    public SbVideo AddVideo(string assetPath)
    {
        var video = new SbVideo { Path = assetPath, Time = SnappedTime };
        Doc.Objects.Add(video);
        SelectOnly(video);
        Touch();
        return video;
    }

    public SbSprite AddBeatmapBackground(string assetPath)
    {
        var background = new SbSprite
        {
            Path = OsbParser.CleanPath(assetPath), Layer = SbLayer.Background,
            Origin = SbOrigin.TopLeft, X = 0, Y = 0, BeatmapBackgroundPlaceholder = true,
            Difficulty = Project!.CurrentDifficulty?.File,
        };
        if (background.Path.Length == 0) throw new ArgumentException("A background image is required.", nameof(assetPath));
        CommitEdits();
        Doc.Objects.Add(background);
        SelectOnly(background);
        Touch(background);
        return background;
    }

    public void ClearBeatmapBackground(SbSprite background)
    {
        if (!IsProtectedBeatmapBackground(background) || !Doc.Objects.Contains(background)) return;
        CommitEdits();
        Doc.Objects.Remove(background);
        SelectEmptyBeatmapBackground();
        Touch();
    }

    public bool IsProtectedBeatmapBackground(SbObject obj) => Project?.IsBeatmapBackgroundPlaceholder(obj) == true;

    /// <summary>Deletes only the commands of one property in its owning sprite, loop, trigger or group.</summary>
    public void DeleteCommandTrack(System.Collections.IList nodes, CommandKind kind, char parameter = '\0')
    {
        if (Project == null) return;
        var sprite = Doc.Sprites.FirstOrDefault(s => s.Nodes == nodes || s.DepthCommands == nodes || s.Nodes.Any(n => n is SbGroup block && block.Commands == nodes));
        var group = Doc.Groups.FirstOrDefault(g => g.Commands == nodes || g.Camera?.Commands == nodes);
        if (sprite == null && group == null || sprite != null && IsProtectedBeatmapBackground(sprite)) return;
        if(group?.Camera?.Commands==nodes && !group.CameraActive) return;
        if(RejectPluginEdit(sprite!=null && PluginLink.IsGenerated(sprite) || group!=null && PluginLink.IsGenerated(group))) return;
        var commands = nodes.OfType<SbCommand>().Where(c => c.Kind == kind && (parameter == '\0' || char.ToUpperInvariant(c.Param) == parameter)).ToArray();
        if (commands.Length == 0) return;
        CommitEdits();
        foreach (var command in commands)
        {
            nodes.Remove(command);
            SelectedCommands.Remove(command);
        }
        SelectedKeys.RemoveAll(key => key.Nodes == nodes && key.Kind == kind);
        if (IsActiveTrack(nodes, kind)) ActiveTrack = null;
        if (group != null) TouchGroup(group);
        else Touch(sprite);
        CommitEdits();
    }

    public void DeleteSelection()
    {
        if (Project == null) return;
        if(RejectPluginEdit(selection.Any(PluginLink.IsGenerated)||selectedGroups.Any(PluginLink.IsGenerated))) return;
        if (SelectedKeys.Count > 0)
        {
            // From the last one of each list down, so that the places of the others stay what they are.
            foreach (var key in SelectedKeys.Distinct().OrderByDescending(k => k.Index).ToList())
            {
                if (SelectedGroup is { } owner && (owner.Commands == key.Nodes || owner.Camera?.Commands == key.Nodes))
                {
                    if (KeyTrack.Remove(key.Nodes, key.Kind, key.Index)) TouchGroup(owner);
                    continue;
                }
                // A list that belongs to nothing selected is left from before an undo or an edit of the script.
                var sprite = selection.OfType<SbSprite>().FirstOrDefault(s => s.Nodes == key.Nodes || s.DepthCommands == key.Nodes || s.Nodes.Any(n => n is SbGroup g && g.Commands == key.Nodes));
                if (sprite != null && !IsProtectedBeatmapBackground(sprite) && KeyTrack.Remove(key.Nodes, key.Kind, key.Index)) Touch(sprite);
            }
            SelectedKeys.Clear();
            if (SelectedCommands.Count == 0) return;
        }
        if (SelectedCommands.Count > 0)
        {
            foreach (var sprite in Doc.Sprites)
            {
                if (IsProtectedBeatmapBackground(sprite)||PluginLink.IsGenerated(sprite)) continue;
                int before = sprite.Nodes.Count;
                sprite.Nodes.RemoveAll(n => n is SbCommand c && SelectedCommands.Contains(c));
                bool changed = sprite.Nodes.Count != before;
                if(sprite.DepthCommands.RemoveAll(SelectedCommands.Contains)>0) changed=true;
                foreach (var node in sprite.Nodes)
                    if (node is SbGroup g && g.Commands.RemoveAll(SelectedCommands.Contains) > 0) changed = true;
                if (changed) Touch(sprite);
            }
            foreach (var group in Doc.Groups.Where(g=>!PluginLink.IsGenerated(g)))
                if (group.Commands.RemoveAll(SelectedCommands.Contains) + (group.Camera?.Commands.RemoveAll(SelectedCommands.Contains) ?? 0) > 0) TouchGroup(group);
            SelectedCommands.Clear();
            return;
        }

        if (SelectedBlock is { } block)
        {
            SelectedBlock = null;
            // One that is no longer part of a selected sprite is left from before an undo or an edit of the script.
            var owner = selection.OfType<SbSprite>().FirstOrDefault(s => s.Nodes.Contains(block));
            if (owner != null && !IsProtectedBeatmapBackground(owner))
            {
                owner.Nodes.Remove(block);
                Touch(owner);
                return;
            }
        }

        if (selection.Count == 0 && selectedGroups.Count == 0) return;
        // A group goes with all that is in it, the sprites of the difficulties that are not shown too.
        var doomed = new HashSet<SbObject>(selectionSet);
        foreach (var group in selectedGroups)
            foreach (var sprite in Doc.SpritesIn(group, true)) doomed.Add(sprite);
        doomed.RemoveWhere(IsProtectedBeatmapBackground);
        if (doomed.Count == 0 && selectedGroups.Count == 0) return;
        int count = Doc.Objects.RemoveAll(doomed.Contains) + selectedGroups.Count;
        Doc.Parked.RemoveAll(doomed.Contains);
        Doc.Groups.RemoveAll(g=>selectedGroups.Any(g.IsWithin));
        SyncPlugins();
        // A group that lost all its members has nothing left to move.
        ClearSelection();
        PruneGroups();
        Touch();
        SetStatus(L.T("status.deleted", count));
    }

    // ---------------------------------------------------------------- groups

    /// <summary>
    /// Puts what is selected into a new group: sprites, and groups as well, which are then groups in a
    /// group. The new one takes their place where they were, in the group that is open. Its pivot starts
    /// in the middle of where they are right now, and since a new group has no commands nothing moves.
    /// </summary>
    public SbObjectGroup? GroupSelection()
    {
        if(RejectPluginEdit(OpenGroup?.PluginRoot!=null || selection.Any(PluginLink.IsGenerated)||selectedGroups.Any(PluginLink.IsGenerated))) return null;
        if (Project == null) return null;
        var sprites = Doc.Sprites.Where(s => selectionSet.Contains(s) && s.Group == OpenGroup && !IsProtectedBeatmapBackground(s)).ToList();
        var groups = selectedGroups.Where(g => g.Parent == OpenGroup).ToList();
        int count = sprites.Count + groups.Count;
        if (count == 0)
        {
            SetStatus(L.T("status.groupNothing"));
            return null;
        }

        double x = 0, y = 0;
        foreach (var member in sprites)
        {
            var state = member.Timeline.Evaluate(Time, member);
            x += state.X;
            y += state.Y;
        }
        foreach (var member in groups)
        {
            var state = member.Timeline.Evaluate(Time, member);
            x += state.X;
            y += state.Y;
        }
        x = Math.Round(x / count);
        y = Math.Round(y / count);

        int number = 1;
        while (Doc.Groups.Any(g => g.Name == L.T("group.defaultName", number))) number++;
        var group = new SbObjectGroup { Name = L.T("group.defaultName", number), PivotX = x, PivotY = y, X = x, Y = y, Parent = OpenGroup };
        Doc.Groups.Add(group);
        foreach (var member in sprites) member.Group = group;
        foreach (var member in groups)
        {
            member.Parent = group;
            if(member.Camera!=null) SetStatus(L.T("camera.disabledStatus",member.Name));
        }

        SelectGroup(group);
        Touch();
        SetStatus(groups.FirstOrDefault(g=>g.Camera!=null) is {} inactiveCamera ? L.T("camera.disabledStatus",inactiveCamera.Name) : L.T("status.grouped", count, group.Name));
        return group;
    }

    /// <summary>
    /// Dissolves a group: what was in it stands where the group stood, in the group that one was in or in
    /// the storyboard. With <paramref name="keepMotion"/> the motion of the group is written into each of
    /// its members as commands of their own, the groups among them included; otherwise they fall back to
    /// their own motion.
    /// </summary>
    public void Ungroup(SbObjectGroup group, bool keepMotion)
    {
        if (Project == null || !Doc.Groups.Contains(group)) return;
        if(RejectPluginEdit(group.PluginRoot!=null)) return;
        var outer = group.Parent;

        if (OpenGroup != null && OpenGroup.IsWithin(group) && OpenGroup == group) OpenGroup = outer;
        ClearSelection();

        var released = new List<SbObject>();
        void Release(List<SbObject> list, bool shown)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is not SbSprite member || member.Group != group) continue;
                if (keepMotion)
                {
                    var baked = GroupBaker.BakeSprite(member, stopAt: outer);
                    baked.Expanded = member.Expanded;
                    list[i] = member = baked;
                }
                member.Group = outer;
                if(outer==null) { member.DepthCommands.Clear(); member.FaceCamera=false; }
                if (shown) released.Add(member);
            }
        }
        Release(Doc.Objects, true);
        // The same for the members that belong to difficulties not shown now.
        Release(Doc.Parked, false);

        var inner = Doc.ChildrenOf(group).ToList();
        foreach (var child in inner)
        {
            if (keepMotion) GroupBaker.BakeGroup(child);
            else child.Parent = outer;
        }
        Doc.Groups.Remove(group);
        DropStaleGroups();
        // What came out of it is selected, where it can be seen.
        if (outer == OpenGroup)
        {
            foreach (var member in released) AddToSelection(member);
            foreach (var child in inner) selectedGroups.Add(child);
        }
        Touch();
        SetStatus(released.OfType<SbSprite>().Any(s=>s.CompositionWarning) || inner.Any(g=>g.CompositionWarning) ? L.T("problem.spaceComposition") : L.T("status.ungrouped", group.Name) + (keepMotion ? L.T("status.ungroupedKept") : ""));
    }

    /// <summary>
    /// Takes the sprites out of the storyboard that the cutting of a group left with nothing to show
    /// (<see cref="GroupClip.Trim"/>), and with them the groups that are empty then.
    /// </summary>
    public void DropEmptied(IReadOnlyCollection<SbSprite> emptied)
    {
        if (Project == null || emptied.Count == 0) return;
        var gone = new HashSet<SbObject>(emptied);
        Doc.Objects.RemoveAll(gone.Contains);
        Doc.Parked.RemoveAll(gone.Contains);
        foreach (var obj in gone)
            if (selectionSet.Remove(obj)) selection.Remove(obj);
        PruneGroups();
        Touch();
        SetStatus(L.T("status.trimmedAway", emptied.Count));
    }

    /// <summary>
    /// Takes a group out of the group it is in, to where that one stands: it no longer moves with it.
    /// </summary>
    public void MoveOut(SbObjectGroup group)
    {
        if(RejectPluginEdit(PluginLink.IsGenerated(group))) return;
        if (Project == null || group.Parent is not { } outer) return;
        group.Parent = outer.Parent;
        selectedGroups.Remove(group);
        PruneGroups();
        TouchGroup(group);
    }

    /// <summary>Moves a sprite into a group, or with null out of every group into the storyboard.</summary>
    public void SetGroup(SbSprite sprite, SbObjectGroup? group)
    {
        if(RejectPluginEdit(PluginLink.IsGenerated(sprite)||group?.PluginRoot!=null)) return;
        if (sprite.Group == group) return;
        sprite.Group = group;
        if(group==null) {sprite.DepthCommands.Clear();sprite.FaceCamera=false;}
        // It is no longer in the place that is open: with it selected nothing could be seen of it.
        if (group != OpenGroup && selectionSet.Remove(sprite)) selection.Remove(sprite);
        PruneGroups();
        Touch(sprite);
    }

    public void DuplicateSelection()
    {
        if (Project == null || (selection.Count == 0 && selectedGroups.Count == 0)) return;
        // A group is copied with the groups and sprites in it; the copies are in each other the same way.
        int nextPlugin=Doc.Groups.Select(g=>g.Plugin?.Instance??0).DefaultIfEmpty().Max();
        var copyOf = new Dictionary<SbObjectGroup, SbObjectGroup>();
        var tops = selectedGroups.ToList();
        foreach (var group in Doc.Groups.ToList())
        {
            if (!tops.Any(group.IsWithin)) continue;
            var copy = group.Clone();
            if(copy.Plugin!=null) { copy.Plugin.Instance=++nextPlugin; if(copy.Plugin.Effect!=null) copy.Plugin.Effect.InstanceGuid=Guid.NewGuid(); }
            copyOf[group] = copy;
            Doc.Groups.Add(copy);
        }
        foreach (var copy in copyOf.Values)
            if (copy.Parent != null && copyOf.TryGetValue(copy.Parent, out var inside)) copy.Parent = inside;

        var clones = new List<SbObject>();
        var objects = Doc.Objects;
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            SbObjectGroup? into = null;
            bool member = objects[i] is SbSprite { Group: not null } sprite && copyOf.TryGetValue(sprite.Group, out into);
            if (!member && !selectionSet.Contains(objects[i])) continue;
            var clone = objects[i].Clone();
            if (member) ((SbSprite)clone).Group = into;
            objects.Insert(i + 1, clone);
            if (!member) clones.Add(clone);
        }
        ClearSelection();
        clones.Reverse();
        foreach (var clone in clones) AddToSelection(clone);
        foreach (var top in tops) selectedGroups.Add(copyOf[top]);
        SyncPlugins();
        Touch();
    }

    /// <summary>Selected objects as script, in file order. A member of a group names it by the number the group has in the storyboard.</summary>
    public string SelectionScript()
    {
        var ordered = Doc.Objects.Where(selectionSet.Contains);
        return OsbWriter.WriteObjects(ordered, new OsbWriteOptions { IncludeEditorMeta = true }, Doc.Groups);
    }

    /// <summary>
    /// What is selected as script to be pasted elsewhere: the selected objects, and the selected groups
    /// whole, each with its lines, those of the groups in it, and every sprite that is in them. The
    /// groups are numbered within the text, which so stands by itself.
    /// </summary>
    public string CopyScript()
    {
        var groups = Doc.Groups.Where(g => selectedGroups.Any(g.IsWithin)).ToList();
        var objects = Doc.Objects.Where(o => selectionSet.Contains(o) || (o is SbSprite { Group: not null } s && groups.Contains(s.Group)));
        var options = new OsbWriteOptions { IncludeEditorMeta = true, PreserveVariables = false };
        string script = OsbWriter.WriteGroups(groups, options) + OsbWriter.WriteObjects(objects, options, groups);
        if (Project != null && groups.Any(g => g.Plugin?.Effect != null)) Effects.EffectClipboard.Remember(script, Project);
        return script;
    }

    /// <summary>
    /// Connects parsed sprites to this storyboard's groups: a snippet names a group by its number, and
    /// groups the text defines itself are added.
    /// </summary>
    private void AdoptGroups(ParseResult parsed)
    {
        foreach (var (sprite, number) in parsed.GroupRefs)
            sprite.Group = number >= 1 && number <= Doc.Groups.Count ? Doc.Groups[number - 1] : null;
        foreach (var group in parsed.Storyboard.Groups)
            if (group.Plugin!=null || parsed.Storyboard.MembersOf(group).Any()) Doc.Groups.Add(group);
    }

    /// <summary>
    /// Adds objects parsed from script, to the group that is open. Groups the script defines come with
    /// their members, as new groups. With <paramref name="atPlayhead"/> the earliest object starts at the
    /// current time.
    /// </summary>
    public int PasteScript(string text, bool atPlayhead)
    {
        if (Project == null || string.IsNullOrWhiteSpace(text)) return 0;
        if(RejectPluginEdit(OpenGroup?.PluginRoot!=null)) return 0;
        // The groups of this storyboard are lent to the parser only so that a sprite naming one keeps its
        // depth. Where it goes is decided as before: a group the script does not define is not followed.
        var parsed = OsbParser.Parse(text, externalGroups: Doc.Groups, variables: Doc.Variables);
        foreach (var named in parsed.GroupRefs.Keys) named.Group = null;
        if (AssetPathPolicy.InvalidPath(Project, parsed.Storyboard) is { } invalidPath)
        {
            SetStatus(AssetPathPolicy.Message(invalidPath));
            return 0;
        }
        Project.RecordDiagnostics(parsed.Diagnostics, DiagnosticOrigin.Paste);
        if (parsed.Diagnostics.Count > 0) MarkSettingsChanged();
        var objects = parsed.Storyboard.Objects;
        var groups = parsed.Storyboard.Groups;
        try { Effects.EffectClipboard.Collect(text, Project, parsed.Storyboard); }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.Text.Json.JsonException) { SetStatus(ex.Message); return 0; }
        PreparePastedPlugins(groups);
        if (objects.Count == 0 && groups.All(g=>g.Plugin==null))
        {
            SetStatus(L.T("status.pasteNothing"));
            return 0;
        }

        if (atPlayhead && parsed.Storyboard.TimeRange() is { } range)
        {
            int delta = SnappedTime - range.Start;
            foreach (var obj in objects) obj.ShiftTime(delta);
            foreach (var group in groups) {group.ShiftTime(delta);if(group.Plugin!=null) group.Plugin.Offset+=delta;}
        }

        // What stood by itself in the script stands in the open group now.
        foreach (var group in groups)
        {
            group.Parent ??= OpenGroup;
            if(group.Parent!=null && group.Camera!=null) SetStatus(L.T("camera.disabledStatus",group.Name));
        }
        foreach (var obj in objects)
        {
            if (obj is not SbSprite { Group: null } sprite) continue;
            sprite.Group = OpenGroup;
            // Depth is only had in a group: a sprite that lands in none leaves it behind.
            if (OpenGroup == null)
            {
                sprite.DepthCommands.Clear();
                sprite.FaceCamera = false;
            }
        }
        Doc.Groups.AddRange(groups);
        Doc.Objects.AddRange(objects);
        VariableEditing.AdoptDefinitions(Doc, parsed.Storyboard);
        SyncPlugins();
        Arrange();
        ClearSelection();
        PruneGroups();
        foreach (var obj in objects)
            if (!Doc.Parked.Contains(obj) && (obj is SbSprite sprite ? sprite.Group == OpenGroup : OpenGroup == null)) AddToSelection(obj);
        foreach (var group in groups)
            if (group.Parent == OpenGroup && Doc.Groups.Contains(group)) selectedGroups.Add(group);
        Touch();
        SetStatus(groups.FirstOrDefault(g=>g.Camera!=null && g.Parent!=null) is {} inactiveCamera ? L.T("camera.disabledStatus",inactiveCamera.Name) : L.T("status.pasted", objects.Count) + (parsed.HasErrors ? L.T("status.pastedErrors", parsed.Diagnostics.Count) : ""));
        return objects.Count;
    }

    /// <summary>Moves the selected objects one step forward (+1) or backward (-1) among objects of their layer.</summary>
    public void MoveSelection(int direction)
    {
        if (Project == null || (selection.Count == 0 && selectedGroups.Count == 0)) return;
        var objects = Doc.Objects;
        bool moved = MoveSelectedGroups(direction);
        if (direction > 0)
        {
            for (int i = objects.Count - 2; i >= 0; i--)
            {
                if (!selectionSet.Contains(objects[i])) continue;
                int j = NextInLayer(objects, i, 1);
                if (j < 0 || selectionSet.Contains(objects[j])) continue;
                var obj = objects[i];
                objects.RemoveAt(i);
                objects.Insert(j, obj);
                moved = true;
            }
        }
        else
        {
            for (int i = 1; i < objects.Count; i++)
            {
                if (!selectionSet.Contains(objects[i])) continue;
                int j = NextInLayer(objects, i, -1);
                if (j < 0 || selectionSet.Contains(objects[j])) continue;
                var obj = objects[i];
                objects.RemoveAt(i);
                objects.Insert(j, obj);
                moved = true;
            }
        }
        if (moved)
        {
            Arrange();
            Touch();
        }
    }

    /// <summary>The next object of the same layer and of the same place: the storyboard itself, or the same group.</summary>
    private static int NextInLayer(List<SbObject> objects, int index, int step)
    {
        var obj = objects[index];
        var place = (obj as SbSprite)?.Group;
        for (int j = index + step; j >= 0 && j < objects.Count; j += step)
            if (objects[j].Layer == obj.Layer && objects[j].GetType() == obj.GetType() && (objects[j] as SbSprite)?.Group == place) return j;
        return -1;
    }

    /// <summary>
    /// Moves the selected groups one place forward (+1) or backward (-1) among the groups of the place
    /// they are in. The groups are drawn in the order they are listed in, the later ones in front, so this
    /// is what puts one group over another.
    /// </summary>
    private bool MoveSelectedGroups(int direction)
    {
        if (selectedGroups.Count == 0) return false;
        var groups = Doc.Groups;
        bool moved = false;
        int Sibling(int index)
        {
            for (int j = index + direction; j >= 0 && j < groups.Count; j += direction)
                if (groups[j].Parent == groups[index].Parent) return j;
            return -1;
        }
        // From the far end, so that two selected groups next to each other do not hold each other up.
        for (int i = direction > 0 ? groups.Count - 1 : 0; i >= 0 && i < groups.Count; i -= direction)
        {
            if (!selectedGroups.Contains(groups[i])) continue;
            int j = Sibling(i);
            if (j < 0 || selectedGroups.Contains(groups[j])) continue;
            var group = groups[i];
            groups.RemoveAt(i);
            groups.Insert(j, group);
            moved = true;
        }
        return moved;
    }

    /// <summary>Brings the selected objects in front of (or behind) everything else.</summary>
    public void MoveSelectionToEdge(bool front)
    {
        if (Project == null || (selection.Count == 0 && selectedGroups.Count == 0)) return;
        // The selected groups in front of, or behind, the other groups.
        if (selectedGroups.Count > 0)
        {
            var movingGroups = Doc.Groups.Where(selectedGroups.Contains).ToList();
            Doc.Groups.RemoveAll(selectedGroups.Contains);
            if (front) Doc.Groups.AddRange(movingGroups);
            else Doc.Groups.InsertRange(0, movingGroups);
        }
        var objects = Doc.Objects;
        var moving = objects.Where(selectionSet.Contains).ToList();
        objects.RemoveAll(selectionSet.Contains);
        if (front) objects.AddRange(moving);
        else objects.InsertRange(0, moving);
        Arrange();
        Touch();
    }

    /// <summary>Replaces one object by what the script editor parsed for it.</summary>
    public void ReplaceObject(SbObject old, ParseResult parsed)
    {
        if(RejectPluginEdit(PluginLink.IsGenerated(old))) return;
        var replacements = parsed.Storyboard.Objects;
        int index = Doc.Objects.IndexOf(old);
        if (index < 0 || replacements.Count == 0) return;
        VariableEditing.AdoptDefinitions(Doc, parsed.Storyboard);
        AdoptGroups(parsed);
        Doc.Objects.RemoveAt(index);
        Doc.Objects.InsertRange(index, replacements);
        foreach (var obj in replacements) obj.Expanded = old.Expanded;
        PruneGroups();
        Arrange();
        ClearSelection();
        if (!Doc.Parked.Contains(replacements[0])) AddToSelection(replacements[0]);
        Touch();
    }

    /// <summary>
    /// Replaces the whole storyboard: the objects of every difficulty, and the groups. Script of the whole
    /// storyboard, as the script panel and the autosave hold it, names the difficulty of each object.
    /// </summary>
    public void ReplaceAll(Storyboard storyboard)
    {
        int[] selected = SelectedIndices();
        int[] groupsSelected = selectedGroups.Select(g => HistoryGroups.IndexOf(g)).ToArray();
        int open = OpenGroup == null ? -1 : HistoryGroups.IndexOf(OpenGroup);
        bool sameGroups = storyboard.Groups.Count(g=>!PluginLink.IsGenerated(g)) == HistoryGroups.Count;
        Doc.Objects = storyboard.Objects;
        Doc.Parked = storyboard.Parked;
        Doc.Groups = storyboard.Groups;
        Doc.Variables = storyboard.Variables.ToList();
        SyncPlugins(true);
        Arrange();
        ClearSelection();
        // The same place stays open while the script has the groups it had.
        var editableObjects=HistoryObjects.ToList();var editableGroups=HistoryGroups;
        OpenGroup = sameGroups && open >= 0 && open<editableGroups.Count ? editableGroups[open] : null;
        if(OpenGroup?.PluginRoot!=null) OpenGroup=OpenGroup.PluginRoot.Parent;
        foreach (int i in selected)
            if (i < editableObjects.Count) AddToSelection(editableObjects[i]);
        foreach (int i in groupsSelected)
            if (sameGroups && i >= 0 && i<editableGroups.Count) selectedGroups.Add(editableGroups[i]);
        Touch();
    }

    // ---------------------------------------------------------------- screenshots

    public bool ReorderSprites(IReadOnlyList<SbSprite> sprites, SbLayer layer, SbSprite? target, bool above)
    {
        if(RejectPluginEdit(sprites.Any(PluginLink.IsGenerated)||OpenGroup?.PluginRoot!=null||target!=null&&PluginLink.IsGenerated(target))) return false;
        if (Project == null || sprites.Any(IsProtectedBeatmapBackground) || !SpriteOrder.CanMove(sprites, layer, target, OpenGroup)) return false;
        CommitEdits();
        var previousOrder = HistoryObjects.Select((obj, index) => (obj, index)).ToDictionary(p => p.obj, p => p.index);
        if (!SpriteOrder.Move(Doc, sprites, layer, target, above, OpenGroup)) return false;
        Arrange();
        Touch();
        CommitEdits();
        if (committed != null)
        {
            committed = committed with { PreviousObjectOrder = HistoryObjects.Select(obj => previousOrder[obj]).ToArray(), HistoryLabel = new(HistoryAction.Reorder) };
            HistoryRevision++;
        }
        return true;
    }

    /// <summary>The heights a picture of the storyboard can be saved at. 480 is what osu! itself draws a storyboard at.</summary>
    public static readonly int[] FrameHeights = { 480, 720, 1080, 1440, 2160 };

    /// <summary>
    /// The width that goes with a height: 16:9 for a widescreen storyboard, 4:3 otherwise. Always an even
    /// number, which is what a video needs; for the heights of <see cref="FrameHeights"/> it is the exact one.
    /// </summary>
    public static int FrameWidth(int height, bool widescreen) =>
        (int)Math.Round(height * (widescreen ? 16.0 / 9 : 4.0 / 3) / 2) * 2;

    /// <summary>
    /// Saves the picture a player sees at the current time: the storyboard alone at the given height,
    /// without the guides, outlines and whatever else the preview draws over it, and nothing of what lies
    /// outside the picture. The file's extension decides between PNG and JPG.
    /// </summary>
    public bool SaveFrame(string path, int height)
    {
        if (Project == null) return false;
        try
        {
            int width = FrameWidth(height, Project.Settings.Widescreen);
            float scale = height / 480f;
            // The 640 wide playfield sits in the middle; a widescreen picture shows more on both sides.
            var view = new ViewTransform { Scale = scale, Offset = new Vector2((width - 640 * scale) / 2, 0) };
            byte[] pixels;
            using (var target = new RenderTarget(gl))
            {
                target.Resize(width, height);
                Renderer.Render(Project, Index, Time, target, view, RenderOptions, ShownBeatmap);
                pixels = target.ReadPixels();
            }
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is ".jpg" or ".jpeg") JpegWriter.Write(path, width, height, pixels, Settings.JpegQuality);
            else PngWriter.Write(path, width, height, pixels);
            SetStatus(L.T("status.screenshot", path, width, height));
            return true;
        }
        catch (Exception ex)
        {
            ShowMessage(L.T("msg.screenshotFailed"), ex.Message);
            return false;
        }
    }

    // ---------------------------------------------------------------- difficulties

    /// <summary>Puts the objects in the order the selected difficulty gives them; see <see cref="Storyboard.Arrange"/>.</summary>
    private void Arrange()
    {
        if (Project != null && Project.ArrangeStoryboard()) DocRevision++;
        if(Project!=null) GroupSpace.Rebind(Doc);
    }

    /// <summary>The audio is the one thing of a difficulty that has to be loaded again when it changes.</summary>
    private void AfterDifficultyChange(string previousAudio)
    {
        if (!string.Equals(previousAudio, Project!.Settings.Audio, StringComparison.OrdinalIgnoreCase)) ReloadMusic();
    }

    /// <summary>
    /// Shows another difficulty: its own storyboard objects, with its background, picture format and timing,
    /// together with the storyboard all difficulties share. One undo step.
    /// </summary>
    /// <param name="file">The name of the difficulty's .osu file, or empty for the shared storyboard alone.</param>
    public void SelectDifficulty(string file)
    {
        if (Project == null || file == Project.Settings.Difficulty) return;
        if (Settings.SeamlessPlaybackRefresh)
        {
            CommitEdits();
            bool playing = Playing;
            string previousAudio = Project.Settings.Audio;
            if (!Project.SelectDifficulty(file)) return;
            bool changedAudio = !string.Equals(previousAudio, Project.Settings.Audio, StringComparison.OrdinalIgnoreCase);
            if (changedAudio) ReloadMusic();
            ClearSelection();
            Touch();
            SetStatus(L.T("status.difficulty", Project.CurrentDifficulty?.DisplayName ?? L.T("viewer.sharedOnly")));
            if (playing)
            {
                if (changedAudio)
                {
                    Audio.SetTimingMode(Settings.ShowTimingRecheck, Settings.TimingSoundFollowsTempo, Settings.TimingSlowPlayback);
                    ApplyPlaybackGates();
                    var samples = ScheduledSamples();
                    // Changing the music stopped the track where it had got to, after the difficulty loaded.
                    Audio.Play(Audio.CurrentTimeMs, Rate, samples);
                }
                else RefreshPlayback();
            }
            return;
        }
        CommitEdits();
        bool wasPlaying = Playing;
        if (wasPlaying) Pause();
        string audio = Project.Settings.Audio;
        if (!Project.SelectDifficulty(file)) return;
        AfterDifficultyChange(audio);
        ClearSelection();
        Touch();
        SetStatus(L.T("status.difficulty", Project.CurrentDifficulty?.DisplayName ?? L.T("viewer.sharedOnly")));
        if (wasPlaying) Play();
    }

    /// <summary>
    /// Makes an object part of the selected difficulty alone, which is where its script then goes (the
    /// .osu), or of the storyboard all difficulties share (the .osb).
    /// </summary>
    public void SetOwnDifficulty(SbObject obj, bool own)
    {
        if(RejectPluginEdit(PluginLink.IsGenerated(obj))) return;
        if (Project?.CurrentDifficulty is not { } current) return;
        string? difficulty = own ? current.File : null;
        if (obj.Difficulty == difficulty) return;
        obj.Difficulty = difficulty;
        Arrange();
        Touch(obj);
    }

    public void Dispose()
    {
        ReleasePlugins();
        watcher?.Dispose();
        ClearVideoExport();
        Audio.Dispose();
        Videos?.Dispose();
        HitObjects?.Dispose();
        Textures?.Dispose();
        Batch?.Dispose();
    }
}
