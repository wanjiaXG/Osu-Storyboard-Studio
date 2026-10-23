using System.Numerics;
using ImGuiNET;
using SbStudio.App.Gfx;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class TimelinePanel
{
    private OsuBeatmap? referenceMap;
    private TimelineHitObjectIndex referenceObjects = TimelineHitObjectIndex.Empty;
    private bool rowsHaveMusic, waveformScrubbing;
    private int rowsWaveformChannels = 1;
    private TimelineWaveformStyle rowsWaveformStyle;
    private bool ShowReferenceObjects => app.Settings.ShowHitSoundRecheck || app.Settings.ShowTimingRecheck;
    private string ReferenceReadOnlyHint => L.T(app.Settings.OsuStyleTimelineIndicator ? "timeline.referenceFixedTip" : "timeline.referenceTip");

    private void UpdateReferenceRows()
    {
        var map = app.Beatmap; // independent of the preview's hit-object visibility switch
        if (map != referenceMap)
        {
            referenceMap = map;
            referenceObjects = app.HitSoundRecheckSupported ? new(map) : TimelineHitObjectIndex.Empty;
            hitObjectSelection.Reset(referenceObjects);
            hitObjectSelectionFocused = false;
            doubleClickSelection = null;
            hitObjectMarqueePending = hitObjectMarqueeActive = false;
            hitObjectClickSeek = null;
            LastOsuEditLink = null;
            rowsStale = true;
        }
        if (rowsHaveMusic != app.Audio.HasMusic)
        {
            rowsHaveMusic = app.Audio.HasMusic;
            rowsStale = true;
        }
        int channels = app.Audio.Waveform?.Detail?.ChannelCount ?? 1;
        if (rowsWaveformChannels != channels || rowsWaveformStyle != app.Settings.WaveformStyle)
        {
            rowsWaveformChannels = channels;
            rowsWaveformStyle = app.Settings.WaveformStyle;
            rowsStale = true;
        }
    }
    private void AddReferenceRows()
    {
        if (rowsHaveMusic)
        {
            sectionCount[WaveformSection] = 1;
            rows.Add(new Row { Kind = RowKind.LayerHeader, Layer = WaveformSection });
            if (!collapsed[WaveformSection]) rows.Add(new Row { Kind = RowKind.Waveform, Layer = WaveformSection });
        }
        if (ShowReferenceObjects && referenceMap != null)
        {
            sectionCount[HitObjectsSection] = referenceObjects.Objects.Length;
            rows.Add(new Row { Kind = RowKind.LayerHeader, Layer = HitObjectsSection });
            if (!collapsed[HitObjectsSection]) rows.Add(new Row { Kind = RowKind.HitObjects, Layer = HitObjectsSection });
        }
        if (app.Settings.ShowTimingRecheck)
        {
            sectionCount[TimingSection] = app.Project?.Settings.Timing.Count ?? 0;
            rows.Add(new Row { Kind = RowKind.LayerHeader, Layer = TimingSection });
            if (!collapsed[TimingSection])
            {
                rows.Add(new Row { Kind = RowKind.TimingLane, Layer = TimingSection });
                rows.Add(new Row { Kind = RowKind.TimingControls, Layer = TimingSection });
            }
        }
    }
    private void DrawReferenceLabel(ImDrawListPtr dl, Row row, Vector2 pos, Vector2 max, float textY, bool hovered)
    {
        string label = row.Kind == RowKind.Waveform ? L.T("timeline.audio", Path.GetFileName(app.Project!.Settings.Audio))
            : app.HitSoundRecheckProblem ?? app.Project!.CurrentDifficulty?.Name ?? L.T("timeline.hitObjectLayer");
        if (hovered) dl.AddRectFilled(pos, max, Theme.RowAlt);
        dl.PushClipRect(pos + new Vector2(26, 0), max - new Vector2(5, 0), true);
        dl.AddText(new(pos.X + 30, textY - ImGui.GetFontSize() / 2), Theme.TextDim, label);
        dl.AddText(new(pos.X + 30, textY + ImGui.GetFontSize() / 2 + 2), Theme.TextDim, L.T("timeline.referenceReadOnly"));
        dl.PopClipRect();
        if (hovered) Theme.SetTooltip(label + "\n" + (row.Kind == RowKind.HitObjects ? app.HitSoundRecheckProblem ?? L.T("timeline.hitObject.selectionTip") : ReferenceReadOnlyHint));
    }
    private void DrawReferenceTrack(ImDrawListPtr dl, Row row, float y, float height, float right)
    {
        bool folded = row.Kind == RowKind.LayerHeader;
        if (folded) return;
        if (row.Layer == WaveformSection)
        {
            if (!folded) DrawWaveform(dl, y, height);
            return;
        }
        if (referenceMap == null) return;
        if (referenceObjects.Objects.Length == 0)
        {
            if (!folded) dl.AddText(new(trackX + 8, y + (height - ImGui.GetFontSize()) / 2), Theme.TextDim, app.HitSoundRecheckProblem ?? L.T("timeline.hitObjectEmpty"));
            return;
        }
        var renderer = app.HitObjects;
        if (AppSettings.ReadOnly)
        {
            var entry = referenceObjects.Objects[0];
            var geometry = HitObjectGeometry(entry, 0, y, height);
            if (X(entry.Object.StartTime) >= trackX && X(entry.Object.StartTime) <= right)
                UiScriptTargets.Position("timeline.firstHitObject", new(X(entry.Object.StartTime), geometry.Centre));
        }
        float diameter = folded ? Theme.Px(10) : Theme.Px(renderer.TimelineLazerStyle ? 32 : 38);
        double from = TimeAt(trackX - diameter), until = TimeAt(right + diameter);
        int first = referenceObjects.FirstVisible(from), last = referenceObjects.FirstAfter(until) - 1;
        dl.PushClipRect(new(trackX, y), new(right, y + height), true);
        if (referenceMap.Mode is 0 or 2 && !renderer.TimelineLazerStyle && !folded)
        {
            DrawLegacyObjectTrack(dl, y, height, right, first, last);
            DrawHitObjectSelection(dl, y, height, right, first, last);
            dl.PopClipRect();
            return;
        }
        // Stable draws earlier circles above later circles; retain that legibility in dense passages.
        for (int i = last; i >= first; i--)
        {
            var entry = referenceObjects.Objects[i];
            var obj = entry.Object;
            if (obj.EndTime < from) continue;
            Vector3 tint = renderer.TimelineColour(referenceMap, obj);
            float opacity = 1;
            uint colour = ImGui.ColorConvertFloat4ToU32(new Vector4(tint, opacity));
            float radius = diameter / 2;
            float centreY = y + height / 2 + (folded ? 0 : Theme.Px(2));
            if (!folded) centreY -= Math.Min(Theme.Px((renderer.TimelineLazerStyle ? 5 : 2) * entry.Stack), centreY - y - radius - 1);
            float start = X(obj.StartTime), end = X(obj.EndTime);
            if (end > start)
            {
                float left = Math.Max(trackX - radius, start), limit = Math.Min(right + radius, end);
                uint fill = Theme.WithAlpha(colour, folded ? .4f : renderer.TimelineLazerStyle ? .85f : .5f);
                uint tailColour = colour;
                if (renderer.TimelineLazerStyle && !folded)
                {
                    var lighter = Vector3.Lerp(tint, Vector3.One, .4f);
                    // The gradient is tied to source time, so scrolling into a long object doesn't restart it.
                    float startMix = Math.Clamp((left - start) / (end - start), 0, 1), endMix = Math.Clamp((limit - start) / (end - start), 0, 1);
                    uint a = ImGui.ColorConvertFloat4ToU32(new Vector4(Vector3.Lerp(tint, lighter, startMix), opacity));
                    uint b = ImGui.ColorConvertFloat4ToU32(new Vector4(Vector3.Lerp(tint, lighter, endMix), opacity));
                    dl.AddRectFilledMultiColor(new(left, centreY - radius), new(limit, centreY + radius), a, b, b, a);
                    tailColour = ImGui.ColorConvertFloat4ToU32(new Vector4(lighter, opacity));
                }
                else dl.AddRectFilled(new(left, centreY - radius), new(limit, centreY + radius), fill);
                dl.AddLine(new(left, centreY - radius), new(limit, centreY - radius), Theme.Rgba(210, 210, 215, (int)(opacity * 200)), 1.5f);
                dl.AddLine(new(left, centreY + radius), new(limit, centreY + radius), Theme.Rgba(210, 210, 215, (int)(opacity * 200)), 1.5f);
                DrawTimelineCircle(dl, referenceMap, obj, new(end, centreY), diameter, tailColour, false, folded);
                if (!folded && obj is Slider slider)
                    for (int repeat = 1; repeat < slider.SpanCount; repeat++)
                    {
                        float x = X(slider.StartTime + slider.SpanDuration * repeat);
                        if (x < trackX - radius || x > right + radius) continue;
                        if (renderer.TimelineLazerStyle) dl.AddCircleFilled(new(x, centreY), Theme.Px(4), Theme.Rgba(255, 255, 255, (int)(opacity * 210)));
                        else
                        {
                            DrawTimelineCircle(dl, referenceMap, obj, new(x, centreY), diameter, colour, false, false);
                            var arrow = renderer.TimelineImage("reversearrow");
                            if (arrow != null) TimelineImage(dl, arrow, new(x, centreY), new(diameter * .4f), Theme.Rgba(255, 255, 255, (int)(opacity * 255)));
                        }
                    }
            }
            DrawTimelineCircle(dl, referenceMap, obj, new(start, centreY), diameter, colour, true, folded);
        }
        if (!folded && referenceMap.Mode is 0 or 2) DrawObjectTicks(dl, y, height, right);
        DrawHitObjectSelection(dl, y, height, right, first, last);
        dl.PopClipRect();
    }
    private void DrawTimelineCircle(ImDrawListPtr dl, OsuBeatmap map, HitObject obj, Vector2 centre, float diameter, uint colour, bool number, bool folded)
    {
        var renderer = app.HitObjects;
        uint white = Theme.Rgba(255, 255, 255, (int)((colour >> 24) & 255));
        if (folded || renderer.TimelineLazerStyle)
        {
            dl.AddCircleFilled(centre, diameter / 2, colour, 32);
            dl.AddCircle(centre, diameter / 2, folded ? white : Theme.Rgba(191, 191, 191), 32, folded ? 1 : Theme.Px(4));
            if (number && !folded && map.Mode is 0 or 2)
            {
                string text = (obj.IndexInCurrentCombo + 1).ToString();
                dl.AddText(centre - ImGui.CalcTextSize(text) / 2, white, text);
            }
            return;
        }
        string imageName = map.Mode switch { 1 => "taikohitcircle", 3 => renderer.TimelineNoteImage(map, obj), _ => "hitcircle" };
        var circle = renderer.TimelineImage(imageName);
        if (circle == null) dl.AddCircleFilled(centre, diameter / 2, colour, 32);
        else
        {
            Vector2 size = map.Mode == 3 ? new(diameter, diameter * circle.Height / circle.Width) : new(diameter);
            TimelineImage(dl, circle, centre, size, colour);
        }
        var overlay = map.Mode is 0 or 1 or 2 ? renderer.TimelineImage(map.Mode == 1 ? "taikohitcircleoverlay" : "hitcircleoverlay") : null;
        bool above = renderer.TimelineOverlayAboveNumber;
        if (!above && overlay != null) TimelineImage(dl, overlay, centre, new(diameter), white);
        if (number && map.Mode is 0 or 2) DrawTimelineNumber(dl, obj.IndexInCurrentCombo + 1, centre, diameter, white);
        if (above && overlay != null) TimelineImage(dl, overlay, centre, new(diameter), white);
    }
    private static void TimelineImage(ImDrawListPtr dl, Texture texture, Vector2 centre, Vector2 size, uint colour) =>
        dl.AddImage(texture.ImGuiId, centre - size / 2, centre + size / 2, Vector2.Zero, Vector2.One, colour);

    private void DrawTimelineNumber(ImDrawListPtr dl, int number, Vector2 centre, float diameter, uint colour, float unitOverride = 0)
    {
        var renderer = app.HitObjects;
        string text = number.ToString();
        var glyphs = text.Select(c =>
        {
            string key = "digit:" + (c - '0');
            return (Texture: renderer.TimelineImage(key), Size: renderer.TimelineImageSize(key));
        }).ToArray();
        if (glyphs.Any(g => g.Texture == null))
        {
            dl.AddText(centre - ImGui.CalcTextSize(text) / 2, colour, text);
            return;
        }
        float unit = unitOverride > 0 ? unitOverride : diameter * .48f / glyphs[0].Size.Y;
        float overlap = renderer.TimelineNumberOverlap * unit;
        float width = glyphs.Sum(g => g.Size.X * unit) - (glyphs.Length - 1) * overlap;
        float x = centre.X - width / 2;
        foreach (var glyph in glyphs)
        {
            Vector2 size = glyph.Size * unit;
            TimelineImage(dl, glyph.Texture!, new(x + size.X / 2, centre.Y), size, colour);
            x += size.X - overlap;
        }
    }

    private void HandleReferenceInput(Row row, ImGuiIOPtr io, float y, float height)
    {
        if (row.Layer == WaveformSection)
        {
            if (!app.Settings.OsuStyleTimelineIndicator && row.Kind == RowKind.Waveform && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                BeginTimelineScrub();
                waveformScrubbing = true;
                app.SeekSnapped(ScrubTimeAt(io.MousePos.X));
            }
            Theme.SetTooltip(ReferenceReadOnlyHint + "\n" + Theme.FormatTime(app.SnapMs(waveformScrubbing ? ScrubTimeAt(io.MousePos.X) : TimeAt(io.MousePos.X))));
            return;
        }
        if (row.Kind == RowKind.LayerHeader)
        {
            return;
        }
        if (!app.HitSoundRecheckSupported)
        {
            Theme.SetTooltip(app.HitSoundRecheckProblem!);
            return;
        }
        var hit = HitReferenceObject(io.MousePos, y, height, out double seek);
        SelectReferenceObject(hit, seek, io);
        if (hit == null) return;
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
        ImGui.TextDisabled(L.T("timeline.hitObject.selectionTip"));
        var item = hit.Object;
        string kind = item switch { Slider => L.T("timeline.hitObject.slider"), Spinner => L.T("timeline.hitObject.spinner"), HoldNote => L.T("timeline.hitObject.hold"), _ => L.T("timeline.hitObject.circle") };
        ImGui.TextUnformatted($"#{hit.Index + 1} · {kind} · {Theme.FormatTime(item.StartTime)}");
        if (item.EndTime > item.StartTime) ImGui.TextUnformatted(L.T("timeline.hitObject.end", Theme.FormatTime(item.EndTime), item.EndTime - item.StartTime));
        if (referenceMap?.Mode == 3) ImGui.TextUnformatted(L.T("timeline.hitObject.column", referenceMap.ColumnOf(item) + 1));
        else if (referenceMap?.Mode is 0 or 2) ImGui.TextUnformatted(L.T("timeline.hitObject.combo", item.IndexInCurrentCombo + 1));
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
    private void UpdateWaveformScrub(ImGuiIOPtr io)
    {
        if (!waveformScrubbing) return;
        if (!app.Settings.OsuStyleTimelineIndicator && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            scrubbing = true;
            app.SeekSnapped(ScrubTimeAt(io.MousePos.X));
        }
        else
        {
            waveformScrubbing = scrubbing = false;
            if (scrubWasPlaying) { scrubWasPlaying = false; app.Play(); }
        }
    }
}
