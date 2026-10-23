using System.Numerics;
using ImGuiNET;
using SbStudio.App.Gfx;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>The composition view: renders the storyboard at the current time and lets sprites be picked and moved.</summary>
public sealed unsafe partial class ViewerPanel
{
    public const string AssetPayload = "SB_ASSET";

    private readonly EditorApp app;
    private readonly RenderTarget target;

    private float zoom = 1f;
    private Vector2 pan;
    private bool showGuides = true;
    /// <summary>What lies outside the picture shows through faintly. Off, only what a player would see is shown.</summary>
    private bool showOutside = true;
    private bool panning;
    /// <summary>Button that started the pan in progress: middle, or left when Alt was held.</summary>
    private ImGuiMouseButton panButton;
    private bool dragging;
    private Vector2 dragStart;
    private Vector2 spaceDragMouse;
    private SpacePose? spaceAdjustPose;
    private Vector2 dragApplied;
    /// <summary>The press in progress has moved far enough to be a drag. Until then it is a click and moves nothing.</summary>
    private bool dragMoved;
    /// <summary>
    /// One thing of the place that is open, as a click in the picture selects it: a sprite of that place,
    /// or a group of it, which stands for every sprite in it. A group is one thing until it is gone into.
    /// </summary>
    private readonly record struct Pick(SbSprite? Sprite, SbObjectGroup? Group);

    /// <summary>
    /// Pressed on something that was selected already: the thing below it at that point, which becomes the
    /// selection if the mouse does not move. Click after click goes through everything under the mouse.
    /// </summary>
    private Pick? clickNext;
    private bool clearOnClick;
    private TrackRef? previewTrack;
    private bool preserveSelectionOnClick;
    private readonly List<SbSprite> pixelHits = new(), boxHits = new();
    private readonly List<Pick> picks = new();
    /// <summary>
    /// The drag in progress sets the picked property at the playhead instead of moving things: the sprite
    /// or the group it belongs to, and the value the property had when the mouse began to move.
    /// </summary>
    private bool adjusting;
    private SbSprite? adjustSprite;
    private SbObjectGroup? adjustGroup;
    /// <summary>The loop of <see cref="adjustSprite"/> the picked property belongs to; null when it is the sprite's own.</summary>
    private SbGroup? adjustLoop;
    private double[]? adjustStart;

    private static readonly double[] Rates = { 0.25, 0.5, 0.75, 1, 1.5, 2 };

    /// <summary>The music was playing when a drag on the progress bar began, and goes on when it ends.</summary>
    private bool seekWasPlaying;

    private readonly HoverPopup audioPopup = new(), dimPopup = new();

    private static readonly Localized<string> WindowTitle = new(() => L.T("panel.viewer") + "###viewer");

    public bool Open = true;
    /// <summary>The panel had keyboard focus last frame; arrow keys then nudge sprites instead of stepping time.</summary>
    public bool Focused { get; private set; }

    public ViewerPanel(EditorApp app, RenderTarget target)
    {
        this.app = app;
        app.SimulationRequested += OpenSimulationTrigger;
        app.SimulationRuleRequested += AddMatchingSimulationRule;
        this.target = target;
    }

    public void ResetView()
    {
        zoom = 1f;
        pan = Vector2.Zero;
    }

    /// <summary>A command button keeps its current object selected on the next preview interaction.</summary>
    public void KeepCommandSelection()
    {
        previewTrack = app.ActiveTrack;
        preserveSelectionOnClick = previewTrack != null;
    }

