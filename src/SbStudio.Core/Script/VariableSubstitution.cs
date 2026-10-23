using System.Text;

namespace SbStudio.Core;

/// <summary>
/// osu!lazer replaces each variable once, in definition order, before reading event indentation.
/// Ordinary independent variables can be looked up together instead of scanning every definition
/// on every line. Unusual definitions keep the same ordered string-replacement semantics.
/// </summary>
internal sealed class VariableSubstitution
{
    private sealed class Node
    {
        public readonly Dictionary<char, Node> Children = new();
        public string? Value;
        public int Order = int.MaxValue;
    }

    private readonly Dictionary<string, string> variables;
    private readonly Node? root;

    public VariableSubstitution(Dictionary<string, string> variables)
    {
        this.variables = variables;
        // A replacement containing '$' can introduce a variable used later in the same pass.
        // A key containing another '$' can overlap a reference that starts inside it.
        if (variables.Any(v => !v.Key.StartsWith('$') || v.Key.AsSpan(1).Contains('$') || v.Value.Contains('$'))) return;
        root = new Node();
        int order = 0;
        foreach (var (key, value) in variables)
        {
            var node = root;
            foreach (char c in key.AsSpan(1))
            {
                if (!node.Children.TryGetValue(c, out var next)) node.Children[c] = next = new Node();
                node = next;
            }
            node.Value = value;
            node.Order = order++;
        }
    }

    public string Expand(string line)
    {
        int start = line.IndexOf('$');
        if (start < 0 || variables.Count == 0) return line;
        if (root == null) return ExpandInOrder(line);

        var expanded = new StringBuilder(line.Length + 32);
        int copied = 0;
        while (start >= 0)
        {
            var node = root;
            Node? match = node.Value != null ? node : null;
            int end = start + 1;
            for (int i = start + 1; i < line.Length && node.Children.TryGetValue(line[i], out var next); i++)
            {
                node = next;
                if (node.Value != null && (match == null || node.Order < match.Order))
                {
                    match = node;
                    end = i + 1;
                }
            }
            // An unrecognised '$' can join a later replacement to form a new variable name.
            // Fall back on the original line, so no replacement is applied twice.
            if (match == null) return ExpandInOrder(line);
            expanded.Append(line.AsSpan(copied, start - copied));
            expanded.Append(match.Value);
            copied = end;
            start = line.IndexOf('$', copied);
        }
        expanded.Append(line.AsSpan(copied));
        return expanded.ToString();
    }

    private string ExpandInOrder(string line)
    {
        foreach (var (key, value) in variables) line = line.Replace(key, value, StringComparison.Ordinal);
        return line;
    }
}
