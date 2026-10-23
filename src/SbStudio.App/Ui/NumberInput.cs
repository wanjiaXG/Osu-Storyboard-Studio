using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>What a numeric field accepts and how it is shown.</summary>
/// <param name="Name">What the value is, for the message shown when input is rejected.</param>
/// <param name="Speed">Change per pixel when dragging.</param>
/// <param name="Format">printf-style display format.</param>
/// <param name="Suffix">Unit the user may type after the number, such as ° or ms.</param>
/// <param name="Caption">What the field says about itself inside its own box, ahead of the number: "X", "Opacity"…</param>
public readonly record struct NumberSpec(
    string Name,
    float Speed,
    string Format,
    double Min = double.NegativeInfinity,
    double Max = double.PositiveInfinity,
    bool Integer = false,
    string Suffix = "",
    string Caption = "");

/// <summary>
/// The caption inside a field: what the number in it is, in dimmed letters at the left of the box, with
/// the number at the right. It takes no room of its own, which matters in a panel as narrow as the
/// properties, where a row of boxes would otherwise be a row of bare numbers.
/// </summary>
internal static class FieldCaption
{
    private static float Gap => Theme.Px(6);

    /// <summary>
    /// Has the widget that follows leave out its own text; <see cref="Draw"/> then writes caption and
    /// value in its place. Returns whether it did, which is what <see cref="End"/> wants to be told.
    /// </summary>
    public static bool Begin(string caption)
    {
        if (caption.Length == 0) return false;
        ImGui.PushStyleColor(ImGuiCol.Text, 0u);
        return true;
    }

    public static void End(bool begun)
    {
        if (begun) ImGui.PopStyleColor();
    }

    /// <summary>
    /// Writes caption and value into the box of the widget that was drawn last. A box too narrow for both
    /// shows the value alone, as it would without a caption, and names itself when the mouse rests on it.
    /// </summary>
    /// <param name="widest">The widest text the value usually has; room is kept for it so that the caption does not come and go as digits do.</param>
    public static void Draw(string caption, string value, string widest)
    {
        Vector2 min = ImGui.GetItemRectMin(), max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        float padding = ImGui.GetStyle().FramePadding.X;
        float captionWidth = ImGui.CalcTextSize(caption).X, valueWidth = ImGui.CalcTextSize(value).X;
        float y = min.Y + ImGui.GetStyle().FramePadding.Y;
        uint text = ImGui.GetColorU32(ImGuiCol.Text);

        dl.PushClipRect(min, max, true);
        bool fits = captionWidth + Gap + MathF.Max(valueWidth, ImGui.CalcTextSize(widest).X) <= max.X - min.X - padding * 2;
        if (fits)
        {
            dl.AddText(new Vector2(min.X + padding, y), ImGui.GetColorU32(Theme.TextDim), caption);
            dl.AddText(new Vector2(max.X - padding - valueWidth, y), text, value);
        }
        else
        {
            dl.AddText(new Vector2(MathF.Round((min.X + max.X - valueWidth) / 2), y), text, value);
        }
        dl.PopClipRect();
        if (!fits) Theme.Tooltip(caption);
    }

    /// <summary>
    /// What a printf format of the kind the fields use makes of a number: "%.1f°" and 45 give "45.0°",
    /// "%d ms" and 1200 give "1200 ms".
    /// </summary>
    public static string Printf(string format, double value)
    {
        int percent = format.IndexOf('%');
        while (percent >= 0 && percent + 1 < format.Length && format[percent + 1] == '%') percent = format.IndexOf('%', percent + 2);
        if (percent < 0) return format.Replace("%%", "%");

        int end = percent + 1, precision = -1;
        while (end < format.Length && !char.IsLetter(format[end]))
        {
            if (format[end] == '.')
            {
                int digits = end + 1;
                while (digits < format.Length && char.IsAsciiDigit(format[digits])) digits++;
                precision = digits > end + 1 ? int.Parse(format.AsSpan(end + 1, digits - end - 1), CultureInfo.InvariantCulture) : 0;
            }
            end++;
        }
        bool whole = end < format.Length && format[end] is 'd' or 'i' or 'u';
        string number = whole
            ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("F" + (precision < 0 ? 6 : precision), CultureInfo.InvariantCulture);
        string after = end + 1 < format.Length ? format.Substring(end + 1) : "";
        return format.Substring(0, percent).Replace("%%", "%") + number + after.Replace("%%", "%");
    }
}

/// <summary>
/// The typing half of every numeric field. A field is dragged to change its value and clicked to type one:
/// a click without movement turns it into a text box until Enter, Escape or a click elsewhere.
/// </summary>
internal static class TextEntry
{
    private static uint editingId;
    private static string buffer = "";
    private static bool focusPending;
    private static bool moved;
    private static int lastFrame;
    private static int idleFrames;

    public static bool IsEditing(uint id)
    {
        // The field went away (selection changed, panel closed) while it was being edited.
        if (editingId != 0 && ImGui.GetFrameCount() - lastFrame > 1) editingId = 0;
        return editingId == id;
    }

