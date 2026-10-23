using System.Numerics;
using ImGuiNET;
using SbStudio.App.Audio;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class TimelinePanel
{
    private HitSoundEventIndex recheckIndex = HitSoundEventIndex.Empty;
    private bool rowsRecheck;
    private HitSoundEvent[] auditionHits = Array.Empty<HitSoundEvent>();
    private double auditionAt = double.NegativeInfinity;
    internal string RecheckState => $"supported={app.HitSoundRecheckSupported}\nobjects={referenceObjects.Objects.Length}\nsounds={recheckIndex.Events.Length}\nproblem={app.HitSoundRecheckProblem}\n";

    private void UpdateHitSoundRows()
    {
        bool enabled = app.Settings.ShowHitSoundRecheck;
        var snapshot = enabled ? app.HitSoundRecheckEvents : HitSoundEventIndex.Empty;
        if (snapshot == recheckIndex && enabled == rowsRecheck) return;
        rowsRecheck = enabled;
        recheckIndex = snapshot;
        auditionHits = Array.Empty<HitSoundEvent>();
        rowsStale = true;
    }

    private static uint HitSoundBankColour(int bank) => bank switch
    {
        2 => Theme.Rgba(235, 180, 80),
        3 => Theme.Rgba(240, 115, 100),
        _ => Theme.Rgba(120, 170, 215)
    };
    private static string HitSoundLaneName(int lane) => lane switch
    {
        0 => "Hit", 1 => "Whistle", 2 => "Finish", 3 => "Clap", _ => L.T("hitsound.custom")
    };
    private string HitSoundReadOnlyHint => app.HitSoundRecheckProblem ?? L.T(app.Settings.OsuStyleTimelineIndicator ? "hitsound.readOnlyFixed" : "hitsound.readOnly");

    private float HitSoundClickFlash => app.Settings.HitSoundBrightnessFeedback
        ? Math.Clamp(1 - (float)((ImGui.GetTime() - auditionAt) / 0.25), 0, 1) : 0;
    private float HitSoundFlash(HitSoundEvent sound)
    {
        if (!app.Settings.HitSoundBrightnessFeedback) return 0;
        float playback = app.Playing && app.Time >= sound.Time ? Math.Clamp(1 - (float)((app.Time - sound.Time) / (250 * app.Rate)), 0, 1) : 0;
        return Math.Max(playback, Array.IndexOf(auditionHits, sound) >= 0 ? HitSoundClickFlash : 0);
    }

    private void DrawHitSoundLabel(ImDrawListPtr dl, int lane, Vector2 pos, Vector2 max, float textY, bool hovered)
    {
        if (hovered) dl.AddRectFilled(pos, max, Theme.RowAlt);
        float flash = 0;
        uint bankColour = 0;
        if (app.Settings.HitSoundBrightnessFeedback && app.Playing && !app.IsHitSoundLaneHidden(lane))
        {
            var events = recheckIndex.Lanes[lane];
            int index = HitSoundEventIndex.FirstAtOrAfter(events, app.Time);
            if (index >= events.Length || events[index].Time > app.Time) index--;
            if (index >= 0)
            {
                flash = HitSoundFlash(events[index]);
                bankColour = HitSoundBankColour(events[index].Bank);
            }
        }
        var clicked = auditionHits.FirstOrDefault(e => e.Lane == lane);
        if (!app.IsHitSoundLaneHidden(lane) && clicked != null && HitSoundClickFlash > flash)
        {
            flash = HitSoundClickFlash;
            bankColour = HitSoundBankColour(clicked.Bank);
        }
        if (flash > 0)
        {
            dl.AddRectFilled(pos, max, Theme.WithAlpha(bankColour, flash * 0.4f));
            dl.AddRectFilled(pos, new(pos.X + Theme.Px(3), max.Y), Theme.WithAlpha(bankColour, flash));
        }
        DrawEye(dl, new(pos.X + 14, (pos.Y + max.Y) / 2), !app.IsHitSoundLaneHidden(lane));
        uint colour = app.IsHitSoundLaneHidden(lane) ? Theme.TextDim : ImGui.GetColorU32(ImGuiCol.Text);
        if (flash > 0) colour = ImGui.ColorConvertFloat4ToU32(Vector4.Lerp(ImGui.ColorConvertU32ToFloat4(colour), Vector4.One, flash));
        if (lane < 4) DrawHitSoundIcon(dl, lane, new(pos.X + Theme.Px(38), (pos.Y + max.Y) / 2), colour);
        dl.AddText(new(pos.X + Theme.Px(lane < 4 ? 54 : 30), textY), colour, HitSoundLaneName(lane));
        string count = recheckIndex.Lanes[lane].Length.ToString();
        uint countColour = ImGui.ColorConvertFloat4ToU32(Vector4.Lerp(ImGui.ColorConvertU32ToFloat4(Theme.TextDim), Vector4.One, flash));
        dl.AddText(new(max.X - ImGui.CalcTextSize(count).X - 10, textY), countColour, count);
        if (hovered)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ImGui.GetIO().MousePos.X < pos.X + 26)
                app.SetHitSoundLaneHidden(lane, !app.IsHitSoundLaneHidden(lane));
            Theme.SetTooltip(ImGui.GetIO().MousePos.X < pos.X + 26 ? L.T("hitsound.laneVisibility.tip")
                : HitSoundReadOnlyHint + "\n" + L.T("hitsound.legend"));
        }
    }

    private void DrawHitSoundTrack(ImDrawListPtr dl, int lane, float y, float height, float right)
    {
        if (lane >= 0 && app.IsHitSoundLaneHidden(lane)) return;
        var events = lane < 0 ? recheckIndex.Events : recheckIndex.Lanes[lane];
        double from = TimeAt(trackX - 4), until = TimeAt(right + 4);
        if (events.Length == 0)
        {
            if (lane < 0 || lane == 0)
                dl.AddText(new(trackX + 8, y + (height - ImGui.GetFontSize()) / 2), Theme.TextDim,
                    app.HitSoundRecheckProblem ?? app.BeatmapProblem ?? L.T("hitsound.empty"));
            return;
        }
        // Sub-pixel events share a small pixel bucket. Retain the loudest one as the visible marker,
        // while the complete source events remain available in the hover card.
        int previousBucket = int.MinValue;
        float previousVolume = -1;
        for (int i = HitSoundEventIndex.FirstAtOrAfter(events, from); i < events.Length; i++)
        {
            var sound = events[i];
            if (sound.Time > until) break;
            if (sound.End < from || app.IsHitSoundLaneHidden(sound.Lane)) continue;
            float x = MathF.Round(X(sound.Time));
            float markerHeight = height * 0.85f, top = y + (height - markerHeight) / 2, bottom = top + markerHeight;
            uint bankColour = HitSoundBankColour(sound.Bank);
            float opacity = 0.3f + 0.7f * sound.Volume;
            if (x < trackX - 3) continue;
            if (lane < 0 && sound.Lane is >= 1 and <= 3)
                dl.AddCircleFilled(new(x, y + 3 + sound.Lane * 3), 1.7f, sound.Lane switch { 1 => Theme.Rgba(170, 150, 245), 2 => Theme.Rgba(245, 210, 80), _ => Theme.Rgba(130, 225, 180) });
            int bucket = (int)(x / 3);
            if (bucket == previousBucket && sound.Volume <= previousVolume) continue;
            previousVolume = bucket == previousBucket ? Math.Max(previousVolume, sound.Volume) : sound.Volume;
            previousBucket = bucket;
            float halfWidth = Theme.Px(2.25f); // 1.5 times the previous 3 px marker.
            float flash = HitSoundFlash(sound);
            if (flash > 0)
            {
                dl.AddRectFilled(new(x - halfWidth - 4, top - 1), new(x + halfWidth + 4, bottom + 1),
                    Theme.WithAlpha(bankColour, flash * 0.65f), 3);
                dl.AddRect(new(x - halfWidth - 1, top - 1), new(x + halfWidth + 1, bottom + 1),
                    Theme.Rgba(255, 255, 255, (byte)(flash * 230)), 1);
            }
            var markerColour = Vector4.Lerp(ImGui.ColorConvertU32ToFloat4(bankColour), Vector4.One, flash * 0.9f);
            markerColour.W = Math.Max(opacity, flash);
            dl.AddRectFilled(new(x - halfWidth, top), new(x + halfWidth, bottom), ImGui.ColorConvertFloat4ToU32(markerColour), 1);
            if (sound.ExplicitBank) dl.AddRect(new(x - halfWidth - 1, top - 1), new(x + halfWidth + 1, bottom + 1), Theme.Rgba(240, 240, 245, 170), 1);
            if (sound.File == null)
            {
                dl.AddLine(new(x - 3, y + 3), new(x + 3, y + 9), Theme.TextError, 1.3f);
                dl.AddLine(new(x + 3, y + 3), new(x - 3, y + 9), Theme.TextError, 1.3f);
            }
            if ((lane < 0 || lane == 4) && sound.CustomIndex > 1 && pxPerMs >= 0.05 && (i + 1 == events.Length || X(events[i + 1].Time) - x > 18))
                dl.AddText(new(x + 4, y + 1), Theme.TextDim, sound.CustomIndex.ToString());
        }
    }

    private static string HitSoundParameters(HitSoundEvent sound) =>
        $"{(sound.Bank switch { 2 => "S", 3 => "D", _ => "N" })}:{sound.CustomIndex} {sound.Volume * 100:0}%";

    // Temporarily hide the floating parameter card near the playhead or hovered marker.
    // Keep the drawing code available for restoring this feature later.
    private static readonly bool ShowHitSoundParameterCallouts = false;

    private void DrawHitSoundParameters(ImDrawListPtr dl, int lane, float y, float height, float right)
    {
        if (lane is < 0 or > 3 || app.IsHitSoundLaneHidden(lane)) return;
        var events = recheckIndex.Lanes[lane];
        double from = TimeAt(trackX - 6), until = TimeAt(right + 6);
        int first = HitSoundEventIndex.FirstAtOrAfter(events, from), last = HitSoundEventIndex.FirstAtOrAfter(events, until);
        int priority = -1;
        float priorityDistance = float.PositiveInfinity;
        var io = ImGui.GetIO();
        bool hovering = io.MousePos.Y >= y && io.MousePos.Y < y + height && io.MousePos.X >= trackX && io.MousePos.X <= right
            && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);
        float focus = hovering ? io.MousePos.X : X(app.Time);
        for (int i = first; ShowHitSoundParameterCallouts && i < last; i++)
        {
            var sound = events[i];
            if (sound.End < from || focus < trackX || focus > right) continue;
            float distance = Math.Abs(X(sound.Time) - focus);
            if (distance > 6 || distance >= priorityDistance) continue;
            priority = i;
            priorityDistance = distance;
        }
        string? priorityText = priority >= 0 ? HitSoundParameters(events[priority]) : null;
        float priorityWidth = priorityText == null ? 0 : ImGui.CalcTextSize(priorityText).X;
        float priorityLeft = priority >= 0 ? Math.Clamp(X(events[priority].Time) + 6, trackX + 4, Math.Max(trackX + 4, right - priorityWidth - 4)) : 0;
        float priorityRight = priorityLeft + priorityWidth;
        float previousRight = trackX, textY = y + (height - ImGui.GetFontSize()) / 2;
        dl.PushClipRect(new(trackX, y), new(right, y + height), true);
        for (int i = first; i < last; i++)
        {
            var sound = events[i];
            float x = X(sound.Time);
            if (i == priority || x < trackX || sound.End < from) continue;
            // Concurrent sample types in one lane share a marker; prefer the actual hit sample.
            if (i > first && events[i - 1].Time == sound.Time) continue;
            float left = x + 6;
            int next = i + 1;
            while (next < events.Length && events[next].Time == sound.Time) next++;
            float limit = next < events.Length ? Math.Min(right - 4, X(events[next].Time) - 5) : right - 4;
            if (limit - left < Theme.Px(58)) continue;
            string text = HitSoundParameters(sound);
            float end = left + ImGui.CalcTextSize(text).X;
            if (left < previousRight + 4 || end > limit || priority >= 0 && left < priorityRight + 4 && end > priorityLeft - 4) continue;
            dl.AddText(new(left, textY), HitSoundBankColour(sound.Bank), text);
            previousRight = end;
        }
        if (priority >= 0 && priorityText != null)
        {
            // The hovered/current marker remains readable even at dense zoom levels.
            dl.AddRectFilled(new(priorityLeft - 2, y + 1), new(priorityRight + 2, y + height - 1), Theme.Rgba(27, 27, 30, 230), 2);
            dl.AddText(new(priorityLeft, textY), HitSoundBankColour(events[priority].Bank), priorityText);
        }
        dl.PopClipRect();
    }

    private void HandleHitSoundInput(Row row, float mouseX)
    {
        if (row.Kind != RowKind.HitSoundLane || collapsed[HitSoundsSection]) return;
        int lane = row.HitSoundLane;
        var events = recheckIndex.Lanes[lane];
        double time = TimeAt(mouseX), radius = 6 / pxPerMs;
        var hits = new List<HitSoundEvent>();
        for (int i = HitSoundEventIndex.FirstAtOrAfter(events, time - radius); i < events.Length && events[i].Time <= time + radius; i++)
        {
            var sound = events[i];
            if (app.IsHitSoundLaneHidden(sound.Lane) || sound.End < time - radius) continue;
            if (Math.Abs(sound.Time - time) <= radius) hits.Add(sound);
        }
        if (hits.Count == 0) return;
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            double closest = hits.MinBy(e => Math.Abs(e.Time - time))!.Time;
            auditionHits = hits.Where(e => e.Time == closest).ToArray();
            auditionAt = ImGui.GetTime();
            if (!app.Settings.OsuStyleTimelineIndicator && !app.Playing) app.Seek(closest);
            app.Audio.Audition(app.HitSoundAudition(auditionHits));
        }
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 34);
        ImGui.TextDisabled(HitSoundReadOnlyHint);
        foreach (var sound in hits.Take(8))
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"{Theme.FormatTime(sound.Time)} · {HitSoundOriginText(sound.Origin)}");
            ImGui.TextUnformatted($"{(sound.Kind == HitSoundKind.Normal ? "Hit" : sound.Kind)} · {HitSoundParameters(sound)}");
            if (sound.Duration > 0) ImGui.TextUnformatted(L.T("hitsound.duration", sound.Duration));
            ImGui.TextUnformatted(L.T("hitsound.file", sound.File == null ? sound.SampleName : Path.GetFileName(sound.File)));
            ImGui.TextUnformatted(L.T("hitsound.source", sound.Source switch
            {
                HitSoundSource.Beatmap => L.T("hitsound.source.Beatmap"), HitSoundSource.Skin => L.T("hitsound.source.Skin"),
                HitSoundSource.Default => L.T("hitsound.source.Default"), _ => L.T("hitsound.source.Missing")
            }));
            if (sound.ExplicitBank) ImGui.TextDisabled(L.T("hitsound.explicit"));
            if (sound.File != null && !SbStudio.Resources.ResourceAssets.IsEmbedded(sound.File)) ImGui.TextWrapped(sound.File);
        }
        if (hits.Count > 8) ImGui.TextDisabled(L.T("hitsound.more", hits.Count - 8));
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static string HitSoundOriginText(HitSoundOrigin origin) => origin switch
    {
        HitSoundOrigin.Circle => L.T("hitsound.origin.Circle"), HitSoundOrigin.SliderHead => L.T("hitsound.origin.SliderHead"),
        HitSoundOrigin.SliderRepeat => L.T("hitsound.origin.SliderRepeat"), HitSoundOrigin.SliderTail => L.T("hitsound.origin.SliderTail"),
        HitSoundOrigin.SliderTick => L.T("hitsound.origin.SliderTick"), HitSoundOrigin.SliderBody => L.T("hitsound.origin.SliderBody"),
        HitSoundOrigin.SpinnerTail => L.T("hitsound.origin.SpinnerTail"), HitSoundOrigin.SpinnerBody => L.T("hitsound.origin.SpinnerBody"),
        HitSoundOrigin.ManiaNote => L.T("hitsound.origin.ManiaNote"), HitSoundOrigin.HoldHead => L.T("hitsound.origin.HoldHead"),
        HitSoundOrigin.TaikoRoll => L.T("hitsound.origin.TaikoRoll"), HitSoundOrigin.TaikoSwell => L.T("hitsound.origin.TaikoSwell"),
        HitSoundOrigin.CatchFruit => L.T("hitsound.origin.CatchFruit"), HitSoundOrigin.CatchDroplet => L.T("hitsound.origin.CatchDroplet"),
        _ => L.T("hitsound.origin.CatchBanana")
    };
}
