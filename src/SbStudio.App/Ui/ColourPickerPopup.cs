using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>The same colour picker and preview/confirm/cancel behaviour in every editor panel.</summary>
internal sealed class ColourPickerPopup(EditorApp app)
{
    private uint activeId;
    private int lastFrame;
    private Vector4 colour, originalColour;
    private double[] original = Array.Empty<double>(), preview = Array.Empty<double>();
    private Action<double[]>? apply;
    private object? owner;

    public void Tick()
    {
        if (activeId != 0 && (!app.PreviewingEdit || ImGui.GetFrameCount() - lastFrame > 1)) Cancel();
    }

    public void Open(string id, double[] values, Action<double[]> applyValues, object editingOwner)
    {
        Cancel();
        original = (double[])values.Clone();
        preview = (double[])values.Clone();
        apply = applyValues;
        owner = editingOwner;
        var (r, g, b) = TimelineValueInput.ColourBytes(values);
        colour = originalColour = new Vector4(r / 255f, g / 255f, b / 255f, 1);
        var restore = original;
        app.BeginPreviewEdit(() => applyValues(restore));
        activeId = ImGui.GetID(id);
        lastFrame = ImGui.GetFrameCount();
        ImGui.OpenPopup(id);
    }

    public void Cancel() => Finish(false);
    public void Cancel(object editingOwner)
    {
        if (ReferenceEquals(owner, editingOwner)) Cancel();
    }

    private void Finish(bool confirm)
    {
        if (activeId == 0) return;
        app.FinishPreviewEdit(confirm && !preview.AsSpan().SequenceEqual(original));
        activeId = 0;
        apply = null;
        owner = null;
    }

    public bool Draw(string id)
    {
        bool active = activeId == ImGui.GetID(id) && app.PreviewingEdit;
        if (active) lastFrame = ImGui.GetFrameCount();
        if (!ImGui.BeginPopup(id, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (active) Cancel();
            return false;
        }
        if (!active) ImGui.CloseCurrentPopup();
        else
        {
            ImGui.SetNextItemWidth(Theme.Px(300));
            const ImGuiColorEditFlags flags = ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoLabel |
                ImGuiColorEditFlags.DisplayRGB | ImGuiColorEditFlags.DisplayHSV | ImGuiColorEditFlags.DisplayHex |
                ImGuiColorEditFlags.PickerHueBar | ImGuiColorEditFlags.Uint8;
            if (ImGui.ColorPicker4("##picker", ref colour, flags, ref originalColour.X))
            {
                double[] values = { Math.Round(Math.Clamp(colour.X, 0, 1) * 255),
                    Math.Round(Math.Clamp(colour.Y, 0, 1) * 255), Math.Round(Math.Clamp(colour.Z, 0, 1) * 255) };
                if (!values.AsSpan().SequenceEqual(preview))
                {
                    preview = values;
                    apply!(values);
                }
            }
            Vector2 pickerMax = ImGui.GetItemRectMax(), afterPicker = ImGui.GetCursorScreenPos();
            float width = Theme.Px(68), height = ImGui.GetFrameHeight(), gap = ImGui.GetStyle().ItemSpacing.Y;
            ImGui.SetCursorScreenPos(new(pickerMax.X - width, pickerMax.Y - height * 2 - gap));
            if (ImGui.Button(L.T("common.confirm"), new Vector2(width, height)))
            {
                Finish(true);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SetCursorScreenPos(new(pickerMax.X - width, pickerMax.Y - height));
            if (ImGui.Button(L.T("common.cancel"), new Vector2(width, height)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                Cancel();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SetCursorScreenPos(afterPicker);
        }
        ImGui.EndPopup();
        return activeId != 0;
    }
}