    /// <summary>Call right after the drag widget: starts typing when the widget was clicked and not dragged.</summary>
    public static void TrackClick(uint id, string text)
    {
        if (ImGui.IsItemActivated()) moved = false;
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3f)) moved = true;
        if (!ImGui.IsItemDeactivated() || moved || !ImGui.IsItemHovered()) return;
        editingId = id;
        buffer = text;
        focusPending = true;
        idleFrames = 0;
        lastFrame = ImGui.GetFrameCount();
    }

    /// <summary>Draws the text box of the field being edited.</summary>
    /// <returns>The text when the user confirmed it this frame; null while typing or when cancelled.</returns>
    public static string? Draw(string label, float width)
    {
        lastFrame = ImGui.GetFrameCount();
        ImGui.SetNextItemWidth(width);
        if (focusPending)
        {
            ImGui.SetKeyboardFocusHere();
            focusPending = false;
        }

        bool enter = ImGui.InputText(label, ref buffer, 64, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        if (enter)
        {
            editingId = 0;
            return buffer;
        }
        if (ImGui.IsItemDeactivated())
        {
            editingId = 0;
            // Escape abandons the edit; clicking elsewhere confirms it.
            return ImGui.IsKeyPressed(ImGuiKey.Escape) ? null : buffer;
        }
        // Should the box never receive focus, do not leave the field stuck as a text box.
        if (ImGui.IsItemActive()) idleFrames = 0;
        else if (++idleFrames > 3) editingId = 0;
        return null;
    }
}

/// <summary>
/// Numeric fields of the properties panel. Typed input is checked: text that is not a number, or a number
/// the field cannot take, brings up a message and leaves the old value in place.
/// </summary>
public static class NumberInput
{
    public static bool Field(EditorApp app, string id, ref double value, float width, in NumberSpec spec)
    {
        uint itemId = ImGui.GetID(id);
        if (TextEntry.IsEditing(itemId))
        {
            string? text = TextEntry.Draw(id, width);
            if (text == null || !TryAccept(app, text, value, spec, out double typed) || typed == value) return false;
            value = typed;
            return true;
        }

        ImGui.SetNextItemWidth(width);
        float shown = (float)value;
        bool captioned = FieldCaption.Begin(spec.Caption);
        bool dragged = ImGui.DragFloat(id, ref shown, spec.Speed, 0, 0, spec.Format, ImGuiSliderFlags.NoInput);
        FieldCaption.End(captioned);
        TextEntry.TrackClick(itemId, EditText(value));

        bool changed = false;
        if (dragged)
        {
            // Dragging cannot produce an invalid value: it simply stops at the limits.
            double result = Math.Clamp(shown, spec.Min, spec.Max);
            result = spec.Integer ? Math.Round(result) : Math.Round(result, 4);
            changed = result != value;
            value = result;
        }
        if (captioned)
        {
            // Room for the largest value the field can take, or for a hundred where it has no limit.
            double large = double.IsFinite(spec.Max) ? spec.Max : 100;
            FieldCaption.Draw(spec.Caption, FieldCaption.Printf(spec.Format, (float)value), FieldCaption.Printf(spec.Format, large));
        }
        return changed;
    }

    /// <summary>Two fields side by side for an x,y pair.</summary>
    /// <param name="first">What the fields say they are inside their boxes: X and Y unless told otherwise.</param>
    public static bool Pair(EditorApp app, string id, ref double x, ref double y, float width, in NumberSpec spec, string first = "X", string second = "Y")
    {
        float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        float each = MathF.Max(20, (width - spacing) / 2);
        bool changed = Field(app, id + "x", ref x, each, spec with { Name = spec.Name + " X", Caption = first });
        ImGui.SameLine(0, spacing);
        changed |= Field(app, id + "y", ref y, each, spec with { Name = spec.Name + " Y", Caption = second });
        return changed;
    }

    public static bool Field(EditorApp app, string id, ref int value, float width, in NumberSpec spec)
    {
        double number = value;
        if (!Field(app, id, ref number, width, spec with { Integer = true })) return false;
        value = (int)number;
        return true;
    }

    /// <summary>Text shown when a field switches to typing: the plain number, without unit or padding.</summary>
    public static string EditText(double value) => Math.Round(value, 5).ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>Checks typed text against the field's rules; on failure tells the user and reports false.</summary>
    public static bool TryAccept(EditorApp app, string text, double current, in NumberSpec spec, out double value)
    {
        string? error = Validate(text, spec, out value);
        if (error == null) return true;
        app.ShowMessage(L.T("input.invalid.title"), L.T("input.invalid.text", spec.Name, error, EditText(current)));
        return false;
    }

    /// <returns>Null when the text is acceptable, otherwise what is wrong with it.</returns>
    public static string? Validate(string text, in NumberSpec spec, out double value)
    {
        value = 0;
        string trimmed = text.Trim();
        if (spec.Suffix.Length > 0 && trimmed.EndsWith(spec.Suffix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(0, trimmed.Length - spec.Suffix.Length).Trim();

        if (trimmed.Length == 0) return L.T("input.empty");
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || double.IsInfinity(value))
            return L.T("input.notNumber", text.Trim());
        if (spec.Integer && value != Math.Round(value)) return L.T("input.notInteger", EditText(value));
        if (spec.Integer && Math.Abs(value) > int.MaxValue / 4) return L.T("input.tooLarge");

        bool hasMin = !double.IsNegativeInfinity(spec.Min), hasMax = !double.IsPositiveInfinity(spec.Max);
        if (value < spec.Min || value > spec.Max)
        {
            return hasMin && hasMax ? L.T("input.range", EditText(spec.Min), EditText(spec.Max), EditText(value))
                : hasMin ? L.T("input.min", EditText(spec.Min))
                : L.T("input.max", EditText(spec.Max));
        }
        return null;
    }
}
