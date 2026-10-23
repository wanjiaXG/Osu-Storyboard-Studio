using System.Globalization;
using System.Text;

namespace SbStudio.Core;

public sealed class OsbWriteOptions
{
    /// <summary>Write "//@" editor lines (name, hidden, locked). Off for exported files.</summary>
    public bool IncludeEditorMeta;
    public bool SkipGenerated;
    /// <summary>Retain unchanged variable references in editable scripts. Exports always use concrete values.</summary>
    public bool PreserveVariables = true;
    /// <summary>Keep control characters out of native text widgets by quoting their reference line.</summary>
    public bool SafeVariableReferences;
    /// <summary>Write a concrete event after each quoted reference, readable by earlier editors too.</summary>
    public bool ConcreteVariableReferences;
    /// <summary>Optional identities used only while transforming a live document.</summary>
    public IReadOnlyDictionary<SbObject, int>? ObjectKeys;
    public IReadOnlyDictionary<SbObjectGroup, int>? GroupKeys;
    /// <summary>Keep the user's "//" comment lines.</summary>
    public bool IncludeComments = true;
    /// <summary>Leave out the end time or end values when they equal the start ones.</summary>
    public bool UseShorthand = true;
}

public static class OsbWriter
{
    private static readonly string[] LayerHeaders =
    {
        "//Storyboard Layer 0 (Background)",
        "//Storyboard Layer 1 (Fail)",
        "//Storyboard Layer 2 (Pass)",
        "//Storyboard Layer 3 (Foreground)",
        "//Storyboard Layer 4 (Overlay)",
    };

    /// <summary>A complete .osb file, with everything the storyboard holds: parked objects too.</summary>
    public static string WriteFile(Storyboard storyboard, OsbWriteOptions? options = null)
    {
        options ??= new OsbWriteOptions();
        if(options.SkipGenerated)
        {
            var copy=storyboard.CloneContent(true);
            var filtered=new Storyboard {Objects=copy.Objects,Parked=copy.Parked,Groups=copy.Groups,Variables=storyboard.Variables.ToList()};
            var result=WriteFile(filtered,new OsbWriteOptions {IncludeEditorMeta=options.IncludeEditorMeta,IncludeComments=options.IncludeComments,UseShorthand=options.UseShorthand,PreserveVariables=options.PreserveVariables,SafeVariableReferences=options.SafeVariableReferences,ConcreteVariableReferences=options.ConcreteVariableReferences});
            foreach(var g in storyboard.Groups.Where(g=>g.Plugin!=null))
            {
                string line="//@plugin "+g.Plugin!.ToLine()+"\n";
                result=result.Replace(line,line+"//@generated "+storyboard.SpritesIn(g,true).Count()+"\n");
            }
            return result;
        }
        var sb = new StringBuilder();
        if (options.IncludeEditorMeta && storyboard.Variables.Count > 0)
            sb.Append("//@variables ").Append(System.Text.Json.JsonSerializer.Serialize(storyboard.Variables)).Append('\n');
        sb.Append("[Events]\n");
        sb.Append("//Background and Video events\n");
        WriteVideos(sb, storyboard, options);

        // Groups exist only for the editor. Without the editor lines the sprites are written with their own
        // commands alone; use GroupBaker first to get a file that plays the group motion in osu!.
        if (options.IncludeEditorMeta) WriteGroups(sb, storyboard.Groups, options);

        WriteLayers(sb, storyboard, options);
        return sb.ToString();
    }

    /// <summary>
    /// The lines that define groups, each with the number it has in the list: the one its members name it
    /// by, and the groups that are in it. A group that is in one the list does not have is written as one
    /// that stands by itself.
    /// </summary>
    public static string WriteGroups(IReadOnlyList<SbObjectGroup> groups, OsbWriteOptions? options = null)
    {
        var sb = new StringBuilder();
        WriteGroups(sb, groups, options ?? new OsbWriteOptions { IncludeEditorMeta = true });
        return sb.ToString();
    }

