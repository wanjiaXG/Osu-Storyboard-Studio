using SbStudio.Core;

namespace SbStudio.App;

/// <summary>The camera of a space and what the space needs from the editor (see <see cref="GroupSpace"/>).</summary>
public sealed partial class EditorApp
{
    /// <summary>Picture sizes by full path; null for a picture that could not be read. Emptied when assets change.</summary>
    private readonly Dictionary<string, (int Width, int Height)?> imageSizes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The size of a picture of the project, for the tolerances of baking through a camera. Only the header
    /// is read, and only once.
    /// </summary>
    private (int Width, int Height)? ImageSize(string path)
    {
        string file = Textures.FullPath(path);
        if (imageSizes.TryGetValue(file, out var size)) return size;
        try
        {
            using var stream = File.OpenRead(file);
            var info = StbImageSharp.ImageInfo.FromStream(stream);
            size = info is { } i ? (i.Width, i.Height) : null;
        }
        catch
        {
            // Without a size the tolerances are the ones that do not need it.
            size = null;
        }
        imageSizes[file] = size;
        return size;
    }

    /// <summary>For the automated checks: the space of the selection at the playhead, written to a file as JSON.</summary>
    public void DumpSpace(string file)
    {
        var group = SelectedGroup ?? OpenGroup ?? (Primary as SbSprite)?.Group;
        if (group == null)
        {
            File.WriteAllText(file, "{}");
            return;
        }
        var root = group.Root;
        var camera = GroupSpace.Camera(root, Time);
        var sprites = Selection.OfType<SbSprite>().Select(s =>
        {
            var pose = GroupSpace.Pose(s, Time);
            var projection = GroupSpace.Project(s, Time);
            var shown = s.ShownState(Time);
            return new
            {
                s.Path,
                Position = pose.Position,
                projection.Depth,
                shown.X,
                shown.Y,
                ScaleX = shown.Scale * shown.ScaleX,
                ScaleY = shown.Scale * shown.ScaleY,
                shown.Rotation,
                shown.Opacity,
            };
        }).ToArray();
        var dump = new
        {
            Root = root.Name,
            UsesDepth = GroupSpace.UsesDepth(root),
            HasCamera = root.Camera != null,
            CameraActive = group.CameraActive,
            GroupCamera = group.Camera != null,
            Camera = camera,
            CameraPosition = camera.Position,
            Sprites = sprites,
            Dirty,
            CanUndo,
            HistoryPosition,
        };
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(dump, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }

    /// <summary>The group whose camera the property being edited belongs to, if it is one of a camera.</summary>
    public SbObjectGroup? CameraSelection => ActiveTrack is { } track ? Doc.Groups.FirstOrDefault(g => g.Camera?.Commands == track.Nodes) : null;

    /// <summary>A camera was selected explicitly and its rows should be brought into view.</summary>
    public event Action<SbObjectGroup>? CameraSelected;

    /// <summary>Selects a group and, if it has a camera, the camera's target as the property to edit.</summary>
    public void SelectCamera(SbObjectGroup group)
    {
        // Its commands are listed alongside the group, in the parent container.
        if (group.Camera != null) OpenContainer(group.Parent);
        SelectGroup(group);
        if (group.Camera == null) return;
        group.Expanded = true;
        group.Camera.Expanded = true;
        TouchFolds();
        SelectTrack(group.Camera.Commands, CommandKind.CT);
        CameraSelected?.Invoke(group);
    }

    /// <summary>Gives an outermost group a camera, as one step of history. Only outermost groups have one that works.</summary>
    public void AddCamera(SbObjectGroup group)
    {
        if (group.Parent != null || group.Camera != null) return;
        CommitEdits();
        group.Camera = new SbCamera();
        TouchGroup(group);
        SelectCamera(group);
        CommitEdits();
        if (committed != null) committed = committed with { HistoryLabel = new(HistoryAction.AddCamera, group.Name) };
    }

    /// <summary>Takes a group's camera away, as one step of history.</summary>
    public void RemoveCamera(SbObjectGroup group)
    {
        if (group.Camera == null || !Doc.Groups.Contains(group)) return;
        CommitEdits();
        group.Camera = null;
        ActiveTrack = null;
        TouchGroup(group);
        CommitEdits();
        if (committed != null) committed = committed with { HistoryLabel = new(HistoryAction.RemoveCamera, group.Name) };
    }

    /// <summary>
    /// Hands the camera a group kept from when it was outermost to the outermost group it is in now, if
    /// that has none: as one step of history.
    /// </summary>
    public void MoveCameraToRoot(SbObjectGroup group)
    {
        var root = group.Root;
        if (root == group || root.Camera != null || group.Camera == null) return;
        CommitEdits();
        root.Camera = group.Camera;
        group.Camera = null;
        TouchGroup(group);
        TouchGroup(root);
        SelectCamera(root);
        CommitEdits();
        if (committed != null) committed = committed with { HistoryLabel = new(HistoryAction.MoveCamera, group.Name + " → " + root.Name) };
    }

    /// <summary>
    /// <see cref="AddKey(SbObjectGroup, CommandKind)"/> for the camera of a group: a keyframe at the
    /// playhead, holding the camera as it is there. Refused for a camera that does not work.
    /// </summary>
    public KeyResult AddCameraKey(SbObjectGroup group, CommandKind kind)
    {
        if (!group.CameraActive || !SbInfo.CameraKinds.Contains(kind)) return KeyResult.Refused;
        var nodes = group.Camera!.Commands;
        int time = SnappedTime, index = KeyAtPlayhead(nodes, kind);
        if (index < 0)
        {
            var value = group.Camera.Evaluate(time).Values(kind);
            index = KeyTrack.Insert(nodes, kind, time, value);
            if (index < 0)
            {
                // Commands that run at the same time have no keyframes to add one to. Those cut or added
                // there are an edit like any other, for the history and the baked members alike.
                var pinned = PinKey(nodes, kind, ref time, value, 0, out bool changed);
                if (changed) TouchGroup(group);
                if (pinned == KeyResult.Done && Time != time) Seek(time);
                return pinned;
            }
            TouchGroup(group);
            if (Time != time) Seek(time);
        }
        SelectKey(new KeyRef(nodes, kind, index));
        return KeyResult.Done;
    }
}
