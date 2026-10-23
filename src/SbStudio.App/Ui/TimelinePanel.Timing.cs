using System.Numerics;
using System.Globalization;
using ImGuiNET;
using SbStudio.App.Audio;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class TimelinePanel
{
    private static readonly uint TimingColour = Theme.Rgba(235, 180, 80);
    private static readonly uint TimingOffsetColour = Theme.Rgba(245, 80, 80);
    private TimingEventIndex timingIndex = TimingEventIndex.Empty;
    private bool rowsTiming;
    private TimingBeat? auditionBeat;
    private double timingAuditionAt = double.NegativeInfinity;
    private const string AddTimingPopupId = "###addTimingOffset";
    private bool requestAddTiming;
    private Project? timingEditProject;
    private string newTimingOffset = "0", newTimingBpm = "120";
    private int newTimingSignature, newTimingMeter = 5, newTimingBpmDecimals = 3;
    private enum TimingField { Bpm, Offset }
    // One typed value at a time: the field, which of the two copies (toolbar or side) shows the input, and its text.
    private Project? timingValueProject;
    private TimingPoint? timingEditPoint;
    private TimingField timingEditField;
    private string timingEditText = "";
    private bool timingEditToolbar, timingEditFocus;
    private int timingEditFrame;
    public event Action? TimingListRequested;
    internal Action<string> WriteTimingClipboard { get; set; } = ImGui.SetClipboardText;
    internal string CopiedTimingCode { get; private set; } = "";

    /// <summary>Space between the toolbar's groups, the same everywhere (that beside the calculator).</summary>
    internal static float ToolbarGap => Theme.Px(8);

    private void DrawTimingToolbarControls()
    {
        ImGui.PushID("timingToolbar");
        float height = ImGui.GetFrameHeight(), gap = ToolbarGap;
        float Text(string label) => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
        // BPM, calculator, Offset, divisor, previous/next red line, snap, clicks on every snap line, add/delete,
        // Timing list, BPM analysis, copy: each group is drawn without padding at its edges, so the same gap separates all of
        // them. The calculator wraps together with BPM, and the add/delete button with the box before it, so
        // neither starts a line on its own.
        float Check(string label) => height + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;
        float[] widths = { Theme.Px(222) - height, height, Theme.Px(172), DivisorComboWidth, height * 2 + Theme.Px(4),
            Check(L.T("timeline.snapOn")), Check(L.T("timing.snapSound")), TimingToggleWidth, Text(L.T("timing.list")), Text(L.T("timing.analysis")), Text(L.T("timing.copy")) };
        for (int group = 0; group < widths.Length; group++)
        {
            float width = widths[group];
            float needed = group is 0 or 6 ? width + gap + widths[group + 1] : width;
            if (group is not (1 or 7) && ImGui.GetContentRegionAvail().X < needed) ImGui.NewLine();
            Vector2 pos = ImGui.GetCursorScreenPos(), max = pos + new Vector2(width, height);
            float textY = pos.Y + (height - ImGui.GetFontSize()) / 2;
            switch (group)
            {
                case 0:
                    DrawTimingLabel(ImGui.GetWindowDrawList(), pos, max, textY, ImGui.IsMouseHoveringRect(pos, max), toolbar: true);
                    break;
                case 1:
                    if (UiScriptTargets.Item("calculator.open", Theme.IconButton("##timingCalculator", height, DrawCalculatorIcon))) OpenTimingCalculator();
                    if (ImGui.IsItemHovered()) Theme.SetTooltip(L.T("timing.calculateBpm") + "\n(C)");
                    break;
                case 2: DrawTimingOffsetControls(ImGui.GetWindowDrawList(), pos, max, textY, toolbar: true); break;
                case 3:
                    ImGui.SetCursorScreenPos(pos);
                    DrawDivisorCombo();
                    break;
                case 4: DrawTimingJumps(pos); break;
                case 5:
                    ImGui.SetCursorScreenPos(pos);
                    DrawSnapCheckbox();
                    break;
                case 6:
                    ImGui.SetCursorScreenPos(pos);
                    bool snapSound = app.Settings.TimingSnapSound;
                    if (ImGui.Checkbox(L.T("timing.snapSound"), ref snapSound))
                    {
                        app.SetTimingSnapSound(snapSound);
                        app.Settings.Save();
                    }
                    Theme.Tooltip(L.T("timing.snapSound.tip") + Environment.NewLine + L.T("timing.snapSound.hold"));
                    break;
                case 7: DrawTimingToggle(pos, width, height); break;
                case 8:
                    if (ImGui.Button(L.T("timing.list") + "###timingList", new(width, height))) TimingListRequested?.Invoke();
                    Theme.Tooltip(L.T("timing.list") + " (F6)");
                    break;
                case 9:
                    ImGui.BeginDisabled(app.PreviewingEdit);
                    if (ImGui.Button(L.T("timing.analysis") + "###timingAnalysis", new(width, height))) OpenTimingAnalysis();
                    ImGui.EndDisabled();
                    Theme.Tooltip(L.T("timing.analysis.tip"));
                    break;
                default:
                    ImGui.BeginDisabled(!app.HasTiming);
                    if (ImGui.Button(L.T("timing.copy") + "###copyTiming", new(width, height))) CopyTiming(app.Project!.Settings.Timing);
                    ImGui.EndDisabled();
                    Theme.Tooltip(L.T("timing.copy.tip"));
                    break;
            }
            ImGui.SetCursorScreenPos(pos);
            ImGui.Dummy(new(width, height));
            if (group < widths.Length - 1) ImGui.SameLine(0, gap);
        }
        ImGui.PopID();
    }

    /// <summary>Puts red lines on the clipboard as osu! [TimingPoints] code; the toolbar and the Timing list share it.</summary>
    internal void CopyTiming(IReadOnlyList<TimingPoint> timing)
    {
        CopiedTimingCode = TimingCode.Format(timing);
        WriteTimingClipboard(CopiedTimingCode);
        app.SetStatus(L.T("timing.copied", timing.Count));
    }

    // Wide enough for either label, so the groups after it do not move when it switches.
    private static float TimingToggleWidth => Math.Max(ImGui.CalcTextSize(L.T("timing.addOffset")).X,
        ImGui.CalcTextSize(L.T("timing.deleteOffset.short")).X) + ImGui.GetStyle().FramePadding.X * 2;

    /// <summary>Adds a red line at the playhead, or deletes the one exactly there.</summary>
    private void DrawTimingToggle(Vector2 pos, float width, float height)
    {
        var aligned = app.TimingOffsetAtPlayhead;
        string label = L.T(aligned == null ? "timing.addOffset" : "timing.deleteOffset.short");
        ImGui.SetCursorScreenPos(pos);
        if (ImGui.Button(label + "###toggleTimingOffset", new(width, height))) ToggleTimingOffset();
        Theme.Tooltip((aligned == null ? L.T("timing.addOffset")
            : L.T("timing.deleteOffset") + $"\n{aligned.Offset:0.###} ms · {aligned.Bpm:0.##} BPM") + "\n(R)");
    }

    /// <summary>What the add/delete red line button does; R does the same while Timing check is on.</summary>
    internal void ToggleTimingOffset()
    {
        var aligned = app.TimingOffsetAtPlayhead;
        if (aligned != null)
        {
            app.DeleteTimingOffset(aligned);
            rowsStale = true;
        }
        else
        {
            timingEditProject = app.Project;
            newTimingOffset = TimingPointEditing.CurrentOffset(app.Time).ToString(CultureInfo.InvariantCulture);
            newTimingBpm = FormatNewTimingBpm(app.HasTiming ? 60000 / app.BeatLength : 120);
            int meter = app.CurrentTimingOffset?.Meter ?? 4;
            newTimingSignature = meter == 3 ? 0 : meter == 4 ? 1 : 2;
            newTimingMeter = meter is 3 or 4 ? 5 : meter;
            requestAddTiming = true;
        }
    }

    /// <summary>Jumps to the previous or next red line.</summary>
    private void DrawTimingJumps(Vector2 pos)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Theme.Px(4), 0));
        ImGui.SetCursorScreenPos(pos);
        foreach (int direction in new[] { -1, 1 })
        {
            if (direction > 0) ImGui.SameLine();
            double? target = app.TimingOffsetTarget(direction);
            ImGui.BeginDisabled(target == null);
            if (Theme.IconButton(direction < 0 ? "##previousTimingOffset" : "##nextTimingOffset", ImGui.GetFrameHeight(),
                (dl, centre, size, colour) => DrawTimingJumpIcon(dl, centre, size, colour, direction)))
            {
                app.JumpTimingOffset(direction);
                playheadToFront = true;
            }
            ImGui.EndDisabled();
            string tip = L.T(direction < 0 ? "timing.previousOffset" : "timing.nextOffset");
            if (target is { } offset) tip += $"\n{offset:0.###} ms";
            Theme.Tooltip(tip);
        }
        ImGui.PopStyleVar();
    }

    private void DrawAddTimingPopup()
    {
        string title = L.T("timing.addOffset") + AddTimingPopupId;
        if (requestAddTiming)
        {
            requestAddTiming = false;
            ImGui.OpenPopup(title);
        }
        var viewport = ImGui.GetMainViewport();
        if (!MainUi.BeginModal(title, viewport.Pos + viewport.Size / 2, 360)) return;
        if (app.Project != timingEditProject)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        ImGui.SetNextItemWidth(Theme.Px(210));
        ImGui.InputText("BPM", ref newTimingBpm, 32);
        // As in the BPM analysis: whole BPMs or 1 to 3 decimals; choosing one rounds the BPM typed so far.
        ImGui.SetNextItemWidth(Theme.Px(210));
        if (ImGui.BeginCombo(L.T("timing.analysis.bpmStep") + "##newTimingBpmDecimals", BpmDecimalsLabel(newTimingBpmDecimals)))
        {
            for (int decimals = 0; decimals <= 3; decimals++)
                if (ImGui.Selectable(BpmDecimalsLabel(decimals), decimals == newTimingBpmDecimals) && decimals != newTimingBpmDecimals)
                {
                    newTimingBpmDecimals = decimals;
                    if (double.TryParse(newTimingBpm.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out double typed) && double.IsFinite(typed) && typed > 0)
                        newTimingBpm = FormatNewTimingBpm(typed);
                }
            ImGui.EndCombo();
        }
        Theme.Tooltip(L.T("timing.addOffset.bpmStep.tip"));
        ImGui.SetNextItemWidth(Theme.Px(210));
        ImGui.InputText(L.T("settings.timing.offset") + " (ms)", ref newTimingOffset, 32);
        if (ImGui.Button(L.T("simulation.current") + "###timingUseCurrentTime"))
            newTimingOffset = TimingPointEditing.CurrentOffset(app.Time).ToString(CultureInfo.InvariantCulture);
        ImGui.SeparatorText(L.T("timing.timeSignature"));
        if (ImGui.RadioButton("3 / 4", newTimingSignature == 0)) newTimingSignature = 0;
        if (ImGui.RadioButton("4 / 4", newTimingSignature == 1)) newTimingSignature = 1;
        if (ImGui.RadioButton(L.T("timing.otherSignature"), newTimingSignature == 2)) newTimingSignature = 2;
        ImGui.SameLine();
        ImGui.BeginDisabled(newTimingSignature != 2);
        Theme.Stepper("timingMeter", ref newTimingMeter, 1, 16, Theme.Px(50));
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextUnformatted("/ 4");
        int meter = newTimingSignature == 0 ? 3 : newTimingSignature == 1 ? 4 : newTimingMeter;
        string? error = TimingPointEditing.ValidateInput(app.Project!.Settings.Timing, newTimingOffset, newTimingBpm, meter,
            out int offset, out double bpm);
        if (error == null && DecimalPlaces(newTimingBpm) > newTimingBpmDecimals) error = "timing.bpmPrecisionN";
        if (error != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            ImGui.TextWrapped(error switch
            {
                "timing.invalidOffset" => L.T("timing.invalidOffset"),
                "timing.invalidBpm" => L.T("timing.invalidBpm"),
                "timing.bpmPrecision" => L.T("timing.bpmPrecision"),
                "timing.bpmPrecisionN" => L.T("timing.bpmPrecisionN", newTimingBpmDecimals),
                "timing.invalidMeter" => L.T("timing.invalidMeter"),
                _ => L.T("timing.duplicateOffset"),
            });
            ImGui.PopStyleColor();
        }
        ImGui.Separator();
        ImGui.BeginDisabled(error != null);
        if (Theme.Button(L.T("common.add"), 110) && app.AddTimingOffset(offset, bpm, meter))
        {
            rowsStale = true;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (Theme.Button(L.T("common.cancel"), 110) || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private string FormatNewTimingBpm(double bpm)
    {
        double step = Math.Pow(10, -newTimingBpmDecimals);
        return Math.Clamp(Math.Round(bpm, newTimingBpmDecimals), step, 1000)
            .ToString(newTimingBpmDecimals == 0 ? "0" : "0." + new string('#', newTimingBpmDecimals), CultureInfo.InvariantCulture);
    }

    private static int DecimalPlaces(string text)
    {
        string trimmed = text.Trim().TrimEnd('0');
        int dot = trimmed.IndexOf('.');
        return dot < 0 ? 0 : trimmed.Length - dot - 1;
    }

    private static void DrawTimingJumpIcon(ImDrawListPtr dl, Vector2 centre, float size, uint colour, int direction)
    {
        float half = size * 0.24f, bar = centre.X + direction * size * 0.25f;
        uint red = Theme.WithAlpha(TimingOffsetColour, ImGui.GetStyle().Alpha);
        dl.AddLine(new(bar, centre.Y - half), new(bar, centre.Y + half), red, Theme.Px(2));
        Vector2 tip = centre + new Vector2(direction * size * 0.09f, 0);
        Vector2 back = centre - new Vector2(direction * size * 0.21f, 0);
        if (direction > 0) dl.AddTriangleFilled(back - new Vector2(0, half), tip, back + new Vector2(0, half), colour);
        else dl.AddTriangleFilled(tip, back - new Vector2(0, half), back + new Vector2(0, half), colour);
    }

    private void DrawTimingOffsets(ImDrawListPtr dl, float top, float bottom, float right, bool ruler = false)
    {
        if (!app.Settings.ShowTimingRecheck || !app.HasTiming) return;
        foreach (var point in app.Project!.Settings.Timing)
        {
            if (!double.IsFinite(point.Offset)) continue;
            float x = MathF.Round(X(point.Offset));
            if (x < trackX || x > right) continue;
            dl.AddLine(new(x, top), new(x, bottom), TimingOffsetColour, Theme.Px(1.5f));
            if (ruler)
            {
                // A fixed diamond distinguishes timing-section offsets from the moving playhead.
                Vector2 centre = new(x, top + Theme.Px(22));
                float radius = Theme.Px(4);
                dl.AddQuadFilled(centre - new Vector2(0, radius), centre + new Vector2(radius, 0),
                    centre + new Vector2(0, radius), centre - new Vector2(radius, 0), TimingOffsetColour);
            }
        }
    }

    private TimingPoint? TimingOffsetUnderMouse(float mouseX)
    {
        TimingPoint? closest = null;
        float distance = Theme.Px(6);
        if (!app.Settings.ShowTimingRecheck || !app.HasTiming) return null;
        foreach (var point in app.Project!.Settings.Timing)
        {
            if (!double.IsFinite(point.Offset)) continue;
            float delta = Math.Abs(X(point.Offset) - mouseX);
            if (delta > distance) continue;
            closest = point;
            distance = delta;
        }
        return closest;
    }

    private void UpdateTimingRows()
    {
        bool enabled = app.Settings.ShowTimingRecheck;
        var snapshot = enabled ? app.TimingRecheckEvents : TimingEventIndex.Empty;
        if (snapshot == timingIndex && enabled == rowsTiming) return;
        rowsTiming = enabled;
        timingIndex = snapshot;
        auditionBeat = null;
        rowsStale = true;
    }

    private float TimingFlash(TimingBeat beat)
    {
        if (!app.Settings.TimingBrightnessFeedback || app.TimingLaneHidden) return 0;
        float playback = app.Playing && app.Time >= beat.Time
            ? Math.Clamp(1 - (float)((app.Time - beat.Time) / (250 * app.Rate)), 0, 1) : 0;
        float click = auditionBeat == beat ? Math.Clamp(1 - (float)((ImGui.GetTime() - timingAuditionAt) / 0.25), 0, 1) : 0;
        return Math.Max(playback, click);
    }

    private void DrawTimingLabel(ImDrawListPtr dl, Vector2 pos, Vector2 max, float textY, bool hovered, bool toolbar = false)
    {
        // No playback flash here: the markers in the track on the right show the beats and snap lines.
        if (hovered) dl.AddRectFilled(pos, max, Theme.RowAlt);
        if (!toolbar) DrawEye(dl, new(pos.X + 14, (pos.Y + max.Y) / 2), !app.TimingLaneHidden);
        uint colour = app.TimingLaneHidden ? Theme.TextDim : ImGui.GetColorU32(ImGuiCol.Text);
        float buttonSize = ImGui.GetFrameHeight(), buttonX = max.X - buttonSize - Theme.Px(toolbar ? 0 : 8);
        float adjustX = pos.X + (toolbar ? 0 : Theme.Px(54));
        if (!toolbar && max.X - pos.X >= Theme.Px(250))
        {
            dl.AddText(new(adjustX, textY), colour, "BPM");
            adjustX += ImGui.CalcTextSize("Timing").X + Theme.Px(6);
        }
        var point = TimingControlPoint;
        string bpm = point != null ? $"{point.Bpm:0.###} BPM" : app.HasTiming ? $"{60000 / app.BeatLength:0.###} BPM" : "— BPM";
        float bpmX = adjustX + buttonSize + Theme.Px(4);
        float plusX = buttonX;
        ImGui.BeginDisabled(point == null || app.PreviewingEdit);
        ImGui.SetCursorScreenPos(new(adjustX, (pos.Y + max.Y - buttonSize) / 2));
        if (ImGui.Button("-##decreaseTimingBpm", new(buttonSize, buttonSize))) app.AdjustTimingBpm(point, -1, ImGui.GetIO().KeyShift);
        Theme.Tooltip(L.T("timing.adjustBpm.tip", "-0.1", "-1"));
        bool adjustmentHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        float bpmRight = Math.Max(bpmX, plusX - Theme.Px(4));
        bool bpmHovered = DrawTimingValue(dl, new(bpmX, (pos.Y + max.Y - buttonSize) / 2),
            bpmRight - bpmX, buttonSize, textY, colour, bpm, toolbar, TimingField.Bpm);
        ImGui.SetCursorScreenPos(new(plusX, (pos.Y + max.Y - buttonSize) / 2));
        if (ImGui.Button("+##increaseTimingBpm", new(buttonSize, buttonSize))) app.AdjustTimingBpm(point, 1, ImGui.GetIO().KeyShift);
        Theme.Tooltip(L.T("timing.adjustBpm.tip", "+0.1", "+1"));
        adjustmentHovered |= ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        ImGui.EndDisabled();
        if (hovered && !adjustmentHovered && !bpmHovered)
        {
            bool eye = !toolbar && ImGui.GetIO().MousePos.X < pos.X + 26;
            if (eye && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                app.SetTimingLaneHidden(!app.TimingLaneHidden);
                app.Settings.Save();
            }
            Theme.SetTooltip(eye ? L.T("timing.laneVisibility.tip") : L.T("timing.readOnly"));
        }
    }

    /// <summary>A BPM or Offset value; double-click types it in. Returns whether the mouse is over it.</summary>
    private bool DrawTimingValue(ImDrawListPtr dl, Vector2 pos, float width, float height,
        float textY, uint colour, string text, bool toolbar, TimingField field)
    {
        if (timingEditPoint != null && (timingValueProject != app.Project || TimingControlPoint != timingEditPoint
            || app.PreviewingEdit || ImGui.GetFrameCount() - timingEditFrame > 1)) timingEditPoint = null;
        ImGui.SetCursorScreenPos(pos);
        width = Math.Max(1, width);
        if (timingEditPoint != null && timingEditToolbar == toolbar && timingEditField == field)
        {
            timingEditFrame = ImGui.GetFrameCount();
            ImGui.SetNextItemWidth(width);
            if (timingEditFocus) { ImGui.SetKeyboardFocusHere(); timingEditFocus = false; }
            bool entered = ImGui.InputText(field == TimingField.Bpm ? "##timingBpmInput" : "##timingOffsetInput", ref timingEditText, 64,
                ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
            bool hovered = ImGui.IsItemHovered();
            bool cancelled = ImGui.IsKeyPressed(ImGuiKey.Escape);
            if (cancelled || entered || ImGui.IsItemDeactivated())
            {
                var edited = timingEditPoint;
                timingEditPoint = null;
                if (!cancelled) CommitTimingValue(edited, field, timingEditText);
            }
            return hovered;
        }
        dl.PushClipRect(pos, pos + new Vector2(width, height), true);
        dl.AddText(new(Math.Max(pos.X, pos.X + (width - ImGui.CalcTextSize(text).X) / 2), textY), colour, text);
        dl.PopClipRect();
        ImGui.InvisibleButton(field == TimingField.Bpm ? "##timingBpmValue" : "##timingOffsetValue", new(width, height));
        bool valueHovered = ImGui.IsItemHovered();
        if (valueHovered && TimingControlPoint is { } point && !app.PreviewingEdit)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.TextInput);
            Theme.SetTooltip(L.T(field == TimingField.Bpm ? "timing.editBpm.tip" : "timing.editOffset.tip"));
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                timingValueProject = app.Project;
                timingEditPoint = point;
                timingEditField = field;
                timingEditToolbar = toolbar;
                timingEditText = (field == TimingField.Bpm ? point.Bpm : point.Offset).ToString("0.###", CultureInfo.InvariantCulture);
                timingEditFocus = true;
                timingEditFrame = ImGui.GetFrameCount();
            }
        }
        return valueHovered;
    }

    private void CommitTimingValue(TimingPoint point, TimingField field, string text)
    {
        if (field == TimingField.Bpm)
        {
            string? error = TimingPointEditing.ValidateBpmInput(text, out double bpm);
            if (error != null)
                app.ShowMessage(L.T("input.invalid.title"), L.T("input.invalid.text", "BPM",
                    error == "timing.bpmPrecision" ? L.T("timing.bpmPrecision") : L.T("timing.invalidBpm"),
                    point.Bpm.ToString("0.###", CultureInfo.InvariantCulture)));
            else if (app.SetTimingBpm(point, bpm)) rowsStale = true;
            return;
        }
        double time = app.Time;
        if (app.SetTimingOffset(point, text, out string? offsetError, out string neighbours))
        {
            // As with the -/+ buttons: a red line moved past the paused playhead stays the one being edited.
            offsetAdjustedPoint = point;
            offsetAdjustedProject = app.Project;
            offsetAdjustedTime = time;
            rowsStale = true;
        }
        else if (offsetError != null)
            app.ShowMessage(L.T("input.invalid.title"), L.T("input.invalid.text", "Offset",
                offsetError == "timing.offsetNeighbours" ? L.T("timing.offsetNeighbours", neighbours) : L.T("timing.invalidOffset"),
                point.Offset.ToString("0.###", CultureInfo.InvariantCulture) + " ms"));
    }

    private void DrawTimingTrack(ImDrawListPtr dl, float y, float height, float right)
    {
        if (app.TimingLaneHidden) return;
        if (timingIndex.Events.Length == 0)
        {
            dl.AddText(new(trackX + 8, y + (height - ImGui.GetFontSize()) / 2), Theme.TextDim, L.T("timing.empty"));
            return;
        }
        double from = TimeAt(trackX - 4), until = TimeAt(right + 4);
        int previousBucket = int.MinValue;
        for (int i = timingIndex.FirstAtOrAfter(from); i < timingIndex.Events.Length; i++)
        {
            var beat = timingIndex.Events[i];
            if (beat.Time > until) break;
            float x = MathF.Round(X(beat.Time));
            float flash = TimingFlash(beat);
            int bucket = (int)(x / 3);
            if (bucket == previousBucket && flash <= 0) continue;
            previousBucket = bucket;
            // Snap lines between beats: thinner, shorter, in the ruler's colour for their divisor, without a label.
            float inset = beat.Subdivision ? 0.25f : 0.075f;
            float top = y + height * inset, bottom = y + height * (1 - inset), halfWidth = Theme.Px(beat.Subdivision ? 1.25f : 2.25f);
            if (flash > 0)
            {
                dl.AddRectFilled(new(x - halfWidth - 4, top - 1), new(x + halfWidth + 4, bottom + 1),
                    Theme.WithAlpha(TimingColour, flash * 0.65f), 3);
                dl.AddRect(new(x - halfWidth - 1, top - 1), new(x + halfWidth + 1, bottom + 1),
                    Theme.Rgba(255, 255, 255, (byte)(flash * 230)), 1);
            }
            var colour = Vector4.Lerp(ImGui.ColorConvertU32ToFloat4(beat.Subdivision ? Theme.SnapColor(beat.Divisor) : TimingColour),
                Vector4.One, flash * 0.9f);
            colour.W = Math.Max(beat.Measure ? 1 : beat.Subdivision ? 0.8f : 0.65f, flash);
            dl.AddRectFilled(new(x - halfWidth, top), new(x + halfWidth, bottom), ImGui.ColorConvertFloat4ToU32(colour), 1);
            if (beat.Subdivision) continue;
            string bpm = $"{beat.Bpm:0.##} BPM";
            // Only where it fits before the next marker, snap lines included: every marker stays visible.
            float limit = i + 1 < timingIndex.Events.Length ? Math.Min(right - 4, X(timingIndex.Events[i + 1].Time) - 5) : right - 4;
            if (x >= trackX && x + 6 + ImGui.CalcTextSize(bpm).X <= limit)
                dl.AddText(new(x + 6, y + (height - ImGui.GetFontSize()) / 2), TimingColour, bpm);
        }
    }

    private void HandleTimingInput(float mouseX)
    {
        if (app.TimingLaneHidden) return;
        double time = TimeAt(mouseX), radius = 6 / pxPerMs;
        int next = timingIndex.FirstAtOrAfter(time);
        int closest = next;
        if (next > 0 && (next == timingIndex.Events.Length || time - timingIndex.Events[next - 1].Time < timingIndex.Events[next].Time - time)) closest--;
        if (closest < 0 || closest >= timingIndex.Events.Length) return;
        var beat = timingIndex.Events[closest];
        if (Math.Abs(beat.Time - time) > radius) return;
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            auditionBeat = beat;
            timingAuditionAt = ImGui.GetTime();
            if (!app.Settings.OsuStyleTimelineIndicator && !app.Playing) app.Seek(beat.Time);
            if (app.Settings.TimingAssistSound) app.Audio.Audition(new[] { beat.Sample });
        }
        ImGui.BeginTooltip();
        ImGui.TextDisabled(L.T("timing.readOnly"));
        ImGui.Separator();
        ImGui.TextUnformatted($"{Theme.FormatTime(beat.Time)} · {beat.Bpm:0.##} BPM");
        ImGui.TextUnformatted(beat.Subdivision ? L.T("timing.snapLine", $"1/{beat.Divisor}") : L.T(beat.Measure ? "timing.measure" : "timing.beat"));
        ImGui.EndTooltip();
    }
}
