using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using SbStudio.App.Gfx;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed unsafe partial class ViewerPanel
{
    public bool Fullscreen { get; private set; }
    public ScreenRect? PreviewBounds { get; private set; }
    private int previewSeenFrame = -10;
    internal ScreenRect? VisiblePreviewBounds => ImGui.GetFrameCount() - previewSeenFrame <= 1 ? PreviewBounds : null;
    public event Action<bool>? FullscreenRequested;
    public event Action? DebugLocated;
    private readonly FullscreenControls controls = new();
    private readonly RenderRecord record = new();
    private readonly List<RenderRow> frozenRows = new();
    private readonly HashSet<SbObject> filterHits = new();
    private readonly List<int> debugHits = new(), displayedRows = new();
    private float previewZoom, fullscreenTop = 90, fullscreenBottom;
    private Vector2 previewPan, picturePosition, pictureSize, lastMouse, watchStart;
    private ViewTransform pictureView;
    private Vector2 editingButton, fullscreenButton, firstDebugRow;
    private readonly Dictionary<string, Vector2> debugHeaders = new();
    private bool pictureHovered, cursorHidden, watchPressed, watchDouble, freezeDebug, onlyUnderMouse;
    private string debugFilter = "";
    private RenderRow? rowHovered;
    private readonly FrameHistory frameHistory = new();
    private bool debugListDirty = true;
    private int sortedColumn;
    private bool sortDescending;
    private double processMeasured, managedBytes, workingBytes;

    public void EnterFullscreen()
    {
        if (Fullscreen) return;
        FinishPictureGesture();
        previewZoom = zoom;
        previewPan = pan;
        ResetView();
        lastMouse = ImGui.GetIO().MousePos;
        controls.Enter(ImGui.GetTime());
        Fullscreen = true;
        freezeDebug = false;
        debugListDirty = true;
    }

    public void ExitFullscreen()
    {
        if (!Fullscreen) return;
        FinishPictureGesture();
        Fullscreen = false;
        zoom = previewZoom;
        pan = previewPan;
        Focused = cursorHidden = watchPressed = false;
        rowHovered = null;
        ImGui.SetMouseCursor(ImGuiMouseCursor.Arrow);
    }

    private void FinishPictureGesture()
    {
        if (dragging) app.CommitEdits();
        dragging = panning = adjusting = false;
        freeGesture = null;
        clickNext = null;
    }

    public void SetDebugPanel(bool value)
    {
        app.Settings.FullscreenDebugPanel = value;
        app.Settings.Save();
        debugListDirty = true;
    }

    private RenderRecord? CaptureRecord()
    {
        if (!Fullscreen || !app.Settings.FullscreenDebugPanel) return null;
        record.Prepare(app.Doc, app.DocRevision);
        // Freezing the table never stops the renderer or its live counters.
        return record;
    }

    private bool Inside(Vector2 point, Vector2 pos, Vector2 size) =>
        point.X >= pos.X && point.Y >= pos.Y && point.X < pos.X + size.X && point.Y < pos.Y + size.Y;

    private bool FullscreenObstructed(Vector2 point)
    {
        bool bars = controls.Alpha > 0 && (point.Y < picturePosition.Y + fullscreenTop || point.Y >= picturePosition.Y + pictureSize.Y - fullscreenBottom);
        return bars || (app.Settings.FullscreenDebugPanel && Inside(point, picturePosition + new Vector2(0, fullscreenTop),
            new Vector2(DebugWidth, Math.Max(1, pictureSize.Y - fullscreenTop - fullscreenBottom))));
    }

    private float DebugWidth => Math.Min(Math.Max(Theme.Px(420), pictureSize.X * 0.3f), Math.Max(1, pictureSize.X - Theme.Px(16)));

    public void DrawFullscreen()
    {
        var viewport = ImGui.GetMainViewport();
        var io = ImGui.GetIO();
        picturePosition = viewport.Pos;
        pictureSize = viewport.Size;
        fullscreenBottom = MeasurePlayer(pictureSize.X).Height;
        bool inside = Inside(io.MousePos, picturePosition, pictureSize);
        bool moved = inside && io.MousePos != lastMouse;
        lastMouse = io.MousePos;
        bool onBar = inside && controls.Alpha > 0 && (io.MousePos.Y < picturePosition.Y + fullscreenTop || io.MousePos.Y >= picturePosition.Y + pictureSize.Y - fullscreenBottom);
        bool interacting = ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel) || ImGui.IsAnyItemActive() || io.WantTextInput;
        controls.Update(ImGui.GetTime(), moved, onBar, interacting);
        if (app.Settings.FullscreenDebugPanel) frameHistory.Add(ImGui.GetTime(), io.DeltaTime * 1000);

        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);
        ImGui.Begin("##fullscreenPreview", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar(3);
        DrawPicture(picturePosition, pictureSize);

        if (controls.Alpha > 0)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, controls.Alpha);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.WithAlpha(Theme.PanelDark, 0.85f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, PlayerPadding);
            ImGui.SetCursorScreenPos(picturePosition);
            ImGui.BeginChild("##fullscreenTop", new Vector2(pictureSize.X, fullscreenTop), ImGuiChildFlags.AlwaysUseWindowPadding,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            // On the dark bar the usual field colour would not show at all, as on the player bar.
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Theme.Rgba(38, 38, 42));
            DrawToolbar(app.Project!);
            ImGui.PopStyleColor();
            fullscreenTop = ImGui.GetCursorPosY() + PlayerPadding.Y;
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
            ImGui.SetCursorScreenPos(picturePosition + new Vector2(0, pictureSize.Y - fullscreenBottom));
            DrawPlayer(MeasurePlayer(pictureSize.X));
            ImGui.PopStyleVar();
        }

        rowHovered = null;
        if (app.Settings.FullscreenDebugPanel)
        {
            if (pictureHovered)
            {
                record.At(pictureView.ToStoryboard(io.MousePos - picturePosition), debugHits);
                filterHits.Clear();
                foreach (int i in debugHits) if (record.Rows[i].Object is { } obj) filterHits.Add(obj);
            }
            else debugHits.Clear();
            DrawDebugPanel();
            var dl = ImGui.GetWindowDrawList();
            dl.PushClipRect(picturePosition, picturePosition + pictureSize, true);
            foreach (int index in debugHits) OutlineRow(dl, record.Rows[index], Theme.Rgba(80, 235, 255), 1.5f);
            if (rowHovered is { } row) OutlineRow(dl, row, Theme.Rgba(255, 210, 60), 3);
            dl.PopClipRect();
        }
        cursorHidden = controls.Alpha == 0 && inside && !(app.Settings.FullscreenDebugPanel && FullscreenObstructed(io.MousePos));
        if (cursorHidden) ImGui.SetMouseCursor(ImGuiMouseCursor.None);
        ImGui.End();
    }

    public void DrawTriggerSimulationWindow() => DrawTriggerSimulation();

    private void HandleWatchInput(ImGuiIOPtr io, bool hovered)
    {
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            watchPressed = true;
            watchStart = io.MousePos;
            watchDouble = ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        }
        if (!watchPressed) return;
        if (Vector2.Distance(watchStart, io.MousePos) > Theme.Px(ClickSlop)) watchPressed = false;
        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left)) return;
        watchPressed = false;
        if (!hovered || Vector2.Distance(watchStart, io.MousePos) > Theme.Px(ClickSlop)) return;
        app.TogglePlay();
        if (watchDouble) FullscreenRequested?.Invoke(false);
    }

    private void OutlineRow(ImDrawListPtr dl, RenderRow row, uint color, float thickness)
    {
        if (row.Texture is not { } texture) return;
        Span<Vector2> corners = stackalloc Vector2[4];
        SpriteGeometry.Corners(row.State, row.Origin, texture.Width, texture.Height, corners);
        for (int i = 0; i < 4; i++) dl.AddLine(picturePosition + pictureView.ToPixels(corners[i]),
            picturePosition + pictureView.ToPixels(corners[(i + 1) % 4]), color, thickness);
    }

    private void DrawDebugPanel()
    {
        ImGui.SetCursorScreenPos(picturePosition + new Vector2(0, fullscreenTop));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.WithAlpha(Theme.PanelDark, 0.9f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Theme.Px(10)));
        ImGui.BeginChild("##fullscreenDebug", new Vector2(DebugWidth, Math.Max(1, pictureSize.Y - fullscreenTop - fullscreenBottom)),
            ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        if (DebugHeader(L.T("debug.performance"), "performance")) DrawDebugPerformance();
        if (DebugHeader(L.T("debug.timing"), "timing")) DrawDebugTiming();
        if (DebugHeader(L.T("debug.project"), "project")) DrawDebugProject();
        if (DebugHeader(L.T("debug.layers"), "layers")) DrawDebugLayers();
        if (DebugHeader(L.T("debug.drawList"), "drawList")) DrawDebugList();
        if (DebugHeader(L.T("debug.underMouse"), "underMouse"))
        {
            var at = pictureView.ToStoryboard(ImGui.GetIO().MousePos - picturePosition);
            Theme.TextDimmed(L.T("viewer.cursor", at.X, at.Y));
            foreach (int index in debugHits)
            {
                var row = record.Rows[index];
                ImGui.TextUnformatted($"#{row.ObjectNumber}  {row.Path}");
            }
        }
        ImGui.EndChild();
    }

    private bool DebugHeader(string label, string id)
    {
        bool open = ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen);
        debugHeaders[id] = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2 - ImGui.GetMainViewport().Pos;
        return open;
    }

    private static string YesNo(bool value) => value ? L.T("debug.yes") : L.T("debug.no");

    private void DrawDebugPerformance()
    {
        var stats = frameHistory.Statistics();
        Theme.TextDimmed(L.T("debug.frame", ImGui.GetIO().Framerate, stats.Average, stats.Maximum, stats.Low));
        Theme.TextDimmed(L.T("debug.cpu", Perf.Read("render"), Program.UiMs, YesNo(app.Settings.VSync && !app.Exporting)));
        Theme.TextDimmed(L.T("debug.resolution", target.Width, target.Height, pictureView.Scale));
        Theme.TextDimmed(L.T("viewer.info", app.Renderer.VisibleSprites, app.Batch.DrawCalls));
        Theme.TextDimmed(L.T("debug.quads", app.Batch.Quads));
        if (app.Renderer.Load >= StoryboardLoad.High) ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
        ImGui.TextUnformatted(L.T("debug.load", app.Renderer.Load));
        if (app.Renderer.Load >= StoryboardLoad.High) ImGui.PopStyleColor();
        Theme.TextDimmed(L.T("debug.textures", app.Textures.LoadedCount, app.Textures.PendingCount, app.Textures.UnavailableCount, app.Textures.EstimatedBytes / 1048576.0));
        if (ImGui.GetTime() - processMeasured >= 0.5)
        {
            processMeasured = ImGui.GetTime();
            managedBytes = GC.GetTotalMemory(false);
            workingBytes = Environment.WorkingSet;
        }
        Theme.TextDimmed(L.T("debug.memory", managedBytes / 1048576, workingBytes / 1048576, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)));
    }

    private void DrawDebugTiming()
    {
        Theme.TextDimmed(L.T("debug.clock", app.Time, Theme.FormatTime(app.Time), app.Playing ? L.T("debug.playing") : L.T("debug.paused"), app.Rate));
        var timing = app.Project!.Settings.Timing;
        if (TimingUtil.At(timing, app.Time) is { } point)
        {
            var pos = TimingUtil.PositionAt(timing, app.Time, app.Divisor);
            Theme.TextDimmed(L.T("debug.redLine", point.Bpm, point.Offset, point.Meter, pos.Bar, pos.Beat, pos.Snap, app.Divisor));
        }
        string device = app.Audio.OutputDevices.FirstOrDefault(d => d.Id == app.Audio.OutputDeviceId)?.Name ?? L.T("audio.defaultDevice");
        Theme.TextWrapped(L.T("audio.outputDevice", device));
        Theme.TextDimmed(L.T("debug.audioLatency", (object?)app.Audio.DeviceLatencyMilliseconds ?? "—", (object?)app.Audio.OutputBufferMilliseconds ?? "—"));
        Theme.TextWrapped(app.Project.Settings.Audio.Length > 0 ? app.Project.AssetFullPath(app.Project.Settings.Audio) : "—");
        if (app.Audio.Error is { } error) Theme.TextWrapped(error);
    }

    private void DrawDebugProject()
    {
        var project = app.Project!;
        Theme.TextWrapped(project.Settings.Name + "\n" + project.Folder);
        Theme.TextWrapped(L.T("debug.difficulty", project.CurrentDifficulty?.File ?? L.T("viewer.sharedOnly"), app.ShownBeatmap?.Mode.ToString() ?? "—"));
        Theme.TextDimmed(L.T("debug.projectOptions", YesNo(project.Settings.Widescreen), app.HitObjects.SkinLibrary.Find(app.HitObjects.SkinId).Name));
        if (project.Settings.Background.Length > 0)
        {
            string background = project.ImageFullPath(project.Settings.Background);
            Theme.TextWrapped(L.T("debug.background", background, YesNo(File.Exists(background))));
        }
        else Theme.TextWrapped(L.T("debug.background", "—", "—"));
        foreach (var video in app.Index.Videos)
        {
            string path = project.AssetFullPath(video.Path);
            Theme.TextWrapped(L.T("debug.video", path, video.Time, app.Time - video.Time, app.Videos.Problem(path) ?? "—"));
        }
        bool simulated = app.TriggerSimulation.Stream.HasStateRules;
        Theme.TextDimmed(L.T("debug.state", (simulated ? app.TriggerSimulation.FailStateAt(app.Time) : app.RenderOptions.FailState) ? L.T("viewer.stateFail") : L.T("viewer.statePass"), YesNo(simulated),
            project.Settings.TriggerSimulationRules.Count(r => r.Enabled)));
        Theme.TextWrapped(L.T("debug.openGroup", GroupPath(app.OpenGroup)));
    }

    private static string GroupPath(SbObjectGroup? group)
    {
        if (group == null) return "—";
        var names = new List<string>();
        for (var g = group; g != null && names.Count < SbObjectGroup.MaxDepth; g = g.Parent) names.Add(g.Name);
        names.Reverse();
        return string.Join(" › ", names);
    }

    private void DrawDebugLayers()
    {
        if (ImGui.BeginTable("##layerCounts", 4, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(L.T("debug.layer"));
            ImGui.TableSetupColumn(L.T("debug.alive"));
            ImGui.TableSetupColumn(L.T("debug.drawn"));
            ImGui.TableSetupColumn(L.T("debug.hidden"));
            ImGui.TableHeadersRow();
            for (int i = 0; i < SbInfo.LayerCount; i++)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Theme.LayerNames[i]);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(record.Alive[i].ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(record.Drawn[i].ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(record.Hidden[i].ToString());
            }
            ImGui.EndTable();
        }
        Theme.TextWrapped(L.T("debug.totals", record.Objects, record.Sprites, record.Animations, record.Samples, record.Videos, record.Commands));
    }

    private void DrawDebugList()
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##debugFilter", L.T("debug.filter"), ref debugFilter, 512)) debugListDirty = true;
        if (ImGui.Checkbox(L.T("debug.onlyUnderMouse"), ref onlyUnderMouse)) debugListDirty = true;
        if (ImGui.Checkbox(L.T("debug.freeze"), ref freezeDebug))
        {
            if (freezeDebug) { frozenRows.Clear(); frozenRows.AddRange(record.Rows); frozenRows.AddRange(record.Missing); }
            debugListDirty = true;
        }
        IReadOnlyList<RenderRow> rows = freezeDebug ? frozenRows : record;
        // Live draw order needs no sorting or full-list copy. Filtering and sorting are opt-in.
        bool indexed = debugFilter.Length > 0 || onlyUnderMouse || sortedColumn != 0 || sortDescending;
        if ((!freezeDebug || debugListDirty) && indexed)
        {
            displayedRows.Clear();
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Path.Contains(debugFilter, StringComparison.OrdinalIgnoreCase) &&
                    (!onlyUnderMouse || (rows[i].Object is { } obj && filterHits.Contains(obj)))) displayedRows.Add(i);
            if (sortedColumn != 0 || sortDescending) displayedRows.Sort((a, b) => CompareRows(rows, a, b));
            debugListDirty = false;
        }
        float height = Theme.Px(260);
        if (ImGui.BeginTable("##drawRecords", 14, ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable | ImGuiTableFlags.ScrollX |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV, new Vector2(0, height)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.WidthFixed, Theme.Px(40), 0);
            ImGui.TableSetupColumn(L.T("debug.path"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(220), 4);
            ImGui.TableSetupColumn(L.T("debug.object"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(60), 2);
            ImGui.TableSetupColumn(L.T("debug.layer"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(100), 1);
            ImGui.TableSetupColumn(L.T("debug.type"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(160), 3);
            ImGui.TableSetupColumn(L.T("debug.size"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(90), 5);
            ImGui.TableSetupColumn(L.T("debug.position"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(130), 6);
            ImGui.TableSetupColumn(L.T("debug.scale"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(130), 7);
            ImGui.TableSetupColumn(L.T("debug.rotation"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(100), 8);
            ImGui.TableSetupColumn(L.T("debug.opacity"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(90), 9);
            ImGui.TableSetupColumn(L.T("debug.color"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(140), 10);
            ImGui.TableSetupColumn(L.T("debug.flags"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(200), 11);
            ImGui.TableSetupColumn(L.T("debug.group"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(180), 12);
            ImGui.TableSetupColumn(L.T("debug.contribution"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(90), 13);
            ImGui.TableHeadersRow();
            var specs = ImGui.TableGetSortSpecs();
            if (specs.SpecsDirty && specs.SpecsCount > 0)
            {
                sortedColumn = (int)specs.Specs.ColumnUserID;
                sortDescending = specs.Specs.SortDirection == ImGuiSortDirection.Descending;
                specs.SpecsDirty = false;
                debugListDirty = true;
            }
            int count = indexed ? displayedRows.Count : rows.Count;
            ImGuiListClipperPtr clipper = new(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin(count);
            while (clipper.Step())
                for (int n = clipper.DisplayStart; n < clipper.DisplayEnd; n++)
                {
                    int index = indexed ? displayedRows[n] : n;
                    DrawDebugRow(rows[index], index);
                }
            clipper.End();
            clipper.Destroy();
            ImGui.EndTable();
        }
        if (record.Missing.Count > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(L.T("debug.missing"));
            foreach (var missing in record.Missing.Take(20)) ImGui.TextUnformatted($"#{missing.ObjectNumber} {missing.Path}");
            ImGui.PopStyleColor();
        }
    }

    private int CompareRows(IReadOnlyList<RenderRow> rows, int a, int b)
    {
        var x = rows[a]; var y = rows[b];
        int result = sortedColumn switch
        {
            1 => x.Layer.CompareTo(y.Layer), 2 => x.ObjectNumber.CompareTo(y.ObjectNumber),
            3 => x.Frames.CompareTo(y.Frames), 4 => StringComparer.OrdinalIgnoreCase.Compare(x.Path, y.Path),
            5 => (x.Texture?.Width ?? 0).CompareTo(y.Texture?.Width ?? 0), 6 => x.State.X.CompareTo(y.State.X),
            7 => (x.State.Scale * x.State.ScaleX).CompareTo(y.State.Scale * y.State.ScaleX), 8 => x.State.Rotation.CompareTo(y.State.Rotation),
            9 => x.State.Opacity.CompareTo(y.State.Opacity), 10 => x.State.R.CompareTo(y.State.R),
            11 => x.State.Additive.CompareTo(y.State.Additive), 12 => StringComparer.OrdinalIgnoreCase.Compare(x.Group, y.Group),
            13 => x.Load.CompareTo(y.Load), _ => a.CompareTo(b),
        };
        if (result == 0) result = a.CompareTo(b);
        return sortDescending ? -result : result;
    }

    private void DrawDebugRow(RenderRow row, int index)
    {
        ImGui.PushID(index);
        ImGui.TableNextRow();
        bool missing = row.Texture == null || app.Textures.IsUnavailable(row.Path) && row.Object is not SbVideo;
        if (missing) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, Theme.Rgba(160, 30, 40, 180));
        ImGui.TableNextColumn();
        ImGui.Selectable((index + 1).ToString(), false, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick);
        if (index == 0) firstDebugRow = new Vector2(ImGui.GetItemRectMin().X + Theme.Px(20), (ImGui.GetItemRectMin().Y + ImGui.GetItemRectMax().Y) / 2) - ImGui.GetMainViewport().Pos;
        if (ImGui.IsItemHovered())
        {
            rowHovered = row;
            Theme.SetTooltip(FullPath(row));
            if (row.Object != null && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) LocateDebugRow(row);
        }
        if (ImGui.BeginPopupContextItem("##recordMenu"))
        {
            if (ImGui.MenuItem(L.T("debug.copyPath"))) ImGui.SetClipboardText(row.Path);
            if (ImGui.MenuItem(L.T("debug.copyFullPath"))) ImGui.SetClipboardText(FullPath(row));
            if (ImGui.MenuItem(L.T("debug.reveal")))
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.ArgumentList.Add("/select," + FullPath(row));
                try { Process.Start(start); } catch (Exception e) { app.ShowMessage(L.T("debug.reveal"), e.Message); }
            }
            ImGui.EndPopup();
        }
        ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Path);
        ImGui.TableNextColumn(); ImGui.TextUnformatted(row.ObjectNumber.ToString());
        ImGui.TableNextColumn(); ImGui.TextUnformatted(Theme.LayerNames[(int)row.Layer]);
        ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Texture == null ? L.T("debug.missing") : row.Object is SbVideo ? L.T("debug.videoType") : !row.IsSprite ? L.T("debug.backgroundType") : row.Object is SbSprite { IsAnimation: true } ? L.T("debug.animationType", row.Frame + 1, row.Frames) : L.T("debug.spriteType"));
        ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.Texture?.Width}×{row.Texture?.Height}");
        ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.State.X:0.00}, {row.State.Y:0.00}");
        ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.State.Scale * row.State.ScaleX:0.000}, {row.State.Scale * row.State.ScaleY:0.000}");
        ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.State.Rotation * (app.Settings.RotationInDegrees ? 180 / Math.PI : 1):0.000} {(app.Settings.RotationInDegrees ? "°" : "rad")}");
        ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.State.Opacity:0.000}");
        ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.State.R:0}, {row.State.G:0}, {row.State.B:0}");
        ImGui.TableNextColumn(); ImGui.TextUnformatted(L.T("debug.flagsValue", YesNo(row.State.Additive), YesNo(row.State.FlipH), YesNo(row.State.FlipV), row.Origin, YesNo(row.Simulated)));
        ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Group);
        ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.Load:0.0000}");
        ImGui.PopID();
    }

    private string FullPath(RenderRow row) => row.Object is SbVideo ? app.Project!.AssetFullPath(row.Path) : app.Textures.FullPath(row.Path);

    private void LocateDebugRow(RenderRow row)
    {
        if (row.Object is SbSprite sprite)
        {
            if (!app.Doc.Objects.Contains(sprite)) return;
            if (app.ItemOf(sprite, out var group))
            {
                if (group != null) app.SelectGroup(group); else app.SelectOnly(sprite);
            }
            else
            {
                // The inspector may expose a drawn object outside the open container.
                app.OpenContainer(null);
                if (app.ItemOf(sprite, out group) && group != null) app.SelectGroup(group); else app.SelectOnly(sprite);
            }
        }
        else if (row.Object is { } obj && app.Doc.Objects.Contains(obj)) app.SelectOnly(obj);
        else return;
        DebugLocated?.Invoke();
        FullscreenRequested?.Invoke(false);
    }

    internal bool FullscreenControlsHidden => Fullscreen && controls.Alpha == 0;

    internal string FullscreenState
    {
        get
        {
            var text = new StringBuilder();
            text.AppendLine(FormattableString.Invariant($"fullscreen={Fullscreen.ToString().ToLowerInvariant()} controlsVisible={controls.Visible.ToString().ToLowerInvariant()} controlsAlpha={controls.Alpha:0.000} cursorHidden={cursorHidden.ToString().ToLowerInvariant()} debugPanel={app.Settings.FullscreenDebugPanel.ToString().ToLowerInvariant()} editing={app.Settings.FullscreenEditing.ToString().ToLowerInvariant()} viewW={pictureSize.X} viewH={pictureSize.Y} scale={pictureView.Scale:0.000000} visibleSprites={app.Renderer.VisibleSprites} recorded={(app.Settings.FullscreenDebugPanel ? record.SpriteCount : 0)} sbLoad={app.Renderer.Load:0.000000} quads={app.Batch.Quads} drawCalls={app.Batch.DrawCalls} top={fullscreenTop} bottom={fullscreenBottom} editX={editingButton.X:0} editY={editingButton.Y:0} buttonX={fullscreenButton.X:0} buttonY={fullscreenButton.Y:0} vsync={(app.Settings.VSync && !app.Exporting).ToString().ToLowerInvariant()} popupOpen={ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel).ToString().ToLowerInvariant()} rowX={firstDebugRow.X:0} rowY={firstDebugRow.Y:0}"));
            for (int i = 0; i < (Fullscreen && app.Settings.FullscreenDebugPanel ? Math.Min(20, record.Rows.Count) : 0); i++)
            {
                var row = record.Rows[i];
                text.AppendLine($"row={i + 1} layer={row.Layer} object={row.ObjectNumber} path={row.Path}");
            }
            if (Fullscreen && app.Settings.FullscreenDebugPanel)
                for (int i = 0; i < SbInfo.LayerCount; i++) text.AppendLine($"layer={((SbLayer)i)} alive={record.Alive[i]} drawn={record.Drawn[i]} hidden={record.Hidden[i]}");
            foreach (int i in debugHits) text.AppendLine($"hit={record.Rows[i].ObjectNumber} path={record.Rows[i].Path}");
            foreach (var header in debugHeaders) text.AppendLine(FormattableString.Invariant($"header={header.Key} x={header.Value.X:0} y={header.Value.Y:0}"));
            foreach (var row in record.Missing.Take(20)) text.AppendLine($"missing={row.ObjectNumber} path={row.Path}");
            return text.ToString();
        }
    }
}

/// <summary>A one-second rolling sample of real frame durations, with reusable percentile scratch space.</summary>
public sealed class FrameHistory
{
    private readonly Queue<(double Time, double Ms)> samples = new();
    private double[] scratch = new double[256];
    public void Add(double now, double ms)
    {
        samples.Enqueue((now, ms));
        while (samples.Count > 0 && samples.Peek().Time < now - 1) samples.Dequeue();
    }
    public (double Average, double Maximum, double Low) Statistics()
    {
        if (samples.Count == 0) return default;
        if (scratch.Length < samples.Count) Array.Resize(ref scratch, Math.Max(scratch.Length * 2, samples.Count));
        double sum = 0;
        int n = 0;
        foreach (var sample in samples) { scratch[n++] = sample.Ms; sum += sample.Ms; }
        Array.Sort(scratch, 0, n);
        int lowCount = Math.Max(1, (int)Math.Ceiling(n * 0.01));
        double slow = 0;
        for (int i = n - lowCount; i < n; i++) slow += scratch[i];
        return (sum / n, scratch[n - 1], slow > 0 ? 1000 * lowCount / slow : 0);
    }
}