    private static void WriteGroups(StringBuilder sb, IReadOnlyList<SbObjectGroup> groups, OsbWriteOptions options)
    {
        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            sb.Append(OsbParser.MetaPrefix).Append("group ").Append(i + 1);
            if (group.Parent != null)
            {
                for (int p = 0; p < groups.Count; p++)
                    if (groups[p] == group.Parent) sb.Append('^').Append(p + 1);
            }
            sb.Append(' ')
                .Append(Num(group.PivotX)).Append(',').Append(Num(group.PivotY)).Append(' ')
                .Append(Num(group.X)).Append(',').Append(Num(group.Y)).Append(' ')
                .Append(group.Name.Replace("\n", " ")).Append('\n');
            sb.Append(OsbParser.MetaPrefix).Append("precision ").Append(group.BakeStep).Append(',')
                .Append(group.BakeTolerance.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            if (options.GroupKeys?.TryGetValue(group, out int groupKey) == true) sb.Append("//@groupref=").Append(groupKey).Append('\n');
            if(group.Plugin is {} plugin)
            {
                sb.Append("//@plugin ").Append(plugin.ToLine()).Append('\n');
                foreach(var param in plugin.Parameters) sb.Append("//@param ").Append(param).Append('\n');
            }
            var fields = new List<string>();
            if(group.NearClip!=100 || group.NearFade!=200) fields.Add("near="+Num(group.NearClip)+","+Num(group.NearFade));
            if(group.Fog!=FogMode.None || group.FogStart!=2000 || group.FogEnd!=6000) fields.Add("fog="+group.Fog.ToString().ToLowerInvariant()+","+Num(group.FogStart)+","+Num(group.FogEnd));
            if(fields.Count>0) sb.Append(OsbParser.MetaPrefix).Append("space ").AppendJoin(' ',fields).Append('\n');
            if(group.Camera!=null)
            {
                sb.Append(OsbParser.MetaPrefix).Append("camera\n");
                foreach(var c in group.Camera.Commands) { sb.Append(OsbParser.MetaPrefix).Append("cam "); WriteCommand(sb,c,0,options); }
            }
            foreach (var c in group.Commands)
            {
                sb.Append(OsbParser.MetaPrefix).Append("g ");
                WriteCommand(sb, c, 0, options);
            }
        }
    }

    /// <summary>
    /// The storyboard part of an [Events] section alone: one block per layer and one for the samples. This
    /// is what a .osu file holds of its difficulty's storyboard, between its other events. The videos are
    /// not in it: see <see cref="WriteVideos(Storyboard, OsbWriteOptions?)"/>.
    /// </summary>
    public static string WriteLayers(Storyboard storyboard, OsbWriteOptions? options = null)
    {
        var sb = new StringBuilder();
        WriteLayers(sb, storyboard, options ?? new OsbWriteOptions());
        return sb.ToString();
    }

    /// <summary>
    /// The video lines alone. In a .osu file they stand with the background, ahead of the storyboard.
    /// </summary>
    public static string WriteVideos(Storyboard storyboard, OsbWriteOptions? options = null)
    {
        var sb = new StringBuilder();
        WriteVideos(sb, storyboard, options ?? new OsbWriteOptions());
        return sb.ToString();
    }

    private static void WriteVideos(StringBuilder sb, Storyboard storyboard, OsbWriteOptions options)
    {
        foreach (var obj in storyboard.AllObjects)
            if (obj is SbVideo v) WriteObject(sb, v, options, storyboard.Groups);
    }

    private static void WriteLayers(StringBuilder sb, Storyboard storyboard, OsbWriteOptions options)
    {
        for (int layer = 0; layer < SbInfo.LayerCount; layer++)
        {
            sb.Append(LayerHeaders[layer]).Append('\n');
            foreach (var obj in storyboard.AllObjects)
                if (obj is SbSprite s && (int)s.Layer == layer) WriteObject(sb, s, options, storyboard.Groups);
        }
        sb.Append("//Storyboard Sound Samples\n");
        foreach (var obj in storyboard.AllObjects)
            if (obj is SbSample a) WriteObject(sb, a, options, storyboard.Groups);
    }

    /// <summary>Script of some objects without any section header.</summary>
    /// <param name="groups">The storyboard's groups, so members can name theirs by number.</param>
    public static string WriteObjects(IEnumerable<SbObject> objects, OsbWriteOptions? options = null, IReadOnlyList<SbObjectGroup>? groups = null)
    {
        options ??= new OsbWriteOptions();
        var sb = new StringBuilder();
        foreach (var obj in objects) WriteObject(sb, obj, options, groups);
        return sb.ToString();
    }

