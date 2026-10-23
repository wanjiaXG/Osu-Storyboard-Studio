using System.Collections;
using System.Numerics;
using ImGuiNET;
using SbStudio.App.Audio;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>
/// Layer list on the left, time tracks on the right. Objects are grouped by storyboard layer with the
/// front-most on top; unfolding an object shows one row per command type, loop and trigger.
///
/// A group of sprites is one row, as a composition inside a composition is one clip in a video editor:
/// its members are not listed, and its bar runs from the first moment one of them is shown to the last.
/// Moved, everything in it moves; an end dragged, what is in it is cut or drawn out. A double click on
/// its name goes into it like into a folder: the list then has what is in that group, and a line over
/// the list says where one is and leads back out.
///
/// The groups have a section of their own at the top of the list, beside the layers of osu!. It is no
/// layer that anything is drawn in: the members of a group may be of several layers and keep them, and
/// inside the group they are listed by layer. What the section stands for is precedence: in each layer
/// what is in a group is drawn on top of what is in none, and a group on top of the ones listed under
/// it, so that two groups that change places change who covers whom.
/// </summary>
public sealed partial class TimelinePanel
{
    /// <summary>
    /// GroupHeader and GroupProperty are a loop or trigger inside a sprite; ObjectGroup and
    /// ObjectGroupProperty are a group of sprites and its own commands.
    /// </summary>
    private enum RowKind { Camera, LayerHeader, Sprite, Sample, Video, Property, GroupHeader, GroupProperty, ObjectGroup, ObjectGroupProperty, HitSoundLane, Waveform, HitObjects, BeatmapBackgroundSlot, TimingLane, TimingControls }

    private struct Row
    {
        public RowKind Kind;
        public int Layer;
        public SbObject? Obj;
        public SbGroup? Group;
        public SbObjectGroup? ObjectGroup;
        public CommandKind Command;
        public char Parameter;
        public int HitSoundLane;
        public bool Camera;
    }

    private enum DragMode { None, Objects, Keys, CommandBody, CommandStart, CommandEnd, Group, GroupStart, GroupEnd, TrimStart, TrimEnd }

    private const int SamplesSection = (int)TimelineSection.Samples;
    /// <summary>The section of the groups: no layer of osu!, but listed like one, above them all.</summary>
    private const int GroupsSection = (int)TimelineSection.Groups;
    private const int VideoSection = (int)TimelineSection.Videos;
    private const int HitSoundsSection = (int)TimelineSection.HitSounds;
    private const int WaveformSection = (int)TimelineSection.Waveform;
    private const int HitObjectsSection = (int)TimelineSection.HitObjects;
    private const int BeatmapBackgroundSection = (int)TimelineSection.BeatmapBackground;
    private const int TimingSection = (int)TimelineSection.Timing;
    private const int SectionCount = TimingSection + 1;

    private static readonly uint VideoColor = Theme.Rgba(120, 200, 235);

    private static readonly double[] TickSteps =
    {
        1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 15000, 30000, 60000, 120000, 300000, 600000, 1800000,
    };

    private readonly EditorApp app;
    private const string SpriteOrderPayload = "SB_SPRITE_ORDER";
    private SbSprite[] draggedSprites = Array.Empty<SbSprite>();
    private bool rowDragFromName;
    private IList? valueEditNodes;
    private CommandKind valueEditKind;
    private double valueEditTime;
    private int valueEditOffset, valueEditFrame;
    private string valueEditText = "";
    private string valueEditOriginalText = "";
    private bool valueEditFocus, valueEditDegrees;
    private readonly ColourPickerPopup colourPicker;
    private readonly Action drawCommandToolbar;
    private readonly List<Row> rows = new();
    private readonly bool[] collapsed = new bool[SectionCount];
    /// <summary>What <see cref="rows"/> was built from: it is only built again when one of these changes.</summary>
    private int rowsRevision = -1, rowsFolds = -1;
    private SbObjectGroup? rowsOpen;
    /// <summary>How many things each section has in the place that is open, whatever narrows the list.</summary>
    private readonly int[] sectionCount = new int[SectionCount];
    private string rowsFilter = "";
    private bool rowsOwnOnly, rowsShowingOnly;
    /// <summary>How many objects the list comes to while it is cut down to what shows now.</summary>
    private int rowsObjects;
    /// <summary>A section was folded or unfolded since <see cref="rows"/> was built.</summary>
    private bool rowsStale;
    private Row? pendingExpansion;
    private SbObjectGroup? pendingCamera;
    private SbObjectGroup? cameraToRemove;

    private double pxPerMs = 0.08;
    private double viewStart = -1000;
    private float leftWidth;
    private float trackX, trackRight;
    private string filter = "";
    /// <summary>List only the objects that belong to the shown difficulty alone. The preview goes on showing everything.</summary>
    private bool ownOnly;
    /// <summary>
    /// List only what is being rendered at this moment, and whatever is selected. The list follows the
    /// playhead; the preview itself is not touched.
    /// </summary>
    private bool showingOnly;
    /// <summary>The objects that list is made of, and the set it is checked against when the moment changes.</summary>
    private HashSet<SbObject> showing = new(), showingNext = new();
    /// <summary>How many objects of each section the list has, counting the ones in sections that are folded.</summary>
    private readonly int[] showingCount = new int[SectionCount];
    /// <summary>What <see cref="showing"/> was worked out for.</summary>
    private (double Time, int Revision, bool Fail, int Selection) showingFor = (double.NaN, -1, false, 0);
    /// <summary>How far the list is scrolled and how tall it and its rows are, as the last frame had them.</summary>
    private float bodyScroll, bodyRowHeight, bodyViewHeight;
    /// <summary>
    /// How many rows the list has to be moved so that the row that was being looked at is where it was,
    /// after a filter changed what is listed around it.
    /// </summary>
    private int rowShift;
    private TimelineRowLayout rowLayout = new(Array.Empty<int>());
    private bool needFit;
    private int zoomSteps;
    private bool needDefaultView = true;
    /// <summary>The row of what is selected, and the playhead, are to be brought into view; see <see cref="RevealSelection"/>.</summary>
    private bool needReveal, needPlayhead;
    /// <summary>Snap ticks inside the visible time range, rebuilt every frame.</summary>
    private readonly List<SnapTick> ticks = new();
    private bool splitterDrag;
    private bool scrubWasPlaying;
    private SbObject? rangeAnchor;
    /// <summary>The same for a group: the row a range of rows is selected from with Shift.</summary>
    private SbObjectGroup? rangeAnchorGroup;
    private SbObject? lastPrimary;
    private SbObjectGroup? lastGroup;
    private double lastTime;
    /// <summary>The view is to move so that the playhead stands near the left end of the tracks.</summary>
    private bool playheadToFront;
    private bool scrubbing;
    /// <summary>Wheel notches waiting to be applied to the row list.</summary>
    private float wheelRows;

    /// <summary>How far one wheel notch moves the time view.</summary>
    private const float WheelPixels = 120;

    private DragMode drag;
    private float dragMouseX;
    private double dragOriginTime;
    private int dragApplied;
    private int dragOffset;
    private bool dragMoved;
    private SbCommand? dragCommand;
    /// <summary>
    /// The command a plain click passes the selection on to: the next one under the mouse, when the press
    /// was on a selected command that others lie over or under. Null when the press was no such thing.
    /// </summary>
    private SbCommand? clickNext;
    private SbGroup? dragGroup;
    private SbSprite? dragSprite;
    private SbObjectGroup? dragObjectGroup;
    /// <summary>The keyframes being dragged, each with the time it had when the drag began.</summary>
    private readonly List<(Keyframe Key, int Origin)> dragKeys = new();
    /// <summary>How far they can all go together before one of them meets a keyframe that stays.</summary>
    private int dragKeyMin, dragKeyMax;

    /// <summary>The sprite as it was when a trim began. Every step of the drag trims this, not the last result.</summary>
    private SbSprite? trimOriginal;
    private (int Start, int End) trimApplied;
    /// <summary>Sprite whose bar edge is under the mouse or being dragged, and which edge.</summary>
    private SbSprite? edgeSprite;
    private DragMode edgePart;
    /// <summary>The same for a group of sprites.</summary>
    private SbObjectGroup? edgeGroup;
    /// <summary>
    /// A double click has just gone into a group. The row that is under the mouse now is another one, and
    /// letting go of the button is not a click on it.
    /// </summary>
    private bool wentInto;
    /// <summary>The group whose end is being dragged, with everything in it as it was when the drag began.</summary>
    private GroupClip.Trim? groupTrim;
    private SbObjectGroup? effectTrim;
    private (int Start, double Duration) effectTrimOriginal;
    private bool effectTrimTimingMapped;
    private long effectTrimTick;
    private SbObjectGroup? trimGroup;

    private static readonly Localized<string> WindowTitle = new(() => L.T("panel.timeline") + "###timeline");

    public bool Open = true;

    public TimelinePanel(EditorApp app, Action drawCommandToolbar) : this(app, drawCommandToolbar, new ColourPickerPopup(app)) { }

    internal TimelinePanel(EditorApp app, Action drawCommandToolbar, ColourPickerPopup picker)
    {
        this.app = app;
        app.CameraSelected += group => pendingCamera = group;
        colourPicker = picker;
        this.drawCommandToolbar = drawCommandToolbar;
    }

    /// <summary>Zooms out so the whole project is visible next time the panel is drawn.</summary>
    public void RequestFit() => needFit = true;

    /// <summary>Returns to the working zoom, where every snap tick can be told apart, around the playhead.</summary>
    public void RequestDefaultView() => needDefaultView = true;

    /// <summary>
    /// Brings the row of the selected object or group into view the next time the panel is drawn, and the
    /// playhead with it; and when a property of it is picked, or one of its loops is selected, the row of
    /// that. For going to a place from elsewhere: the row of something that was selected already, and a
    /// playhead that stands where it stood, do not come into view by themselves.
    /// </summary>
    public void RevealSelection() => needReveal = needPlayhead = true;
    internal void RevealPlayhead() => playheadToFront = true;

    /// <summary>Unfolds the owner of the command just added and reveals its selected row.</summary>
    public void RevealAddedCommand()
    {
        if (app.SelectedGroup is { } group) group.Expanded = true;
        else if (app.Primary is SbSprite sprite) sprite.Expanded = true;
        app.TouchFolds();
        RevealSelection();
    }

    private float X(double time) => trackX + (float)((time - viewStart) * pxPerMs);
    private double TimeAt(float x) => viewStart + (x - trackX) / pxPerMs;

