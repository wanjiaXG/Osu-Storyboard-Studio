using SbStudio.Core;

namespace SbStudio.App;

/// <summary>Builds a candidate script before an edit is allowed to change the live document.</summary>
internal static class VariableEditing
{
    internal static void AdoptDefinitions(Storyboard document, Storyboard imported)
    {
        bool conflict = imported.Variables.Any(v => document.Variables.Any(old => old.Name == v.Name && old.Difficulty == v.Difficulty && old.Value != v.Value));
        if (conflict) StoryboardVariables.DetachReferences(imported.AllObjects);
        foreach (var variable in imported.Variables)
            if (!document.Variables.Any(v => v.Name == variable.Name && v.Difficulty == variable.Difficulty)) document.Variables.Add(variable);
    }

    internal static ParseResult Apply(Storyboard document, StoryboardVariable? old, StoryboardVariable? value)
    {
        var content = document.CloneContent(skipGenerated: true);
        var copy = new Storyboard { Objects = content.Objects, Parked = content.Parked, Groups = content.Groups,
            Variables = document.Variables.ToList() };
        if (old == null) copy.Variables.Add(value!);
        else
        {
            int index = copy.Variables.IndexOf(old);
            if (index < 0) throw new InvalidOperationException("The variable is no longer in this document.");
            if (value == null) copy.Variables.RemoveAt(index);
            else copy.Variables[index] = value;
            if (value?.Name != old.Name)
            {
                // Removing a definition keeps its current result. Renaming retains the reference.
                string replacement = value?.Name ?? old.Value;
                var lookups = new Dictionary<string, Dictionary<string, string>>();
                Dictionary<string, string> Lookup(string? scope)
                {
                    string id = scope ?? "";
                    if (!lookups.TryGetValue(id, out var found)) lookups[id] = found = StoryboardVariables.Lookup(document.Variables, scope);
                    return found;
                }
                bool Applies(string? scope) => old.Difficulty == scope || old.Difficulty == null &&
                    !document.Variables.Any(v => v.Difficulty == scope && scope != null && v.Name == old.Name);
                VariableLine? Rewrite(VariableLine? source, string? scope)
                {
                    if (source == null || !Applies(scope)) return source;
                    var definitions = Lookup(scope);
                    if (value == null && old.Value.Contains('$'))
                    {
                        // A chained replacement can intentionally leave an earlier key unexpanded.
                        // Removing that step must not cause a second expansion of its concrete result.
                        var affected = new HashSet<string>(StringComparer.Ordinal) { old.Name };
                        bool added;
                        do
                        {
                            added = false;
                            foreach (var (key, text) in definitions)
                                if (!affected.Contains(key) && affected.Any(k => StoryboardVariables.Rewrite(text, definitions, k, "") != text))
                                    added |= affected.Add(key);
                        } while (added);
                        if (affected.Any(k => StoryboardVariables.Rewrite(source.Text, definitions, k, "") != source.Text)) return null;
                    }
                    return source with { Text = StoryboardVariables.Rewrite(source.Text, definitions, old.Name, replacement) };
                }
                foreach (var obj in copy.AllObjects)
                {
                    obj.VariableSource = Rewrite(obj.VariableSource, obj.Difficulty);
                    if (obj is not SbSprite sprite) continue;
                    foreach (var node in sprite.Nodes)
                    {
                        node.VariableSource = Rewrite(node.VariableSource, obj.Difficulty);
                        if (node is SbGroup block)
                            foreach (var command in block.Commands) command.VariableSource = Rewrite(command.VariableSource, obj.Difficulty);
                    }
                }
                for (int i = 0; i < copy.Variables.Count; i++)
                {
                    var variable = copy.Variables[i];
                    if (variable.Name == value?.Name && variable.Difficulty == old.Difficulty || !Applies(variable.Difficulty)) continue;
                    copy.Variables[i] = variable with { Value = StoryboardVariables.Rewrite(variable.Value, Lookup(variable.Difficulty), old.Name, replacement) };
                }
            }
        }
        return OsbParser.Parse(OsbWriter.WriteFile(copy, new OsbWriteOptions
        {
            IncludeEditorMeta = true,
            SafeVariableReferences = true,
            ObjectKeys = document.AllObjects.Where(o => !PluginLink.IsGenerated(o)).Zip(copy.AllObjects).ToDictionary(p => p.Second, p => p.First.Id),
            GroupKeys = document.Groups.Where(g => !PluginLink.IsGenerated(g)).Zip(copy.Groups).ToDictionary(p => p.Second, p => p.First.Id),
        }));
    }
}