    private static void WriteObject(StringBuilder sb, SbObject obj, OsbWriteOptions options, IReadOnlyList<SbObjectGroup>? groups)
    {
        if (options.IncludeComments)
            foreach (var comment in obj.Comments) sb.Append(comment).Append('\n');

        if (options.IncludeEditorMeta)
        {
            if (obj is SbSprite { BeatmapBackgroundPlaceholder: true }) sb.Append(OsbParser.MetaPrefix).Append("beatmapbg\n");
            if (!string.IsNullOrWhiteSpace(obj.Name)) sb.Append(OsbParser.MetaPrefix).Append("name=").Append(obj.Name!.Replace("\n", " ")).Append('\n');
            if (obj.Difficulty != null) sb.Append(OsbParser.MetaPrefix).Append("difficulty=").Append(obj.Difficulty).Append('\n');
            if (obj.OwnHidden) sb.Append(OsbParser.MetaPrefix).Append("hidden\n");
            if (obj.LockOverride is { } locked) sb.Append(OsbParser.MetaPrefix).Append(locked ? "locked\n" : "unlocked\n");
            if (obj is SbSprite { Group: { } group } && groups != null)
            {
                for (int i = 0; i < groups.Count; i++)
                    if (groups[i] == group) sb.Append(OsbParser.MetaPrefix).Append("ingroup=").Append(i + 1).Append('\n');
            }
        }

        if(options.IncludeEditorMeta && obj is SbSprite {Group:not null} member)
        {
            foreach(var c in member.DepthCommands) { sb.Append(OsbParser.MetaPrefix).Append("k "); WriteCommand(sb,c,0,options); }
            if(member.FaceCamera) sb.Append(OsbParser.MetaPrefix).Append("facecamera\n");
        }
        if (options.ObjectKeys?.TryGetValue(obj, out int objectKey) == true) sb.Append("//@objectref=").Append(objectKey).Append('\n');
        string declaration = Declaration(obj);
        if (options.IncludeEditorMeta && options.PreserveVariables && obj.VariableSource is { } source && source.Canonical == declaration)
            AppendReference(sb, source, options);
        else AppendConcrete(sb, declaration, options);
        if (obj is SbSprite sprite && !sprite.BeatmapBackgroundPlaceholder)
            WriteNodes(sb, sprite.Nodes, 1, options);
    }

    internal static string Declaration(SbObject obj)
    {
        var sb = new StringBuilder();
        switch (obj)
        {
            case SbSprite { BeatmapBackgroundPlaceholder: true } background:
                sb.Append("Sprite,Background,TopLeft,\"").Append(background.Path).Append("\",0,0\n");
                break;
            case SbSprite s:
                sb.Append(s.IsAnimation ? "Animation," : "Sprite,");
                sb.Append(s.Layer).Append(',').Append(s.Origin).Append(",\"").Append(s.Path).Append("\",");
                sb.Append(Num(s.X)).Append(',').Append(Num(s.Y));
                if (s.IsAnimation)
                    sb.Append(',').Append(s.FrameCount).Append(',').Append(Num(s.FrameDelay)).Append(',').Append(s.LoopType);
                sb.Append('\n');
                break;

            case SbSample a:
                sb.Append("Sample,").Append(a.Time).Append(',').Append((int)a.Layer).Append(",\"").Append(a.Path).Append("\",").Append(Num(a.Volume)).Append('\n');
                break;

            case SbVideo v:
                sb.Append("Video,").Append(v.Time).Append(",\"").Append(v.Path).Append("\"\n");
                break;
        }
        return sb.ToString();
    }

    private static void AppendConcrete(StringBuilder sb, string text, OsbWriteOptions options)
    {
        if (options.IncludeEditorMeta && text.Contains('$')) sb.Append("//@literal\n");
        sb.Append(text);
    }

    private static void AppendReference(StringBuilder sb, VariableLine source, OsbWriteOptions options)
    {
        if (options.ConcreteVariableReferences || options.SafeVariableReferences && source.Text.Any(char.IsControl))
        {
            sb.Append("//@reference ").Append(System.Text.Json.JsonSerializer.Serialize(source)).Append('\n');
            sb.Append(source.Expanded).Append('\n');
        }
        else sb.Append(source.Text).Append('\n');
    }