    public void Draw()
    {
        if (!Open)
        {
            CancelColourEdit();
            return;
        }
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4, 4));
        bool visible = ImGui.Begin(WindowTitle.Value, ref Open, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        if (!visible || app.Project == null)
        {
            CancelColourEdit();
            ImGui.End();
            return;
        }

        DrawToolbar();

        var io = ImGui.GetIO();
        Vector2 origin = ImGui.GetCursorScreenPos();
        if (valueEditNodes != null && (app.Time != valueEditTime || app.Playing || ImGui.GetFrameCount() - valueEditFrame > 1)) valueEditNodes = null;
        colourPicker.Tick();
        if (valueEditNodes == null && valueEditKind == CommandKind.C) colourPicker.Cancel(this);
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) && !ImGui.IsMouseReleased(ImGuiMouseButton.Left)) draggedSprites = Array.Empty<SbSprite>();
        Vector2 avail = ImGui.GetContentRegionAvail();
        float rowHeight = Theme.Px(22);
        float scrollHeight = Theme.Px(12);
        float rulerHeight = TimelineHeaderHeight(avail.Y, rowHeight, scrollHeight);
        float headerHeight = rulerHeight;
        float bodyHeight = avail.Y - headerHeight - scrollHeight;

        if (leftWidth <= 0) leftWidth = Theme.Px(300);
        leftWidth = Math.Clamp(leftWidth, Theme.Px(150), MathF.Max(Theme.Px(150), avail.X - Theme.Px(120)));
        trackX = origin.X + leftWidth;
        trackRight = origin.X + avail.X;
        float trackWidth = trackRight - trackX;
        if (trackWidth < 30 || bodyHeight < rowHeight)
        {
            ImGui.End();
            return;
        }

        if (needDefaultView)
        {
            DefaultView(trackWidth);
            needDefaultView = false;
        }
        if (needFit)
        {
            Fit(trackWidth);
            needFit = false;
        }
        if (zoomSteps != 0)
        {
            float anchorX = app.Settings.OsuStyleTimelineIndicator
                ? trackX + (float)(trackWidth * TimelineViewport.OsuAnchor)
                : Math.Clamp(X(app.Time), trackX, trackRight);
            double anchorTime = TimeAt(anchorX);
            pxPerMs = Math.Clamp(pxPerMs * Math.Pow(1.1, zoomSteps), 0.0005, 5);
            viewStart = anchorTime - (anchorX - trackX) / pxPerMs;
            zoomSteps = 0;
        }
        if (app.Settings.OsuStyleTimelineIndicator)
            viewStart = TimelineViewport.FixedStart(app.Time, trackWidth, pxPerMs);

        // Splitter between the layer list and the tracks.
        bool inPanel = io.MousePos.Y >= origin.Y && io.MousePos.Y <= origin.Y + avail.Y;
        bool nearSplitter = inPanel && MathF.Abs(io.MousePos.X - trackX) < 4 &&
                            ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);
        if (nearSplitter || splitterDrag) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        if (nearSplitter && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) splitterDrag = true;
        if (splitterDrag)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) leftWidth += io.MouseDelta.X;
            else splitterDrag = false;
        }

        // The wheel does different things on the two sides of the splitter. Over the time side (ruler,
        // waveform, tracks) it scrolls through time, or the rows with Shift. Over the layer list it scrolls
        // the rows, regardless of modifiers. With Alt (as in the osu! editor) or Ctrl it zooms the time
        // scale only on the time side: a layer-list gesture must never change the time view.
        bool panelHovered = inPanel && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        bool overTracks = panelHovered && io.MousePos.X >= trackX && io.MousePos.X <= trackRight;
        bool overLayers = panelHovered && io.MousePos.X >= origin.X && io.MousePos.X < trackX;
        if (overLayers && io.MousePos.Y >= origin.Y + headerHeight)
        {
            wheelRows += io.MouseWheel;
        }
        else if (overTracks && io.MouseWheel != 0 && (io.KeyAlt || io.KeyCtrl))
        {
            float anchorX = app.Settings.OsuStyleTimelineIndicator ? PlayheadX : io.MousePos.X;
            double anchor = TimeAt(anchorX);
            pxPerMs = Math.Clamp(pxPerMs * Math.Pow(1.2, io.MouseWheel), 0.0005, 5);
            viewStart = anchor - (anchorX - trackX) / pxPerMs;
        }
        else if (overTracks)
        {
            if (io.MouseWheel != 0 && !hitObjectMarqueePending)
            {
                if (io.KeyShift)
                {
                    wheelRows += io.MouseWheel;
                }
                else if (app.Settings.OsuStyleTimelineIndicator && app.Snapping)
                {
                    StepFixedWheel(io.MouseWheel);
                }
                else
                {
                    snapWheelRemainder = 0;
                    // Wheel down moves forward in time, as in the osu! editor.
                    viewStart -= io.MouseWheel * WheelPixels / pxPerMs;
                    SeekFixedView();
                }
            }
            if (io.MouseWheelH != 0 && !hitObjectMarqueePending)
            {
                if (app.Settings.OsuStyleTimelineIndicator && app.Snapping) StepFixedWheel(io.MouseWheelH);
                else
                {
                    snapWheelRemainder = 0;
                    viewStart -= io.MouseWheelH * WheelPixels / pxPerMs;
                    SeekFixedView();
                }
            }
        }

        // Keep the playhead in sight when it moves by itself. A seek (arrow keys, the time field) always
        // brings it back; during playback the view only pages when the playhead runs off the edge, so
        // scrolling away to look at something else is not undone.
        bool timeMoved = app.Time != lastTime || needPlayhead;
        needPlayhead = false;
        float previousX = X(lastTime);
        bool wasInView = previousX >= trackX && previousX <= trackRight - 30;
        lastTime = app.Time;
        if (!app.Settings.OsuStyleTimelineIndicator && timeMoved && !scrubbing && (!app.Playing || wasInView))
        {
            float x = X(app.Time);
            if (x > trackRight - 30) viewStart = app.Time - trackWidth * 0.1 / pxPerMs;
            else if (x < trackX) viewStart = app.Time - trackWidth * (app.Playing ? 0.1 : 0.85) / pxPerMs;
        }
        // A double click on a name has gone to where that object begins. The playhead is put near the left
        // end of the tracks, where it also stands after paging on: a little of what comes before that moment
        // stays in sight, and the rest of the width shows the object itself.
        if (playheadToFront)
        {
            if (!app.Settings.OsuStyleTimelineIndicator) viewStart = app.Time - trackWidth * 0.1 / pxPerMs;
            playheadToFront = false;
        }
        if (app.Settings.OsuStyleTimelineIndicator)
            viewStart = TimelineViewport.FixedStart(app.Time, trackWidth, pxPerMs);

        // Ticks nearer than a few pixels to each other are thinned out (finest subdivisions first).
        ticks.Clear();
        if (app.HasTiming)
            ticks.AddRange(TimingUtil.Ticks(app.Project.Settings.Timing, app.Divisor, viewStart, TimeAt(trackRight), MinTickPixels / pxPerMs));

        DrawHeader(origin, rulerHeight, trackWidth);

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + headerHeight));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        // The wheel is handled above, so the child must not scroll on its own.
        ImGui.BeginChild("##timeline_body", new Vector2(avail.X, bodyHeight), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        DrawBody(rowHeight, bodyHeight);
        ImGui.EndChild();

        DrawHorizontalScrollbar(new Vector2(trackX, origin.Y + headerHeight + bodyHeight), new Vector2(trackWidth, scrollHeight));

        // Playhead on top of the header.
        float px = PlayheadX;
        if (px >= trackX && px <= trackRight)
        {
            var dl = ImGui.GetWindowDrawList();
            dl.AddLine(new Vector2(px, origin.Y), new Vector2(px, origin.Y + headerHeight), Theme.Playhead, 1.5f);
            dl.AddTriangleFilled(new Vector2(px - 6, origin.Y), new Vector2(px + 6, origin.Y), new Vector2(px, origin.Y + 9), Theme.Playhead);
        }

        DrawAddTimingPopup();
        DrawTimingCalculatorPopup();
        DrawTimingAnalysisPopup();
        DrawClipTimeActions();
        ApplyCameraRemoval();
        ImGui.End();
    }

    private void ApplyCameraRemoval()
    {
        if (cameraToRemove == null) return;
        var group = cameraToRemove;
        cameraToRemove = null;
        if (group.Camera == null || !app.Doc.Groups.Contains(group)) return;
        // Apply after all rows have been drawn, so their camera references remain valid this frame.
        app.RemoveCamera(group);
        rowsStale = true;
    }
    private const float MinTickPixels = 4;

    /// <summary>Like the osu! editor: one beat is wide enough that every snap tick is clearly separate.</summary>
    private void DefaultView(float trackWidth)
    {
        if (!app.HasTiming)
        {
            Fit(trackWidth);
            return;
        }
        pxPerMs = Math.Clamp(Theme.Px(96) / app.BeatLength, 0.0005, 5);
        viewStart = app.Time - trackWidth * 0.12 / pxPerMs;
    }

    private void Fit(float trackWidth)
    {
        var (start, end) = app.TimeBounds;
        if (app.Settings.ShowHitSoundRecheck && app.HitSoundRecheckEvents.Events is { Length: > 0 } sounds)
        {
            start = Math.Min(start, sounds[0].Time);
            end = Math.Max(end, sounds.Max(e => e.End));
        }
        if (app.Beatmap is { Objects.Count: > 0 } map)
        {
            start = Math.Min(start, map.Objects.Min(o => o.StartTime));
            end = Math.Max(end, map.Objects.Max(o => o.EndTime));
        }
        double span = Math.Max(1000, end - start);
        pxPerMs = app.Settings.OsuStyleTimelineIndicator ? TimelineViewport.FixedFitScale(start, end, app.Time, trackWidth)
            : Math.Clamp(trackWidth / (span * 1.08), 0.0005, 5);
        viewStart = start - span * 0.04;
    }

    // ---------------------------------------------------------------- toolbar

    private void DrawToolbar()
    {
        var project = app.Project!;

        if (!app.Settings.ShowTimingRecheck)
        {
            ImGui.AlignTextToFramePadding();
            var timing = TimingUtil.At(project.Settings.Timing, app.Time);
            if (timing != null)
            {
                ImGui.TextUnformatted($"{timing.Bpm:0.##} BPM");
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
                ImGui.TextUnformatted(L.T("timeline.noBpm"));
                ImGui.PopStyleColor();
                Theme.Tooltip(L.T("timeline.noBpm.tip"));
            }
            // Every group in this row is the same distance apart (TimingToolbar's gap, also used beside the calculator).
            ImGui.SameLine(0, ToolbarGap);

            DrawDivisorCombo();
            ImGui.SameLine(0, ToolbarGap);
        }
        if (DrawZoomButton("##zoomIn", true)) zoomSteps++;
        Theme.Tooltip(L.T("timeline.zoomIn.tip"));
        ImGui.SameLine(0, ToolbarGap);
        if (DrawZoomButton("##zoomOut", false)) zoomSteps--;
        Theme.Tooltip(L.T("timeline.zoomOut.tip"));

        // While checking Timing the snap box sits with the red line controls, just before adding one.
        if (!app.Settings.ShowTimingRecheck)
        {
            ImGui.SameLine(0, ToolbarGap);
            DrawSnapCheckbox();
        }

        ImGui.SameLine(0, ToolbarGap);
        bool osuIndicator = app.Settings.OsuStyleTimelineIndicator;
        if (ImGui.Checkbox(L.T("timeline.osuIndicator"), ref osuIndicator))
        {
            app.Settings.OsuStyleTimelineIndicator = osuIndicator;
            snapWheelRemainder = 0;
            app.Settings.Save();
        }
        Theme.Tooltip(L.T("timeline.osuIndicator.tip"));

        if (app.Settings.ShowTimingRecheck)
        {
            ImGui.SameLine(0, ToolbarGap);
            DrawTimingToolbarControls();
        }

        ImGui.SameLine(0, ToolbarGap);
        if (ImGui.GetContentRegionAvail().X < ImGui.CalcTextSize(L.T("props.addKey")).X + Theme.Px(40)) ImGui.NewLine();
        drawCommandToolbar();
    }

    private static bool DrawZoomButton(string id, bool zoomIn)
    {
        bool clicked = ImGui.Button(id, new Vector2(Theme.Px(28), ImGui.GetFrameHeight()));
        Vector2 centre = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f - new Vector2(Theme.Px(1.5f));
        centre = new Vector2(MathF.Floor(centre.X) + 0.5f, MathF.Floor(centre.Y) + 0.5f);
        var draw = ImGui.GetWindowDrawList();
        uint colour = ImGui.GetColorU32(ImGuiCol.Text);
        // Preserve the half-pixel length so the small sign stays symmetric about the lens.
        float half = 2.5f * Theme.Scale, stroke = Theme.Px(1.25f);
        draw.AddCircle(centre, Theme.Px(5.5f), colour, 24, Theme.Px(1.25f));
        draw.AddLine(centre + new Vector2(Theme.Px(4)), centre + new Vector2(Theme.Px(7)), colour, Theme.Px(1.5f));
        float bar = stroke * 0.5f;
        draw.AddRectFilled(centre - new Vector2(half, bar), centre + new Vector2(half, bar), colour);
        if (zoomIn) draw.AddRectFilled(centre - new Vector2(bar, half), centre + new Vector2(bar, half), colour);
        return clicked;
    }

    /// <summary>Beat snap divisor, coloured like the ticks it adds.</summary>
    private void DrawDivisorCombo()
    {
        var project = app.Project!;
        // Placed in the Timing toolbar the label has no aligned text before it on the line.
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("timeline.snap"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.Px(66));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.SnapColor(app.Divisor));
        // Tall enough to show every divisor without scrolling.
        bool comboOpen = ImGui.BeginCombo("##divisor", $"1/{app.Divisor}", ImGuiComboFlags.HeightLarge);
        ImGui.PopStyleColor();
        if (comboOpen)
        {
            foreach (int d in TimingUtil.Divisors)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.SnapColor(d));
                if (ImGui.Selectable($"1/{d}", d == app.Divisor))
                {
                    project.Settings.BeatDivisor = d;
                    app.MarkSettingsChanged();
                }
                ImGui.PopStyleColor();
            }
            ImGui.EndCombo();
        }
        // The tip also says so when the zoom hides the finest ticks: snapping still uses the full divisor.
        bool thinned = app.HasTiming && app.BeatLength / app.Divisor * pxPerMs < MinTickPixels;
        Theme.Tooltip(thinned ? L.T("timeline.snap.tip") + "\n" + L.T("timeline.snapThinned") : L.T("timeline.snap.tip"));
    }

    /// <summary>Width of <see cref="DrawDivisorCombo"/>: its label, the usual spacing and the list.</summary>
    private static float DivisorComboWidth => ImGui.CalcTextSize(L.T("timeline.snap")).X + ImGui.GetStyle().ItemSpacing.X + Theme.Px(66);

    /// <summary>Whether the playhead and edits land on beat snap ticks.</summary>
    private void DrawSnapCheckbox()
    {
        bool snap = app.Settings.Snap;
        if (ImGui.Checkbox(L.T("timeline.snapOn"), ref snap))
        {
            app.Settings.Snap = snap;
            snapWheelRemainder = 0;
            app.Settings.Save();
        }
        Theme.Tooltip(L.T("timeline.snapOn.tip"));
    }

    // ---------------------------------------------------------------- header (ruler)

    private void DrawHeader(Vector2 origin, float rulerHeight, float trackWidth)
    {
        var io = ImGui.GetIO();
        var dl = ImGui.GetWindowDrawList();
        float headerHeight = rulerHeight;
        dl.AddRectFilled(origin, new Vector2(trackX, origin.Y + headerHeight), Theme.PanelDark);
        DrawHeaderOptions(origin, headerHeight);

        dl.AddRectFilled(new Vector2(trackX, origin.Y), new Vector2(trackRight, origin.Y + headerHeight), Theme.Rgba(30, 30, 33));
        dl.PushClipRect(new Vector2(trackX, origin.Y), new Vector2(trackRight, origin.Y + headerHeight), true);

        // Upper half: clock time. Lower half: the snap ticks, coloured and sized by divisor as in osu!.
        double step = TickStep(90);
        double viewEnd = TimeAt(trackRight);
        float labelBottom = origin.Y + rulerHeight * 0.46f;
        float rulerBottom = origin.Y + rulerHeight;
        for (double t = Math.Floor(viewStart / step) * step; t <= viewEnd; t += step)
        {
            float x = X(t);
            dl.AddLine(new Vector2(x, origin.Y), new Vector2(x, labelBottom), Theme.Rgba(255, 255, 255, 40));
            dl.AddText(new Vector2(x + 4, origin.Y + 1), Theme.TextDim, TickLabel(t, step));
        }

        if (app.HasTiming)
        {
            float tickArea = rulerBottom - labelBottom;
            foreach (var tick in ticks)
            {
                // On whole pixels: a line between two pixels is spread over both and loses its colour.
                float x = MathF.Round(X(tick.Time));
                float height = tickArea * (tick.Measure ? 1f : Theme.SnapTickHeight(tick.Divisor));
                dl.AddLine(new Vector2(x, rulerBottom - height), new Vector2(x, rulerBottom), Theme.SnapColor(tick.Divisor), tick.Measure ? 2f : 1f);
            }
        }
        else
        {
            // No BPM: there is no snap grid to show, only plain time marks.
            double minor = step / 5;
            for (double t = Math.Floor(viewStart / minor) * minor; t <= viewEnd; t += minor)
                dl.AddLine(new Vector2(X(t), rulerBottom - 5), new Vector2(X(t), rulerBottom), Theme.Rgba(255, 255, 255, 45));
        }

        DrawTimingOffsets(dl, origin.Y, rulerBottom, trackRight, ruler: true);
        dl.PopClipRect();
        dl.AddLine(new Vector2(origin.X, origin.Y + headerHeight), new Vector2(trackRight, origin.Y + headerHeight), Theme.Rgba(0, 0, 0, 160));

        // Scrubbing.
        ImGui.SetCursorScreenPos(new Vector2(trackX, origin.Y));
        ImGui.InvisibleButton("##ruler", new Vector2(trackWidth, headerHeight));
        if (!app.Settings.OsuStyleTimelineIndicator && ImGui.IsItemActivated() && !splitterDrag)
        {
            BeginTimelineScrub();
        }
        // The playhead only ever stops on snap ticks.
        scrubbing = !app.Settings.OsuStyleTimelineIndicator && ImGui.IsItemActive() && !splitterDrag;
        if (scrubbing) app.SeekSnapped(ScrubTimeAt(io.MousePos.X));
        if (ImGui.IsItemDeactivated() && scrubWasPlaying)
        {
            scrubWasPlaying = false;
            app.Play();
        }
        if (ImGui.IsItemHovered())
        {
            var point = TimingOffsetUnderMouse(io.MousePos.X);
            Theme.SetTooltip(point == null
                ? Theme.FormatTime(app.SnapMs(scrubbing ? ScrubTimeAt(io.MousePos.X) : TimeAt(io.MousePos.X)))
                : L.T("timing.offsetInfo", point.Offset, point.Bpm));
        }
    }

    private double TickStep(float minPixels)
    {
        double wanted = minPixels / pxPerMs;
        foreach (double s in TickSteps)
            if (s >= wanted) return s;
        return TickSteps[^1];
    }

    private static string TickLabel(double t, double step)
    {
        long total = (long)Math.Round(Math.Abs(t));
        string sign = t < -0.5 ? "-" : "";
        return step >= 1000
            ? $"{sign}{total / 60000}:{total / 1000 % 60:00}"
            : $"{sign}{total / 60000}:{total / 1000 % 60:00}.{total % 1000:000}";
    }

    private void DrawWaveform(ImDrawListPtr dl, float top, float height)
    {
        var waveform = app.Audio.Waveform;
        float mid = top + height / 2;
        // lazer applies this to waveform visuals only, to match existing beatmaps' timing
        // expectations. Audio decoding, storyboard command times and export remain in track time.
        float x0 = MathF.Max(trackX, X(-Waveform.VisualOffsetMs)), x1 = MathF.Min(trackRight, X(app.Audio.DurationMs - Waveform.VisualOffsetMs));
        if (x1 > x0) dl.AddRectFilled(new Vector2(x0, top + 2), new Vector2(x1, top + height - 2), Theme.Rgba(64, 150, 255, 26));
        if (app.Settings.WaveformStyle == TimelineWaveformStyle.Green)
        {
            DrawGreenWaveform(dl, waveform, top, height, x0, x1);
            return;
        }
        if (waveform == null)
        {
            dl.AddText(new Vector2(MathF.Max(trackX, x0) + 8, mid - ImGui.GetFontSize() / 2), Theme.TextDim, L.T("timeline.analysing"));
            return;
        }

        uint color = Theme.Rgba(100, 170, 255, 200);
        for (float x = x0; x < x1; x += 1)
        {
            float peak = waveform.PeakForDisplay(TimeAt(x), TimeAt(x + 1));
            float h = MathF.Max(0.5f, MathF.Min(1, peak) * (height / 2 - 2));
            dl.AddLine(new Vector2(x, mid - h), new Vector2(x, mid + h), color);
        }
    }

    /// <summary>
    /// Where one is, while a group is open: the storyboard and the groups one went into, each to be
    /// clicked to go back there, and an arrow in front that goes one step out.
    /// </summary>
    private void DrawPath(float left, float top, float height)
    {
        var path = new List<SbObjectGroup>();
        for (var g = app.OpenGroup; g != null && path.Count < SbObjectGroup.MaxDepth; g = g.Parent) path.Insert(0, g);

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(Theme.Px(4), 0));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Theme.Px(3), 0));
        ImGui.SetCursorScreenPos(new Vector2(left, top + MathF.Round((height - ImGui.GetFrameHeight()) / 2)));
        float right = trackX - Theme.Px(6);
        if (ImGui.Button("←##leave")) app.LeaveGroup();
        Theme.Tooltip(L.T("timeline.leaveGroup.tip"));
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, 0u);
        if (ImGui.Button(L.T("timeline.root") + "##root")) app.OpenContainer(null);
        for (int i = 0; i < path.Count; i++)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            Theme.TextDimmed("›");
            ImGui.SameLine();
            // No further than the list is wide: the group one is in is the one that has to be read.
            if (ImGui.GetCursorScreenPos().X > right - Theme.Px(30)) break;
            string name = path[i].Name.Length > 0 ? path[i].Name : L.T("group.unnamed");
            bool last = i == path.Count - 1;
            if (last) ImGui.PushStyleColor(ImGuiCol.Text, Theme.GroupColor);
            ImGui.PushID(i);
            if (ImGui.Button(name) && !last) app.OpenContainer(path[i]);
            ImGui.PopID();
            if (last) ImGui.PopStyleColor();
        }
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    /// <summary>A small checkbox on a line of its own among those over the names of the rows.</summary>
    private void HeaderCheckbox(string label, ref bool value, float left, float top, float height, string tip, string target)
    {
        // No taller than the text, so that two of them on lines one under the other stay apart.
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(Theme.Px(3), 0));
        // On this dark strip the usual field colour would not show at all.
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Theme.Rgba(52, 52, 58));
        ImGui.SetCursorScreenPos(new Vector2(left, top + MathF.Round((height - ImGui.GetFrameHeight()) / 2)));
        UiScriptTargets.Item(target, ImGui.Checkbox(label, ref value));
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        Theme.Tooltip(tip);
    }

    // ---------------------------------------------------------------- rows

    /// <summary>
    /// Works out what is being rendered at this moment: every sprite whose commands span the current
    /// time, whether or not anything of it can be seen just now (it may be transparent, off the screen,
    /// or switched off with the eye), and every video that is playing. What is selected is always part of
    /// it, so that the thing being worked on does not leave the list. Sounds have no picture and are left out.
    /// </summary>
    /// <returns>True when it is not the same set as before.</returns>
    private bool FindShowing()
    {
        int selection = app.Selection.Count;
        foreach (var obj in app.Selection) selection = selection * 31 + obj.Id;
        foreach (var group in app.SelectedGroups) selection = selection * 31 + group.Id;
        var now = (app.Time, app.DocRevision, app.RenderOptions.FailState, selection);
        if (now == showingFor) return false;
        showingFor = now;

        var index = app.Index;
        var found = showingNext;
        found.Clear();
        for (int layer = 0; layer < SbInfo.LayerCount; layer++)
        {
            // Of the Pass and Fail layers only one is rendered.
            if (layer == (int)SbLayer.Fail && !now.FailState) continue;
            if (layer == (int)SbLayer.Pass && now.FailState) continue;
            var sprites = index.Layers[layer];
            for (int i = 0; i < sprites.Count; i++)
                if (sprites.IsActive(i, now.Time)) found.Add(sprites[i]);
        }
        foreach (var video in index.Videos)
        {
            double at = now.Time - video.Time;
            if (at < 0) continue;
            // From its start for as long as the file lasts; a file that cannot be read may last any time.
            var info = video.Path.Length == 0 ? null : app.Videos.Info(app.Project!.AssetFullPath(video.Path));
            if (info == null || at <= info.DurationMs) found.Add(video);
        }
        foreach (var obj in app.Selection) found.Add(obj);

        if (found.SetEquals(showing)) return false;
        (showing, showingNext) = (found, showing);
        return true;
    }

    /// <summary>The current list in display order, including rows outside the viewport.</summary>
    internal string RowsState => string.Join("\n", rows.Select((row, index) => System.Text.Json.JsonSerializer.Serialize(new
    {
        Kind = row.Kind.ToString(),
        Owner = row.ObjectGroup?.Name ?? (row.Obj as SbSprite)?.Path ?? "",
        Command = row.Kind is RowKind.Property or RowKind.GroupProperty or RowKind.ObjectGroupProperty ? row.Command.ToString() : null,
        Parameter = row.Parameter == '\0' ? null : row.Parameter.ToString(),
        row.Camera,
        CameraActive = row.Camera && row.ObjectGroup!.CameraActive,
        Visible = bodyRowHeight > 0 && rowLayout.Top(index) * bodyRowHeight >= bodyScroll + bodyRowHeight &&
            (rowLayout.Top(index) + rowLayout.Height(index)) * bodyRowHeight <= bodyScroll + bodyViewHeight,
    }))) + "\n";

    /// <summary>Lists the rows again if the storyboard, the filter or a fold changed since the last time.</summary>
    private void BuildRows()
    {
        UpdateHitSoundRows();
        UpdateReferenceRows();
        UpdateTimingRows();
        // Without a difficulty there is nothing of its own to single out.
        bool own = ownOnly && app.Project!.CurrentDifficulty != null;
        // While something is dragged the list stays as it is: nothing may slip away under the hand.
        bool changed = showingOnly && (drag == DragMode.None || !rowsShowingOnly) && FindShowing();
        if (!changed && !rowsStale && rowsRevision == app.DocRevision && rowsFolds == app.FoldRevision && rowsFilter == filter &&
            rowsOwnOnly == own && rowsShowingOnly == showingOnly && rowsOpen == app.OpenGroup) return;
        // When it is a filter that changes the list, rather than an edit or a fold, the row that was being
        // looked at stays where it is on screen: switching a filter off must not send one looking for it again.
        bool refiltered = changed || rowsFilter != filter || rowsOwnOnly != own || rowsShowingOnly != showingOnly;
        var (anchor, anchorRow) = refiltered ? Anchor() : (null, -1);
        rowsStale = false;
        rowsRevision = app.DocRevision;
        rowsFolds = app.FoldRevision;
        rowsFilter = filter;
        rowsOwnOnly = own;
        rowsShowingOnly = showingOnly;
        rowsOpen = app.OpenGroup;

        int anchorTop = anchorRow >= 0 ? rowLayout.Top(anchorRow) : 0;
        rows.Clear();
        Array.Clear(sectionCount);
        Array.Clear(showingCount);
        rowsObjects = 0;
        var index = app.Index;
        var open = app.OpenGroup;
        bool filtering = filter.Length > 0, now = showingOnly;

        AddReferenceRows();
        if (app.Settings.ShowHitSoundRecheck)
        {
            sectionCount[HitSoundsSection] = recheckIndex.Events.Length;
            rows.Add(new Row { Kind = RowKind.LayerHeader, Layer = HitSoundsSection });
            if (!collapsed[HitSoundsSection])
                for (int lane = 0; lane < 5; lane++)
                    if (lane < 4 || recheckIndex.Lanes[lane].Length > 0)
                        rows.Add(new Row { Kind = RowKind.HitSoundLane, Layer = HitSoundsSection, HitSoundLane = lane });
        }

        // The list has what is in the place that is open: the storyboard, or a group that was gone into.
        // Its groups come first, in a section of their own: they are in no layer, whatever layers their
        // sprites are in. A group whose sprites all belong to difficulties that are not shown is left out
        // with them.
        rows.Add(new Row { Kind = RowKind.LayerHeader, Layer = GroupsSection });
        // A later group is drawn in front, so it comes first, as with the sprites of a layer.
        var groups = app.Doc.Groups;
        for (int g = groups.Count - 1; g >= 0; g--)
        {
            var group = groups[g];
            if (group.Parent != open) continue;
            var content = index.ContentOf(group);
            if (content.Sprites.Count == 0 && group.Plugin==null) continue;
            sectionCount[GroupsSection]++;
            if (filtering && !group.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) && !content.Sprites.Any(Matches)) continue;
            if (own && !content.Sprites.Any(m => m.Difficulty != null)) continue;
            if (now && !app.IsSelected(group) && !content.Sprites.Any(showing.Contains)) continue;
            showingCount[GroupsSection]++;
            rowsObjects++;
            if (collapsed[GroupsSection]) continue;
            rows.Add(new Row { Kind = RowKind.ObjectGroup, Layer = GroupsSection, ObjectGroup = group });
            if (!group.Expanded) continue;
            if (group.Camera is { } camera)
            {
                rows.Add(new Row { Kind = RowKind.Camera, Layer = GroupsSection, ObjectGroup = group, Camera = true });
                if (camera.Expanded)
                    foreach (var kind in SbInfo.CameraKinds)
                        rows.Add(new Row { Kind = RowKind.ObjectGroupProperty, Layer = GroupsSection, ObjectGroup = group, Camera = true, Command = kind });
            }
            foreach (var kind in SbInfo.AllKinds.Concat(SbInfo.DepthKinds))
            {
                if (group.Commands.Any(c => c.Kind == kind))
                    rows.Add(new Row { Kind = RowKind.ObjectGroupProperty, Layer = GroupsSection, ObjectGroup = group, Command = kind });
            }
        }

        // Then the layers, each with the sprites of the open place that are in it. A sprite that is in a
        // group of that place is not listed: it is part of the group's row. In the storyboard itself the
        // sprites of a layer are those the index has for it; in a group they are its members of that layer.
        var members = open != null ? index.MembersOf(open) : null;
        for (int layer = SbInfo.LayerCount - 1; layer >= 0; layer--)
        {
            rows.Add(new Row { Kind = RowKind.LayerHeader, Layer = layer });
            // Later objects are drawn in front, so they come first.
            var sprites = index.Layers[layer];
            for (int i = (members?.Count ?? sprites.Count) - 1; i >= 0; i--)
            {
                var sprite = members != null ? members[i] : sprites[i];
                if (members != null ? (int)sprite.Layer != layer : sprite.Group != null) continue;
                if (app.IsProtectedBeatmapBackground(sprite)) continue;
                sectionCount[layer]++;
                if (filtering && !Matches(sprite)) continue;
                if (own && sprite.Difficulty == null) continue;
                if (now && !showing.Contains(sprite)) continue;
                showingCount[layer]++;
                rowsObjects++;
                if (collapsed[layer]) continue;
                rows.Add(new Row { Kind = RowKind.Sprite, Layer = layer, Obj = sprite });
                if (sprite.Expanded) AddSpriteRows(sprite, layer);
            }
        }

        // Sounds and videos are not in any group: they are listed with the storyboard itself.
        if (app.OpenGroup == null)
        {
            rows.Add(new Row { Kind = RowKind.LayerHeader, Layer = SamplesSection });
            foreach (var sample in index.Samples)
            {
                sectionCount[SamplesSection]++;
                if (filtering && !Matches(sample)) continue;
                if (own && sample.Difficulty == null) continue;
                if (now && !showing.Contains(sample)) continue;
                showingCount[SamplesSection]++;
                rowsObjects++;
                if (!collapsed[SamplesSection]) rows.Add(new Row { Kind = RowKind.Sample, Layer = SamplesSection, Obj = sample });
            }

            // The videos play behind every layer, so they are listed below them all.
            rows.Add(new Row { Kind = RowKind.LayerHeader, Layer = VideoSection });
            foreach (var video in index.Videos)
            {
                sectionCount[VideoSection]++;
                if (filtering && !Matches(video)) continue;
                if (own && video.Difficulty == null) continue;
                if (now && !showing.Contains(video)) continue;
                showingCount[VideoSection]++;
                rowsObjects++;
                if (!collapsed[VideoSection]) rows.Add(new Row { Kind = RowKind.Video, Layer = VideoSection, Obj = video });
            }

            // Background declarations have their own section, separate from animated storyboard sprites.
            rows.Add(new Row { Kind = RowKind.LayerHeader, Layer = BeatmapBackgroundSection });
            var backgrounds = index.Layers[(int)SbLayer.Background];
            for (int i = backgrounds.Count - 1; i >= 0; i--)
            {
                var background = backgrounds[i];
                if (!app.IsProtectedBeatmapBackground(background)) continue;
                sectionCount[BeatmapBackgroundSection]++;
                if (filtering && !Matches(background)) continue;
                if (own && background.Difficulty == null) continue;
                if (now && !showing.Contains(background)) continue;
                showingCount[BeatmapBackgroundSection]++;
                rowsObjects++;
                if (!collapsed[BeatmapBackgroundSection]) rows.Add(new Row { Kind = RowKind.Sprite, Layer = BeatmapBackgroundSection, Obj = background });
            }
            if (sectionCount[BeatmapBackgroundSection] == 0 && !collapsed[BeatmapBackgroundSection])
                rows.Add(new Row { Kind = RowKind.BeatmapBackgroundSlot, Layer = BeatmapBackgroundSection });
        }

        rowLayout = new(rows.Select(r => r.Kind == RowKind.Waveform && app.Settings.WaveformStyle == TimelineWaveformStyle.Green
            ? rowsWaveformChannels * 3 : r.Kind == RowKind.Waveform ? 4 : r.Kind == RowKind.HitObjects && referenceMap?.Mode is 0 or 2 ? 3
            : r.Kind is RowKind.HitObjects or RowKind.HitSoundLane or RowKind.TimingLane ? 2 : 1));
        if (anchor != null && RowOf(anchor) is var row and >= 0) rowShift += rowLayout.Top(row) - anchorTop;
    }

    /// <summary>
    /// The object whose row is to stay in its place when the list changes around it: the selected one if
    /// its row is in view, else the first one in view. With the row it is in now.
    /// </summary>
    private (SbObject? Obj, int Row) Anchor()
    {
        if (bodyRowHeight <= 0 || rows.Count == 0) return (null, -1);
        int top = Math.Max(0, rowLayout.RowAt(bodyScroll / bodyRowHeight));
        int bottom = Math.Min(rows.Count - 1, rowLayout.RowAt((bodyScroll + bodyViewHeight) / bodyRowHeight));
        if (app.Primary != null)
        {
            int row = RowOf(app.Primary);
            if (row >= top && row <= bottom) return (app.Primary, row);
        }
        for (int i = top; i <= bottom; i++)
            if (rows[i].Kind is RowKind.Sprite or RowKind.Sample or RowKind.Video) return (rows[i].Obj, i);
        return (null, -1);
    }

    /// <summary>The row of an object, or -1 when the list does not have it.</summary>
    private int RowOf(SbObject obj)
    {
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Obj == obj && rows[i].Kind is RowKind.Sprite or RowKind.Sample or RowKind.Video) return i;
        return -1;
    }

    private bool Matches(SbObject obj) =>
        obj.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) || obj.Path.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private void AddSpriteRows(SbSprite sprite, int layer)
    {
        foreach (var kind in SbInfo.AllKinds.Concat(SbInfo.DepthKinds))
        {
            foreach (var node in sprite.Nodes)
            {
                if (node is SbCommand c && c.Kind == kind)
                {
                    AddPropertyRows(new Row { Kind = RowKind.Property, Layer = layer, Obj = sprite, Command = kind });
                    break;
                }
            }
        }

        foreach(var kind in SbInfo.DepthKinds)
            if(sprite.DepthCommands.Any(c=>c.Kind==kind)) rows.Add(new Row{Kind=RowKind.Property,Layer=layer,Obj=sprite,Command=kind});
        foreach (var node in sprite.Nodes)
        {
            if (node is not SbGroup group) continue;
            rows.Add(new Row { Kind = RowKind.GroupHeader, Layer = layer, Obj = sprite, Group = group });
            foreach (var kind in SbInfo.AllKinds.Concat(SbInfo.DepthKinds))
            {
                if (group.Commands.Any(c => c.Kind == kind))
                    AddPropertyRows(new Row { Kind = RowKind.GroupProperty, Layer = layer, Obj = sprite, Group = group, Command = kind });
            }
        }
    }

    /// <summary>How many things a section has in the place that is open: the sprites of a layer, the groups, the sounds or the videos.</summary>
    private int CountInSection(int section) => sectionCount[section];

    private void DrawBody(float rowHeight, float bodyHeight)
    {
        (bodyScroll, bodyRowHeight, bodyViewHeight) = (ImGui.GetScrollY(), rowHeight, bodyHeight);
        if (wentInto && !ImGui.IsMouseDown(ImGuiMouseButton.Left) && !ImGui.IsMouseReleased(ImGuiMouseButton.Left)) wentInto = false;
        if (pendingCamera != null && collapsed[GroupsSection])
        {
            collapsed[GroupsSection] = false;
            rowsStale = true;
        }
        BuildRows();

        var io = ImGui.GetIO();
        var style = ImGui.GetStyle();
        var dl = ImGui.GetWindowDrawList();
        Vector2 winPos = ImGui.GetWindowPos();
        // How far the list can be scrolled. Not what ImGui makes of it: to ImGui the track area counts as
        // content, and that is always put where the view is, so a list that has become shorter than it is
        // scrolled would stay scrolled past its end, with nothing of it left to see.
        float maxScroll = MathF.Max(0, rowLayout.Total * rowHeight - bodyHeight);
        float scrollY = ImGui.GetScrollY();
        // After a filter changed the list, the row that was being looked at stays where it was. Then the wheel.
        float wanted = Math.Clamp(scrollY + rowShift * rowHeight - wheelRows * rowHeight * 3, 0, maxScroll);
        rowShift = 0;
        wheelRows = 0;
        if (pendingExpansion is { } expanded)
        {
            pendingExpansion = null;
            int parent = rows.IndexOf(expanded);
            if (parent >= 0 && parent + 1 < rows.Count)
            {
                var child = rows[parent + 1];
                bool belongs = expanded.Kind switch
                {
                    RowKind.LayerHeader => child.Kind != RowKind.LayerHeader && child.Layer == expanded.Layer,
                    RowKind.Sprite => child.Obj == expanded.Obj,
                    RowKind.Property or RowKind.GroupProperty => expanded.Command == CommandKind.P && child.Parameter != '\0' && child.Obj == expanded.Obj && child.Group == expanded.Group,
                    RowKind.ObjectGroup => child.Kind is RowKind.ObjectGroupProperty or RowKind.Camera && child.ObjectGroup == expanded.ObjectGroup,
                    RowKind.Camera => child.Camera && child.ObjectGroup == expanded.ObjectGroup,
                    _ => false,
                };
                if (belongs) wanted = (float)rowLayout.ScrollAfterExpansion(parent, wanted / rowHeight, bodyHeight / rowHeight) * rowHeight;
            }
        }
        if (wanted != scrollY)
        {
            scrollY = wanted;
            ImGui.SetScrollY(scrollY);
        }
        float contentTop = winPos.Y - scrollY;
        float bodyRight = winPos.X + ImGui.GetWindowWidth() - (ImGui.GetScrollMaxY() > 0 ? style.ScrollbarSize : 0);
        float bodyBottom = winPos.Y + bodyHeight;
        if (draggedSprites.Length > 0 && ImGui.IsMouseDragging(ImGuiMouseButton.Left) && io.MousePos.X < trackX && io.MousePos.X >= winPos.X)
        {
            float edge = Theme.Px(30);
            if (io.MousePos.Y < winPos.Y + edge) ImGui.SetScrollY(Math.Max(0, scrollY - Theme.Px(360) * io.DeltaTime));
            else if (io.MousePos.Y > bodyBottom - edge) ImGui.SetScrollY(scrollY + Theme.Px(360) * io.DeltaTime);
        }

        // Selecting something elsewhere (preview, paste, undo) brings its row into view.
        if (app.Primary != lastPrimary || app.SelectedGroup != lastGroup || needReveal)
        {
            lastPrimary = app.Primary;
            lastGroup = app.SelectedGroup;
            var group = lastPrimary == null ? lastGroup : null;
            bool inside = needReveal;
            needReveal = false;
            if (lastPrimary != null || group != null)
            {
                int section = group != null ? GroupsSection : lastPrimary is SbSample ? SamplesSection : lastPrimary is SbVideo ? VideoSection
                    : app.IsProtectedBeatmapBackground(lastPrimary!) ? BeatmapBackgroundSection : (int)lastPrimary!.Layer;
                if (collapsed[section])
                {
                    collapsed[section] = false;
                    rowsStale = true;
                    BuildRows();
                }
                int index = group != null
                    ? rows.FindIndex(r => r.Kind == RowKind.ObjectGroup && r.ObjectGroup == group)
                    : rows.FindIndex(r => r.Obj == lastPrimary && r.Kind is RowKind.Sprite or RowKind.Sample or RowKind.Video);
                // Gone to from elsewhere, it is the row of what is picked in it that has to be in view.
                int target = inside && index >= 0 ? PickedRow(index) : index;
                float y = rowLayout.Top(target) * rowHeight;
                // The head of the section stays in sight over the first row of the view.
                // Once the list is scrolled, the head of a section lies over its first row.
                float covered = scrollY > 0 ? rowHeight : 0;
                bool hidden = target != index ? y < scrollY + rowHeight || y + rowHeight > scrollY + bodyHeight : y < scrollY + covered || y + rowHeight > scrollY + bodyHeight;
                if (target >= 0 && hidden)
                {
                    // With its object above it where the two fit, the object's row under the head of the section.
                    bool together = target != index && (rowLayout.Top(target) - rowLayout.Top(index) + 2) * rowHeight <= bodyHeight;
                    ImGui.SetScrollY(MathF.Max(0, together ? (rowLayout.Top(index) - 1) * rowHeight : y - bodyHeight / 3));
                }
            }
        }

        if (pendingCamera is { } cameraGroup)
        {
            pendingCamera = null;
            int camera = rows.FindIndex(r => r.Kind == RowKind.Camera && r.ObjectGroup == cameraGroup);
            if (camera >= 0)
            {
                float top = rowLayout.Top(camera) * rowHeight;
                float bottom = rowLayout.Top(camera + 1 + SbInfo.CameraKinds.Length) * rowHeight;
                // Keep the heading and as many properties as fit below the pinned section header.
                float target = MathF.Min(top - rowHeight, MathF.Max(scrollY, bottom - bodyHeight));
                if (top < scrollY + rowHeight || bottom > scrollY + bodyHeight)
                {
                    scrollY = Math.Clamp(target, 0, maxScroll);
                    ImGui.SetScrollY(scrollY);
                }
            }
        }

        contentTop = winPos.Y - scrollY;
        bodyScroll = scrollY;
        int first = Math.Max(0, rowLayout.RowAt(scrollY / rowHeight));
        int last = Math.Min(rows.Count - 1, rowLayout.RowAt((scrollY + bodyHeight) / rowHeight));

        // The head of the section the topmost row belongs to stays in sight, like a frozen row of a
        // spreadsheet: once it is scrolled off it is drawn again at the top, so that it is always plain
        // which layer the rows under it are in. The head of the next section pushes it out as it arrives.
        int pinned = -1;
        float pinnedY = winPos.Y;
        if (first < rows.Count)
        {
            int head = first;
            while (head > 0 && rows[head].Kind != RowKind.LayerHeader) head--;
            if (rows[head].Kind == RowKind.LayerHeader && contentTop + rowLayout.Top(head) * rowHeight < winPos.Y)
            {
                pinned = head;
                for (int i = first + 1; i <= last; i++)
                {
                    if (rows[i].Kind != RowKind.LayerHeader) continue;
                    pinnedY = MathF.Min(pinnedY, contentTop + (rowLayout.Top(i) - 1) * rowHeight);
                    break;
                }
            }
        }

        dl.AddRectFilled(new Vector2(winPos.X, winPos.Y), new Vector2(trackX, bodyBottom), Theme.Rgba(38, 38, 41));
        dl.AddRectFilled(new Vector2(trackX, winPos.Y), new Vector2(bodyRight, bodyBottom), Theme.Rgba(27, 27, 30));

        // One input area for all tracks; rows are hit-tested by hand.
        ImGui.SetCursorScreenPos(new Vector2(trackX, winPos.Y));
        ImGui.InvisibleButton("##tracks", new Vector2(MathF.Max(1, bodyRight - trackX), bodyHeight));
        bool tracksHovered = ImGui.IsItemHovered();
        int hoverRow = rowLayout.RowAt((io.MousePos.Y - contentTop) / rowHeight);
        // Under the head that stays in sight the mouse is on that head, not on the row it covers.
        if (pinned >= 0 && io.MousePos.Y < pinnedY + rowHeight) hoverRow = pinned;
        Row? hovered = hoverRow >= 0 && hoverRow < rows.Count ? rows[hoverRow] : null;
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            hitObjectSelectionFocused = tracksHovered && hovered is { Kind: RowKind.HitObjects } && !splitterDrag;
        UpdateWaveformScrub(io);

        bool hitSoundRow = hovered is { Layer: HitSoundsSection };
        bool referenceRow = hitSoundRow || hovered is { Layer: WaveformSection or HitObjectsSection or TimingSection };
        if (hovered is { Layer: WaveformSection or HitObjectsSection } reference && tracksHovered && !splitterDrag)
            HandleReferenceInput(reference, io, contentTop + rowLayout.Top(hoverRow) * rowHeight, rowLayout.Height(hoverRow) * rowHeight);
        if (hitSoundRow && tracksHovered && !splitterDrag) HandleHitSoundInput(hovered!.Value, io.MousePos.X);
        if (hovered is { Kind: RowKind.TimingLane } && tracksHovered && !splitterDrag) HandleTimingInput(io.MousePos.X);
        UpdateHitObjectMarquee(io, contentTop, rowHeight);
        if (ImGui.IsItemActivated() && !splitterDrag && !referenceRow) BeginDrag(hovered, io);
        unsafe
        {
            if (ImGui.BeginDragDropTarget())
            {
                var payload=ImGui.AcceptDragDropPayload("SB_EFFECT");
                if(payload.NativePtr!=null && payload.IsDelivery() && app.DraggedEffect is {} effect)
                {
                    try {app.AddEffect(effect,app.SnapMs(TimeAt(io.MousePos.X)),hovered?.ObjectGroup);rowsStale=true;}
                    catch(Exception ex) {app.SetStatus(ex.Message);}
                    app.DraggedEffect=null;
                }
                ImGui.EndDragDropTarget();
            }
        }
        if (ImGui.IsItemActive() && drag != DragMode.None) UpdateDrag(io);
        if (ImGui.IsItemDeactivated())
        {
            if (effectTrim != null) { UpdateDrag(io); app.FinishEffectEdit(); effectTrim = null; }
            if (!dragMoved && clickNext != null)
            {
                app.ClearKeySelection();
                app.SelectedCommands.Add(clickNext);
            }
            clickNext = null;
            drag = DragMode.None;
            // The end of a group has been let go of: what it left with nothing to show goes for good.
            if (groupTrim != null)
            {
                app.DropEmptied(groupTrim.Emptied.ToList());
                groupTrim = null;
                trimGroup = null;
            }
        }
        var keyRow = hovered is { Kind: RowKind.Property or RowKind.GroupProperty or RowKind.ObjectGroupProperty } ? hovered : null;
        var hoverKeys = tracksHovered && keyRow != null ? RowKeys(keyRow.Value) : null;
        var hoverKey = hoverKeys != null ? HitKey(hoverKeys, RowOffset(keyRow!.Value), io.MousePos.X) : (-1, false);
        if (tracksHovered && drag == DragMode.None && keyRow is { } deleteRow &&
            hoverKey is (>= 0, true) && ImGui.IsMouseReleased(ImGuiMouseButton.Right) &&
            app.SelectedKeys.Contains(new KeyRef(RowNodes(deleteRow), deleteRow.Command, hoverKey.Item1)))
        {
            // Only an already selected key responds: right-clicking a transition or empty space is harmless.
            app.SelectedCommands.Clear();
            app.DeleteSelection();
        }
        if (tracksHovered && !referenceRow && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !dragMoved)
        {
            if (hovered is { Kind: RowKind.ObjectGroup, ObjectGroup: { Plugin.Kind: PluginKind.Effect } effectGroup } &&
                GroupBarSpan(effectGroup) is {} effectSpan && BarPart(effectSpan.Start, effectSpan.End, io.MousePos.X) != DragMode.None)
            {
                app.Seek(effectSpan.Start);
                playheadToFront = true;
            }
            else if (!app.Settings.OsuStyleTimelineIndicator)
            {
                // On a keyframe the playhead goes to exactly that keyframe, also when it stands between two ticks.
                if (hoverKey is (>= 0, true)) app.Seek(RowOffset(keyRow!.Value) + hoverKeys![hoverKey.Item1].Time);
                else app.SeekSnapped(TimeAt(io.MousePos.X));
            }
        }
        if (tracksHovered && drag == DragMode.None && keyRow is { } hr)
        {
            if (hoverKeys != null)
            {
                // A keyframe is moved along its row.
                if (hoverKey.Item2) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
            }
            else
            {
                var (_, part) = Grabbed(HitCommands(hr, io.MousePos.X), out _);
                if (part is DragMode.CommandStart or DragMode.CommandEnd) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
            }
        }

        if (tracksHovered && !referenceRow && !splitterDrag && ImGui.IsMouseReleased(ImGuiMouseButton.Right) && hovered is {} clipRow)
        {
            if (clipRow is { Kind: RowKind.Sprite, Obj: SbSprite sprite } && BarPart(sprite, io.MousePos.X) != DragMode.None)
                QueueClipTimeMenu(sprite, null);
            else if (clipRow is { Kind: RowKind.ObjectGroup, ObjectGroup: {} clipGroup } && GroupBarSpan(clipGroup) is {} clipSpan &&
                BarPart(clipSpan.Start, clipSpan.End, io.MousePos.X) != DragMode.None)
                QueueClipTimeMenu(null, clipGroup);
        }

        // Edges of a sprite's bar can be dragged to trim it, and those of a group's.
        edgeSprite = null;
        edgeGroup = null;
        edgePart = DragMode.None;
        if (drag is DragMode.TrimStart or DragMode.TrimEnd)
        {
            edgeSprite = dragSprite;
            edgeGroup = effectTrim ?? trimGroup;
            edgePart = drag;
        }
        else if (tracksHovered && drag == DragMode.None && !io.KeyCtrl && hovered is { Kind: RowKind.ObjectGroup, ObjectGroup: { } clip } &&
                 GroupBarSpan(clip) is {} edgeSpan && app.Index.ContentOf(clip) is {} inside &&
                 (inside.Sprites.Count == 0 || inside.Locked < inside.Sprites.Count))
        {
            var part = BarPart(edgeSpan.Start, edgeSpan.End, io.MousePos.X);
            if (part is DragMode.TrimStart or DragMode.TrimEnd)
            {
                if (GroupBarCanResize(clip, inside))
                {
                    edgeGroup = clip;
                    edgePart = part;
                    string tip = part == DragMode.TrimStart
                        ? L.T("timeline.trimStart", Theme.FormatTime(edgeSpan.Start))
                        : L.T("timeline.trimEnd", Theme.FormatTime(edgeSpan.End));
                    Theme.SetTooltip(clip.Plugin == null ? tip + "\n" + L.T("timeline.trimGroup.tip") : tip);
                }
                else if (clip.Plugin is { Locked: false } link)
                    Theme.SetTooltip(L.T(link.Effect != null ? "effects.fixedDuration" : "status.pluginTrim"));
            }
        }
        else if (tracksHovered && drag == DragMode.None && !io.KeyCtrl && hovered is { Kind: RowKind.Sprite, Obj: SbSprite { Locked: false } candidate })
        {
            var part = BarPart(candidate, io.MousePos.X);
            if (part is DragMode.TrimStart or DragMode.TrimEnd)
            {
                edgeSprite = candidate;
                edgePart = part;
                var timeline = candidate.Timeline;
                Theme.SetTooltip(part == DragMode.TrimStart
                    ? L.T("timeline.trimStart", Theme.FormatTime(timeline.StartTime))
                    : L.T("timeline.trimEnd", Theme.FormatTime(timeline.EndTime)));
            }
        }
        if (edgeSprite != null || edgeGroup != null) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);

        // Left column. The head that stays in sight takes the mouse before the rows it lies over, and is
        // drawn after them.
        bool pinnedClicked = false;
        if (pinned >= 0)
        {
            ImGui.SetCursorScreenPos(new Vector2(winPos.X, pinnedY));
            pinnedClicked = ImGui.InvisibleButton("##pinned", new Vector2(MathF.Max(1, trackX - winPos.X - 4), rowHeight)) && !splitterDrag;
            DrawOrderTarget(dl, rows[pinned], new Vector2(winPos.X, pinnedY), rowHeight);
        }
        dl.PushClipRect(new Vector2(winPos.X, winPos.Y), new Vector2(trackX, bodyBottom), true);
        for (int i = first; i <= last; i++)
        {
            ImGui.PushID(i);
            DrawRowLabel(dl, rows[i], new Vector2(winPos.X, contentTop + rowLayout.Top(i) * rowHeight), rowLayout.Height(i) * rowHeight, i);
            ImGui.PopID();
        }
        if (pinned >= 0)
        {
            bool folded = DrawSectionHead(dl, rows[pinned].Layer, new Vector2(winPos.X, pinnedY), rowHeight, pinnedClicked);
            // Folded from here, the section is left with its head where the list begins.
            if (folded) ImGui.SetScrollY(rowLayout.Top(pinned) * rowHeight);
        }
        dl.PopClipRect();

        // Tracks.
        dl.PushClipRect(new Vector2(trackX, winPos.Y), new Vector2(bodyRight, bodyBottom), true);
        for (int i = first; i <= last; i++)
        {
            float y = contentTop + rowLayout.Top(i) * rowHeight;
            float height = rowLayout.Height(i) * rowHeight;
            if (rows[i].Kind == RowKind.LayerHeader)
                dl.AddRectFilled(new Vector2(trackX, y), new Vector2(bodyRight, y + height), Theme.Rgba(44, 44, 48));
            else if ((i & 1) == 1)
                dl.AddRectFilled(new Vector2(trackX, y), new Vector2(bodyRight, y + height), Theme.RowAlt);
            // These reference tracks skip the foreground grid; show it behind their object artwork.
            if (rows[i].Kind == RowKind.HitObjects && referenceMap?.Mode is 0 or 2)
                DrawGrid(dl, y, y + height, bodyRight);
        }
        for (int i = first; i <= last; i++)
            DrawRowTrack(dl, rows[i], contentTop + rowLayout.Top(i) * rowHeight, rowLayout.Height(i) * rowHeight, bodyRight);
        if (pinned >= 0)
            dl.AddRectFilled(new Vector2(trackX, pinnedY), new Vector2(bodyRight, pinnedY + rowHeight), Theme.Rgba(44, 44, 48));
        // On top of the bars, so their edges and every key can be checked against the ticks.
        float gridTop = winPos.Y;
        for (int i = first; i <= last; i++)
        {
            if (rows[i].Kind != RowKind.HitObjects || referenceMap?.Mode is not (0 or 2)) continue;
            float top = Math.Max(winPos.Y, contentTop + rowLayout.Top(i) * rowHeight);
            DrawGrid(dl, gridTop, top, bodyRight);
            gridTop = Math.Min(bodyBottom, contentTop + rowLayout.Top(i + 1) * rowHeight);
        }
        DrawGrid(dl, gridTop, bodyBottom, bodyRight);
        // Parameter text sits above the beat grid so the markers remain easy to read.
        dl.PushClipRect(new Vector2(trackX, pinned >= 0 ? Math.Max(winPos.Y, pinnedY + rowHeight) : winPos.Y),
            new Vector2(bodyRight, bodyBottom), true);
        for (int i = first; i <= last; i++)
            if (rows[i].Kind == RowKind.HitSoundLane && !collapsed[HitSoundsSection])
                DrawHitSoundParameters(dl, rows[i].HitSoundLane, contentTop + rowLayout.Top(i) * rowHeight, rowLayout.Height(i) * rowHeight, bodyRight);
        dl.PopClipRect();

        DrawTimingOffsets(dl, winPos.Y, bodyBottom, bodyRight);
        float px = PlayheadX;
        if (px >= trackX && px <= bodyRight)
            dl.AddLine(new Vector2(px, winPos.Y), new Vector2(px, bodyBottom), Theme.Playhead, 1.5f);
        dl.PopClipRect();

        dl.AddLine(new Vector2(trackX, winPos.Y), new Vector2(trackX, bodyBottom), Theme.Rgba(0, 0, 0, 180));
        // A line under the head that stays in sight sets it off from the rows passing beneath.
        if (pinned >= 0)
            dl.AddLine(new Vector2(winPos.X, pinnedY + rowHeight), new Vector2(bodyRight, pinnedY + rowHeight), Theme.Rgba(0, 0, 0, 200));

        // Gives the child its scroll range.
        ImGui.SetCursorPos(new Vector2(0, rowLayout.Total * rowHeight));
        ImGui.Dummy(new Vector2(1, 1));
    }

    private void DrawGrid(ImDrawListPtr dl, float top, float bottom, float right)
    {
        double viewEnd = TimeAt(right);

        if (!app.HasTiming)
        {
            double step = TickStep(90);
            for (double t = Math.Floor(viewStart / step) * step; t <= viewEnd; t += step)
                dl.AddLine(new Vector2(X(t), top), new Vector2(X(t), bottom), Theme.GridLine);
            return;
        }

        // The same ticks as on the ruler, drawn through every track so bars and keys can be seen to sit on them.
        foreach (var tick in ticks)
        {
            float x = X(tick.Time);
            uint color = tick.Measure ? Theme.Rgba(255, 255, 255, 80)
                : Theme.WithAlpha(Theme.SnapColor(tick.Divisor), tick.Divisor == 1 ? 0.24f : 0.17f);
            dl.AddLine(new Vector2(x, top), new Vector2(x, bottom), color);
        }
    }

    // ---------------------------------------------------------------- left column

    /// <summary>
    /// The head of a section: a layer, the groups, the samples or the videos, with how many things it has
    /// in the place that is open. Names select the section; arrows fold it independently.
    /// </summary>
    private bool DrawSectionHead(ImDrawListPtr dl, int section, Vector2 pos, float rowHeight, bool clicked)
    {
        var max = new Vector2(trackX, pos.Y + rowHeight);
        float textY = pos.Y + (rowHeight - ImGui.GetFontSize()) / 2;
        bool samples = section == SamplesSection, groups = section == GroupsSection, videos = section == VideoSection, hitSounds = section == HitSoundsSection;
        bool waveform = section == WaveformSection, hitObjects = section == HitObjectsSection, background = section == BeatmapBackgroundSection;
        bool timing = section == TimingSection;
        bool reference = hitSounds || waveform || hitObjects || timing;
        uint color = timing ? TimingColour : waveform ? Theme.Accent : hitObjects ? Theme.Rgba(120, 210, 150) : hitSounds ? Theme.Rgba(125, 180, 220) : samples ? Theme.Rgba(235, 200, 90) : groups ? Theme.GroupColor : videos ? VideoColor : Theme.LayerColors[background ? (int)SbLayer.Background : section];
        dl.AddRectFilled(pos, max, Theme.Rgba(48, 48, 52));
        if (app.SelectedTimelineSection == (TimelineSection)section) dl.AddRectFilled(pos, max, Theme.AccentDim);
        dl.AddRectFilled(pos, new Vector2(pos.X + 3, max.Y), color);
        Theme.FoldArrow(dl, new Vector2(pos.X + 14, pos.Y + rowHeight / 2), 9, !collapsed[section], Theme.TextDim);
        string name = Theme.SectionLabel((TimelineSection)section);
        dl.AddText(new Vector2(pos.X + 26, textY), ImGui.GetColorU32(ImGuiCol.Text), name);
        // With the list cut down to what shows now: how many of the section that is.
        string count = rowsShowingOnly && !reference ? $"{showingCount[section]} / {CountInSection(section)}" : CountInSection(section).ToString();
        dl.AddText(new Vector2(max.X - ImGui.CalcTextSize(count).X - 10, textY), Theme.TextDim, count);
        if (clicked)
        {
            if (ImGui.GetIO().MousePos.X >= pos.X + 22)
                app.SelectTimelineSection((TimelineSection)section);
            collapsed[section] = !collapsed[section];
            if (!collapsed[section]) pendingExpansion = new Row { Kind = RowKind.LayerHeader, Layer = section };
            rowsStale = true;
            return true;
        }
        return false;
    }

    private void DrawRowLabel(ImDrawListPtr dl, Row row, Vector2 pos, float rowHeight, int rowIndex)
    {
        var io = ImGui.GetIO();
        float width = trackX - pos.X;
        float textY = pos.Y + (rowHeight - ImGui.GetFontSize()) / 2;
        Vector2 max = pos + new Vector2(width, rowHeight);

        if (row.Kind == RowKind.Waveform)
        {
            bool labelHovered = ImGui.IsMouseHoveringRect(pos, max) && ImGui.IsWindowHovered();
            DrawWaveformLabel(dl, pos, max, textY, labelHovered);
            return;
        }

        if (row.Kind == RowKind.TimingControls)
        {
            DrawTimingOffsetControls(dl, pos, max, textY);
            return;
        }
        if (row.Kind == RowKind.TimingLane)
        {
            bool labelHovered = ImGui.IsMouseHoveringRect(pos, max) && ImGui.IsWindowHovered();
            DrawTimingLabel(dl, pos, max, textY, labelHovered);
            return;
        }

        ImGui.SetCursorScreenPos(pos);
        bool editingValue = row.Kind is RowKind.Property or RowKind.GroupProperty or RowKind.ObjectGroupProperty &&
            valueEditNodes == RowNodes(row) && valueEditKind == row.Command && (row.Command != CommandKind.P || parameterEditFlag == row.Parameter);
        bool clicked = !editingValue && ImGui.InvisibleButton("##row", new Vector2(MathF.Max(1, width - 4), rowHeight)) && !splitterDrag && !wentInto;
        bool hovered = editingValue ? ImGui.IsMouseHoveringRect(pos, max) && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) : ImGui.IsItemHovered();
        if (editingValue && hovered) clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        float relX = io.MousePos.X - pos.X;
        if (ImGui.IsItemActivated()) rowDragFromName = relX >= 62 && !splitterDrag;
        if (row.Kind == RowKind.Sprite && row.Obj is SbSprite { Locked: false } source && !app.IsProtectedBeatmapBackground(source) && rowDragFromName && ImGui.BeginDragDropSource())
        {
            if (!app.IsSelected(source)) app.SelectOnly(source);
            draggedSprites = app.Selection.OfType<SbSprite>().Where(s => s.Group == app.OpenGroup).ToArray();
            ImGui.SetDragDropPayload(SpriteOrderPayload, IntPtr.Zero, 0);
            ImGui.TextUnformatted(L.T("timeline.reorder", draggedSprites.Length));
            ImGui.EndDragDropSource();
        }
        DrawOrderTarget(dl, row, pos, rowHeight);

        if (row.Command == CommandKind.P && row.Kind is RowKind.Property or RowKind.GroupProperty)
        {
            DrawParameterLabel(dl, row, pos, max, rowHeight, hovered, clicked);
            dl.AddLine(new(pos.X, max.Y - 1), new(max.X, max.Y - 1), Theme.Rgba(0, 0, 0, 60));
            return;
        }

        switch (row.Kind)
        {
            case RowKind.Waveform:
            case RowKind.HitObjects:
                DrawReferenceLabel(dl, row, pos, max, textY, hovered);
                break;
            case RowKind.HitSoundLane:
                DrawHitSoundLabel(dl, row.HitSoundLane, pos, max, textY, hovered);
                break;
            case RowKind.TimingLane:
                DrawTimingLabel(dl, pos, max, textY, hovered);
                break;
            case RowKind.LayerHeader:
                DrawSectionHead(dl, row.Layer, pos, rowHeight, clicked);
                break;

            case RowKind.BeatmapBackgroundSlot:
                if (app.EmptyBeatmapBackgroundSelected) dl.AddRectFilled(pos, max, Theme.Rgba(48, 100, 170, 170));
                else if (hovered) dl.AddRectFilled(pos, max, Theme.RowAlt);
                dl.AddText(new Vector2(pos.X + 42, textY), Theme.TextDim, "BG");
                dl.AddText(new Vector2(pos.X + 68, textY), ImGui.GetColorU32(ImGuiCol.Text), L.T("common.none"));
                if (clicked) app.SelectEmptyBeatmapBackground();
                break;

            case RowKind.Sprite:
            case RowKind.Sample:
            case RowKind.Video:
            {
                var obj = row.Obj!;
                bool selected = app.IsSelected(obj);
                if (selected) dl.AddRectFilled(pos, max, obj == app.Primary ? Theme.Rgba(48, 100, 170, 170) : Theme.Rgba(48, 100, 170, 100));
                else if (hovered) dl.AddRectFilled(pos, max, Theme.RowAlt);

                float arrowEnd = 22, eyeEnd = 42, lockEnd = 62;
                var sprite = obj as SbSprite;
                UiScriptTargets.Position("timeline.fold.sprite." + obj.Path, new(pos.X + 13, pos.Y + rowHeight / 2));
                if (sprite != null && sprite.Nodes.Count > 0)
                    Theme.FoldArrow(dl, new Vector2(pos.X + 13, pos.Y + rowHeight / 2), 8, sprite.Expanded, Theme.TextDim);
                bool backgroundPlaceholder = app.Project!.IsBeatmapBackgroundPlaceholder(obj);
                if (backgroundPlaceholder)
                {
                    Vector2 centre = new(pos.X + 52, pos.Y + rowHeight / 2);
                    var texture = app.Textures.Get(obj.Path);
                    if (texture != null)
                    {
                        float fit = MathF.Min(16f / texture.Width, 16f / texture.Height);
                        Vector2 half = new Vector2(texture.Width, texture.Height) * fit / 2;
                        dl.AddImage(texture.ImGuiId, centre - half, centre + half);
                    }
                    else dl.AddText(centre - ImGui.CalcTextSize("BG") / 2, Theme.TextDim, "BG");
                }
                else
                {
                    DrawEye(dl, new Vector2(pos.X + 32, pos.Y + rowHeight / 2), !obj.Hidden);
                    DrawLock(dl, new Vector2(pos.X + 52, pos.Y + rowHeight / 2), obj.Locked);
                }

                bool missing = sprite != null
                    ? app.Textures.IsUnavailable(sprite.IsAnimation ? SbSprite.FramePath(sprite.Path, 0) : sprite.Path)
                    : !app.Project!.AssetExists(obj.Path);
                uint textColor = missing ? Theme.TextError : obj.Hidden ? Theme.TextDim : ImGui.GetColorU32(ImGuiCol.Text);
                string label = backgroundPlaceholder ? Path.GetFileName(obj.Path) : obj.DisplayName;
                // An object of the shown difficulty alone carries that difficulty's name; one without it is
                // part of the storyboard all difficulties share.
                if (obj.Difficulty != null && app.Project!.CurrentDifficulty is { } difficulty) label += "(" + difficulty.DisplayName + ")";
                if (sprite is { IsAnimation: true }) label += "  " + L.T("timeline.frames", sprite.FrameCount);

                dl.PushClipRect(pos, new Vector2(max.X - 4, max.Y), true);
                dl.AddText(new Vector2(pos.X + lockEnd + 6, textY), textColor, label);
                dl.PopClipRect();

                if (hovered)
                {
                    if (app.Settings.ShowTimelineThumbnails && sprite != null && relX >= lockEnd &&
                        !missing && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal) &&
                        !ImGui.IsMouseDown(ImGuiMouseButton.Left) && !ImGui.IsMouseDown(ImGuiMouseButton.Right))
                    {
                        string path = sprite.IsAnimation ? SbSprite.FramePath(sprite.Path, 0) : sprite.Path;
                        var texture = app.Textures.Get(path);
                        if (texture != null)
                        {
                            ImGui.BeginTooltip();
                            float fit = MathF.Min(1, MathF.Min(Theme.Px(260) / texture.Width, Theme.Px(200) / texture.Height));
                            ImGui.Image(texture.ImGuiId, new Vector2(texture.Width, texture.Height) * fit);
                            ImGui.TextUnformatted(L.T("assets.pixels", texture.Width, texture.Height));
                            ImGui.TextUnformatted(obj.Path);
                            ImGui.EndTooltip();
                        }
                    }
                    else if (backgroundPlaceholder) Theme.SetTooltip(obj.Path);
                    else if (relX < arrowEnd) Theme.SetTooltip(L.T("timeline.fold.tip"));
                    else if (relX < eyeEnd) Theme.SetTooltip(L.T("timeline.eye.tip"));
                    else if (relX < lockEnd) Theme.SetTooltip(L.T("timeline.lock.tip"));
                    else if (missing) Theme.SetTooltip(L.T("timeline.missing.tip", obj.Path));
                }

                if (clicked)
                {
                    if (backgroundPlaceholder) ClickObject(obj, io);
                    else if (relX < arrowEnd && sprite != null)
                    {
                        sprite.Expanded = !sprite.Expanded;
                        if (sprite.Expanded) pendingExpansion = row;
                        app.TouchFolds();
                    }
                    else if (relX < eyeEnd)
                    {
                        if (!app.IsProtectedBeatmapBackground(obj))
                        {
                            obj.Hidden = !obj.Hidden;
                            app.Touch(obj);
                            if (obj is SbSample) app.RefreshPlayback();
                        }
                    }
                    else if (relX < lockEnd)
                    {
                        obj.Locked = !obj.Locked;
                        app.Touch(obj);
                    }
                    else
                    {
                        ClickObject(obj, io);
                        if (sprite != null)
                        {
                            sprite.Expanded = !sprite.Expanded;
                            if (sprite.Expanded) pendingExpansion = row;
                            app.TouchFolds();
                        }
                    }
                }
                if (hovered && relX >= lockEnd && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    if (sprite != null && sprite.Timeline.HasCommands) app.Seek(sprite.Timeline.StartTime);
                    else if (obj is SbSample sample) app.Seek(sample.Time);
                    else if (obj is SbVideo video) app.Seek(video.Time);
                    playheadToFront = true;
                }

                if (ImGui.BeginPopupContextItem("ctx"))
                {
                    if (!app.IsSelected(obj)) app.SelectOnly(obj);
                    Menus.ObjectItems(app);
                    ImGui.EndPopup();
                }
                break;
            }

            case RowKind.Property:
            case RowKind.GroupProperty:
            {
                float indent = row.Kind == RowKind.Property ? 44 : 60;
                // Every property row can be picked: the properties panel then shows that property alone, and
                // the preview adjusts it where it can. A row of a loop is like the sprite's own there, the
                // playhead standing for the moment of the pass it is in; and of commands that run at the same
                // time, which are no keyframes, the preview adjusts the one that has its way.
                const bool pickable = true;
                if (pickable && app.IsActiveTrack(RowNodes(row), row.Command)) dl.AddRectFilled(pos, max, Theme.AccentDim);
                dl.AddRectFilled(new Vector2(pos.X + indent - 10, pos.Y + 5), new Vector2(pos.X + indent - 7, max.Y - 5), Theme.KindColor(row.Command));
                dl.AddText(new Vector2(pos.X + indent, textY), Theme.Rgba(190, 190, 196), Theme.KindLabel(row.Command));
                bool onKeyButton = KeyButton(dl, row, max, rowHeight, hovered, out float buttonRoom, out int? keyDirection);
                bool onValue = DrawPropertyValue(dl, row, pos, max, rowHeight, buttonRoom, Theme.Accent, hovered);
                if (clicked && (!onKeyButton || keyDirection != null) && row.Obj != null && !app.IsSelected(row.Obj)) app.SelectOnly(row.Obj);
                if (clicked && onKeyButton) { if (keyDirection is { } step) UseKeyButton(row, step); }
                else if (clicked && pickable && !onValue) PickTrack(row);
                else if (!onKeyButton && !onValue && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                    Theme.SetTooltip(!Adjustable(row) ? L.T("timeline.track.pickTip")
                        : row.Kind == RowKind.GroupProperty ? L.T("timeline.track.tip") + "\n\n" + L.T("timeline.track.loopTip")
                        : L.T("timeline.track.tip"));
                break;
            }

            case RowKind.GroupHeader:
            {
                string label = row.Group switch
                {
                    SbLoop loop => L.T("timeline.loopRow", loop.Iterations, loop.IterationLength),
                    SbTrigger trigger => L.T("timeline.triggerRow", trigger.Name)
                        + (app.TriggerAnchor(trigger, row.Obj as SbSprite).Simulated ? "" : " " + L.T("timeline.triggerNotSimulated")),
                    _ => "",
                };
                bool selected = app.SelectedBlock == row.Group;
                if (selected) dl.AddRectFilled(pos, max, Theme.AccentDim);
                else if (hovered) dl.AddRectFilled(pos, max, Theme.RowAlt);
                dl.AddText(new Vector2(pos.X + 44, textY), Theme.Rgba(235, 200, 90), label);
                // Selected as a whole: Delete then takes the loop or trigger away, not the sprite.
                if (clicked && row.Obj is SbSprite owner && row.Group != null) app.SelectBlock(owner, row.Group);
                else if (!selected && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal)) Theme.SetTooltip(L.T("timeline.block.tip"));
                break;
            }

            case RowKind.ObjectGroup:
            {
                // One row for the group and all that is in it, with what a sprite's row has: the eye and the
                // lock, which here are those of everything in it.
                var group = row.ObjectGroup!;
                var content = app.Index.ContentOf(group);
                UiScriptTargets.Position("timeline.fold.group." + group.Name, new(pos.X + 13, pos.Y + rowHeight / 2));
                if (group.Plugin is {} effectLink)
                    UiScriptTargets.Position("timeline.effect." + effectLink.Instance + ".label", new(pos.X + 108, pos.Y + rowHeight / 2));
                bool selected = app.IsSelected(group);
                if (selected) dl.AddRectFilled(pos, max, Theme.WithAlpha(Theme.GroupColor, group == app.SelectedGroup ? 0.3f : 0.2f));
                else if (hovered) dl.AddRectFilled(pos, max, Theme.RowAlt);

                const float arrowEnd = 22, eyeEnd = 42, lockEnd = 62;
                bool any = content.Sprites.Count > 0;
                bool hidden = any && content.Hidden == content.Sprites.Count, locked = any && content.Locked == content.Sprites.Count;
                if (group.Commands.Count > 0 || group.Camera != null)
                    Theme.FoldArrow(dl, new Vector2(pos.X + 13, pos.Y + rowHeight / 2), 8, group.Expanded, Theme.TextDim);
                DrawEye(dl, new Vector2(pos.X + 32, pos.Y + rowHeight / 2), !hidden);
                DrawLock(dl, new Vector2(pos.X + 52, pos.Y + rowHeight / 2), locked);

                // The mark of a folder, and the name.
                float markX = pos.X + lockEnd + 6, markY = pos.Y + rowHeight / 2;
                uint mark = Theme.WithAlpha(Theme.GroupColor, hidden ? 0.5f : 1f);
                if(group.Plugin==null)
                {
                    dl.AddRectFilled(new Vector2(markX, markY - 5), new Vector2(markX + 6, markY - 2), mark, 1);
                    dl.AddRectFilled(new Vector2(markX, markY - 3), new Vector2(markX + 13, markY + 5), mark, 1.5f);
                }
                else
                {
                    dl.AddRect(new Vector2(markX,markY-5),new Vector2(markX+9,markY+1),mark,3,ImDrawFlags.None,2);
                    dl.AddRect(new Vector2(markX+5,markY-1),new Vector2(markX+14,markY+5),mark,3,ImDrawFlags.None,2);
                }
                string count = L.T("timeline.memberCount", content.Sprites.Count);
                if(group.Plugin is {} link && app.Plugins.TryGetValue(link.Instance,out var runtime))
                {
                    float stateX=max.X-ImGui.CalcTextSize(count).X-30;
                    string symbol=runtime.Status switch {PluginStatus.Failed=>"!",PluginStatus.Unsupported=>"?",_=>""};
                    if(runtime.Status==PluginStatus.LinkMissing)
                    {
                        float cy=pos.Y+rowHeight/2;
                        dl.AddRect(new Vector2(stateX,cy-5),new Vector2(stateX+5,cy+1),Theme.TextDim,2,ImDrawFlags.None,1.5f);
                        dl.AddRect(new Vector2(stateX+9,cy-1),new Vector2(stateX+14,cy+5),Theme.TextDim,2,ImDrawFlags.None,1.5f);
                        dl.AddLine(new Vector2(stateX+5,cy+4),new Vector2(stateX+9,cy-4),Theme.TextDim,1.5f);
                    }
                    if(symbol.Length>0) dl.AddText(new Vector2(stateX,textY),runtime.Status==PluginStatus.Failed?Theme.Rgba(255,70,70):Theme.TextDim,symbol);
                    if(runtime.Status==PluginStatus.Importing) dl.AddCircleFilled(new Vector2(stateX+(float)Math.Cos(ImGui.GetTime()*5)*4,pos.Y+rowHeight/2+(float)Math.Sin(ImGui.GetTime()*5)*4),2,mark);
                    if(hovered) Theme.SetTooltip(app.PluginStatusText(runtime.Status)+"\n"+link.File+"\n"+runtime.Error);
                }
                float countX = max.X - ImGui.CalcTextSize(count).X - 10;
                dl.PushClipRect(pos, new Vector2(countX - (group.Plugin==null?4:40), max.Y), true);
                dl.AddText(new Vector2(markX + 19, textY), mark, group.Name.Length > 0 ? group.Name : L.T("group.unnamed"));
                dl.PopClipRect();
                dl.AddText(new Vector2(countX, textY), Theme.TextDim, count);

                if (hovered)
                {
                    if (relX < arrowEnd && (group.Commands.Count > 0 || group.Camera != null)) Theme.SetTooltip(L.T("timeline.fold.tip"));
                    else if (relX >= arrowEnd && relX < eyeEnd) Theme.SetTooltip(L.T("timeline.eye.tip"));
                    else if (relX >= eyeEnd && relX < lockEnd) Theme.SetTooltip(L.T("timeline.lock.tip"));
                    else if (relX >= lockEnd && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                        Theme.SetTooltip(L.T(group.Plugin?.Kind == PluginKind.Effect ? "plugin.panel.tip" : "timeline.group.tip"));
                }

                if (clicked)
                {
                    if (relX < arrowEnd)
                    {
                        group.Expanded = !group.Expanded;
                        if (group.Expanded) pendingExpansion = row;
                        app.TouchFolds();
                    }
                    else if (relX < eyeEnd)
                    {
                        app.SetGroupVisibility(group,!hidden);
                    }
                    else if (relX < lockEnd)
                    {
                        app.SetGroupLocked(group,!locked);
                    }
                    else ClickGroup(group, io);
                }
                // Into it, like into a folder.
                if (hovered && relX >= lockEnd && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    if (group.Plugin is { Kind: PluginKind.Effect } sourceLink)
                    {
                        app.Seek(GroupBarSpan(group)?.Start ?? sourceLink.Offset);
                        playheadToFront = true;
                    }
                    else
                    {
                        app.OpenContainer(group);
                        wentInto = true;
                    }
                }

                if (ImGui.BeginPopupContextItem("groupctx"))
                {
                    if (!app.IsSelected(group)) app.SelectGroup(group);
                    Menus.GroupItems(app, group);
                    ImGui.EndPopup();
                }
                break;
            }

            case RowKind.Camera:
            {
                var group = row.ObjectGroup!;
                var camera = group.Camera!;
                UiScriptTargets.Position("timeline.fold.camera." + group.Name, new(pos.X + 13, pos.Y + rowHeight / 2));
                uint color = group.CameraActive ? Theme.GroupColor : Theme.Rgba(120, 120, 120);
                string label = group.CameraActive ? L.T("camera.title") : L.T("camera.inactive");
                if (app.CameraSelection == group) dl.AddRectFilled(pos, max, Theme.AccentDim);
                else if (hovered) dl.AddRectFilled(pos, max, Theme.RowAlt);
                Theme.FoldArrow(dl, new Vector2(pos.X + 13, pos.Y + rowHeight / 2), 8, camera.Expanded, Theme.TextDim);
                // The lens lines reach 0.8 of the size to the right: centred here they stop short of the label.
                Theme.DrawCameraRig(dl, new Vector2(pos.X + 28, pos.Y + rowHeight / 2), Theme.Px(12),
                    group.CameraActive ? Theme.Accent : color);
                dl.AddText(new Vector2(pos.X + 44, textY), color, label);
                if (clicked)
                {
                    if (relX < 22)
                    {
                        camera.Expanded = !camera.Expanded;
                        if (camera.Expanded) pendingExpansion = row;
                        app.TouchFolds();
                    }
                    else app.SelectCamera(group);
                }
                if (hovered && !group.CameraActive) Theme.SetTooltip(L.T("camera.onlyRoot"));
                if (ImGui.BeginPopupContextItem("cameractx"))
                {
                    if (ImGui.MenuItem(L.T("camera.remove")))
                    {
                        cameraToRemove = group;
                    }
                    if (ImGui.MenuItem(L.T("camera.move"), "", false, !group.CameraActive && group.Root.Camera == null))
                        app.MoveCameraToRoot(group);
                    ImGui.EndPopup();
                }
                break;
            }
            case RowKind.ObjectGroupProperty:
            {
                var group = row.ObjectGroup!;
                const bool pickable = true;
                if (app.IsActiveTrack(RowNodes(row), row.Command)) dl.AddRectFilled(pos, max, Theme.AccentDim);
                float indent = row.Camera ? 16 : 0;
                uint color = row.Camera && !group.CameraActive ? Theme.Rgba(120, 120, 120) : Theme.Rgba(190, 190, 196);
                dl.AddRectFilled(new Vector2(pos.X + 34 + indent, pos.Y + 5), new Vector2(pos.X + 37 + indent, max.Y - 5),
                    row.Camera && !group.CameraActive ? color : Theme.KindColor(row.Command));
                dl.AddText(new Vector2(pos.X + 44 + indent, textY), color, Theme.KindLabel(row.Command));
                bool onKeyButton = KeyButton(dl, row, max, rowHeight, hovered, out float buttonRoom, out int? keyDirection);
                bool onValue = DrawPropertyValue(dl, row, pos, max, rowHeight, buttonRoom,
                    row.Camera && !group.CameraActive ? color : Theme.GroupColor, hovered);
                if (clicked && (!onKeyButton || keyDirection != null) && app.SelectedGroup != group) app.SelectGroup(group);
                if (clicked && onKeyButton) { if (keyDirection is { } step) UseKeyButton(row, step); }
                else if (clicked && pickable && !onValue) PickTrack(row);
                else if (pickable && !onKeyButton && !onValue && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal)) Theme.SetTooltip(L.T("timeline.track.tip"));
                break;
            }
        }

        // A camera always has all six of its rows: there is nothing to delete, its keys have the buttons.
        if (row.Kind is RowKind.Property or RowKind.GroupProperty or RowKind.ObjectGroupProperty && !row.Camera)
            DrawCommandContextMenu(row, hovered);

        dl.AddLine(new Vector2(pos.X, max.Y - 1), new Vector2(max.X, max.Y - 1), Theme.Rgba(0, 0, 0, 60));
    }

    private void DrawCommandContextMenu(Row row, bool hovered)
    {
        // Use the whole label row, including its value and key button, regardless of the last drawn widget.
        if (hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
        {
            if (row.ObjectGroup != null) app.SelectGroup(row.ObjectGroup);
            else app.SelectOnly(row.Obj!);
            app.SelectTrack(RowNodes(row), row.Command, row.Parameter);
            ImGui.OpenPopup("commandctx");
        }
        if (ImGui.BeginPopup("commandctx"))
        {
            ImGui.BeginDisabled(row.Camera && !row.ObjectGroup!.CameraActive);
            Menus.CommandTrackItems(app, RowNodes(row), row.Command, row.Parameter);
            ImGui.EndDisabled();
            ImGui.EndPopup();
        }
    }

    /// <summary>Double-click a value at an existing keyframe to edit that key, without inserting one.</summary>
    private bool DrawPropertyValue(ImDrawListPtr dl, Row row, Vector2 pos, Vector2 max, float rowHeight, float buttonRoom, uint color, bool rowHovered)
    {
        if (row.Command == CommandKind.P) return DrawParameterValue(dl, row, pos, max, rowHeight, buttonRoom, color, rowHovered);
        var nodes = RowNodes(row);
        bool editing = valueEditNodes == nodes && valueEditKind == row.Command;
        int offset = PlayheadOffset(row);
        var keys = RowKeys(row);
        int now = (int)Math.Round(app.Time) - offset;
        int index = keys?.FindIndex(k => k.Time == now) ?? -1;
        var key = index >= 0 ? keys![index] : null;
        bool allowed = (!row.Camera || row.ObjectGroup!.CameraActive) && key != null && !app.Playing && row.Command != CommandKind.P &&
            (row.Obj == null || !app.IsProtectedBeatmapBackground(row.Obj)) &&
            (row.Group is not SbLoop loop || app.Time >= loop.AbsoluteStart && app.Time <= loop.AbsoluteEnd);
        if (editing && (!allowed || offset != valueEditOffset))
        {
            valueEditNodes = null;
            editing = false;
        }
        double[]? colourValues = null;
        if (row.Command == CommandKind.C)
        {
            var sprite = (SbSprite)row.Obj!;
            colourValues = key?.Value ?? EditorApp.StateValues(CommandKind.C, sprite.Timeline.Evaluate(app.Time, sprite), sprite.Timeline.HasCommands);
        }
        string value = colourValues != null ? TimelineValueInput.Display(colourValues, CommandKind.C, false)
            : key != null && row.Command != CommandKind.P
            ? TimelineValueInput.Display(key.Value, row.Command, app.Settings.RotationInDegrees)
            : row.ObjectGroup is { } group
                ? TimelineValueInput.Display((row.Camera ? group.Camera!.Evaluate(app.Time).Values(row.Command) : EditorApp.StateValues(row.Command, group.Timeline.Evaluate(app.Time, group))), row.Command, app.Settings.RotationInDegrees)
                : CurrentValue((SbSprite)row.Obj!, row.Command);
        float right = max.X - 10 - buttonRoom;
        float valueWidth = ImGui.CalcTextSize(value).X;
        Vector2 textPos = new(right - valueWidth, pos.Y + (rowHeight - ImGui.GetFontSize()) / 2);
        UiScriptTargets.Position(RowKeyTarget(row) + ".value", new(right - valueWidth / 2, pos.Y + rowHeight / 2));
        float swatchSide = MathF.Min(Theme.Px(12), rowHeight - Theme.Px(6));
        float swatchRoom = colourValues != null ? swatchSide + Theme.Px(6) : 0;
        bool over = rowHovered && ImGui.IsMouseHoveringRect(new(textPos.X - swatchRoom - 4, pos.Y), new(right + 4, max.Y));
        if (!editing || row.Command == CommandKind.C)
        {
            if (colourValues != null) DrawColourSwatch(textPos.X - swatchRoom, colourValues);
            dl.AddText(textPos, color, value);
            if (over && allowed && !editing)
            {
                ImGui.SetMouseCursor(row.Command == CommandKind.C ? ImGuiMouseCursor.Hand : ImGuiMouseCursor.TextInput);
                Theme.SetTooltip(L.T(row.Command == CommandKind.C ? "timeline.value.colour.edit" : "timeline.value.edit"));
                int clickCount = ImGui.GetIO().MouseClickedLastCount[0];
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) ||
                    (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && clickCount > 2 && clickCount % 2 == 0))
                {
                    if (row.ObjectGroup != null) app.SelectGroup(row.ObjectGroup);
                    else app.SelectOnly(row.Obj!);
                    app.SelectKey(new KeyRef(nodes, row.Command, index));
                    app.CommitEdits();
                    valueEditNodes = nodes;
                    valueEditKind = row.Command;
                    valueEditTime = app.Time;
                    valueEditOffset = offset;
                    valueEditText = TimelineValueInput.EditText(key!.Value, row.Command, app.Settings.RotationInDegrees);
                    valueEditOriginalText = valueEditText;
                    valueEditDegrees = app.Settings.RotationInDegrees;
                    valueEditFocus = true;
                    valueEditFrame = ImGui.GetFrameCount();
                    if (row.Command == CommandKind.C)
                    {
                        var editedKey = key;
                        var editedSprite = row.Obj;
                        colourPicker.Open("##colourPicker", key.Value, values =>
                        {
                            KeyTrack.SetValue(editedKey, values);
                            app.Touch(editedSprite);
                        }, this);
                        editing = true;
                    }
                }
            }
            if (row.Command == CommandKind.C)
            {
                if (editing) valueEditFrame = ImGui.GetFrameCount();
                if (!colourPicker.Draw("##colourPicker") && editing) valueEditNodes = null;
            }
            return over;
        }

        valueEditFrame = ImGui.GetFrameCount();
        float indent = row.Kind == RowKind.GroupProperty ? 60 : 44;
        float left = pos.X + indent + ImGui.CalcTextSize(Theme.KindLabel(row.Command)).X + Theme.Px(8);
        float width = MathF.Min(Theme.Px(key!.Value.Length > 1 ? 150 : 90), MathF.Max(20, right - left - swatchRoom));
        Vector2 inputPos = new(right - width, pos.Y + (rowHeight - ImGui.GetFrameHeight()) / 2);
        ImGui.SetCursorScreenPos(inputPos);
        ImGui.SetNextItemWidth(width);
        if (valueEditFocus)
        {
            ImGui.SetKeyboardFocusHere();
            valueEditFocus = false;
        }
        bool entered = ImGui.InputText("##keyValue", ref valueEditText, 128, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        bool cancelled = ImGui.IsKeyPressed(ImGuiKey.Escape);
        bool confirm = entered || ImGui.IsItemDeactivated();
        over = ImGui.IsMouseHoveringRect(new(inputPos.X - swatchRoom, inputPos.Y), inputPos + new Vector2(width, rowHeight));
        if (cancelled || confirm)
        {
            valueEditNodes = null;
            if (cancelled || valueEditText == valueEditOriginalText) return over;
            string? error = TimelineValueInput.Validate(valueEditText, row.Command, valueEditDegrees, out var values);
            if (error != null)
                app.ShowMessage(L.T("input.invalid.title"), L.T("input.invalid.text", Theme.KindLabel(row.Command), error, TimelineValueInput.EditText(key.Value, row.Command, valueEditDegrees)));
            else if (!values.AsSpan().SequenceEqual(key.Value))
            {
                KeyTrack.SetValue(key, values);
                if (row.ObjectGroup != null) app.TouchGroup(row.ObjectGroup);
                else app.Touch(row.Obj);
                app.CommitEdits();
            }
        }
        return over;

        void DrawColourSwatch(float x, double[] values)
        {
            var (r, g, b) = TimelineValueInput.ColourBytes(values);
            Vector2 corner = new(x, pos.Y + (rowHeight - swatchSide) / 2);
            dl.AddRectFilled(corner, corner + new Vector2(swatchSide), Theme.Rgba(r, g, b), Theme.Px(2));
            dl.AddRect(corner, corner + new Vector2(swatchSide), Theme.Rgba(255, 255, 255, 150), Theme.Px(2));
        }
    }

    private void CancelColourEdit()
    {
        colourPicker.Cancel(this);
        valueEditNodes = null;
    }

    private unsafe void DrawOrderTarget(ImDrawListPtr dl, Row row, Vector2 pos, float rowHeight)
    {
        if (row.Kind != RowKind.Sprite && row.Kind != RowKind.LayerHeader) return;
        if (row.Layer < 0 || row.Layer >= SbInfo.LayerCount) return;
        var target = row.Obj as SbSprite;
        if (!SpriteOrder.CanMove(draggedSprites, (SbLayer)row.Layer, target, app.OpenGroup)) return;
        if (!ImGui.BeginDragDropTarget()) return;
        var payload = ImGui.AcceptDragDropPayload(SpriteOrderPayload, ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
        if (payload.NativePtr != null)
        {
            bool above = row.Kind == RowKind.LayerHeader || ImGui.GetIO().MousePos.Y < pos.Y + rowHeight / 2;
            float y = above ? pos.Y : pos.Y + rowHeight;
            dl.AddLine(new Vector2(pos.X + 62, y), new Vector2(trackX - 4, y), Theme.Accent, Theme.Px(2));
            if (payload.IsDelivery())
            {
                app.ReorderSprites(draggedSprites, (SbLayer)row.Layer, target, above);
                draggedSprites = Array.Empty<SbSprite>();
            }
        }
        ImGui.EndDragDropTarget();
    }

    private void ClickObject(SbObject obj, ImGuiIOPtr io)
    {
        // Back to the object as a whole: dragging it in the preview moves it again.
        app.ClearKeySelection();
        app.ActiveTrack = null;
        if (io.KeyShift && SelectRange(rows.FindIndex(r => r.Obj == obj && r.Kind is RowKind.Sprite or RowKind.Sample or RowKind.Video), io.KeyCtrl))
        {
            app.AddToSelection(obj);
            return;
        }

        if (io.KeyCtrl) app.ToggleSelection(obj);
        else app.SelectOnly(obj);
        rangeAnchor = obj;
        rangeAnchorGroup = null;
    }

    /// <summary>The same for the row of a group: it is selected like a sprite, by itself or with others.</summary>
    private void ClickGroup(SbObjectGroup group, ImGuiIOPtr io)
    {
        app.ClearKeySelection();
        app.ActiveTrack = null;
        if (io.KeyShift && SelectRange(rows.FindIndex(r => r.Kind == RowKind.ObjectGroup && r.ObjectGroup == group), io.KeyCtrl))
        {
            app.AddToSelection(group);
            return;
        }

        if (io.KeyCtrl) app.ToggleSelection(group);
        else app.SelectGroup(group);
        rangeAnchorGroup = group;
        rangeAnchor = null;
    }

    /// <summary>
    /// Selects every object and group from the row a range begins at to another row, as Shift and a click do.
    /// </summary>
    /// <returns>False when there is no row to begin from, or it is the same one.</returns>
    private bool SelectRange(int to, bool keep)
    {
        int from = rangeAnchorGroup != null
            ? rows.FindIndex(r => r.Kind == RowKind.ObjectGroup && r.ObjectGroup == rangeAnchorGroup)
            : rangeAnchor != null ? rows.FindIndex(r => r.Obj == rangeAnchor && r.Kind is RowKind.Sprite or RowKind.Sample or RowKind.Video) : -1;
        if (from < 0 || to < 0 || from == to) return false;
        if (!keep) app.ClearSelection();
        for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
        {
            if (rows[i].Kind is RowKind.Sprite or RowKind.Sample or RowKind.Video) app.AddToSelection(rows[i].Obj!);
            else if (rows[i].Kind == RowKind.ObjectGroup) app.AddToSelection(rows[i].ObjectGroup!);
        }
        return true;
    }

    private string CurrentValue(SbSprite sprite, CommandKind kind)
    {
        if(SbInfo.DepthKinds.Contains(kind)) return TimelineValueInput.Display(new[]{GroupSpace.Value(sprite.DepthCommands,kind,app.Time,0)},kind,app.Settings.RotationInDegrees);
        var st = sprite.Timeline.Evaluate(app.Time, sprite);
        return kind switch
        {
            CommandKind.F => $"{st.Opacity:0.##}",
            CommandKind.M => $"{st.X:0.#}, {st.Y:0.#}",
            CommandKind.MX => $"{st.X:0.#}",
            CommandKind.MY => $"{st.Y:0.#}",
            CommandKind.S => $"{st.Scale:0.###}",
            CommandKind.V => $"{st.ScaleX:0.###}, {st.ScaleY:0.###}",
            CommandKind.R => app.Settings.RotationInDegrees ? $"{st.Rotation * 180 / Math.PI:0.#}°" : $"{st.Rotation:0.###}",
            CommandKind.C => TimelineValueInput.Display(new[] { st.R, st.G, st.B }, kind, false),
            _ => (st.FlipH ? "H" : "") + (st.FlipV ? "V" : "") + (st.Additive ? "A" : ""),
        };
    }

    private static void DrawEye(ImDrawListPtr dl, Vector2 c, bool visible)
    {
        uint color = visible ? Theme.Rgba(200, 200, 205) : Theme.Rgba(90, 90, 95);
        dl.AddEllipse(c, new Vector2(6.5f, 4f), color, 0, 16, 1.2f);
        if (visible) dl.AddCircleFilled(c, 2.2f, color);
        else dl.AddLine(c + new Vector2(-6, 5), c + new Vector2(6, -5), color, 1.2f);
    }

    private static void DrawLock(ImDrawListPtr dl, Vector2 c, bool locked)
    {
        if (!locked)
        {
            dl.AddCircleFilled(c, 1.5f, Theme.Rgba(80, 80, 85));
            return;
        }
        uint color = Theme.Rgba(235, 200, 90);
        dl.AddRectFilled(c + new Vector2(-4.5f, -1), c + new Vector2(4.5f, 5.5f), color, 1);
        dl.AddCircle(c + new Vector2(0, -2), 3f, color, 12, 1.4f);
    }

    // ---------------------------------------------------------------- tracks

    private void DrawRowTrack(ImDrawListPtr dl, Row row, float y, float rowHeight, float right)
    {
        if (row.Layer == TimingSection)
        {
            if (row.Kind == RowKind.TimingLane) DrawTimingTrack(dl, y, rowHeight, right);
            else if (row.Kind == RowKind.TimingControls && TimingControlPoint is { } point)
                dl.AddText(new(trackX + Theme.Px(8), y + (rowHeight - ImGui.GetFontSize()) / 2), Theme.TextDim,
                    $"{point.Offset:0.###} ms · {point.Bpm:0.##} BPM · {point.Meter}/4");
            return;
        }
        if (row.Layer is WaveformSection or HitObjectsSection)
        {
            DrawReferenceTrack(dl, row, y, rowHeight, right);
            return;
        }
        if (row.Layer == HitSoundsSection)
        {
            if (row.Kind == RowKind.HitSoundLane && !collapsed[HitSoundsSection])
                DrawHitSoundTrack(dl, row.HitSoundLane, y, rowHeight, right);
            return;
        }
        float mid = y + rowHeight / 2;
        if (row.Kind == RowKind.LayerHeader && row.Layer == GroupsSection)
            UiScriptTargets.Position("timeline.drop", new Vector2(trackX + Math.Min(240, (right-trackX)/2), mid));

        switch (row.Kind)
        {
            case RowKind.Sprite:
            {
                var sprite = (SbSprite)row.Obj!;
                var timeline = sprite.Timeline;
                bool selected = app.IsSelected(sprite);
                if (timeline.HasCommands)
                {
                    float x0 = X(timeline.StartTime), x1 = MathF.Max(x0 + 3, X(timeline.EndTime));
                    if (x1 >= trackX && x0 <= right)
                    {
                        UiScriptTargets.Position("timeline.clip.sprite." + sprite.Path, new((Math.Max(trackX, x0) + Math.Min(right, x1)) / 2, mid));
                        uint color = Theme.LayerColors[(int)sprite.Layer];
                        dl.AddRectFilled(new Vector2(x0, y + 3), new Vector2(x1, y + rowHeight - 3),
                            Theme.WithAlpha(color, sprite.Hidden ? 0.3f : selected ? 1f : 0.75f), 3);
                        if (selected) dl.AddRect(new Vector2(x0, y + 3), new Vector2(x1, y + rowHeight - 3), Theme.Rgba(255, 255, 255, 220), 3);
                        if (edgeSprite == sprite)
                        {
                            // Trim handle on the edge under the mouse.
                            float hx = edgePart == DragMode.TrimStart ? x0 : x1;
                            dl.AddRectFilled(new Vector2(hx - 2, y + 1), new Vector2(hx + 2, y + rowHeight - 1), Theme.Rgba(255, 255, 255), 2);
                        }
                    }
                }
                foreach (var node in sprite.Nodes)
                {
                    if (node is not SbTrigger trigger) continue;
                    dl.AddRect(new Vector2(X(trigger.Start), y + 6), new Vector2(MathF.Max(X(trigger.Start) + 3, X(trigger.End)), y + rowHeight - 6),
                        Theme.Rgba(235, 200, 90, 150), 2);
                    DrawTriggerFirings(dl, sprite, trigger, y, rowHeight, right);
                }
                break;
            }

            case RowKind.Sample:
            {
                var sample = (SbSample)row.Obj!;
                float x = X(sample.Time);
                if (x < trackX - 10 || x > right + 10) break;
                bool selected = app.IsSelected(sample);
                dl.AddLine(new Vector2(x, y + 2), new Vector2(x, y + rowHeight - 2), Theme.Rgba(235, 200, 90, 120));
                Theme.Diamond(dl, new Vector2(x, mid), 5.5f, Theme.WithAlpha(Theme.Rgba(235, 200, 90), sample.Hidden ? 0.35f : 1f),
                    selected ? Theme.Rgba(255, 255, 255) : Theme.Rgba(0, 0, 0, 160));
                break;
            }

            case RowKind.Video:
            {
                var video = (SbVideo)row.Obj!;
                var (x0, x1) = VideoBar(video);
                if (x1 < trackX || x0 > right) break;
                bool selected = app.IsSelected(video);
                dl.AddRectFilled(new Vector2(x0, y + 3), new Vector2(x1, y + rowHeight - 3),
                    Theme.WithAlpha(VideoColor, video.Hidden ? 0.3f : selected ? 1f : 0.75f), 3);
                if (selected) dl.AddRect(new Vector2(x0, y + 3), new Vector2(x1, y + rowHeight - 3), Theme.Rgba(255, 255, 255, 220), 3);
                // The second it fades in over, and the one it fades out over.
                float fade = (float)(1000 * pxPerMs);
                if (x1 - x0 > fade * 2 + 4)
                {
                    uint shade = Theme.Rgba(0, 0, 0, 70);
                    dl.AddTriangleFilled(new Vector2(x0, y + 3), new Vector2(x0 + fade, y + 3), new Vector2(x0, y + rowHeight - 3), shade);
                    dl.AddTriangleFilled(new Vector2(x1, y + 3), new Vector2(x1 - fade, y + 3), new Vector2(x1, y + rowHeight - 3), shade);
                }
                break;
            }

            case RowKind.Property:
                DrawKeyRow(dl, row, mid, right);
                break;

            case RowKind.GroupHeader:
            {
                uint color = Theme.Rgba(235, 200, 90);
                bool selected = app.SelectedBlock == row.Group;
                uint frame = Theme.Rgba(255, 255, 255, 220);
                if (row.Group is SbLoop loop && loop.Commands.Count > 0)
                {
                    float x0 = X(loop.AbsoluteStart), x1 = MathF.Max(x0 + 3, X(loop.AbsoluteEnd));
                    dl.AddRectFilled(new Vector2(x0, mid - 4), new Vector2(x1, mid + 4), Theme.WithAlpha(color, selected ? 0.8f : 0.45f), 2);
                    if (selected) dl.AddRect(new Vector2(x0, mid - 5), new Vector2(x1, mid + 5), frame, 2);
                    double step = loop.IterationLength * pxPerMs;
                    if (step >= 5)
                    {
                        int from = Math.Max(0, (int)((trackX - x0) / step));
                        for (int k = from; k <= loop.Iterations && x0 + k * step <= right; k++)
                        {
                            float x = (float)(x0 + k * step);
                            dl.AddLine(new Vector2(x, mid - 6), new Vector2(x, mid + 6), color);
                        }
                    }
                }
                else if (row.Group is SbTrigger trigger)
                {
                    float x0 = X(trigger.Start), x1 = MathF.Max(x0 + 3, X(trigger.End));
                    dl.AddRectFilled(new Vector2(x0, mid - 4), new Vector2(x1, mid + 4), Theme.WithAlpha(color, selected ? 0.4f : 0.18f), 2);
                    dl.AddRect(new Vector2(x0, mid - 4), new Vector2(x1, mid + 4), selected ? frame : Theme.WithAlpha(color, 0.8f), 2);
                    if (row.Obj is SbSprite owner) DrawTriggerFirings(dl, owner, trigger, y, rowHeight, right);
                }
                break;
            }

            case RowKind.GroupProperty:
            {
                var group = row.Group!;
                int offset = GroupOffset(group);
                if (group is SbLoop loop && loop.Iterations > 1)
                {
                    // Repeats are shown faded; the keyframes themselves are those of the first pass.
                    int length = loop.IterationLength;
                    double step = length * pxPerMs;
                    if (step >= 4)
                    {
                        int kMin = Math.Max(1, (int)Math.Floor((viewStart - loop.Start - loop.CommandsEnd) / length));
                        int kMax = Math.Min(loop.Iterations - 1, (int)Math.Ceiling((TimeAt(right) - loop.Start) / length));
                        for (int k = kMin; k <= kMax && k - kMin < 500; k++)
                            foreach (var c in group.Commands)
                                if (MatchesCommand(row, c)) DrawCommandMark(dl, c, offset + k * length, mid, right, true);
                    }
                }
                DrawKeyRow(dl, row, mid, right);
                break;
            }

            case RowKind.ObjectGroup:
            {
                // From the first moment something in the group is shown to the last: the group as one clip.
                var group = row.ObjectGroup!;
                var content = app.Index.ContentOf(group);
                if (GroupBarSpan(group) is not {} span) break;
                float x0 = X(span.Start), x1 = MathF.Max(x0 + 3, X(span.End));
                if (x1 < trackX || x0 > right) break;
                UiScriptTargets.Position("timeline.clip.group." + group.Name, new((Math.Max(trackX, x0) + Math.Min(right, x1)) / 2, mid));
                if (group.Plugin is {} binding)
                {
                    string target = "timeline.effect." + binding.Instance;
                    if (x0 >= trackX && x0 < right) UiScriptTargets.Position(target + ".start", new(x0+1, y+rowHeight/2));
                    if (x1 > trackX && x1 <= right) UiScriptTargets.Position(target + ".end", new(x1-1, y+rowHeight/2));
                    UiScriptTargets.Position(target + ".middle", new((Math.Max(trackX,x0)+Math.Min(right,x1))/2, y+rowHeight/2));
                }
                bool selected = app.IsSelected(group);
                bool hidden = content.Sprites.Count > 0 && content.Hidden == content.Sprites.Count;
                if (content.HasSpan && (content.Start < span.Start || content.End > span.End))
                    dl.AddRectFilled(new Vector2(X(Math.Min(content.Start, span.Start)), y + 5),
                        new Vector2(X(Math.Max(content.End, span.End)), y + rowHeight - 5), Theme.WithAlpha(Theme.GroupColor, .2f), 3);
                dl.AddRectFilled(new Vector2(x0, y + 3), new Vector2(x1, y + rowHeight - 3), Theme.WithAlpha(Theme.GroupColor, hidden ? 0.3f : selected ? 0.95f : 0.65f), 3);
                // A darker strip along the bottom tells it from the bar of a sprite: there is more in here.
                dl.AddRectFilled(new Vector2(x0, y + rowHeight - 7), new Vector2(x1, y + rowHeight - 3), Theme.Rgba(0, 0, 0, 60), 3);
                if (selected) dl.AddRect(new Vector2(x0, y + 3), new Vector2(x1, y + rowHeight - 3), Theme.Rgba(255, 255, 255, 220), 3);
                if (edgeGroup == group)
                {
                    float hx = edgePart == DragMode.TrimStart ? x0 : x1;
                    dl.AddRectFilled(new Vector2(hx - 2, y + 1), new Vector2(hx + 2, y + rowHeight - 1), Theme.Rgba(255, 255, 255), 2);
                }
                break;
            }

            case RowKind.ObjectGroupProperty:
                DrawKeyRow(dl, row, mid, right);
                break;
        }
    }

    // ---------------------------------------------------------------- keyframes

    /// <summary>The list the commands of a property row are in: that of a sprite, of a loop or trigger, or of a group of sprites.</summary>
    private static IList RowNodes(Row row) => row.Kind switch
    {
        RowKind.Property => SbInfo.DepthKinds.Contains(row.Command) ? ((SbSprite)row.Obj!).DepthCommands : ((SbSprite)row.Obj!).Nodes,
        RowKind.ObjectGroupProperty => row.Camera ? row.ObjectGroup!.Camera!.Commands : row.ObjectGroup!.Commands,
        _ => row.Group!.Commands,
    };

    /// <summary>The time the times of a property row count from: the start of its loop or trigger.</summary>
    private int RowOffset(Row row) => row.Kind == RowKind.GroupProperty ? GroupOffset(row.Group!) : 0;

    /// <summary>
    /// The same for telling where the playhead is among the keyframes of a row: in a loop the times count
    /// from the start of the pass the playhead is in, so that it stands on a keyframe in every pass.
    /// </summary>
    private int PlayheadOffset(Row row) => row.Kind == RowKind.GroupProperty ? app.BlockOffset(row.Group) : 0;

    /// <summary>
    /// The row of what is picked inside an object or a group of sprites: the property that is picked, or
    /// the loop or trigger that is selected as a whole. The row of the object itself when nothing is, or
    /// when the object is folded.
    /// </summary>
    /// <param name="index">The row of the object or the group of sprites.</param>
    private int PickedRow(int index)
    {
        var head = rows[index];
        for (int i = index + 1; i < rows.Count; i++)
        {
            var row = rows[i];
            bool part = head.Kind == RowKind.ObjectGroup
                ? row.Kind is RowKind.ObjectGroupProperty or RowKind.Camera && row.ObjectGroup == head.ObjectGroup
                : row.Kind is RowKind.Property or RowKind.GroupHeader or RowKind.GroupProperty && row.Obj == head.Obj;
            if (!part) break;
            if (row.Kind == RowKind.Camera) continue;
            if (row.Kind == RowKind.GroupHeader ? row.Group == app.SelectedBlock : IsParameterRowActive(row)) return i;
        }
        return index;
    }

    /// <summary>
    /// Whether the property of a row, once picked, is something the preview has a use for. Not a parameter,
    /// which is on or off, and nothing of a trigger: it runs when the game says so, and the preview does
    /// not play it. Such a row is picked all the same, for the properties panel to show it alone.
    /// </summary>
    private static bool Adjustable(Row row) => row.Command != CommandKind.P && row.Kind switch
    {
        RowKind.Property or RowKind.ObjectGroupProperty => true,
        RowKind.GroupProperty => row.Group is SbLoop,
        _ => false,
    };

    /// <summary>
    /// The keyframes of a property row. Null for a row whose commands are shown as they are written: P has
    /// no values, and commands of one kind that run at the same time have no single value at a moment.
    /// </summary>
    private static List<Keyframe>? RowKeys(Row row) => row.Command == CommandKind.P ? null : KeyTrack.Read(RowNodes(row), row.Command);

    /// <summary>
    /// A property row: its keyframes as diamonds, a bar from one to the next where the value is on its way
    /// there, and a faint line where it only waits for the next keyframe.
    /// </summary>
    private void DrawKeyRow(ImDrawListPtr dl, Row row, float mid, float right)
    {
        var nodes = RowNodes(row);
        int offset = RowOffset(row);
        // Where it runs together with a kind it may not run with (a move with MX or MY, the scale with the
        // vector scale): a red line under that stretch of the row.
        foreach (var (start, end) in CommandClash.Overlaps(nodes, row.Command))
        {
            float from = X(offset + start), to = MathF.Max(from + 4, X(offset + end));
            if (to >= trackX && from <= right)
                dl.AddRectFilled(new Vector2(MathF.Max(from, trackX), mid + 6), new Vector2(MathF.Min(to, right), mid + 9), Theme.TextError);
        }

        if (app.IsActiveTrack(nodes, row.Command) && (row.Parameter == '\0' || app.ActiveTrack?.Parameter == row.Parameter))
            dl.AddRectFilled(new Vector2(trackX, mid - 9), new Vector2(right, mid + 9), Theme.WithAlpha(Theme.Accent, 0.12f));

        var keys = RowKeys(row);
        if (row.Parameter != '\0') ParameterSpanTargets(row, offset, mid, right);
        if (keys == null)
        {
            // The selected ones last, so that one lying under another shows once it is selected.
            foreach (object? node in nodes)
                if (node is SbCommand c && MatchesCommand(row, c) && !app.SelectedCommands.Contains(c)) DrawCommandMark(dl, c, offset, mid, right, false);
            foreach (object? node in nodes)
                if (node is SbCommand c && MatchesCommand(row, c) && app.SelectedCommands.Contains(c)) DrawCommandMark(dl, c, offset, mid, right, false);
            return;
        }

        uint color = Theme.KindColor(row.Command);
        bool selected = keys.Count > 0 && app.SelectedKeys.Contains(new KeyRef(nodes, row.Command, 0));
        for (int i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            float x = X(offset + key.Time);
            bool nextSelected = i + 1 < keys.Count && app.SelectedKeys.Contains(new KeyRef(nodes, row.Command, i + 1));
            if (i + 1 < keys.Count)
            {
                float next = X(offset + keys[i + 1].Time);
                if (next - x > 1 && next >= trackX && x <= right)
                {
                    float from = MathF.Max(x, trackX - 10), to = MathF.Min(next, right + 10);
                    if (key.Easing != Keyframe.Hold)
                        dl.AddRectFilled(new Vector2(from, mid - 2), new Vector2(to, mid + 2), Theme.WithAlpha(color, selected && nextSelected ? 0.9f : 0.5f));
                    else
                        dl.AddLine(new Vector2(from, mid), new Vector2(to, mid), Theme.WithAlpha(color, 0.3f));
                }
            }
            if (x >= trackX - 8 && x <= right + 8)
            {
                // Colour keys show their own colour, so they need an outline that also reads on black.
                bool ownColour = row.Command == CommandKind.C && key.Value.Length == 3;
                uint fill = ownColour ? Theme.Rgba((int)key.Value[0], (int)key.Value[1], (int)key.Value[2]) : color;
                uint outline = selected ? Theme.Rgba(255, 255, 255) : ownColour ? Theme.Rgba(255, 255, 255, 110) : Theme.Rgba(0, 0, 0, 170);
                Theme.Diamond(dl, new Vector2(x, mid), selected ? 5.5f : 4.5f, fill, outline);
            }
            selected = nextSelected;
        }
    }

    /// <summary>
    /// The keyframe of a row at a horizontal position: the one the position is on, or else the one whose
    /// transition to the next keyframe it is on. -1 where the row has nothing.
    /// </summary>
    private (int Index, bool OnKey) HitKey(List<Keyframe> keys, int offset, float mouseX)
    {
        int nearest = -1;
        float distance = 5;
        for (int i = 0; i < keys.Count; i++)
        {
            float d = MathF.Abs(mouseX - X(offset + keys[i].Time));
            // Of two at one place the later one, which is the one drawn on top.
            if (d > distance) continue;
            nearest = i;
            distance = d;
        }
        if (nearest >= 0) return (nearest, true);
        for (int i = 0; i + 1 < keys.Count; i++)
            if (keys[i].Easing != Keyframe.Hold && mouseX > X(offset + keys[i].Time) && mouseX < X(offset + keys[i + 1].Time)) return (i, false);
        return (-1, false);
    }

    /// <summary>
    /// The keyframe button at the end of a property row's name: a plus while the playhead stands on no
    /// keyframe of the row, which puts one there, and a minus while it stands on one, which takes it away.
    /// </summary>
    /// <param name="max">The bottom right corner of the row's name, where the tracks begin.</param>
    /// <param name="room">The width the button takes up at that end; nothing for a row that has no keyframes.</param>
    /// <returns>True while the mouse is on it.</returns>
    private bool KeyButton(ImDrawListPtr dl, Row row, Vector2 max, float rowHeight, bool rowHovered, out float room, out int? direction)
    {
        room = 0;
        direction = 0;
        if (RowKeys(row) == null && row.Parameter == '\0') return false;
        float side = rowHeight - 6;
        float gap = Theme.Px(2);
        // Clear of the edge that is dragged to make the names wider.
        Vector2 corner = new(MathF.Round(max.X - 8 - 2 * side - gap), MathF.Round(max.Y - rowHeight + 3));
        room = 3 * side + 2 * gap + 6;

        bool here = row.Parameter == '\0' && app.KeyAtPlayhead(RowNodes(row), row.Command, PlayheadOffset(row)) >= 0;
        float mouseX = ImGui.GetIO().MousePos.X;
        string target = RowKeyTarget(row);
        int origin = row.Group is SbLoop loop ? loop.Start : PlayheadOffset(row);
        var navigationNodes = row.Parameter == '\0' ? RowNodes(row) : ParameterTrack.Commands(RowNodes(row), row.Parameter);
        var available = KeyNavigation.Available(navigationNodes, row.Command, app.Time, origin, row.Group as SbLoop);
        bool previous = JumpButton(-1, available.Previous), next = JumpButton(1, available.Next);
        bool over = rowHovered && mouseX >= corner.X && mouseX < corner.X + side;
        dl.AddRectFilled(corner, corner + new Vector2(side, side), ImGui.GetColorU32(over ? ImGuiCol.ButtonHovered : ImGuiCol.Button), 3);

        // The sign is made of bars rather than of a letter, so that it is sharp and sits in the middle.
        Vector2 centre = corner + new Vector2(MathF.Floor(side / 2), MathF.Floor(side / 2));
        float arm = MathF.Floor(side * 0.28f);
        uint color = ImGui.GetColorU32(ImGuiCol.Text);
        dl.AddRectFilled(centre + new Vector2(-arm, -1), centre + new Vector2(arm, 1), color);
        if (!here) dl.AddRectFilled(centre + new Vector2(-1, -arm), centre + new Vector2(1, arm), color);
        UiScriptTargets.Position(target + ".toggle", centre);

        if (previous || next)
        {
            direction = (previous ? available.Previous : available.Next) ? (previous ? -1 : 1) : null;
            Theme.SetTooltip(previous ? L.T("viewer.previousKey.tip") : L.T("viewer.nextKey.tip"));
            return true;
        }
        if (!over) return false;
        if (row.Parameter != '\0') Theme.SetTooltip(L.T("props.addKey"));
        else if (here) Theme.SetTooltip(L.T("timeline.key.remove"));
        else Theme.SetTooltip(L.T("timeline.key.add"));
        return true;

        bool JumpButton(int step, bool enabled)
        {
            Vector2 at = corner + new Vector2(step * (side + gap), 0);
            bool hovered = rowHovered && mouseX >= at.X && mouseX < at.X + side;
            dl.AddRectFilled(at, at + new Vector2(side), ImGui.GetColorU32(hovered && enabled ? ImGuiCol.ButtonHovered : ImGuiCol.Button), 3);
            Vector2 centre = at + new Vector2(MathF.Floor(side / 2));
            float radius = MathF.Round(side * 0.3f);
            Vector2 top = centre + new Vector2(-step * radius, -radius);
            Vector2 tip = centre + new Vector2(step * radius, 0);
            Vector2 bottom = centre + new Vector2(-step * radius, radius);
            uint color = enabled ? Theme.Rgba(255, 255, 255) : ImGui.GetColorU32(ImGuiCol.TextDisabled);
            if (step < 0) dl.AddTriangleFilled(tip, top, bottom, color);
            else dl.AddTriangleFilled(top, tip, bottom, color);
            UiScriptTargets.Position(target + (step < 0 ? ".previous" : ".next"), centre);
            return hovered;
        }
    }

    private static string RowKeyTarget(Row row) => "timeline.key." + row.Kind + "." +
        (row.ObjectGroup?.Name ?? (row.Obj as SbSprite)?.Path ?? "") + "." + (row.Camera ? "camera." : "") + row.Command + (row.Parameter == '\0' ? "" : "." + row.Parameter);

    private void UseKeyButton(Row row, int direction)
    {
        if (direction == 0) { ToggleKey(row); return; }
        app.SelectTrack(RowNodes(row), row.Command, row.Parameter);
        if (app.JumpKey(direction)) RevealPlayhead();
    }

    /// <summary>Picks the property of a row for adjusting in the preview. Picked already, it is let go of.</summary>
    private void PickTrack(Row row)
    {
        var nodes = RowNodes(row);
        if (app.IsActiveTrack(nodes, row.Command) && (row.Parameter == '\0' || app.ActiveTrack?.Parameter == row.Parameter))
        {
            app.ClearKeySelection();
            app.ActiveTrack = null;
        }
        else
        {
            app.SelectTrack(nodes, row.Command);
        }
    }

    private void ToggleKey(Row row)
    {
        if (row.Parameter != '\0') { AddParameter(row); return; }
        if(row.Camera && !row.ObjectGroup!.CameraActive) return;
        var nodes = RowNodes(row);
        int here = app.KeyAtPlayhead(nodes, row.Command, PlayheadOffset(row));
        if (here >= 0)
        {
            KeyTrack.Remove(nodes, row.Command, here);
            app.SelectedKeys.Clear();
            if (row.ObjectGroup != null) app.TouchGroup(row.ObjectGroup);
            else app.Touch(row.Obj);
        }
        else if (row.ObjectGroup != null) app.AddKey(row.ObjectGroup, row.Command);
        else app.AddKey((SbSprite)row.Obj!, row.Group, row.Command);
    }

    /// <summary>
    /// A press on a row of keyframes: selects the keyframe under the mouse, or the two at the ends of the
    /// transition under it, and gets ready to move every selected keyframe along with it.
    /// </summary>
    private void BeginKeyDrag(Row row, List<Keyframe> keys, float mouseX, ImGuiIOPtr io)
    {
        if(row.Camera && !row.ObjectGroup!.CameraActive) return;
        var nodes = RowNodes(row);
        int offset = RowOffset(row);
        var (index, onKey) = HitKey(keys, offset, mouseX);

        // Selected keyframes are those of the selected objects, or of the selected group.
        if (row.ObjectGroup is { } objectGroup)
        {
            if (app.SelectedGroup != objectGroup) app.SelectGroup(objectGroup);
            dragObjectGroup = objectGroup;
        }
        else
        {
            var sprite = (SbSprite)row.Obj!;
            if (!app.IsSelected(sprite))
            {
                if (io.KeyCtrl) app.AddToSelection(sprite);
                else app.SelectOnly(sprite);
            }
            dragSprite = sprite;
        }

        // A press anywhere on the row picks its property, for the properties panel and for the preview.
        app.ActiveTrack = new TrackRef(nodes, row.Command);
        if (index < 0)
        {
            if (!io.KeyCtrl) app.ClearKeySelection();
            return;
        }

        var first = new KeyRef(nodes, row.Command, index);
        var second = new KeyRef(nodes, row.Command, onKey ? index : index + 1);
        bool both = app.SelectedKeys.Contains(first) && app.SelectedKeys.Contains(second);
        if (io.KeyCtrl)
        {
            app.SelectedKeys.Remove(first);
            app.SelectedKeys.Remove(second);
        }
        else if (!both)
        {
            app.ClearKeySelection();
        }
        if (!both)
        {
            app.SelectedKeys.Add(first);
            if (second != first) app.SelectedKeys.Add(second);
        }
        if (!app.SelectedKeys.Contains(first)) return;

        // Every selected keyframe moves by the same amount, and none of them past a neighbour that stays.
        dragKeys.Clear();
        dragKeyMin = int.MinValue;
        dragKeyMax = int.MaxValue;
        foreach (var track in app.SelectedKeys.GroupBy(k => (k.Nodes, k.Kind)))
        {
            bool inHand = track.Key.Nodes == nodes && track.Key.Kind == row.Command;
            var trackKeys = inHand ? keys : KeyTrack.Read(track.Key.Nodes, track.Key.Kind);
            if (trackKeys == null) continue;
            var picked = track.Select(k => k.Index).Where(i => i < trackKeys.Count).ToHashSet();
            foreach (int i in picked)
            {
                var key = trackKeys[i];
                dragKeys.Add((key, key.Time));
                // On the row in hand they keep a tick away from the neighbour; on the others, which may count
                // their times from elsewhere, a millisecond.
                var (min, max) = inHand
                    ? app.KeyTimeRange(trackKeys, i, offset)
                    : (i > 0 ? Math.Min(trackKeys[i - 1].Time + 1, key.Time) : int.MinValue, i + 1 < trackKeys.Count ? Math.Max(trackKeys[i + 1].Time - 1, key.Time) : int.MaxValue);
                if (i > 0 && !picked.Contains(i - 1)) dragKeyMin = Math.Max(dragKeyMin, min - key.Time);
                if (i + 1 < trackKeys.Count && !picked.Contains(i + 1)) dragKeyMax = Math.Min(dragKeyMax, max - key.Time);
            }
        }

        drag = DragMode.Keys;
        dragOffset = offset;
        dragOriginTime = offset + keys[index].Time;
    }

    /// <summary>Where the times of a loop or trigger row count from: the first pass of a loop, the simulated firing of a trigger at the playhead.</summary>
    private int GroupOffset(SbGroup group) => group switch
    {
        SbLoop loop => loop.Start,
        SbTrigger trigger => app.BlockOffset(trigger),
        _ => 0,
    };

    private void DrawCommandMark(ImDrawListPtr dl, SbCommand c, int offset, float mid, float right, bool ghost)
    {
        float x0 = X(offset + c.Start), x1 = X(offset + c.End);
        if (x1 < trackX - 8 || x0 > right + 8) return;

        bool selected = !ghost && app.SelectedCommands.Contains(c);
        float alpha = ghost ? 0.28f : 1f;
        uint color = Theme.KindColor(c.Kind);
        uint startColor = color, endColor = color;
        if (c.Kind == CommandKind.C && c.From.Length == 3 && c.To.Length == 3)
        {
            startColor = Theme.Rgba((int)c.From[0], (int)c.From[1], (int)c.From[2]);
            endColor = Theme.Rgba((int)c.To[0], (int)c.To[1], (int)c.To[2]);
        }

        // Colour keys show their own colour, so they need an outline that also reads on black.
        uint outline = selected ? Theme.Rgba(255, 255, 255)
            : c.Kind == CommandKind.C ? Theme.Rgba(255, 255, 255, ghost ? 40 : 110)
            : Theme.Rgba(0, 0, 0, ghost ? 60 : 170);
        float radius = selected ? 5.5f : 4.5f;
        if (x1 - x0 > 1)
        {
            dl.AddRectFilled(new Vector2(x0, mid - 2), new Vector2(x1, mid + 2), Theme.WithAlpha(color, alpha * (selected ? 0.9f : 0.5f)));
            Theme.Diamond(dl, new Vector2(x1, mid), radius, Theme.WithAlpha(endColor, alpha), outline);
        }
        Theme.Diamond(dl, new Vector2(x0, mid), radius, Theme.WithAlpha(startColor, alpha), outline);
    }

    // ---------------------------------------------------------------- dragging

    /// <summary>
    /// The commands of a row at a horizontal position, each with the part of it that is there, the one to
    /// take first at the head. Commands that run at the same time lie over one another, so there may be
    /// several: an end comes before a body, since ends are small and a body is everywhere, and of two the
    /// shorter comes first, since the longer one can be had wherever the shorter is not.
    /// </summary>
    private List<(SbCommand Command, DragMode Part)> HitCommands(Row row, float mouseX)
    {
        const float grab = 5;
        var hits = new List<(SbCommand Command, DragMode Part, float Length)>();
        int offset = RowOffset(row);
        foreach (object? node in RowNodes(row))
        {
            if (node is not SbCommand c || !MatchesCommand(row, c)) continue;
            float x0 = X(offset + c.Start), x1 = X(offset + c.End);
            if (mouseX < x0 - grab || mouseX > x1 + grab) continue;
            // Short commands move as a whole; longer ones can be resized from either end.
            DragMode part = x1 - x0 <= grab * 3 ? DragMode.CommandBody
                : mouseX <= x0 + grab ? DragMode.CommandStart
                : mouseX >= x1 - grab ? DragMode.CommandEnd
                : DragMode.CommandBody;
            hits.Add((c, part, x1 - x0));
        }
        // Stable, so that of two alike the one written first stays first.
        return hits
            .OrderBy(h => h.Part != DragMode.CommandBody || h.Length <= grab * 3 ? 0 : 1)
            .ThenBy(h => h.Length)
            .Select(h => (h.Command, h.Part)).ToList();
    }

    /// <summary>
    /// The command a press takes hold of among those under the mouse: one that is selected already, so
    /// that it can be dragged however many lie over it, and otherwise the first.
    /// </summary>
    /// <param name="next">
    /// The one after it, round to the first again: what a plain click passes the selection on to, as a
    /// click in the preview does with sprites that lie over one another. Null when there is no other.
    /// </param>
    private (SbCommand? Command, DragMode Part) Grabbed(List<(SbCommand Command, DragMode Part)> hits, out SbCommand? next)
    {
        next = null;
        if (hits.Count == 0) return (null, DragMode.None);
        int held = hits.FindIndex(h => app.SelectedCommands.Contains(h.Command));
        if (held < 0) return hits[0];
        if (hits.Count > 1) next = hits[(held + 1) % hits.Count].Command;
        return hits[held];
    }

    /// <summary>
    /// A press on a row whose commands are shown as they are written. Like a press on a row of keyframes
    /// it picks the row's property for the preview, unless that is a parameter or belongs to a loop or trigger.
    /// </summary>
    /// <returns>The command the press is on and the part of it; none when it is on no command.</returns>
    private (SbCommand? Command, DragMode Part) PressCommand(Row row, float mouseX, ImGuiIOPtr io)
    {
        app.ActiveTrack = new TrackRef(RowNodes(row), row.Command, row.Parameter);
        var (command, part) = Grabbed(HitCommands(row, mouseX), out var next);
        if (command == null)
        {
            if (!io.KeyCtrl) app.ClearKeySelection();
            return (null, DragMode.None);
        }
        if (io.KeyCtrl)
        {
            if (!app.SelectedCommands.Remove(command)) app.SelectedCommands.Add(command);
        }
        else if (!app.SelectedCommands.Contains(command))
        {
            app.ClearKeySelection();
            app.SelectedCommands.Add(command);
        }
        else
        {
            clickNext = next;
        }
        return app.SelectedCommands.Contains(command) ? (command, part) : (null, DragMode.None);
    }

    /// <summary>
    /// Which part of a sprite's bar is at a horizontal position: TrimStart or TrimEnd near the edges, Objects
    /// (move) in between, None when off the bar.
    /// </summary>
    private DragMode BarPart(SbSprite sprite, float mouseX)
    {
        var timeline = sprite.Timeline;
        return timeline.HasCommands ? BarPart(timeline.StartTime, timeline.EndTime, mouseX) : DragMode.None;
    }

    internal static bool GroupBarCanResize(SbObjectGroup group, StoryboardIndex.Content content) =>
        group.Plugin?.Locked != true && (content.Sprites.Count == 0 || content.Locked < content.Sprites.Count) &&
        (group.Plugin == null || group.Plugin.Effect is { Supported: true, Definition.TimeMode: SbStudio.Effects.TimeMode.AdjustableDuration });

    private (int Start, int End)? GroupBarSpan(SbObjectGroup group) => GroupBarSpan(group, app.Index.ContentOf(group));
    internal static (int Start, int End)? GroupBarSpan(SbObjectGroup group, StoryboardIndex.Content content)
    {
        if (group.Plugin?.Effect is { Supported: true, Definition.TimeMode: SbStudio.Effects.TimeMode.AdjustableDuration } effect)
        {
            double duration = effect.DurationMs ?? effect.Definition.DefaultDuration;
            double start = group.Plugin.Offset + effect.TimeOffset;
            double end = start + duration * effect.TimeScale;
            double from = Math.Min(start, end), to = Math.Max(start, end);
            if (double.IsFinite(from) && double.IsFinite(to) && duration >= 0 && to < int.MaxValue - 1d && from > int.MinValue + 1d)
                return (EffectOutputAdapter.Milliseconds(from), EffectOutputAdapter.Milliseconds(to));
        }
        return content.HasSpan ? (content.Start, content.End) : null;
    }

    /// <summary>The same for a bar from one time to another, such as that of a group.</summary>
    private DragMode BarPart(int start, int end, float mouseX)
    {
        float x0 = X(start), x1 = MathF.Max(x0 + 3, X(end));
        // The grab zones reach a little outside the bar and shrink on short bars so the middle stays usable.
        const float outside = 3;
        if (mouseX < x0 - outside || mouseX > x1 + outside) return DragMode.None;
        float grab = Math.Clamp((x1 - x0) / 3, 2, 6);
        if (mouseX <= x0 + grab) return DragMode.TrimStart;
        if (mouseX >= x1 - grab) return DragMode.TrimEnd;
        return DragMode.Objects;
    }

    /// <summary>
    /// Where the bar of a video starts and ends on screen: from its time for as long as the file lasts.
    /// A file that is missing, or does not say how long it is, gets a stub.
    /// </summary>
    private (float Start, float End) VideoBar(SbVideo video)
    {
        float x0 = X(video.Time);
        double length = video.Path.Length == 0 ? 0 : app.Videos.Info(app.Project!.AssetFullPath(video.Path))?.DurationMs ?? 0;
        if (double.IsInfinity(length)) length = 0;
        return (x0, MathF.Max(x0 + 12, X(video.Time + length)));
    }

    private void BeginDrag(Row? hit, ImGuiIOPtr io)
    {
        drag = DragMode.None;
        dragMouseX = io.MousePos.X;
        dragApplied = 0;
        dragMoved = false;
        dragCommand = null;
        clickNext = null;
        dragGroup = null;
        dragSprite = null;
        dragObjectGroup = null;
        groupTrim = null;
        trimGroup = null;
        effectTrim = null;
        dragKeys.Clear();

        if (hit is not { } row)
        {
            if (!io.KeyCtrl) app.ClearSelection();
            return;
        }

        float mx = io.MousePos.X;

        switch (row.Kind)
        {
            case RowKind.LayerHeader:
                app.SelectTimelineSection((TimelineSection)row.Layer);
                break;
            case RowKind.BeatmapBackgroundSlot:
                app.SelectEmptyBeatmapBackground();
                break;
            case RowKind.ObjectGroup:
            {
                var group = row.ObjectGroup!;
                var content = app.Index.ContentOf(group);
                var span = GroupBarSpan(group);
                var part = span is {} range ? BarPart(range.Start, range.End, mx) : DragMode.None;
                if (part == DragMode.None)
                {
                    if (!io.KeyCtrl) app.ClearSelection();
                    return;
                }

                bool locked = group.Plugin?.Locked == true || content.Sprites.Count > 0 && content.Locked == content.Sprites.Count;
                if (part is DragMode.TrimStart or DragMode.TrimEnd && group.Plugin != null)
                {
                    if (group.Plugin.Effect is not { Supported: true, Definition: { TimeMode: SbStudio.Effects.TimeMode.AdjustableDuration } definition })
                    { app.SetStatus(L.T("effects.fixedDuration")); return; }
                    if (!GroupBarCanResize(group, content) || io.KeyCtrl) return;
                    app.SelectGroup(group); effectTrim = group;
                    effectTrimTimingMapped = group.Plugin.Effect.TimeScale != 1 || group.Plugin.Effect.TimeOffset != 0;
                    effectTrimOriginal = effectTrimTimingMapped && span is {} mapped
                        ? (mapped.Start, (double)mapped.End - mapped.Start)
                        : (group.Plugin.Offset, group.Plugin.Effect.DurationMs ?? definition.DefaultDuration);
                    drag = part; dragOriginTime = part == DragMode.TrimStart ? effectTrimOriginal.Start : effectTrimOriginal.Start + effectTrimOriginal.Duration;
                    effectTrimTick = 0; break;
                }
                if (part is DragMode.TrimStart or DragMode.TrimEnd && app.Doc.GroupsIn(group).Any(g => g.Plugin != null))
                { app.SetStatus(L.T("effects.noParentTrim")); return; }
                if (part is DragMode.TrimStart or DragMode.TrimEnd && !locked && !io.KeyCtrl)
                {
                    // An end: the group is cut or drawn out there, and what is in it with it.
                    app.SelectGroup(group);
                    rangeAnchorGroup = group;
                    rangeAnchor = null;
                    groupTrim = new GroupClip.Trim(app.Doc, group);
                    trimGroup = group;
                    drag = part;
                    trimApplied = (groupTrim.Start, groupTrim.End);
                    dragOriginTime = part == DragMode.TrimStart ? groupTrim.Start : groupTrim.End;
                    break;
                }

                if (io.KeyCtrl) app.ToggleSelection(group);
                else if (!app.IsSelected(group)) app.SelectGroup(group);
                app.ClearKeySelection();
                rangeAnchorGroup = group;
                rangeAnchor = null;
                if (app.IsSelected(group))
                {
                    drag = DragMode.Objects;
                    dragOriginTime = span!.Value.Start;
                }
                break;
            }

            case RowKind.ObjectGroupProperty:
            {
                if (RowKeys(row) is { } groupKeys)
                {
                    BeginKeyDrag(row, groupKeys, mx, io);
                    break;
                }
                var group = row.ObjectGroup!;
                if (app.SelectedGroup != group) app.SelectGroup(group);
                var (command, part) = PressCommand(row, mx, io);
                if (command == null) return;

                drag = part;
                dragCommand = command;
                dragObjectGroup = group;
                dragOffset = 0;
                dragOriginTime = part == DragMode.CommandEnd ? command.End : command.Start;
                break;
            }

            case RowKind.Sprite:
            {
                var sprite = (SbSprite)row.Obj!;
                var timeline = sprite.Timeline;
                var part = BarPart(sprite, mx);
                if (part == DragMode.None)
                {
                    if (!io.KeyCtrl) app.ClearSelection();
                    return;
                }

                if (part is DragMode.TrimStart or DragMode.TrimEnd && !sprite.Locked && !io.KeyCtrl)
                {
                    // An edge: trim this one sprite, like the end of a clip in a video editor.
                    app.SelectOnly(sprite);
                    app.ClearKeySelection();
                    rangeAnchor = sprite;
                    drag = part;
                    dragSprite = sprite;
                    trimOriginal = (SbSprite)sprite.Clone();
                    trimApplied = (timeline.StartTime, timeline.EndTime);
                    dragOriginTime = part == DragMode.TrimStart ? timeline.StartTime : timeline.EndTime;
                    break;
                }

                if (io.KeyCtrl) app.ToggleSelection(sprite);
                else if (!app.IsSelected(sprite)) app.SelectOnly(sprite);
                app.ClearKeySelection();
                rangeAnchor = sprite;
                rangeAnchorGroup = null;
                if (app.IsSelected(sprite))
                {
                    drag = DragMode.Objects;
                    dragOriginTime = timeline.StartTime;
                }
                break;
            }

            case RowKind.Sample:
            {
                var sample = (SbSample)row.Obj!;
                if (MathF.Abs(mx - X(sample.Time)) > 8)
                {
                    if (!io.KeyCtrl) app.ClearSelection();
                    return;
                }
                if (io.KeyCtrl) app.ToggleSelection(sample);
                else if (!app.IsSelected(sample)) app.SelectOnly(sample);
                rangeAnchor = sample;
                if (app.IsSelected(sample))
                {
                    drag = DragMode.Objects;
                    dragOriginTime = sample.Time;
                }
                break;
            }

            case RowKind.Video:
            {
                var video = (SbVideo)row.Obj!;
                var (x0, x1) = VideoBar(video);
                if (mx < x0 - 3 || mx > x1 + 3)
                {
                    if (!io.KeyCtrl) app.ClearSelection();
                    return;
                }
                if (io.KeyCtrl) app.ToggleSelection(video);
                else if (!app.IsSelected(video)) app.SelectOnly(video);
                app.ClearKeySelection();
                rangeAnchor = video;
                if (app.IsSelected(video))
                {
                    drag = DragMode.Objects;
                    dragOriginTime = video.Time;
                }
                break;
            }

            case RowKind.Property:
            case RowKind.GroupProperty:
            {
                if (RowKeys(row) is { } rowKeys)
                {
                    BeginKeyDrag(row, rowKeys, mx, io);
                    break;
                }
                var sprite = (SbSprite)row.Obj!;
                // The commands that can be selected are those of the selected objects.
                if (!app.IsSelected(sprite))
                {
                    if (io.KeyCtrl) app.AddToSelection(sprite);
                    else app.SelectOnly(sprite);
                }
                var (command, part) = PressCommand(row, mx, io);
                if (command == null) return;

                drag = part;
                dragCommand = command;
                dragSprite = sprite;
                dragOffset = row.Kind == RowKind.Property ? 0 : GroupOffset(row.Group!);
                dragOriginTime = dragOffset + (part == DragMode.CommandEnd ? command.End : command.Start);
                break;
            }

            case RowKind.GroupHeader:
            {
                var sprite = (SbSprite)row.Obj!;
                // A press on its row selects the loop or trigger as a whole, on its bar or beside it.
                app.SelectBlock(sprite, row.Group!);
                dragSprite = sprite;
                dragGroup = row.Group;
                if (row.Group is SbLoop loop)
                {
                    if (mx < X(loop.AbsoluteStart) - 3 || mx > X(loop.AbsoluteEnd) + 3) return;
                    drag = DragMode.Group;
                    dragOriginTime = loop.Start;
                }
                else if (row.Group is SbTrigger trigger)
                {
                    float x0 = X(trigger.Start), x1 = X(trigger.End);
                    if (mx < x0 - 5 || mx > x1 + 5) return;
                    bool wide = x1 - x0 > 16;
                    drag = wide && mx <= x0 + 5 ? DragMode.GroupStart : wide && mx >= x1 - 5 ? DragMode.GroupEnd : DragMode.Group;
                    dragOriginTime = drag == DragMode.GroupEnd ? trigger.End : trigger.EditOffset;
                }
                break;
            }
        }
    }

    /// <summary>Marks the owner of the command being dragged as changed: a sprite or a group of sprites.</summary>
    private void TouchDragged()
    {
        if (dragObjectGroup != null) app.TouchGroup(dragObjectGroup);
        else app.Touch(dragSprite);
    }

    private void UpdateDrag(ImGuiIOPtr io)
    {
        float moved = io.MousePos.X - dragMouseX;
        if (!dragMoved && MathF.Abs(moved) < 3) return;
        dragMoved = true;

        // Whatever is grabbed (a bar's start, a key, a loop) lands on a snap tick while snapping is on,
        // and on the millisecond under the mouse while it is off.
        double target = app.SnapTime(dragOriginTime + moved / pxPerMs);
        int delta = (int)Math.Round(target - dragOriginTime);
        int step = delta - dragApplied;

        switch (drag)
        {
            case DragMode.Objects:
                if (step == 0) return;
                foreach (var obj in app.Selection)
                {
                    if (obj.Locked) continue;
                    obj.ShiftTime(step);
                    app.Touch(obj);
                }
                // A group moves with everything in it, unless all of that is locked.
                foreach (var group in app.SelectedGroups)
                {
                    var content = app.Index.ContentOf(group);
                    if (content.Sprites.Count > 0 && content.Locked == content.Sprites.Count) continue;
                    GroupClip.Shift(app.Doc, group, step);
                    app.TouchGroup(group);
                }
                break;

            case DragMode.Keys:
                delta = Math.Clamp(delta, dragKeyMin, dragKeyMax);
                if (delta != dragApplied)
                {
                    foreach (var (key, origin) in dragKeys) KeyTrack.SetTime(key, origin + delta);
                    foreach (var obj in app.Selection) app.Touch(obj);
                    if (app.SelectedGroup != null) app.TouchGroup(app.SelectedGroup);
                }
                Theme.SetTooltip(Theme.FormatTime(dragOriginTime + delta));
                break;

            case DragMode.CommandBody:
                if (step == 0) return;
                foreach (var c in app.SelectedCommands)
                {
                    c.Start += step;
                    c.End += step;
                }
                foreach (var obj in app.Selection) app.Touch(obj);
                if (app.SelectedGroup != null) app.TouchGroup(app.SelectedGroup);
                break;

            case DragMode.CommandStart:
            {
                int value = Math.Min((int)Math.Round(target) - dragOffset, dragCommand!.End);
                if (value == dragCommand.Start) return;
                dragCommand.Start = value;
                TouchDragged();
                break;
            }

            case DragMode.CommandEnd:
            {
                int value = Math.Max((int)Math.Round(target) - dragOffset, dragCommand!.Start);
                if (value == dragCommand.End) return;
                dragCommand.End = value;
                TouchDragged();
                break;
            }

            case DragMode.TrimStart:
            case DragMode.TrimEnd:
            {
                if (effectTrim?.Plugin?.Effect?.Definition is {} definition)
                {
                    double effectEnd = effectTrimOriginal.Start + effectTrimOriginal.Duration;
                    int from = effectTrimOriginal.Start;
                    double duration = Math.Clamp(drag == DragMode.TrimStart ? effectEnd - target : target - from,
                        effectTrimTimingMapped ? 1 : definition.MinDuration, effectTrimTimingMapped ? int.MaxValue / 2 : definition.MaxDuration);
                    if (drag == DragMode.TrimStart) from = checked((int)Math.Round(effectEnd - duration));
                    Theme.SetTooltip(Theme.FormatTime(from) + " – " + Theme.FormatTime(from + duration));
                    if (Environment.TickCount64 - effectTrimTick >= 150 || !io.MouseDown[0])
                    {
                        if (effectTrimTimingMapped)
                        {
                            try { app.SetEffectClipRange(effectTrim, from, checked((int)Math.Round(from + duration))); }
                            catch (Exception ex) when (ex is InvalidOperationException or OverflowException)
                            { app.SetStatus(ex is OverflowException ? L.T("clipTime.overflow") : ex.Message); }
                        }
                        else app.SetEffectRange(effectTrim, from, duration, true);
                        effectTrimTick = Environment.TickCount64;
                    }
                    return;
                }
                if (groupTrim != null)
                {
                    // The end stays on the grid, at least one tick away from the other end.
                    int from = groupTrim.Start, to = groupTrim.End;
                    int at = (int)Math.Round(target);
                    if (drag == DragMode.TrimStart) from = Math.Min(at, app.PreviousTickMs(to));
                    else to = Math.Max(at, app.NextTickMs(from));
                    if ((from, to) != trimApplied)
                    {
                        trimApplied = (from, to);
                        groupTrim.Apply(from, to);
                        app.Touch();
                    }
                    int gone = groupTrim.Emptied.Count();
                    Theme.SetTooltip($"{Theme.FormatTime(from)} – {Theme.FormatTime(to)}\n" + L.T("timeline.duration", to - from) +
                                     (gone > 0 ? "\n" + L.T("timeline.trimGroup.gone", gone) : ""));
                    return;
                }
                if (trimOriginal == null || dragSprite == null) return;
                var original = trimOriginal.Timeline;
                int start = original.StartTime, end = original.EndTime;
                int edge = (int)Math.Round(target);
                // The edge stays on the grid, at least one tick away from the other edge, and out of loops.
                var (maxStart, minEnd) = SpriteTrim.Limits(trimOriginal);
                if (drag == DragMode.TrimStart) start = Math.Min(Math.Min(edge, app.PreviousTickMs(end)), maxStart);
                else end = Math.Max(Math.Max(edge, app.NextTickMs(start)), minEnd);

                if ((start, end) != trimApplied)
                {
                    trimApplied = (start, end);
                    dragSprite.Nodes = SpriteTrim.Apply(trimOriginal, start, end);
                    dragSprite.DepthCommands = SpriteTrim.Depth(trimOriginal,start,end);
                    app.Touch(dragSprite);
                }
                Theme.SetTooltip($"{Theme.FormatTime(start)} – {Theme.FormatTime(end)}\n" + L.T("timeline.duration", end - start));
                return;
            }

            case DragMode.Group:
                if (step == 0) return;
                if (dragGroup is SbLoop loop) loop.Start += step;
                else if (dragGroup is SbTrigger trigger)
                {
                    trigger.ShiftWindow(step);
                }
                app.Touch(dragSprite);
                break;

            case DragMode.GroupStart:
                if (dragGroup is SbTrigger ts)
                {
                    ts.Start = Math.Min((int)Math.Round(target), ts.End);
                    app.Touch(dragSprite);
                }
                break;

            case DragMode.GroupEnd:
                if (dragGroup is SbTrigger te)
                {
                    te.End = Math.Max((int)Math.Round(target), te.Start);
                    app.Touch(dragSprite);
                }
                break;
        }
        dragApplied = delta;
    }

    // ---------------------------------------------------------------- horizontal scrollbar

    private void DrawHorizontalScrollbar(Vector2 pos, Vector2 size)
    {
        var io = ImGui.GetIO();
        var dl = ImGui.GetWindowDrawList();
        var (start, end) = app.TimeBounds;
        double span = size.X / pxPerMs;
        double lo = Math.Min(start - 2000, viewStart);
        double hi = Math.Max(end + 2000, viewStart + span);
        double range = Math.Max(1, hi - lo);

        dl.AddRectFilled(pos, pos + size, Theme.Rgba(28, 28, 30));
        float grabX0 = pos.X + (float)((viewStart - lo) / range) * size.X;
        float grabX1 = MathF.Max(grabX0 + 14, pos.X + (float)((viewStart + span - lo) / range) * size.X);

        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton("##hscroll", size);
        bool active = ImGui.IsItemActive();
        bool moved = false;
        if (!app.Settings.OsuStyleTimelineIndicator && ImGui.IsItemActivated() && (io.MousePos.X < grabX0 || io.MousePos.X > grabX1))
        {
            viewStart = lo + (io.MousePos.X - pos.X) / size.X * range - span / 2;
            moved = true;
        }
        else if (active && io.MouseDelta.X != 0 && (!app.Settings.OsuStyleTimelineIndicator || !ImGui.IsItemActivated()))
        {
            viewStart += io.MouseDelta.X / size.X * range;
            moved = true;
        }
        if (moved) SeekFixedView();

        uint color = active ? Theme.Rgba(110, 110, 120) : ImGui.IsItemHovered() ? Theme.Rgba(92, 92, 100) : Theme.Rgba(72, 72, 78);
        dl.AddRectFilled(new Vector2(grabX0, pos.Y + 2), new Vector2(grabX1, pos.Y + size.Y - 2), color, 4);
    }
}
