using SbStudio.Core;

namespace SbStudio.App.Ui;

internal static class TimelineValueInput
{
    public static string EditText(double[] values, CommandKind kind, bool degrees) =>
        kind == CommandKind.C ? Hex(values)
            : string.Join(", ", values.Select(v => NumberInput.EditText(SbInfo.IsAngle(kind) && degrees ? v * 180 / Math.PI : v)));

    public static (int R, int G, int B) ColourBytes(double[] values) =>
        ((int)Math.Round(Math.Clamp(values[0], 0, 255)), (int)Math.Round(Math.Clamp(values[1], 0, 255)), (int)Math.Round(Math.Clamp(values[2], 0, 255)));

    private static string Hex(double[] values)
    {
        var (r, g, b) = ColourBytes(values);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    public static string Display(double[] values, CommandKind kind, bool degrees)
    {
        if (kind == CommandKind.C) return Hex(values);
        string format = kind switch { CommandKind.F => "0.##", CommandKind.M or CommandKind.MX or CommandKind.MY => "0.#", _ => "0.###" };
        return string.Join(", ", values.Select(v => (SbInfo.IsAngle(kind) && degrees ? v * 180 / Math.PI : v)
            .ToString(format, System.Globalization.CultureInfo.InvariantCulture))) + (SbInfo.IsAngle(kind) && degrees ? "°" : "");
    }

    public static string? Validate(string text, CommandKind kind, bool degrees, out double[] values)
    {
        values = Array.Empty<double>();
        if (kind == CommandKind.C && text.Trim().StartsWith('#'))
        {
            string hex = text.Trim().Substring(1);
            if (hex.Length is not (3 or 6) || !hex.All(char.IsAsciiHexDigit) ||
                !uint.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out uint rgb))
                return L.T("timeline.value.colour.format");
            values = hex.Length == 3
                ? new[] { (double)((rgb >> 8) * 17), (double)(((rgb >> 4) & 15) * 17), (double)((rgb & 15) * 17) }
                : new[] { (double)(rgb >> 16), (double)((rgb >> 8) & 255), (double)(rgb & 255) };
            return null;
        }
        int count = kind switch { CommandKind.M or CommandKind.V => 2, CommandKind.C or CommandKind.CT => 3, CommandKind.P => 0, _ => 1 };
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (count == 0 || parts.Length != count) return L.T("timeline.value.components", count);
        var parsed = new double[count];
        var spec = PropertiesPanel.KeyValueSpec(kind, degrees);
        for (int i = 0; i < count; i++)
        {
            string? error = NumberInput.Validate(parts[i], spec, out parsed[i]);
            if (error != null) return error;
        }
        if (SbInfo.IsAngle(kind) && degrees) parsed[0] = Math.Round(parsed[0] * Math.PI / 180, 5);
        values = parsed;
        return null;
    }
}