    public void Draw()
    {
        if (!Open) return;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4, 4));
        bool visible = ImGui.Begin(WindowTitle.Value, ref Open, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        // A project that is still loading is not shown at all: better nothing than a picture with pieces missing.
        if (!visible || app.Project == null || app.IsLoading)
        {
            ImGui.End();
            return;
        }

        previewSeenFrame = ImGui.GetFrameCount();
        PreviewBounds = new ScreenRect((int)ImGui.GetWindowPos().X, (int)ImGui.GetWindowPos().Y,
            (int)ImGui.GetWindowSize().X, (int)ImGui.GetWindowSize().Y);
        var project = app.Project;
        DrawToolbar(project);

        // As in a video player: the picture, and under it the progress bar and the transport.
        Vector2 imagePos = ImGui.GetCursorScreenPos();
        Vector2 size = ImGui.GetContentRegionAvail();
        var player = MeasurePlayer(size.X);
        size.Y -= player.Height + ImGui.GetStyle().ItemSpacing.Y;
        size = new Vector2(MathF.Max(16, MathF.Floor(size.X)), MathF.Max(16, MathF.Floor(size.Y)));

        DrawPicture(imagePos, size);

        ImGui.SetCursorScreenPos(new Vector2(imagePos.X, imagePos.Y + size.Y + ImGui.GetStyle().ItemSpacing.Y));
        DrawPlayer(player);

        ImGui.End();
        DrawTriggerSimulation();
    }

    /// <summary>The same picture and input path in either preview rectangle.</summary>
    private void DrawPicture(Vector2 imagePos, Vector2 size)
    {
        bool wide = app.Project!.Settings.Widescreen;
        float frameWidth = wide ? 854f : 640f;
        var view = ComputeView(size, frameWidth);

        target.Resize((int)size.X, (int)size.Y);
        // While a video is being exported the picture stays as it was: the renderer and the video decoder
        // are busy with the frames of the export, at other times than this one.
        if (!app.Exporting)
        {
            using (Perf.Measure("render")) app.Renderer.Render(app.Project!, app.Index, app.Time, target, view, app.RenderOptions, app.ShownBeatmap, record: CaptureRecord());
        }

        ImGui.Image(target.ImGuiId, size, new Vector2(0, 1), new Vector2(1, 0));
        ImGui.SetCursorScreenPos(imagePos);
        ImGui.InvisibleButton("##viewer_input", size,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        bool hovered = ImGui.IsItemHovered() && (!Fullscreen || !FullscreenObstructed(ImGui.GetIO().MousePos));
        picturePosition = imagePos;
        pictureSize = size;
        pictureView = view;
        pictureHovered = hovered;

        var io = ImGui.GetIO();
        Vector2 mouseLocal = io.MousePos - imagePos;
        Vector2 sbMouse = view.ToStoryboard(mouseLocal);

        if ((!Fullscreen || app.Settings.FullscreenEditing) && ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload(AssetPayload);
            if (payload.NativePtr != null && AssetsPanel.Dragged is { } asset)
            {
                if (asset.Kind == AssetKind.Audio) app.AddSample(asset.Path);
                else if (asset.Kind == AssetKind.Video) app.AddVideo(asset.Path);
                else if (asset.Kind == AssetKind.Image) app.AddSprite(asset.Path, 320, 240, asset.FrameCount);
                AssetsPanel.Dragged = null;
            }
            ImGui.EndDragDropTarget();
        }

        if (!Fullscreen || app.Settings.FullscreenEditing) HandleInput(io, hovered, mouseLocal, sbMouse, size, frameWidth, ref view);
        else { Focused = false; HandleWatchInput(io, hovered); }
        pictureView = view;
        DrawOverlays(imagePos, size, view, wide, hovered, sbMouse);
        if(app.ImportingPlugins>0) ImGui.GetWindowDrawList().AddText(imagePos+new Vector2(12,12),Theme.TextDim,L.T("plugin.previewImporting",app.ImportingPlugins));
        if (!Fullscreen || app.Settings.FullscreenEditing) DrawContextMenu();
    }

    // ---------------------------------------------------------------- player

    /// <summary>Sizes of what sits under the picture, worked out before the picture so that it can leave the room.</summary>
    private readonly record struct PlayerLayout(float Height, float Button, float Clock, float Buttons, float Volume, float Right, bool OneRow)
    {
        public const float TimeField = 92, Rate = 84;
    }

    private static Vector2 PlayerPadding => new(Theme.Px(8), Theme.Px(5));
    private static float SeekHeight => Theme.Px(14);
    private static float GroupGap => Theme.Px(14);
    private static float CameraGap => Theme.Px(12);

    private string ClockTotal => " / " + Theme.FormatTime(app.PlayRange.End);

    private PlayerLayout MeasurePlayer(float width)
    {
        var style = ImGui.GetStyle();
        float button = Theme.Px(28);
        // As wide as the clock gets, so that nothing shifts while its digits change.
        float clock = MathF.Max(ImGui.CalcTextSize(Theme.FormatTime(app.Time)).X, ImGui.CalcTextSize("-00:00.000").X) + ImGui.CalcTextSize(ClockTotal).X;
        // The seven transport buttons, and the camera a little apart from them.
        float buttons = button * 7 + Theme.Px(2) * 6 + CameraGap + button;
        float volume = ImGui.CalcTextSize(L.T("viewer.volumeFormat", "100")).X + style.ItemSpacing.X + Theme.Px(72);

        // Clock and time field on the left, the buttons in the middle, speed and volume on the right. A
        // panel too narrow for that gets a second row instead of losing any of them.
        float left = clock + style.ItemSpacing.X + Theme.Px(PlayerLayout.TimeField);
        float right = ImGui.CalcTextSize(L.T("viewer.rate")).X + style.ItemSpacing.X + Theme.Px(PlayerLayout.Rate) +
            style.ItemSpacing.X + volume + style.ItemSpacing.X + ImGui.GetFrameHeight() * 2 + style.ItemSpacing.X;
        bool oneRow = left + GroupGap + buttons + GroupGap + right <= width - PlayerPadding.X * 2;
        float height = PlayerPadding.Y * 2 + SeekHeight + style.ItemSpacing.Y + button;
        if (!oneRow) height += style.ItemSpacing.Y + ImGui.GetFrameHeight();
        return new PlayerLayout(height, button, clock, buttons, volume, right, oneRow);
    }

    private void DrawPlayer(in PlayerLayout layout)
    {
        // A window of its own: with the keyboard on it the arrow keys step through time, as they do
        // everywhere but on the picture, where they nudge sprites.
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.PanelDark);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, PlayerPadding);
        bool visible = ImGui.BeginChild("##player", new Vector2(0, layout.Height), ImGuiChildFlags.AlwaysUseWindowPadding,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        if (visible)
        {
            DrawSeekBar();
            // On the dark bar the usual field colour would not show at all.
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Theme.Rgba(38, 38, 42));
            DrawTransport(layout);
            ImGui.PopStyleColor();
            DrawScreenshotMenu();
        }
        ImGui.EndChild();
    }

    /// <summary>The progress bar: click or drag to go to a moment of the map.</summary>
    private void DrawSeekBar()
    {
        var (start, end) = app.PlayRange;
        double span = Math.Max(1, end - start);
        Vector2 pos = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X, height = SeekHeight;
        ImGui.InvisibleButton("##seek", new Vector2(MathF.Max(1, width), height));
        bool active = ImGui.IsItemActive(), lit = active || ImGui.IsItemHovered();

        // The knob's centre travels between the two ends, so it never sticks out of the bar.
        float radius = Theme.Px(6);
        float x0 = pos.X + radius, x1 = MathF.Max(x0 + 1, pos.X + width - radius);
        // On a snap tick like everything done with the mouse, except at the left end: that is the very
        // start, wherever it lies, as with the button for it.
        double along = Math.Clamp((ImGui.GetIO().MousePos.X - x0) / (x1 - x0), 0, 1);
        double under = along <= 0 ? start : app.SnapMs(start + along * span);

        if (ImGui.IsItemActivated())
        {
            seekWasPlaying = app.Playing;
            app.Pause();
        }
        if (active) app.Seek(under);
        if (ImGui.IsItemDeactivated() && seekWasPlaying)
        {
            seekWasPlaying = false;
            app.Play();
        }
        if (lit) Theme.SetTooltip(Theme.FormatTime(under));

        Theme.DrawProgressTrack(ImGui.GetWindowDrawList(), pos, new Vector2(width, height),
            (float)((app.Time - start) / span), lit);
    }

    private void DrawTransport(in PlayerLayout layout)
    {
        var style = ImGui.GetStyle();
        Vector2 origin = ImGui.GetCursorPos();
        float width = ImGui.GetContentRegionAvail().X;
        // Fields are lower than the buttons and sit on their middle line.
        float fieldY = origin.Y + MathF.Round((layout.Button - ImGui.GetFrameHeight()) / 2);
        float left = layout.Clock + style.ItemSpacing.X + Theme.Px(PlayerLayout.TimeField);
        float right = layout.Right;

        // The buttons are in the middle of the bar unless what is beside them needs the room.
        float buttonsX = MathF.Round((width - layout.Buttons) / 2);
        if (layout.OneRow) buttonsX = MathF.Max(left + GroupGap, MathF.Min(buttonsX, width - right - GroupGap - layout.Buttons));
        buttonsX = MathF.Max(0, buttonsX);

        if (layout.OneRow || layout.Clock + GroupGap <= buttonsX)
        {
            ImGui.SetCursorPos(new Vector2(origin.X, fieldY));
            ImGui.AlignTextToFramePadding();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
            ImGui.TextUnformatted(Theme.FormatTime(app.Time));
            ImGui.PopStyleColor();
            ImGui.SameLine(0, 0);
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
            ImGui.TextUnformatted(ClockTotal);
            ImGui.PopStyleColor();
        }

        ImGui.SetCursorPos(new Vector2(origin.X + buttonsX, origin.Y));
        DrawButtons(layout.Button);

        if (layout.OneRow)
        {
            ImGui.SetCursorPos(new Vector2(origin.X + layout.Clock + style.ItemSpacing.X, fieldY));
            DrawTimeField();
            ImGui.SetCursorPos(new Vector2(origin.X + width - right, fieldY));
        }
        else
        {
            ImGui.SetCursorPos(new Vector2(origin.X, origin.Y + layout.Button + style.ItemSpacing.Y));
            DrawTimeField();
            ImGui.SameLine();
            float settingsX = origin.X + width - right;
            if (settingsX > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(settingsX);
        }
        DrawRate();
        ImGui.SameLine();
        DrawVolume(layout.Volume);
        ImGui.SameLine();
        Theme.IconButton("##audioSettings", ImGui.GetFrameHeight(), Theme.DrawAudioSettings, audioPopup.Open);
        if (!audioPopup.Open) Theme.Tooltip(L.T("audio.advancedSettings"));
        DrawAudioPopup();
        ImGui.SameLine();
        if (Theme.IconButton("##fullscreen", ImGui.GetFrameHeight(), Fullscreen ? Theme.DrawExitFullscreen : Theme.DrawFullscreen))
            FullscreenRequested?.Invoke(!Fullscreen);
        fullscreenButton = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2 - ImGui.GetMainViewport().Pos;
        Theme.Tooltip(Fullscreen ? L.T("viewer.fullscreen.exit.tip") : L.T("viewer.fullscreen.tip"));
    }

    private void DrawButtons(float size)
    {
        // Flat, as on a video player: only the button under the mouse has a background.
        ImGui.PushStyleColor(ImGuiCol.Button, 0u);
        float gap = Theme.Px(2);

        if (Theme.IconButton("##tostart", size, Theme.DrawToStart)) app.SeekToStart();
        Theme.Tooltip(L.T("viewer.toStart.tip"));

        // Holding a step button keeps stepping, like holding an arrow key.
        ImGui.PushButtonRepeat(true);
        ImGui.SameLine(0, gap);
        if (Theme.IconButton("##prevkey", size, Theme.DrawPreviousKey)) app.JumpKey(-1);
        Theme.Tooltip(L.T("viewer.previousKey.tip"));
        ImGui.SameLine(0, gap);
        if (Theme.IconButton("##stepback", size, Theme.DrawStepBack)) app.StepTime(-1);
        ImGui.PopButtonRepeat();
        Theme.Tooltip(L.T("viewer.stepBack.tip"));

        ImGui.SameLine(0, gap);
        if (Theme.IconButton("##play", size, app.Playing ? Theme.DrawPause : Theme.DrawPlay)) app.TogglePlay();
        Theme.Tooltip(L.T("viewer.play.tip"));

        ImGui.PushButtonRepeat(true);
        ImGui.SameLine(0, gap);
        if (Theme.IconButton("##stepforward", size, Theme.DrawStepForward)) app.StepTime(1);
        Theme.Tooltip(L.T("viewer.stepForward.tip"));
        ImGui.SameLine(0, gap);
        if (Theme.IconButton("##nextkey", size, Theme.DrawNextKey)) app.JumpKey(1);
        ImGui.PopButtonRepeat();
        Theme.Tooltip(L.T("viewer.nextKey.tip"));

        ImGui.SameLine(0, gap);
        if (Theme.IconButton("##toend", size, Theme.DrawToEnd)) app.SeekSnapped(app.TimeBounds.End);
        Theme.Tooltip(L.T("viewer.toEnd.tip"));

        ImGui.SameLine(0, CameraGap);
        if (Theme.IconButton("##screenshot", size, Theme.DrawCamera)) ImGui.OpenPopup(ScreenshotMenu);
        Theme.Tooltip(L.T("viewer.screenshot.tip"));

        ImGui.PopStyleColor();
    }

    private const string ScreenshotMenu = "screenshot";

    /// <summary>
    /// The format of a screenshot and the sizes it can have. Picking a size asks where to save it and
    /// saves the picture of the current moment there.
    /// </summary>
    private void DrawScreenshotMenu()
    {
        // A set width: the note wraps, and a popup that sizes itself to wrapped text first comes out as a
        // tall column.
        ImGui.SetNextWindowSize(new Vector2(Theme.Px(300), 0));
        if (!ImGui.BeginPopup(ScreenshotMenu)) return;
        var project = app.Project!;

        // The format comes first: it is settled before a size is clicked, and that click is what saves.
        bool jpg = app.Settings.ScreenshotJpg;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("viewer.screenshot.format"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##format", FormatName(jpg)))
        {
            foreach (bool choice in new[] { false, true })
            {
                if (!ImGui.Selectable(FormatName(choice), choice == jpg) || choice == jpg) continue;
                app.Settings.ScreenshotJpg = choice;
                app.Settings.Save();
            }
            ImGui.EndCombo();
        }

        // Only a JPG has a quality to choose; a PNG keeps every pixel as it is.
        if (jpg)
        {
            int quality = app.Settings.JpegQuality;
            ImGui.SetNextItemWidth(-1);
            if (FilledSlider.Int("##jpegquality", ref quality, 1, 100, Theme.NumberFormat("viewer.screenshot.quality", "%d"))) app.Settings.JpegQuality = quality;
            if (ImGui.IsItemDeactivatedAfterEdit()) app.Settings.Save();
        }
        Theme.TextDimmed(L.T("viewer.screenshot.note"));

        ImGui.Separator();
        Theme.TextDimmed(L.T("viewer.screenshot.title"));
        foreach (int height in EditorApp.FrameHeights)
        {
            string label = $"{height}p   {EditorApp.FrameWidth(height, project.Settings.Widescreen)}×{height}";
            if (height == 480) label += "   " + L.T("viewer.screenshot.native");
            if (ImGui.Selectable(label)) SaveScreenshot(project, height);
        }
        ImGui.EndPopup();
    }

    private static string FormatName(bool jpg) => jpg ? "JPG" : "PNG";

    private void SaveScreenshot(Project project, int height)
    {
        // Project name, the date, and the time in the storyboard the picture is of.
        string name = string.IsNullOrEmpty(project.Settings.Name) ? Path.GetFileName(project.Folder) : project.Settings.Name;
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        name = $"{name}_{DateTime.Now:yyyy-MM-dd}_{Math.Round(app.Time):0}";

        string folder = Directory.Exists(app.Settings.ScreenshotFolder) ? app.Settings.ScreenshotFolder : project.Folder;
        bool jpg = app.Settings.ScreenshotJpg;
        string? path = SaveImageDialog.Show(L.T("viewer.screenshot.save"), name, folder,
            L.T(jpg ? "viewer.screenshot.jpg" : "viewer.screenshot.png"), jpg ? "jpg" : "png");
        if (path == null || !app.SaveFrame(path, height)) return;
        app.Settings.ScreenshotFolder = Path.GetDirectoryName(path) ?? "";
        app.Settings.Save();
    }

    private void DrawTimeField()
    {
        int ms = (int)Math.Round(app.Time);
        if (TimeInput.Field(app, "##time", ref ms, Theme.Px(PlayerLayout.TimeField), 0, "%d ms", name: L.T("viewer.currentTime"))) app.Seek(ms);
        Theme.Tooltip(L.T("viewer.currentTime.tip"));
    }

    private void DrawRate()
    {
        // Named in front, like Snap on the timeline: a bare "1×" does not say what it sets.
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("viewer.rate"));
        ImGui.SameLine();
        // Any speed can be typed; the usual ones are in the list behind the arrow.
        float width = Theme.Px(PlayerLayout.Rate);
        string text = rateTyping ?? $"{app.Rate:0.###}";
        ImGui.SetNextItemWidth(width - ImGui.GetFrameHeight());
        ImGui.InputText("##rate", ref text, 12, ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.AutoSelectAll);
        if (ImGui.IsItemActive()) rateTyping = text;
        else if (rateTyping != null)
        {
            string typed = rateTyping;
            rateTyping = null;
            TakeRate(typed);
        }
        Theme.Tooltip(L.T("viewer.rate.tip", PlaybackRate.Min, PlaybackRate.Max));
        ImGui.SameLine(0, 0);
        if (ImGui.BeginCombo("##rateList", "", ImGuiComboFlags.NoPreview | ImGuiComboFlags.PopupAlignLeft))
        {
            foreach (double rate in Rates)
                if (ImGui.Selectable($"{rate:0.##}×", rate == app.Rate)) app.SetRate(rate);
            ImGui.EndCombo();
        }
    }

    private string? rateTyping;

    /// <summary>Takes a typed playback speed, or says why not and keeps the old one.</summary>
    private void TakeRate(string typed)
    {
        string? why = PlaybackRate.Problem(typed, out double rate);
        if (why == null)
        {
            if (rate != app.Rate) app.SetRate(rate);
            return;
        }
        app.ShowMessage(L.T("input.invalid.title"), L.T("input.invalid.text", L.T("viewer.rate"), why, $"{app.Rate:0.###}×"));
    }

    private void DrawVolume(float width)
    {
        float volume = app.Audio.Volume * 100;
        float labelWidth = width - Theme.Px(72) - ImGui.GetStyle().ItemSpacing.X;
        Vector2 labelPos = ImGui.GetCursorPos();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("viewer.volumeFormat", $"{volume:0}"));
        ImGui.SameLine();
        ImGui.SetCursorPosX(labelPos.X + labelWidth + ImGui.GetStyle().ItemSpacing.X);
        if (ProgressSlider.Percent("##volume", ref volume, Theme.Px(72)))
        {
            app.Audio.Volume = volume / 100;
            app.Settings.Volume = app.Audio.Volume;
        }
        if (ImGui.IsItemDeactivated()) app.Settings.Save();
    }

    private void DrawAudioPopup()
        => HoverPopup.Draw("##viewerAudioPopup", audioPopup, 340, 6, false, DrawAdvancedAudioControls);

    private bool DrawAdvancedAudioControls()
    {
        ImGui.SeparatorText(L.T("audio.advancedSettings"));
        return DrawAudioControls();
    }

    private bool DrawDimControls()
    {
        float dim = app.RenderOptions.BackgroundDim * 100;
        ImGui.TextUnformatted(L.T("viewer.dimFormat", $"{dim:0}"));
        if (ProgressSlider.Percent("##dim", ref dim, ImGui.GetContentRegionAvail().X))
            app.RenderOptions.BackgroundDim = dim / 100;
        Theme.Tooltip(L.T("viewer.dim.tip"));
        return false;
    }

    private ViewTransform ComputeView(Vector2 size, float frameWidth)
    {
        if (Fullscreen) return FullscreenControls.Fit(size, frameWidth > 640, app.Settings.FullscreenEditing ? zoom : 1,
            app.Settings.FullscreenEditing ? pan : Vector2.Zero);
        float fit = MathF.Min(size.X / frameWidth, size.Y / 480f) * 0.94f;
        float scale = MathF.Max(0.01f, fit * zoom);
        return new ViewTransform
        {
            Scale = scale,
            Offset = new Vector2(size.X / 2 - 320 * scale, size.Y / 2 - 240 * scale) + pan,
        };
    }

    /// <summary>Why a video of the storyboard is not shown, if one is not.</summary>
    private string? VideoProblem()
    {
        foreach (var video in app.Index.Videos)
        {
            if (video.Hidden || video.Path.Length == 0) continue;
            if (app.Videos.Problem(app.Project!.AssetFullPath(video.Path)) is { } problem) return problem;
        }
        return null;
    }

    /// <summary>
    /// Goes on along the toolbar for its next item, or down to a new line when the item would not fit:
    /// the panel is often narrower than everything the toolbar holds.
    /// </summary>
    private static void NextTool(float width, float gap = -1)
    {
        ImGui.SameLine(0, gap);
        if (ImGui.GetContentRegionAvail().X < width) ImGui.NewLine();
    }

    private static float CheckboxWidth(string label) =>
        ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;

    private void DrawToolbar(Project project)
    {
        if (!Fullscreen || app.Settings.FullscreenEditing)
        {
            if (ImGui.Button(L.T("viewer.fit"))) ResetView();
            Theme.Tooltip(L.T("viewer.fit.tip"));
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            Theme.TextDimmed($"{zoom * 100:0}%");
            NextTool(CheckboxWidth(L.T("viewer.widescreen")), Theme.Px(14));
        }
        bool wide = project.Settings.Widescreen;
        if (ImGui.Checkbox(L.T("viewer.widescreen"), ref wide))
        {
            project.Settings.Widescreen = wide;
            app.MarkSettingsChanged();
        }
        Theme.Tooltip(L.T("viewer.widescreen.tip"));

        NextTool(CheckboxWidth(L.T("viewer.guides")));
        ImGui.Checkbox(L.T("viewer.guides"), ref showGuides);

        if (!Fullscreen)
        {
            NextTool(CheckboxWidth(L.T("viewer.debugInfo")));
            bool debugInfo = app.Settings.ShowDebugInfo;
            if (ImGui.Checkbox(L.T("viewer.debugInfo"), ref debugInfo))
            {
                app.Settings.ShowDebugInfo = debugInfo;
                app.Settings.Save();
            }
        }

        NextTool(CheckboxWidth(L.T("viewer.outside")));
        ImGui.Checkbox(L.T("viewer.outside"), ref showOutside);
        Theme.Tooltip(L.T("viewer.outside.tip"));

        NextTool(CheckboxWidth(L.T("viewer.hitObjects")));
        bool hitObjects = app.RenderOptions.ShowHitObjects;
        if (ImGui.Checkbox(L.T("viewer.hitObjects"), ref hitObjects))
        {
            app.RenderOptions.ShowHitObjects = app.Settings.ShowHitObjects = hitObjects;
            app.Settings.Save();
            if (app.Settings.SeamlessPlaybackRefresh) app.InvalidateHitSoundEvents();
            app.RefreshPlayback();
        }
        Theme.Tooltip(L.T("viewer.hitObjects.tip"));

        string dimLabel = L.T("viewer.dimFormat", $"{app.RenderOptions.BackgroundDim * 100:0}");
        float dimWidth = Theme.ButtonWidth(L.T("viewer.dimFormat", "100"), 140);
        NextTool(dimWidth, Theme.Px(14));
        ImGui.Button(dimLabel + "###backgroundDim", new Vector2(dimWidth, 0));
        HoverPopup.Draw("##viewerDimPopup", dimPopup, 260, 2, true, DrawDimControls);

        string passState = L.T("viewer.statePass"), failState = L.T("viewer.stateFail");
        float stateWidth = MathF.Max(ImGui.CalcTextSize(passState).X, ImGui.CalcTextSize(failState).X) + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2;
        stateWidth = MathF.Max(Theme.Px(104), stateWidth);
        NextTool(stateWidth);
        ImGui.SetNextItemWidth(stateWidth);
        bool simulatedState = app.TriggerSimulation.Stream.HasStateRules;
        int state = (simulatedState ? app.TriggerSimulation.FailStateAt(app.Time) : app.RenderOptions.FailState) ? 1 : 0;
        ImGui.BeginDisabled(simulatedState);
        if (ImGui.Combo("##state", ref state, passState + "\0" + failState + "\0"))
        {
            app.RenderOptions.FailState = state == 1;
            app.RefreshPlayback();
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.DelayShort))
            Theme.SetTooltip(simulatedState ? L.T("viewer.state.simulated.tip") : L.T("viewer.state.tip"));

        DrawSkinSelector();
        DrawDifficulty(project);

        if (app.Textures.PendingCount > 0)
        {
            ImGui.SameLine(0, Theme.Px(14));
            Theme.TextDimmed(L.T("viewer.loading", app.Textures.PendingCount));
        }

        DrawSimulationButton(width => NextTool(width));
        if (Fullscreen)
        {
            NextTool(CheckboxWidth(L.T("viewer.fullscreen.editing")));
            bool editing = app.Settings.FullscreenEditing;
            if (ImGui.Checkbox(L.T("viewer.fullscreen.editing"), ref editing))
            {
                FinishPictureGesture();
                app.Settings.FullscreenEditing = editing;
                app.Settings.Save();
            }
            editingButton = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2 - ImGui.GetMainViewport().Pos;
            Theme.Tooltip(L.T("viewer.fullscreen.editing.tip"));
            NextTool(CheckboxWidth(L.T("viewer.fullscreen.debug")));
            bool debug = app.Settings.FullscreenDebugPanel;
            if (ImGui.Checkbox(L.T("viewer.fullscreen.debug"), ref debug)) SetDebugPanel(debug);
        }

    }

    private void DrawSkinSelector()
    {
        var library = app.HitObjects.SkinLibrary;
        string SkinLabel(PreviewSkin entry) => SkinLibrary.IsBeatmapSkin(entry.Id) ? L.T("viewer.skin.beatmapAudio", library.BeatmapBase(entry.Id))
            : entry.Id is "stable" or "lazer" ? entry.Name : L.T("viewer.skin.custom", entry.Name);
        _ = app.Beatmap; // Update beatmap-local choices after opening a project or changing difficulty.
        string preview = L.T("viewer.skin", SkinLabel(library.Find(app.HitObjects.SkinId)));
        float width = SkinLibrary.IsBeatmapSkin(app.HitObjects.SkinId) ? Math.Max(Theme.Px(190), ImGui.CalcTextSize(preview).X + Theme.Px(36)) : Theme.Px(190);
        // Keep the skin and difficulty selectors together when the toolbar wraps.
        NextTool(width + ImGui.GetStyle().ItemSpacing.X + Theme.Px(230));
        ImGui.SetNextItemWidth(width);
        if (!ImGui.BeginCombo("##previewSkin", preview)) return;
        if (ImGui.IsWindowAppearing()) library.Refresh();
        foreach (var skin in library.Skins)
        {
            string label = SkinLabel(skin);
            if (ImGui.Selectable(label + "##" + skin.Id, skin.Id == app.HitObjects.SkinId)) SelectSkin(skin.Id);
            if (skin.Id.StartsWith("custom:")) Theme.Tooltip(skin.Folder!);
        }
        ImGui.Separator();
        if (ImGui.Selectable(L.T("viewer.skin.import")))
        {
            var result = NativeFileDialogSharp.Dialog.FileOpen("osk", Directory.Exists(library.Root) ? library.Root : null);
            if (result.IsOk) ImportSkin(result.Path);
        }
        if (ImGui.Selectable(L.T("viewer.skin.folder")))
        {
            try
            {
                Directory.CreateDirectory(library.Root);
                var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.ArgumentList.Add(library.Root);
                System.Diagnostics.Process.Start(start);
            }
            catch (Exception ex) { app.ShowMessage(L.T("viewer.skin.failed"), ex.Message); }
        }
        if (ImGui.Selectable(L.T("viewer.skin.refresh")))
        {
            library.Refresh();
            SelectSkin(app.HitObjects.SkinId);
        }
        ImGui.EndCombo();
    }

    private void SelectSkin(string id)
    {
        try
        {
            app.HitObjects.SelectSkin(id);
            app.Settings.PreviewSkin = app.HitObjects.SkinId;
            app.Settings.PreviewBeatmapSkinFallback = app.HitObjects.SkinLibrary.BeatmapFallbackId;
            app.Settings.Save();
            if (app.Settings.SeamlessPlaybackRefresh) app.InvalidateHitSoundEvents();
            app.RefreshPlayback();
        }
        catch (Exception ex) { app.ShowMessage(L.T("viewer.skin.failed"), ex.Message); }
    }

    private bool DrawAudioControls()
    {
        float volume = app.Audio.HitSoundVolume * 100;
        ImGui.TextUnformatted(L.T("viewer.hitSoundVolume", $"{volume:0}"));
        if (ProgressSlider.Percent("##hitSoundVolume", ref volume, ImGui.GetContentRegionAvail().X))
            app.Settings.HitSoundVolume = app.Audio.HitSoundVolume = volume / 100;
        if (ImGui.IsItemDeactivated()) app.Settings.Save();
        Theme.Tooltip(L.T("viewer.hitSoundVolume.tip"));

        float musicVolume = app.Audio.MusicVolume * 100;
        ImGui.TextUnformatted(L.T("viewer.musicVolume", $"{musicVolume:0}"));
        if (ProgressSlider.Percent("##musicVolume", ref musicVolume, ImGui.GetContentRegionAvail().X))
            app.Settings.MusicVolume = app.Audio.MusicVolume = musicVolume / 100;
        if (ImGui.IsItemDeactivated()) app.Settings.Save();
        Theme.Tooltip(L.T("viewer.musicVolume.tip"));

        ImGui.SetNextItemWidth(-1);
        string name = app.Audio.OutputDevices.FirstOrDefault(d => d.Id == app.Audio.OutputDeviceId)?.Name ?? L.T("audio.defaultDevice");
        bool deviceListOpen = ImGui.BeginCombo("##audioOutput", L.T("audio.outputDevice", name));
        if (deviceListOpen)
        {
            if (ImGui.IsWindowAppearing()) app.Audio.UpdateDevices(true);
            string previous = app.Audio.OutputDeviceId;
            if (ImGui.Selectable(L.T("audio.defaultDevice"), app.Audio.OutputDeviceId.Length == 0)) app.Audio.SetOutputDevice("");
            foreach (var device in app.Audio.OutputDevices)
                if (ImGui.Selectable(device.Name + "##" + device.Id, app.Audio.OutputDeviceId == device.Id)) app.Audio.SetOutputDevice(device.Id);
            if (previous != app.Audio.OutputDeviceId)
            {
                app.Settings.AudioOutputDevice = app.Audio.OutputDeviceId;
                app.Settings.Save();
            }
            if (ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
            ImGui.EndCombo();
        }
        Theme.Tooltip(L.T("audio.outputDevice.tip"));
        return deviceListOpen;
    }

    public void ImportSkin(string file)
    {
        try
        {
            var skin = app.HitObjects.SkinLibrary.Import(file);
            SelectSkin(skin.Id);
            app.SetStatus(L.T("viewer.skin.imported", skin.Name));
        }
        catch (Exception ex) { app.ShowMessage(L.T("viewer.skin.failed"), ex.Message); }
    }

    /// <summary>
    /// Which difficulty of the beatmap set is shown: the .osu whose own storyboard is drawn together with
    /// the one all difficulties share. Or none, the shared storyboard alone, which is all there is to
    /// choose while the beatmap has no difficulty yet.
    /// </summary>
    private void DrawDifficulty(Project project)
    {
        var current = project.CurrentDifficulty;
        NextTool(Theme.Px(230));
        float width = MathF.Min(Theme.Px(230), ImGui.GetContentRegionAvail().X);
        ImGui.SetNextItemWidth(width);
        string preview = L.T("viewer.difficulty", current?.File ?? L.T("viewer.sharedOnly"));
        if (ImGui.BeginCombo("##difficulty", preview, ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.Selectable(L.T("viewer.sharedOnly"), current == null)) app.SelectDifficulty("");
            foreach (var difficulty in project.Difficulties.ToArray())
                if (ImGui.Selectable(difficulty.File, difficulty == current)) app.SelectDifficulty(difficulty.File);
            ImGui.EndCombo();
        }
        Theme.Tooltip(current == null ? L.T("viewer.sharedOnly.tip") : L.T("viewer.difficulty.tip", current.File));
    }

    private void HandleInput(ImGuiIOPtr io, bool hovered, Vector2 mouseLocal, Vector2 sbMouse, Vector2 size, float frameWidth, ref ViewTransform view)
    {
        if (previewTrack != app.ActiveTrack)
        {
            previewTrack = app.ActiveTrack;
            preserveSelectionOnClick = previewTrack != null;
        }
        if (hovered && io.MouseWheel != 0)
        {
            zoom = Math.Clamp(zoom * MathF.Pow(1.15f, io.MouseWheel), 0.1f, 32f);
            // Keep the point under the cursor where it is.
            var zoomed = ComputeView(size, frameWidth);
            pan += mouseLocal - zoomed.ToPixels(sbMouse);
            view = ComputeView(size, frameWidth);
        }

        // Alt + left drag pans like the middle button; that press must not pick or deselect anything.
        bool altPress = hovered && io.KeyAlt && !dragging && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if (!panning && (altPress || (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))))
        {
            panning = true;
            panButton = altPress ? ImGuiMouseButton.Left : ImGuiMouseButton.Middle;
        }
        if (panning)
        {
            // Alt only matters on the press: letting go of it mid-drag keeps panning until the button is up.
            if (ImGui.IsMouseDown(panButton))
            {
                pan += io.MouseDelta;
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
            }
            else panning = false;
        }

        if (hovered && !altPress && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var under = SpritesAt(sbMouse);
            // What the sprites there are in the place that is open: themselves, or the group they are in.
            var things = PicksOf(under);
            clickNext = null;
            clearOnClick = false;
            dragMoved = false;
            adjusting = false;
            bool keepSelection = preserveSelectionOnClick;
            preserveSelectionOnClick = false;
            if (!io.KeyCtrl && AdjustmentTarget(out adjustSprite, out adjustGroup))
            {
                // A picked command owns the drag anywhere in the preview. Tiny, zero-scale
                // and transparent images must not lose selection just because they cannot be hit.
                adjusting = true;
                adjustStart = null;
                spaceDragMouse=sbMouse;
                spaceAdjustPose=null;
                int held = things.FindIndex(IsSelected);
                if (!keepSelection)
                {
                    if (held >= 0 && things.Count > 1) clickNext = things[(held + 1) % things.Count];
                    else if (held < 0 && things.Count > 0) clickNext = things[0];
                    else if (things.Count == 0) clearOnClick = true;
                }
                dragging = true;
                dragStart = io.MousePos;
                dragApplied = Vector2.Zero;
            }
            else if (!io.KeyCtrl && (app.Selection.OfType<SbSprite>().Any() || app.SelectedGroup != null))
            {
                // Keep the current editing target until this gesture is known to be a click.
                // A drag may start over another image, or outside the selected image entirely.
                int held = things.FindIndex(IsSelected);
                if (held >= 0 && things.Count > 1) clickNext = things[(held + 1) % things.Count];
                else if (held < 0 && things.Count > 0) clickNext = things[0];
                else if (things.Count == 0) clearOnClick = true;
                app.SelectedCommands.Clear();
                app.SelectedBlock = null;
                dragging = true;
                dragStart = io.MousePos;
                dragApplied = Vector2.Zero;

                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && things.Count > 0)
                {
                    var into = held >= 0 ? things[held].Group : things[0].Group;
                    if (into != null)
                    {
                        app.OpenContainer(into);
                        dragging = false;
                        clickNext = null;
                    }
                }
            }
            else if (things.Count > 0)
            {
                // Something selected under the mouse stays selected, however much lies on top of it: the
                // press may be the start of dragging it. As a plain click it passes the selection on to the
                // thing below, and from the last one back to the first, as in the osu! editor.
                var hit = things[0];
                int held = io.KeyCtrl ? -1 : things.FindIndex(IsSelected);
                if (io.KeyCtrl) Toggle(hit);
                else if (held < 0) SelectOnly(hit);
                else if (things.Count > 1) clickNext = things[(held + 1) % things.Count];
                app.SelectedCommands.Clear();
                // The object as a whole again, not one of its loops.
                app.SelectedBlock = null;
                dragging = held >= 0 || IsSelected(hit);
                dragStart = io.MousePos;
                dragApplied = Vector2.Zero;

                // A double click on a group goes into it, as one on its row does.
                if (!io.KeyCtrl && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    var into = held >= 0 && things[held].Group != null ? things[held].Group : hit.Group;
                    if (into != null)
                    {
                        app.OpenContainer(into);
                        dragging = false;
                        clickNext = null;
                    }
                }
            }
            else if (!io.KeyCtrl)
            {
                app.ClearSelection();
            }
            BeginFreeGesture();
        }

        if (dragging)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                // A hand is never quite still on a click; a few pixels do not make it a drag.
                if (Vector2.Distance(io.MousePos, dragStart) >= Theme.Px(ClickSlop)) dragMoved = true;
                Vector2 total = dragMoved ? (io.MousePos - dragStart) / view.Scale : Vector2.Zero;
                if (io.KeyShift && !(app.ActiveTrack is {} fineTrack && SbInfo.IsEditorOnly(fineTrack.Kind)))
                {
                    // Constrain to the dominant axis.
                    if (MathF.Abs(total.X) >= MathF.Abs(total.Y)) total.Y = 0;
                    else total.X = 0;
                }
                var desired = new Vector2(MathF.Round(total.X), MathF.Round(total.Y));
                if (freeGesture != null) RecordFreeGesture(total);
                else if (adjusting) Adjust(total);
                else MoveSelection(desired - dragApplied);
                dragApplied = desired;
            }
            else
            {
                if (freeGesture != null && dragMoved) RecordFreeGesture(dragApplied, final: true);
                freeGesture = null;
                dragging = false;
                if (!dragMoved && clickNext is { } next) SelectOnly(next);
                else if (!dragMoved && clearOnClick) app.ClearSelection();
                clickNext = null;
                clearOnClick = false;
                adjusting = false;
            }
        }
        else if (hovered && !panning && app.ActiveTrack is { } picked && AdjustmentTarget(out _, out _))
        {
            // The mouse shows which way dragging here counts.
            ImGui.SetMouseCursor(picked.Kind switch
            {
                CommandKind.M or CommandKind.V => ImGuiMouseCursor.ResizeAll,
                CommandKind.MX => ImGuiMouseCursor.ResizeEW,
                _ => ImGuiMouseCursor.ResizeNS,
            });
        }

        Focused = ImGui.IsWindowFocused();
        if (Focused && !io.WantTextInput && !io.KeyCtrl)
        {
            float step = io.KeyShift ? 10 : 1;
            Vector2 nudge = Vector2.Zero;
            if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, true)) nudge.X -= step;
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow, true)) nudge.X += step;
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, true)) nudge.Y -= step;
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, true)) nudge.Y += step;
            if (nudge != Vector2.Zero) MoveSelection(nudge);
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            // The menu is for what is selected under the mouse, also when that lies below other things.
            var things = PicksOf(SpritesAt(sbMouse));
            if (things.Count > 0 && !things.Exists(IsSelected)) SelectOnly(things[0]);
            ImGui.OpenPopup("viewer_context");
        }
    }

    /// <summary>
    /// What a list of sprites comes to in the place that is open, in the same order: a sprite of that place
    /// is itself, one that is in a group of it is that group, once. The list is used again by the next call.
    /// </summary>
    private List<Pick> PicksOf(List<SbSprite> sprites)
    {
        picks.Clear();
        foreach (var sprite in sprites)
        {
            if (!app.ItemOf(sprite, out var group)) continue;
            var pick = group != null ? new Pick(null, group) : new Pick(sprite, null);
            if (!picks.Contains(pick)) picks.Add(pick);
        }
        return picks;
    }

    private bool IsSelected(Pick pick) => pick.Group != null ? app.IsSelected(pick.Group) : app.IsSelected(pick.Sprite!);

    private void SelectOnly(Pick pick)
    {
        if (pick.Group != null) app.SelectGroup(pick.Group);
        else app.SelectOnly(pick.Sprite!);
    }

    private void Toggle(Pick pick)
    {
        if (pick.Group != null) app.ToggleSelection(pick.Group);
        else app.ToggleSelection(pick.Sprite!);
    }

    /// <summary>
    /// Moves what is selected by a distance on the screen: sprites, and groups, which take everything in
    /// them along. A sprite in a group, and a group in a group, have their place in the space of that
    /// group: its turning and scaling, and that of the groups it is in, are undone so that the thing
    /// follows the mouse.
    /// </summary>
    private void MoveSelection(Vector2 delta)
    {
        if (delta == Vector2.Zero) return;
        foreach (var obj in app.Selection)
        {
            if (obj is not SbSprite sprite || sprite.Locked) continue;
            var (dx, dy) = GroupTimeline.ToMemberOffset(sprite.Group, app.Time, delta.X, delta.Y);
            if(sprite.Group!=null && GroupSpace.UsesDepth(sprite.Group) && !GroupSpace.ScreenOffset(sprite.Group,sprite.Group,GroupSpace.Pose(sprite,app.Time),app.Time,delta.X,delta.Y,out dx,out dy)) {app.SetStatus(L.T("space.dragRefused"));continue;}
            var loop = LoopOf(sprite, app.ActiveTrack?.Nodes);
            System.Collections.IList nodes = loop != null ? loop.Commands : sprite.Nodes;
            if (!NudgeKey(nodes, dx, dy, () => AddPickedKey(sprite, loop, app.ActiveTrack!.Value.Kind), loop)) MoveSprite(sprite, dx, dy);
            app.Touch(sprite);
        }
        foreach (var group in app.SelectedGroups)
        {
            var content = app.Index.ContentOf(group);
            if (content.Sprites.Count > 0 && content.Locked == content.Sprites.Count) continue;
            var (dx, dy) = GroupTimeline.ToMemberOffset(group.Parent, app.Time, delta.X, delta.Y);
            if(GroupSpace.UsesDepth(group) && !GroupSpace.ScreenOffset(group,group.Parent,GroupSpace.Pose(group,app.Time),app.Time,delta.X,delta.Y,out dx,out dy)) {app.SetStatus(L.T("space.dragRefused"));continue;}
            // Where it is at this moment alone while its position is the picked property, and otherwise
            // all of it, with every move command.
            bool keyed = app.SelectedGroup == group && NudgeKey(group.Commands, dx, dy, () => app.AddKey(group, app.ActiveTrack!.Value.Kind));
            if (!keyed) group.Offset(dx, dy);
            app.TouchGroup(group);
        }
    }

    /// <summary>
    /// Moves what a list of commands has as position at the playhead, when its position is the picked
    /// property: the keyframe there, which is added if there is none. The rest of the path stays as it is.
    /// </summary>
    /// <returns>False when the position is not what is picked: then the whole path is what moves.</returns>
    /// <param name="loop">The loop the commands belong to, whose times count from the start of the pass the playhead is in.</param>
    private bool NudgeKey(System.Collections.IList nodes, double dx, double dy, Func<EditorApp.KeyResult> addKey, SbGroup? loop = null)
    {
        if (app.ActiveTrack is not { } track || track.Nodes != nodes) return false;
        if (track.Kind is not (CommandKind.M or CommandKind.MX or CommandKind.MY)) return false;
        var result = addKey();
        if (result == EditorApp.KeyResult.NotKeyframes) return false;
        // No keyframe may stand here: nothing moves, and the message says why.
        if (result == EditorApp.KeyResult.Refused) return true;
        // Asked after the keyframe is there: adding it may have put the playhead on the grid, in the next pass.
        var key = app.PlayheadKey(nodes, track.Kind, app.BlockOffset(loop));
        if (key == null) return false;
        var value = key.Value;
        KeyTrack.SetValue(key, track.Kind switch
        {
            CommandKind.M => new[] { value[0] + dx, value[1] + dy },
            CommandKind.MX => new[] { value[0] + dx },
            _ => new[] { value[0] + dy },
        });
        return true;
    }

    // ---------------------------------------------------------------- adjusting a property

    /// <summary>
    /// Whether dragging in the preview adjusts a picked property at all. A colour is picked from a palette
    /// and a parameter is on or off, so neither is dragged; and the property has to be that of the selected
    /// group, of a selected sprite or of one of its loops and triggers (a trigger at its simulated firing).
    /// </summary>
    private bool Adjusts(TrackRef track)
    {
        if (track.Kind is CommandKind.C or CommandKind.P) return false;
        if(app.CameraSelection is {} cameraGroup && cameraGroup.CameraActive) return true;
        if (app.SelectedGroup is { } group && group.Commands == track.Nodes) return true;
        foreach (var obj in app.Selection)
            if (obj is SbSprite sprite && (sprite.Nodes == track.Nodes || sprite.DepthCommands == track.Nodes || LoopOf(sprite, track.Nodes) != null)) return true;
        return false;
    }

    /// <summary>The loop or trigger of a sprite whose commands a list is; null when it is the sprite's own list, or another's.</summary>
    private static SbGroup? LoopOf(SbSprite sprite, System.Collections.IList? nodes)
    {
        if (nodes == null || nodes == sprite.Nodes) return null;
        foreach (var node in sprite.Nodes)
            if (node is SbGroup block and (SbLoop or SbTrigger) && block.Commands == nodes) return block;
        return null;
    }

    /// <summary>
    /// The keyframe at the playhead for the property that is picked, of a sprite or of one of its loops.
    /// A loop only takes one while the playhead is in it: outside of it there is no pass the moment could
    /// belong to, and a keyframe out there would stretch every pass to reach it.
    /// </summary>
    private EditorApp.KeyResult AddPickedKey(SbSprite sprite, SbGroup? block, CommandKind kind)
    {
        // A trigger's commands count from its firing: without a simulated one there is no moment to adjust.
        if (block is SbTrigger trigger && !app.TriggerAnchor(trigger, sprite).Simulated)
        {
            app.ShowMessage(L.T("clash.title"), L.T("props.triggerAdjustRefused"));
            return EditorApp.KeyResult.Refused;
        }
        var loop = block as SbLoop;
        if (loop != null && (app.Time < loop.AbsoluteStart || app.Time > loop.AbsoluteEnd))
        {
            app.ShowMessage(L.T("clash.title"), L.T("loop.outside", Theme.FormatTime(loop.AbsoluteStart), Theme.FormatTime(loop.AbsoluteEnd)));
            return EditorApp.KeyResult.Refused;
        }
        return app.AddKey(sprite, block, kind);
    }

    /// <summary>
    /// The selected owner of the picked property. Editing does not depend on hitting its
    /// rendered pixels: a one-pixel or zero-scale sprite still needs to be adjustable.
    /// </summary>
    private bool AdjustmentTarget(out SbSprite? sprite, out SbObjectGroup? group)
    {
        sprite = null;
        group = null;
        if (app.ActiveTrack is not { } track) return false;
        // A colour is picked from a palette, and a parameter is on or off: neither is dragged.
        if (track.Kind is CommandKind.C or CommandKind.P) return false;
        if(app.CameraSelection is {} cameraGroup && cameraGroup.CameraActive) {group=cameraGroup;return true;}

        if (app.SelectedGroup is { } selectedGroup && selectedGroup.Commands == track.Nodes)
        {
            if (!app.Index.ContentOf(selectedGroup).Sprites.Any(s => !s.Locked && !s.Hidden && s.Timeline.IsActive(app.Time))) return false;
            group = selectedGroup;
            return true;
        }

        foreach (var obj in app.Selection)
        {
            // Its own property, or that of one of its loops.
            if (obj is not SbSprite candidate || (candidate.Nodes != track.Nodes && candidate.DepthCommands != track.Nodes && LoopOf(candidate, track.Nodes) == null)) continue;
            var timeline = candidate.Timeline;
            if (candidate.Locked || candidate.Hidden || !timeline.IsActive(app.Time)) return false;
            sprite = candidate;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Sets the picked property at the playhead from how far the mouse has gone since the press, in
    /// storyboard units. Sensitivities are those of the osu! editor's Design mode: 200 units up for full
    /// opacity, 50 units for one radian. Scale grows with what it was, doubling in 100 units, where the osu!
    /// editor adds 1 in 100 units: that is the same at a scale of 1 and still usable at a scale of 0.03.
    /// </summary>
    private void Adjust(Vector2 total)
    {
        if (!dragMoved || app.ActiveTrack is not { } track) return;
        if(app.Playing && app.Settings.FreeMode && SbInfo.IsEditorOnly(track.Kind)) return;
        var nodes = track.Nodes;
        var kind = track.Kind;

        if (adjustStart == null)
        {
            // The first movement. With no keyframe at the playhead yet, one is put there now: what is
            // adjusted is this moment, and the keyframes around it stay what they are.
            adjustLoop = adjustSprite == null ? null : LoopOf(adjustSprite, nodes);
            bool added = adjustGroup != null
                ? app.AddKey(adjustGroup, kind) == EditorApp.KeyResult.Done
                : adjustSprite != null && AddPickedKey(adjustSprite, adjustLoop, kind) == EditorApp.KeyResult.Done;
            var at = added ? app.PlayheadKey(nodes, kind, app.BlockOffset(adjustLoop)) : null;
            if (at == null)
            {
                // No value can be set here, and a message says why. The drag is over: going on with it would
                // move the object as a whole, which is not what was asked for.
                adjusting = false;
                dragging = false;
                clickNext = null;
                return;
            }
            adjustStart = (double[])at.Value.Clone();
        }

        var key = app.PlayheadKey(nodes, kind, adjustGroup != null ? 0 : app.BlockOffset(adjustLoop));
        if (key == null) return;

        double right = total.X, up = -total.Y;
        if(SbInfo.IsEditorOnly(kind) && kind!=CommandKind.CT) {right=ImGui.GetIO().MousePos.X-dragStart.X;up=dragStart.Y-ImGui.GetIO().MousePos.Y;}
        double dx = Math.Round(total.X), dy = Math.Round(total.Y);
        // A grouped sprite lives in its group's space, and a group in that of the group it is in: the turning
        // and scaling of those are undone so that it follows the mouse.
        var tree=adjustGroup ?? adjustSprite?.Group;
        if(kind is CommandKind.M or CommandKind.MX or CommandKind.MY && tree!=null && GroupSpace.UsesDepth(tree))
        {
            spaceAdjustPose ??= adjustGroup!=null?GroupSpace.Pose(adjustGroup,app.Time):GroupSpace.Pose(adjustSprite!,app.Time);
            var pose=spaceAdjustPose.Value;
            if(!GroupSpace.ScreenOffset(tree,adjustGroup?.Parent ?? adjustSprite?.Group,pose,app.Time,dx,dy,out dx,out dy)) {app.SetStatus(L.T("space.dragRefused"));return;}
        }
        else (dx, dy) = GroupTimeline.ToMemberOffset(adjustGroup != null ? adjustGroup.Parent : adjustSprite?.Group, app.Time, dx, dy);
        double fine=ImGui.GetIO().KeyShift?0.1:1;
        var start = adjustStart;
        double[] value = kind switch
        {
            CommandKind.F => new[] { Math.Round(Math.Clamp(start[0] + up / 200, 0, 1), 3) },
            CommandKind.M => new[] { start[0] + dx, start[1] + dy },
            CommandKind.MX => new[] { start[0] + dx },
            CommandKind.MY => new[] { start[0] + dy },
            CommandKind.S => new[] { Scaled(start[0], up) },
            CommandKind.V => new[] { Scaled(start[0], right), Scaled(start[1], up) },
            CommandKind.Z => new[]{start[0]+up*2*fine},
            CommandKind.RX => new[]{start[0]+up*Math.PI/360*fine},
            CommandKind.RY => new[]{start[0]+right*Math.PI/360*fine},
            CommandKind.CO => new[]{start[0]+right*Math.PI/720},
            CommandKind.CI => new[]{start[0]+up*Math.PI/720},
            CommandKind.CD => new[]{Math.Max(0.01,start[0]-up*2)},
            CommandKind.CF => new[]{Math.Max(0.01,start[0]+up*2)},
            CommandKind.CT => CameraTarget(start,adjustGroup!,right,-up),
            CommandKind.CL => new[]{start[0]+Math.IEEERemainder(Math.Atan2(spaceDragMouse.Y+total.Y-240,spaceDragMouse.X+total.X-320)-Math.Atan2(spaceDragMouse.Y-240,spaceDragMouse.X-320),2*Math.PI)},
            CommandKind.R => new[] { Math.Round(start[0] + up / 50, 5) },
            _ => (double[])start.Clone(),
        };
        if (!value.AsSpan().SequenceEqual(key.Value))
        {
            KeyTrack.SetValue(key, value);
            if (adjustGroup != null) app.TouchGroup(adjustGroup);
            else app.Touch(adjustSprite);
        }
        Theme.SetTooltip(Theme.KindLabel(kind) + "   " + ValueText(kind, value));
    }

    private double[] CameraTarget(double[] start,SbObjectGroup group,double dx,double dy)
    {
        var c=group.Camera!.Evaluate(app.Time);var delta=c.Rotation.Apply(new V3(-dx,-dy,0))*(c.Distance/c.Zoom);
        return new[]{start[0]+delta.X,start[1]+delta.Y,start[2]+delta.Z};
    }
    private static double Scaled(double start, double by) =>
        Math.Round(Math.Max(0, start > 0 ? start * (1 + by / 100) : by / 100), 4);

    private string ValueText(CommandKind kind, double[] value) => kind switch
    {
        CommandKind.F => $"{value[0]:0.###}",
        CommandKind.M => $"{value[0]:0.#}, {value[1]:0.#}",
        CommandKind.MX or CommandKind.MY => $"{value[0]:0.#}",
        CommandKind.S => $"{value[0]:0.####}",
        CommandKind.V => $"{value[0]:0.####}, {value[1]:0.####}",
        _ => app.Settings.RotationInDegrees ? $"{value[0] * 180 / Math.PI:0.#}°" : $"{value[0]:0.####} rad",
    };

    /// <summary>Offsets the declared position and every move command, so the whole path shifts.</summary>
    public static void MoveSprite(SbSprite sprite, double dx, double dy)
    {
        sprite.X += dx;
        sprite.Y += dy;
        foreach (var c in sprite.AllCommands())
        {
            switch (c.Kind)
            {
                case CommandKind.M:
                    c.From[0] += dx; c.From[1] += dy;
                    c.To[0] += dx; c.To[1] += dy;
                    break;
                case CommandKind.MX:
                    c.From[0] += dx; c.To[0] += dx;
                    break;
                case CommandKind.MY:
                    c.From[0] += dy; c.To[0] += dy;
                    break;
            }
        }
    }

    /// <summary>How far, in pixels, the mouse may stray during a press that is still a click.</summary>
    private const float ClickSlop = 3;

    /// <summary>
    /// The visible, unlocked sprites under a storyboard-space point, the front-most first. They are the ones
    /// with visible pixels there; where there is none of those, the ones whose bounding box holds the point.
    /// The list is used again by the next call.
    /// </summary>
    private List<SbSprite> SpritesAt(Vector2 point)
    {
        pixelHits.Clear();
        boxHits.Clear();
        if (!showOutside)
        {
            // What is hidden cannot be picked.
            var (left, right) = FrameEdges(app.Project!.Settings.Widescreen);
            if (point.X < left || point.X > right || point.Y < 0 || point.Y > 480) return pixelHits;
        }

        // In the order they are drawn in, from the front: in each layer what is in a group is in front of
        // what is in none, and the index has it so.
        var index = app.Index;
        Span<Vector2> corners = stackalloc Vector2[4];
        for (int layer = SbInfo.LayerCount - 1; layer >= 0; layer--)
        {
            bool fail = app.TriggerSimulation.Stream.HasStateRules ? app.TriggerSimulation.FailStateAt(app.Time) : app.RenderOptions.FailState;
            if (layer == (int)SbLayer.Fail && !fail) continue;
            if (layer == (int)SbLayer.Pass && fail) continue;
            var drawn = index.Layers[layer];
            for (int i = drawn.Count - 1; i >= 0; i--)
            {
                var sprite = drawn[i];
                if (sprite.Hidden || sprite.Locked || !drawn.IsActive(i, app.Time)) continue;
                var timeline = sprite.Timeline;
                // While a group is open, only what is in it can be picked.
                if (!app.ItemOf(sprite, out _)) continue;
                var state = app.PreviewState(sprite, app.Time);
                if (state.Opacity <= 0.01) continue;
                var texture = app.Textures.Get(StoryboardRenderer.FrameFile(sprite, timeline, app.Time));
                if (texture == null) continue;
                SpriteGeometry.Corners(state, sprite.Origin, texture.Width, texture.Height, corners);
                if (!SpriteGeometry.Contains(corners, point)) continue;
                var uv = SpriteGeometry.ToImageSpace(corners, point, SpriteGeometry.MirrorX(state), SpriteGeometry.MirrorY(state));
                if (texture.IsOpaqueAt(uv.X, uv.Y)) pixelHits.Add(sprite);
                else boxHits.Add(sprite);
            }
        }
        return pixelHits.Count > 0 ? pixelHits : boxHits;
    }

    /// <summary>Left and right edge of the picture in storyboard coordinates. It always runs from 0 to 480 downwards.</summary>
    private static (float Left, float Right) FrameEdges(bool wide) => wide ? (-107, 747) : (0, 640);

    private void DrawOverlays(Vector2 imagePos, Vector2 size, ViewTransform view, bool wide, bool hovered, Vector2 sbMouse)
    {
        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(imagePos, imagePos + size, true);

        Vector2 Screen(float x, float y) => imagePos + view.ToPixels(new Vector2(x, y));

        var (left, right) = FrameEdges(wide);
        Vector2 fMin = Screen(left, 0), fMax = Screen(right, 480);

        // Everything outside the visible frame is dimmed, or covered up when only the picture is wanted.
        uint mask = Theme.Rgba(20, 20, 22, showOutside ? 190 : 255);
        Vector2 pMax = imagePos + size;
        dl.AddRectFilled(imagePos, new Vector2(pMax.X, fMin.Y), mask);
        dl.AddRectFilled(new Vector2(imagePos.X, fMax.Y), pMax, mask);
        dl.AddRectFilled(new Vector2(imagePos.X, fMin.Y), new Vector2(fMin.X, fMax.Y), mask);
        dl.AddRectFilled(new Vector2(fMax.X, fMin.Y), new Vector2(pMax.X, fMax.Y), mask);

        if (showGuides)
        {
            uint guide = Theme.Rgba(255, 255, 255, 38);
            if (wide)
            {
                dl.AddLine(Screen(0, 0), Screen(0, 480), guide);
                dl.AddLine(Screen(640, 0), Screen(640, 480), guide);
            }
            // Play area (where hit objects appear).
            var playfieldMin = OsuBeatmap.ToStoryboard(Vector2.Zero);
            var playfieldMax = OsuBeatmap.ToStoryboard(new Vector2(512, 384));
            dl.AddRect(Screen(playfieldMin.X, playfieldMin.Y), Screen(playfieldMax.X, playfieldMax.Y), Theme.Rgba(120, 220, 140, 45));
            dl.AddLine(Screen(320, 232), Screen(320, 248), guide);
            dl.AddLine(Screen(312, 240), Screen(328, 240), guide);
        }

        if (!Fullscreen || app.Settings.FullscreenEditing)
        {
            foreach (var obj in app.Selection)
            {
                if (obj is SbSprite sprite)
                    DrawOutline(dl, imagePos, view, sprite, obj == app.Primary ? Theme.Accent : Theme.Rgba(140, 190, 255, 200), 1.5f, true);
            }

            foreach (var group in app.SelectedGroups)
            {
                // A selected group: everything in it, and the pivot it turns and scales around, where the
                // groups it is in have carried that to.
                if (app.Settings.OutlineScope == GroupOutlineScope.Members)
                {
                    foreach (var member in app.Index.ContentOf(group).Sprites)
                        DrawOutline(dl, imagePos, view, member, Theme.WithAlpha(Theme.GroupColor, 0.85f), 1.2f, false);
                }
                else
                {
                    var bounds = GroupOutlineBounds.Compute(app.Index.ContentOf(group).Sprites, app.Time,
                        app.RenderOptions, ImageSize);
                    if (bounds is { } box)
                        dl.AddRect(imagePos + view.ToPixels(box.Min), imagePos + view.ToPixels(box.Max),
                            Theme.WithAlpha(Theme.GroupColor, 0.85f), 0, ImDrawFlags.None, 1.2f);
                }

                var state = group.WorldState(app.Time);
                Vector2 pivot = Screen((float)state.X, (float)state.Y);
                float arm = 9;
                Vector2 axis = new(MathF.Cos((float)state.Rotation), MathF.Sin((float)state.Rotation));
                Vector2 normal = new(-axis.Y, axis.X);
                dl.AddLine(pivot - axis * arm, pivot + axis * arm * 1.8f, Theme.GroupColor, 2f);
                dl.AddLine(pivot - normal * arm, pivot + normal * arm, Theme.GroupColor, 2f);
                dl.AddCircle(pivot, 5f, Theme.GroupColor, 16, 2f);
                dl.AddText(pivot + new Vector2(10, 8), Theme.GroupColor, group.Name);
            }
        }

        if (!Fullscreen) DrawPreviewInfo(dl, imagePos, size, hovered, sbMouse);

        float noteY = 6 + (Fullscreen && controls.Alpha > 0 ? fullscreenTop : 0);
        // In full screen the notes start beside the debug panel, which would cover them.
        float noteX = 8 + (Fullscreen && app.Settings.FullscreenDebugPanel ? DebugWidth : 0);
        // Inside a group: which one, since only what is in it can be picked now, and the way back out.
        if (app.OpenGroup is { } open)
        {
            string where = L.T("viewer.insideGroup", open.Name.Length > 0 ? open.Name : L.T("group.unnamed"));
            dl.AddText(imagePos + new Vector2(noteX + 1, noteY + 1), Theme.Rgba(0, 0, 0, 200), where);
            dl.AddText(imagePos + new Vector2(noteX, noteY), Theme.GroupColor, where);
            noteY += ImGui.GetTextLineHeightWithSpacing();
        }
        // A video the beatmap has but the preview cannot show must not go unnoticed: written across the picture.
        if (app.RenderOptions.ShowVideo && VideoProblem() is { } problem)
        {
            dl.AddText(imagePos + new Vector2(noteX, noteY), Theme.TextError, L.T("viewer.videoProblem", problem));
            dl.AddText(imagePos + new Vector2(noteX, noteY + ImGui.GetTextLineHeightWithSpacing()), Theme.Rgba(255, 255, 255, 150), L.T("viewer.videoProblem.tip"));
            noteY += ImGui.GetTextLineHeightWithSpacing() * 2;
        }
        // The same for hit objects that are switched on and have nothing to show.
        if (app.RenderOptions.ShowHitObjects && app.BeatmapProblem is { } why)
        {
            // With a shadow: the picture under it may be as light as the text.
            string note = L.T("viewer.hitObjectsProblem", why);
            dl.AddText(imagePos + new Vector2(noteX + 1, noteY + 1), Theme.Rgba(0, 0, 0, 200), note);
            dl.AddText(imagePos + new Vector2(noteX, noteY), Theme.Rgba(235, 200, 90), note);
        }

        if (app.Doc.Objects.Count == 0)
        {
            string hint = L.T("viewer.emptyHint");
            Vector2 textSize = ImGui.CalcTextSize(hint);
            dl.AddText(imagePos + (size - textSize) / 2, Theme.Rgba(255, 255, 255, 150), hint);
        }

        dl.PopClipRect();
    }

    private void DrawPreviewInfo(ImDrawListPtr dl, Vector2 imagePos, Vector2 size, bool hovered, Vector2 sbMouse)
    {
        if (!app.Settings.ShowDebugInfo && !(app.ActiveTrack is { } track && Adjusts(track))) return;
        uint dim = Theme.Rgba(225, 225, 230, 210);
        var items = new List<(string Text, uint Colour, float MinWidth)>();
        if (app.Settings.ShowDebugInfo)
        {
            items.Add(($"{ImGui.GetIO().Framerate:0} FPS", dim, ImGui.CalcTextSize("8888 FPS").X));
            items.Add((L.T("viewer.info", app.Renderer.VisibleSprites, app.Batch.DrawCalls), dim, 0));
        }
        // Editing guidance remains available when performance figures are hidden.
        if (app.ActiveTrack is { } picked && Adjusts(picked))
            items.Add((L.T("viewer.adjust", Theme.KindLabel(picked.Kind)), Theme.Accent, 0));
        if (app.Settings.ShowDebugInfo && hovered)
            items.Add((L.T("viewer.cursor", sbMouse.X, sbMouse.Y), dim, 0));

        float gap = ImGui.CalcTextSize("   ").X, available = MathF.Max(1, size.X - Theme.Px(16));
        float lineHeight = ImGui.GetTextLineHeightWithSpacing(), x = 0;
        int row = 0;
        var positions = new Vector2[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            float width = MathF.Max(items[i].MinWidth, ImGui.CalcTextSize(items[i].Text).X);
            if (x > 0 && x + width > available) { x = 0; row++; }
            positions[i] = new Vector2(x, row * lineHeight);
            x += width + gap;
        }
        Vector2 origin = imagePos + new Vector2(Theme.Px(8), size.Y - ImGui.GetFontSize() - Theme.Px(6) - row * lineHeight);
        for (int i = 0; i < items.Count; i++)
        {
            Vector2 at = origin + positions[i];
            dl.AddText(at + Vector2.One, Theme.Rgba(0, 0, 0, 210), items[i].Text);
            dl.AddText(at, items[i].Colour, items[i].Text);
        }
    }

    private (int Width, int Height)? ImageSize(SbSprite sprite)
    {
        var texture = app.Textures.Get(StoryboardRenderer.FrameFile(sprite, sprite.Timeline, app.Time));
        return texture == null ? null : (texture.Width, texture.Height);
    }

    /// <summary>Frame of a sprite as it is shown now, with a dot on its origin.</summary>
    private void DrawOutline(ImDrawListPtr dl, Vector2 imagePos, ViewTransform view, SbSprite sprite, uint color, float thickness, bool reportMissing)
    {
        var timeline = sprite.Timeline;
        if (!timeline.IsActive(app.Time)) return;
        var state = app.PreviewState(sprite, app.Time);
        string file = StoryboardRenderer.FrameFile(sprite, timeline, app.Time);
        var texture = app.Textures.Get(file);
        bool missing = texture == null;
        if (missing && reportMissing) color = Theme.TextError;

        Span<Vector2> corners = stackalloc Vector2[4];
        SpriteGeometry.Corners(state, sprite.Origin, texture?.Width ?? 48, texture?.Height ?? 48, corners);
        for (int i = 0; i < 4; i++)
            dl.AddLine(imagePos + view.ToPixels(corners[i]), imagePos + view.ToPixels(corners[(i + 1) % 4]), color, thickness);

        Vector2 origin = imagePos + view.ToPixels(new Vector2((float)state.X, (float)state.Y));
        dl.AddCircleFilled(origin, 3.5f, color);
        dl.AddCircle(origin, 5.5f, Theme.Rgba(0, 0, 0, 160));
        if (missing && reportMissing && app.Textures.IsUnavailable(file))
            dl.AddText(imagePos + view.ToPixels(corners[0]) + new Vector2(4, 2), Theme.TextError, L.T("viewer.missing", file));
    }

    private void DrawContextMenu()
    {
        if (!ImGui.BeginPopup("viewer_context")) return;
        Menus.ObjectItems(app);
        ImGui.Separator();
        if (ImGui.MenuItem(L.T("viewer.fitWindow"))) ResetView();
        ImGui.EndPopup();
    }
}
