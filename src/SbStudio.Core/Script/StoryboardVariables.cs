using System.Globalization;
using System.Text;

namespace SbStudio.Core;

/// <summary>An ordered variable definition; null difficulty denotes the shared storyboard.</summary>
public sealed record StoryboardVariable(string Name, string Value, string? Difficulty = null, bool Ascii = false);

/// <summary>The original variable reference and the concrete line it currently represents.</summary>
public sealed record VariableLine(string Text, string Expanded, string Canonical, int Count = 1);

public static class StoryboardVariables
{
    public static void DetachReferences(IEnumerable<SbObject> objects)
    {
        foreach (var obj in objects)
        {
            obj.VariableSource = null;
            if (obj is not SbSprite sprite) continue;
            foreach (var node in sprite.Nodes)
            {
                node.VariableSource = null;
                if (node is SbGroup block) foreach (var command in block.Commands) command.VariableSource = null;
            }
        }
    }

    public static Dictionary<string, string> Lookup(IEnumerable<StoryboardVariable> variables, string? difficulty)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (difficulty != null)
            foreach (var variable in variables.Where(v => v.Difficulty == difficulty)) result[variable.Name] = variable.Value;
        foreach (var variable in variables.Where(v => v.Difficulty == null)) result.TryAdd(variable.Name, variable.Value);
        return result;
    }

    public static string Codes(string name) => string.Join(", ", name.Select(c => ((int)c).ToString(CultureInfo.InvariantCulture)));
    public static bool UsesCodes(StoryboardVariable variable) => variable.Ascii || variable.Name.Skip(1).Any(char.IsControl);
    public static string DisplayName(StoryboardVariable variable) => UsesCodes(variable)
        ? "ASCII: " + Codes(variable.Name[1..]) : variable.Name[1..];

    public static bool TryAscii(string text, out string name)
    {
        var result = new StringBuilder();
        foreach (string code in text.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value > 127)
            { name = ""; return false; }
            result.Append((char)value);
        }
        name = result.ToString();
        return ValidName(name);
    }

    public static bool ValidName(string name) => name.Length > 0 && name.IndexOfAny(new[] { '\n', '=' }) < 0;
    public static bool ValidValue(string value) => !value.Contains('\n');

    /// <summary>Rewrites actual references using definition priority, leaving similarly named keys alone.</summary>
    public static string Rewrite(string text, IReadOnlyDictionary<string, string> definitions, string key, string replacement)
    {
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length;)
        {
            string? match = text[i] == '$' ? definitions.Keys.FirstOrDefault(k => text.AsSpan(i).StartsWith(k, StringComparison.Ordinal)) : null;
            if (match == null) { result.Append(text[i++]); continue; }
            result.Append(match == key ? replacement : match);
            i += match.Length;
        }
        return result.ToString();
    }
}