    internal static string NodeLines(IEnumerable<SbNode> nodes, int depth)
    {
        var sb = new StringBuilder();
        foreach (var node in nodes) WriteNode(sb, node, depth, new OsbWriteOptions());
        return sb.ToString();
    }

    internal static string BlockHeader(SbGroup block)
    {
        string all = NodeLines(new[] { block }, 1);
        return all[..(all.IndexOf('\n') + 1)];
    }

    private static void WriteNodes(StringBuilder sb, IReadOnlyList<SbNode> nodes, int depth, OsbWriteOptions options)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (options.IncludeEditorMeta && options.PreserveVariables && node is SbCommand && node.VariablePart == 0 && node.VariableSource is { } source &&
                i + source.Count <= nodes.Count)
            {
                var sequence = nodes.Skip(i).Take(source.Count).ToArray();
                if (sequence.Select((n, p) => n is SbCommand && n.VariableSource == source && n.VariablePart == p).All(v => v) &&
                    NodeLines(sequence, depth) == source.Canonical)
                { AppendReference(sb, source, options); i += source.Count - 1; continue; }
            }
            WriteNode(sb, node, depth, options);
        }
    }

    private static void WriteNode(StringBuilder sb, SbNode node, int depth, OsbWriteOptions options)
    {
        switch (node)
        {
            case SbCommand c:
                WriteCommand(sb, c, depth, options);
                break;
            case SbLoop l:
                string loopHeader = " L," + l.Start + "," + l.Count + "\n";
                if (options.IncludeEditorMeta && options.PreserveVariables && l.VariableSource is { } ls && ls.Canonical == loopHeader)
                    AppendReference(sb, ls, options);
                else sb.Append(loopHeader);
                WriteNodes(sb, l.Commands, 2, options);
                break;
            case SbTrigger t:
                var header = new StringBuilder(" T,").Append(t.Name);
                // Bounds left out mean no limit (as osu!lazer reads them). Only trailing ones can be left
                // out: an unbounded start before a bounded end, or a group number, needs every field.
                if (t.Group != 0 || t.End != int.MaxValue) header.Append(',').Append(t.Start).Append(',').Append(t.End);
                else if (t.Start != int.MinValue) header.Append(',').Append(t.Start);
                if (t.Group != 0) header.Append(',').Append(t.Group);
                header.Append('\n');
                string triggerHeader = header.ToString();
                if (options.IncludeEditorMeta && options.PreserveVariables && t.VariableSource is { } ts && ts.Canonical == triggerHeader)
                    AppendReference(sb, ts, options);
                else AppendConcrete(sb, triggerHeader, options);
                WriteNodes(sb, t.Commands, 2, options);
                break;
        }
    }

    private static void WriteCommand(StringBuilder sb, SbCommand c, int depth, OsbWriteOptions options)
    {
        if (depth > 0 && SbInfo.IsEditorOnly(c.Kind)) return;
        sb.Append(' ', depth).Append(c.Kind).Append(',');
        // A transition drawn by hand stands where the number of the easing does. Only the commands of a group
        // of sprites have one, which are written as editor lines with nothing before them: the lines of a
        // sprite are osb, and osb has numbers there and nothing else.
        if (c.Curve != null && depth == 0) sb.Append(c.Curve.ToToken());
        else sb.Append(c.Easing);
        sb.Append(',').Append(c.Start).Append(',');
        if (!(options.UseShorthand && c.End == c.Start)) sb.Append(c.End);

        if (c.Kind == CommandKind.P)
        {
            sb.Append(',').Append(c.Param).Append('\n');
            return;
        }

        foreach (double v in c.From) sb.Append(',').Append(Num(v));
        if (!(options.UseShorthand && SameValues(c.From, c.To)))
            foreach (double v in c.To) sb.Append(',').Append(Num(v));
        sb.Append('\n');
    }

    private static bool SameValues(double[] a, double[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Num(a[i]) != Num(b[i])) return false;
        return true;
    }

    public static string Num(double value)
    {
        string s = Math.Round(value, 5).ToString("0.#####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}
