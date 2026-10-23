using System.Numerics;
using ImGuiNET;

namespace SbStudio.App.Ui;

/// <summary>Floating controls shared by toolbar and panel settings.</summary>
internal sealed class HoverPopup
{
    public bool Open, Dismissed;
    public double HoveredAt;
    public int Frame;
    public float Height;

    /// <summary>Shared hover behaviour and appearance for the preview's floating controls.</summary>
    public static void Draw(string id, HoverPopup popup, float desiredWidth, int rows, bool below, Func<bool> drawControls)
    {
        bool anchorHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup);
        bool anchorActive = ImGui.IsItemActive();
        Vector2 anchorMin = ImGui.GetItemRectMin(), anchorMax = ImGui.GetItemRectMax();
        double now = ImGui.GetTime();
        if (!anchorHovered) popup.Dismissed = false;
        if (ImGui.GetFrameCount() - popup.Frame > 1) popup.Open = false;
        popup.Frame = ImGui.GetFrameCount();
        if (anchorHovered && !popup.Dismissed && !anchorActive && !ImGui.IsAnyItemActive() &&
            !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
            popup.Open = true;
        if (!popup.Open) return;

        // A floating window keeps the toolbar and player usable while these controls are open.
        var viewport = ImGui.GetWindowViewport();
        float margin = Theme.Px(4);
        float width = MathF.Min(Theme.Px(desiredWidth), viewport.WorkSize.X - margin * 2);
        float left = Math.Clamp(below ? anchorMin.X : anchorMax.X - width,
            viewport.WorkPos.X + margin, viewport.WorkPos.X + viewport.WorkSize.X - width - margin);
        float height = MathF.Max(popup.Height,
            ImGui.GetFrameHeight() * rows + ImGui.GetStyle().ItemSpacing.Y * (rows - 1) + Theme.Px(24));
        float top = Math.Clamp(below ? anchorMax.Y + margin : anchorMin.Y - margin - height,
            viewport.WorkPos.Y + margin, MathF.Max(viewport.WorkPos.Y + margin,
                viewport.WorkPos.Y + viewport.WorkSize.Y - height - margin));
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.SetNextWindowPos(new Vector2(left, below ? top : top + height), ImGuiCond.Always, new Vector2(0, below ? 0 : 1));
        ImGui.SetNextWindowSize(new Vector2(width, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Theme.Px(12)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, ImGui.GetStyle().PopupRounding);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ImGui.GetStyle().Colors[(int)ImGuiCol.PopupBg]);
        bool visible = ImGui.Begin(id, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize);
        if (visible)
        {
            popup.Height = ImGui.GetWindowSize().Y;
            bool childPopupOpen = drawControls();
            bool hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByPopup);
            bool adjusting = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.IsAnyItemActive();
            if (anchorHovered || anchorActive || hovered || adjusting || childPopupOpen) popup.HoveredAt = now;
            bool escape = !childPopupOpen && ImGui.IsKeyPressed(ImGuiKey.Escape);
            if (now - popup.HoveredAt > 0.3 ||
                escape ||
                (!anchorHovered && !hovered && !adjusting && !childPopupOpen && ImGui.IsMouseClicked(ImGuiMouseButton.Left)))
                popup.Open = false;
            if (escape) popup.Dismissed = true;
        }
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }
}
