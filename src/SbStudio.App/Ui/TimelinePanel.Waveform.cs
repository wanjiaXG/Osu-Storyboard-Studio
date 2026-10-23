using System.Numerics;
using ImGuiNET;
using SbStudio.App.Audio;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class TimelinePanel
{
    private static string WaveformStyleName(TimelineWaveformStyle style) => style switch
    {
        TimelineWaveformStyle.Green => L.T("timeline.waveformStyle.green"),
        _ => L.T("timeline.waveformStyle.classic"),
    };

    private void DrawWaveformLabel(ImDrawListPtr dl, Vector2 pos, Vector2 max, float textY, bool hovered)
    {
        // Keep the selector at the top when the detailed channel view grows taller.
        textY = pos.Y + (Math.Min(max.Y - pos.Y, Theme.Px(44)) - ImGui.GetFontSize()) / 2;
        if (hovered) dl.AddRectFilled(pos, max, Theme.RowAlt);
        float left = pos.X + Theme.Px(30);
        string label = L.T("timeline.audio", Path.GetFileName(app.Project!.Settings.Audio));
        dl.PushClipRect(new(left, pos.Y), max - new Vector2(Theme.Px(5), 0), true);
        dl.AddText(new(left, textY - ImGui.GetFontSize() / 2), Theme.TextDim, label);
        dl.PopClipRect();

        float desiredWidth = Math.Max(ImGui.CalcTextSize(WaveformStyleName(TimelineWaveformStyle.Classic)).X,
            ImGui.CalcTextSize(WaveformStyleName(TimelineWaveformStyle.Green)).X) + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2;
        float width = Math.Min(desiredWidth, Math.Max(1, max.X - left - Theme.Px(8)));
        float comboX = max.X - Theme.Px(8) - width;
        float secondY = textY + ImGui.GetFontSize() / 2 + 2;
        string readOnly = L.T("timeline.referenceReadOnly");
        if (ImGui.CalcTextSize(readOnly).X <= comboX - left - Theme.Px(6))
            dl.AddText(new(left, secondY), Theme.TextDim, readOnly);

        ImGui.SetCursorScreenPos(new(comboX, secondY - (ImGui.GetFrameHeight() - ImGui.GetFontSize()) / 2));
        ImGui.SetNextItemWidth(width);
        bool open = ImGui.BeginCombo("##waveformStyle", WaveformStyleName(app.Settings.WaveformStyle));
        bool selectorHovered = ImGui.IsItemHovered();
        if (open)
        {
            foreach (var style in Enum.GetValues<TimelineWaveformStyle>())
            {
                bool selected = app.Settings.WaveformStyle == style;
                if (ImGui.Selectable(WaveformStyleName(style), selected) && !selected)
                {
                    app.Settings.WaveformStyle = style;
                    app.Settings.Save();
                    rowsStale = true;
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        if (!open && selectorHovered) Theme.SetTooltip(L.T("timeline.waveformStyle.tip"));
        else if (!open && hovered) Theme.SetTooltip(label + "\n" + ReferenceReadOnlyHint);
    }

    private void DrawGreenWaveform(ImDrawListPtr dl, Waveform? waveform, float top, float height, float x0, float x1)
    {
        float bottom = top + height;
        dl.PushClipRect(new(trackX, top), new(trackRight, bottom), true);

        var detail = waveform?.Detail;
        if (detail == null)
            dl.AddText(new(Math.Max(trackX, x0) + 8, top + Theme.Px(10)), Theme.TextDim, L.T("timeline.analysing"));
        else
        {
            float channelHeight = height / detail.ChannelCount;
            uint colour = Theme.Rgba(53, 211, 148);
            for (int channel = 0; channel < detail.ChannelCount; channel++)
            {
                float channelTop = top + channel * channelHeight, channelBottom = channelTop + channelHeight;
                float mid = (channelTop + channelBottom) / 2, amplitude = Math.Max(0, channelHeight / 2 - Theme.Px(3));
                dl.PushClipRect(new(trackX, channelTop), new(trackRight, channelBottom), true);
                // The upper/lower edges are the actual positive/negative extrema of this channel,
                // independently reduced to each screen pixel. No mirrored peak or artificial centre band.
                for (float x = x0; x < x1; x++)
                {
                    var range = detail.RangeForDisplay(channel, TimeAt(x), TimeAt(x + 1));
                    if (!range.HasSamples) continue;
                    float upper = mid - Math.Clamp(range.Maximum, -1, 1) * amplitude;
                    float lower = mid - Math.Clamp(range.Minimum, -1, 1) * amplitude;
                    if (lower - upper < 1) dl.AddRectFilled(new(x, upper - 0.5f), new(x + 1, lower + 0.5f), colour);
                    else dl.AddLine(new(x, upper), new(x, lower), colour);
                }
                string name = detail.ChannelCount == 1 ? L.T("timeline.waveform.mono")
                    : channel == 0 ? L.T("timeline.waveform.left") : L.T("timeline.waveform.right");
                dl.AddText(new(trackX + Theme.Px(4), channelTop + Theme.Px(2)), Theme.Rgba(120, 150, 135), name);
                dl.PopClipRect();
                if (channel > 0) dl.AddLine(new(trackX, channelTop), new(trackRight, channelTop), Theme.Rgba(145, 155, 150));
            }
        }
        dl.PopClipRect();
    }
}
