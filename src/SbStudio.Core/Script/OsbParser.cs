using System.Globalization;

namespace SbStudio.Core;

public sealed record Diagnostic(int Line, string Message, bool IsError, string SourceFile = "", string SourceText = "");

public sealed class ParseResult
{
    public Storyboard Storyboard = new();
    public List<Diagnostic> Diagnostics = new();
    public Dictionary<string, string> Variables = new();
    public Dictionary<SbObject, int> ObjectKeys = new();
    public Dictionary<SbObjectGroup, int> GroupKeys = new();
    /// <summary>
    /// Sprites that name a group (by its 1-based number) the text does not define. This is how a snippet,
    /// such as a copied object, refers to a group of the storyboard it came from.
    /// </summary>
    public Dictionary<SbSprite, int> GroupRefs = new();

    public bool HasErrors => Diagnostics.Any(d => d.IsError);
}

/// <summary>
/// Parses storyboard script: a whole .osb/.osu file (only [Events] and [Variables] are read) or a bare
/// snippet without section headers.
/// </summary>
public static class OsbParser
{
    public const string MetaPrefix = "//@";

    private static readonly string[] GeneratedCommentPrefixes =
    {
        "//Background and Video events",
        "//Storyboard Layer",
        "//Storyboard Sound Samples",
        "//Break Periods",
        "//Background Colour Transformations",
    };

    /// <param name="progress">Told from time to time how much of the text has been read, from 0 to 1.</param>
    public static ParseResult Parse(string text, Action<double>? progress = null, string sourceFile = "", IReadOnlyList<SbObjectGroup>? externalGroups = null,
        IReadOnlyList<StoryboardVariable>? variables = null)
    {
        var result = new ParseResult();
        if (variables != null)
        {
            result.Storyboard.Variables = variables.ToList();
            result.Variables = StoryboardVariables.Lookup(variables, null);
        }
        bool definitionsChangedAfterEvents = false;
        bool literalNext = false;
        VariableLine? quotedReference = null;
        int? pendingObjectKey = null;
        var objects = result.Storyboard.Objects;
        var substitutions = new Dictionary<string, VariableSubstitution>();
        VariableSubstitution Substitutions(string? scope)
        {
            string id = scope ?? "";
            if (!substitutions.TryGetValue(id, out var found))
                substitutions[id] = found = new VariableSubstitution(StoryboardVariables.Lookup(result.Storyboard.Variables, scope));
            return found;
        }

        string? section = null;
        SbSprite? sprite = null;
        SbGroup? group = null;
        var pendingComments = new List<string>();
        var pendingDepth = new List<SbCommand>();
        bool pendingFace = false;
        var deferredDepth=new Dictionary<SbSprite,(List<SbCommand> Commands,bool Face,int Line)>();
        string? pendingName = null, pendingDifficulty = null;
        bool pendingBackgroundPlaceholder = false;
        bool pendingHidden = false;
        bool? pendingLocked = null;
        bool objectFailed = false;
        int? pendingGroup = null;
        SbObjectGroup? definingGroup = null;
        var groupsByNumber = new Dictionary<int, SbObjectGroup>();
        var pluginDefinitions=new HashSet<SbObjectGroup>();
        // Groups that say they are in another group, with the number of that one: it may be defined further down.
        var groupParents = new List<(SbObjectGroup Group, int Parent)>();

        var lines = text.Split('\n');
        int lastContentLine = LastContentLine(lines);
        for (int i = 0; i < lines.Length; i++)
        {
            if (progress != null && (i & 0xFFF) == 0) progress((double)i / lines.Length);
            int lineNo = i + 1;
            // Whitespace can be part of a variable name. Keep the event text intact until expansion.
            string raw = lines[i].TrimEnd('\r');
            string trimmed = raw.TrimEnd(' ', '\t').TrimStart(' ', '\t', '_');
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                if (trimmed.StartsWith(MetaPrefix, StringComparison.Ordinal))
                {
                    string meta = trimmed.Substring(MetaPrefix.Length).Trim();
                    if (meta.StartsWith("objectref=", StringComparison.Ordinal) && int.TryParse(meta[10..], out int objectKey)) pendingObjectKey = objectKey;
                    else if (meta.StartsWith("groupref=", StringComparison.Ordinal) && definingGroup != null && int.TryParse(meta[9..], out int groupKey)) result.GroupKeys[definingGroup] = groupKey;
                    else if (meta.StartsWith("reference ", StringComparison.Ordinal))
                    {
                        try
                        {
                            quotedReference = System.Text.Json.JsonSerializer.Deserialize<VariableLine>(meta[10..]);
                            if (quotedReference == null || quotedReference.Text == null || quotedReference.Expanded == null ||
                                quotedReference.Text.Contains('\n') || quotedReference.Expanded.Contains('\n'))
                            { quotedReference = null; result.Diagnostics.Add(new Diagnostic(lineNo, L.T("variables.invalidDefinition"), true)); }
                        }
                        catch (System.Text.Json.JsonException) { result.Diagnostics.Add(new Diagnostic(lineNo, L.T("variables.invalidDefinition"), true)); }
                    }
                    else if (meta == "literal") literalNext = true;
                    else if (meta.StartsWith("variables ", StringComparison.Ordinal))
                    {
                        try
                        {
                            var definitions = System.Text.Json.JsonSerializer.Deserialize<List<StoryboardVariable>>(meta[10..]);
                            if (definitions != null && definitions.All(v => v != null && v.Name != null && v.Name.StartsWith('$') &&
                                v.Name.IndexOfAny(new[] { '\n', '=' }) < 0 && v.Value != null && !v.Value.Contains('\n')) &&
                                definitions.Select(v => (v.Name, v.Difficulty)).Distinct().Count() == definitions.Count)
                            {
                                definitionsChangedAfterEvents |= objects.Count > 0;
                                result.Storyboard.Variables = definitions;
                                result.Variables = StoryboardVariables.Lookup(definitions, null);
                                substitutions.Clear();
                            }
                            else result.Diagnostics.Add(new Diagnostic(lineNo, L.T("variables.invalidDefinition"), true));
                        }
                        catch (System.Text.Json.JsonException) { result.Diagnostics.Add(new Diagnostic(lineNo, L.T("variables.invalidDefinition"), true)); }
                    }
                    else if (meta.StartsWith("name=", StringComparison.OrdinalIgnoreCase)) pendingName = meta.Substring(5);
                    else if (meta.StartsWith("difficulty=", StringComparison.OrdinalIgnoreCase)) pendingDifficulty = meta.Substring(11).Trim();
                    else if (meta.Equals("hidden", StringComparison.OrdinalIgnoreCase)) pendingHidden = true;
                    else if (meta.Equals("locked", StringComparison.OrdinalIgnoreCase)) pendingLocked = true;
                    else if (meta.Equals("unlocked", StringComparison.OrdinalIgnoreCase)) pendingLocked = false;
                    else if (meta.Equals("beatmapbg", StringComparison.OrdinalIgnoreCase)) pendingBackgroundPlaceholder = true;
                    else
                    {
                        try
                        {
                            if (meta.StartsWith("plugin ", StringComparison.OrdinalIgnoreCase))
                            {
                                if(definingGroup==null) throw new FormatException(L.T("parse.groupCommandWithoutGroup"));
                                if(!pluginDefinitions.Add(definingGroup)) throw new FormatException(L.T("parse.pluginDuplicate"));
                                definingGroup.Plugin=PluginLink.Parse(meta[7..]);
                            }
                            else if(meta.StartsWith("param ",StringComparison.OrdinalIgnoreCase))
                            {
                                if(definingGroup?.Plugin==null || !PluginLink.ValidParameter(meta[6..])) throw new FormatException(L.T("parse.pluginParameter"));
                                definingGroup.Plugin.Parameters.Add(meta[6..]);
                            }
                            else if(meta.StartsWith("generated ",StringComparison.OrdinalIgnoreCase)) { }
                            else if (meta.StartsWith("ingroup=", StringComparison.OrdinalIgnoreCase))
                            {
                                pendingGroup = Time(meta.Substring(8));
                            }
                            else if (meta.StartsWith("group ", StringComparison.OrdinalIgnoreCase))
                            {
                                // group <number> <pivotX>,<pivotY> <x>,<y> <name>
                                // A group that is in another one has that one's number after its own: 3^1.
                                var fields = meta.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
                                if (fields.Length < 4) throw new FormatException(L.T("parse.groupIncomplete"));
                                var numbers = fields[1].Split('^');
                                int groupNumber = Time(numbers[0]);
                                int? parentNumber = numbers.Length > 1 ? Time(numbers[1]) : null;
                                var pivot = fields[2].Split(',');
                                var position = fields[3].Split(',');
                                if (pivot.Length != 2 || position.Length != 2) throw new FormatException(L.T("parse.groupPivot"));
                                definingGroup = new SbObjectGroup
                                {
                                    PivotX = Number(pivot[0]),
                                    PivotY = Number(pivot[1]),
                                    X = Number(position[0]),
                                    Y = Number(position[1]),
                                    Name = fields.Length > 4 ? fields[4].Trim() : "",
                                };
                                result.Storyboard.Groups.Add(definingGroup);
                                groupsByNumber[groupNumber] = definingGroup;
                                if (parentNumber is { } inside) groupParents.Add((definingGroup, inside));
                            }
                            else if (meta.StartsWith("precision ", StringComparison.OrdinalIgnoreCase))
                            {
                                if (definingGroup == null) throw new FormatException(L.T("parse.groupCommandWithoutGroup"));
                                var values = meta.Substring(10).Split(',');
                                if (values.Length != 2) throw new FormatException(L.T("parse.invalidValue", "precision", meta.Substring(10)));
                                int step = Time(values[0]);
                                double tolerance = Number(values[1]);
                                if (step < BakeOptions.LeastStep || !double.IsFinite(tolerance) || tolerance < BakeOptions.LeastTolerance)
                                    throw new FormatException(L.T("parse.invalidValue", "precision", meta.Substring(10)));
                                definingGroup.BakeStep = step;
                                definingGroup.BakeTolerance = tolerance;
                                definingGroup.HasBakePrecision = true;
                            }
                            else if (meta.Equals("camera", StringComparison.OrdinalIgnoreCase))
                            {
                                if(definingGroup==null) throw new FormatException(L.T("parse.groupCommandWithoutGroup"));
                                definingGroup.Camera ??= new SbCamera();
                            }
                            else if (meta.StartsWith("space ",StringComparison.OrdinalIgnoreCase))
                            {
                                if(definingGroup==null) throw new FormatException(L.T("parse.groupCommandWithoutGroup"));
                                var work=definingGroup.Clone();
                                foreach(var field in meta.Substring(6).Split(' ',StringSplitOptions.RemoveEmptyEntries))
                                {
                                    var pair=field.Split('='); if(pair.Length!=2) throw new FormatException(L.T("parse.spaceValue"));
                                    var v=pair[1].Split(',');
                                    if(pair[0]=="near" && v.Length==2) { work.NearClip=Number(v[0]); work.NearFade=Number(v[1]); }
                                    else if(pair[0]=="fog" && v.Length==3 && Enum.TryParse<FogMode>(v[0],true,out var fog)) { work.Fog=fog; work.FogStart=Number(v[1]); work.FogEnd=Number(v[2]); }
                                    else throw new FormatException(L.T("parse.spaceValue"));
                                }
                                if(!GroupSpace.ValidSpace(work.NearClip,work.NearFade,work.FogStart,work.FogEnd)) throw new FormatException(L.T("parse.spaceValue"));
                                definingGroup.NearClip=work.NearClip; definingGroup.NearFade=work.NearFade; definingGroup.Fog=work.Fog; definingGroup.FogStart=work.FogStart; definingGroup.FogEnd=work.FogEnd;
                            }
                            else if(meta.Equals("facecamera",StringComparison.OrdinalIgnoreCase)) pendingFace=true;
                            else if(meta.StartsWith("k ",StringComparison.OrdinalIgnoreCase) || meta.StartsWith("cam ",StringComparison.OrdinalIgnoreCase))
                            {
                                bool cam=meta.StartsWith("cam ",StringComparison.OrdinalIgnoreCase);
                                if(cam && definingGroup==null) throw new FormatException(L.T("parse.groupCommandWithoutGroup"));
                                var commands=ParseCommands(meta.Substring(cam?4:2).Split(',').Select(p=>p.Trim()).ToArray(),true);
                                if(commands.Any(c=>!(cam?SbInfo.CameraKinds:SbInfo.DepthKinds).Contains(c.Kind))) throw new FormatException(L.T("parse.spaceKind"));
                                if(cam) { definingGroup!.Camera ??= new SbCamera(); definingGroup.Camera.Commands.AddRange(commands); }
                                else pendingDepth.AddRange(commands);
                            }
                            else if (meta.StartsWith("g ", StringComparison.OrdinalIgnoreCase))
                            {
                                if (definingGroup == null) throw new FormatException(L.T("parse.groupCommandWithoutGroup"));
                                var commandParts = meta.Substring(2).Split(',');
                                for (int p = 0; p < commandParts.Length; p++) commandParts[p] = commandParts[p].Trim();
                                var commands = ParseCommands(commandParts, true);
                                if (commands.Any(c => !SbObjectGroup.Accepts(c.Kind)))
                                    throw new FormatException(L.T("parse.groupCommandKind"));
                                definingGroup.Commands.AddRange(commands);
                            }
                        }
                        catch (FormatException ex)
                        {
                            result.Diagnostics.Add(new Diagnostic(lineNo, ex.Message, true));
                        }
                    }
                }
                else if (!GeneratedCommentPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    pendingComments.Add(trimmed);
                }
                continue;
            }

            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                section = SectionName(trimmed.Substring(1, trimmed.Length - 2), lineNo, result);
                continue;
            }

            if (section != null && section.Equals("Variables", StringComparison.OrdinalIgnoreCase))
            {
                string definition = raw.TrimStart(' ', '\t', '_');
                int eq = definition.IndexOf('=');
                if (definition[0] == '$' && eq > 0)
                {
                    result.Variables[definition.Substring(0, eq)] = definition.Substring(eq + 1);
                    definitionsChangedAfterEvents |= objects.Count > 0;
                    string key = definition[..eq];
                    int existing = result.Storyboard.Variables.FindIndex(v => v.Name == key && v.Difficulty == null);
                    var variable = new StoryboardVariable(key, definition[(eq + 1)..]);
                    if (existing < 0) result.Storyboard.Variables.Add(variable);
                    else result.Storyboard.Variables[existing] = variable;
                    substitutions.Clear();
                }
                continue;
            }

            if (section != null && !section.Equals("Events", StringComparison.OrdinalIgnoreCase)) continue;
            if (section == null && trimmed.StartsWith("osu file format", StringComparison.OrdinalIgnoreCase)) continue;

            // A variable may stand for the entire command prefix, including its indentation.
            // Expand first, as lazer does, or a command such as "$move320,240" becomes an object.
            if (quotedReference != null)
            {
                if (raw == quotedReference.Expanded) raw = quotedReference.Text;
                else literalNext = true;
                quotedReference = null;
            }
            string original = raw;
            if (!literalNext && raw.IndexOf('$') >= 0)
            {
                raw = Substitutions(pendingDifficulty ?? sprite?.Difficulty).Expand(raw);
                if (raw.Length > 0 && raw[0] != ' ' && raw[0] != '_')
                    raw = Substitutions(pendingDifficulty).Expand(original);
            }
            string expanded = raw;
            literalNext = false;
            bool referenced = original != expanded;
            raw = raw.TrimEnd(' ', '\t');
            int depth = 0;
            while (depth < raw.Length && (raw[depth] == ' ' || raw[depth] == '_')) depth++;
            if (depth == 0 && i == lastContentLine && raw == "4,3,1,,NaN,-∞")
            {
                result.Diagnostics.Add(new Diagnostic(lineNo, L.T("parse.legacyTerminalSprite"), false));
                continue;
            }
            string content = raw.Substring(depth);
            var parts = content.Split(',');
            for (int p = 0; p < parts.Length; p++) parts[p] = parts[p].Trim();

            try
            {
                if (depth == 0)
                {
                    sprite = null;
                    group = null;
                    objectFailed = false;
                    var obj = ParseObject(parts, lineNo, result, pendingBackgroundPlaceholder);
                    if (obj != null)
                    {
                        obj.Comments.AddRange(pendingComments);
                        obj.Name = string.IsNullOrWhiteSpace(pendingName) ? null : pendingName;
                        obj.Hidden = pendingHidden;
                        obj.LockOverride = pendingLocked;
                        obj.Difficulty = string.IsNullOrEmpty(pendingDifficulty) ? null : pendingDifficulty;
                        objects.Add(obj);
                        if (pendingObjectKey is int key) result.ObjectKeys[obj] = key;
                        if (referenced) obj.VariableSource = new VariableLine(original, expanded, OsbWriter.Declaration(obj));
                        sprite = obj as SbSprite;
                        if (sprite != null) sprite.BeatmapBackgroundPlaceholder = pendingBackgroundPlaceholder;
                        if (sprite != null && pendingGroup is { } number)
                        {
                            if (groupsByNumber.TryGetValue(number, out var owner)) sprite.Group = owner;
                            else
                            {
                                result.GroupRefs[sprite] = number;
                                if(externalGroups!=null && number>=1 && number<=externalGroups.Count) sprite.Group=externalGroups[number-1];
                            }
                        }
                    }
                    if(pendingDepth.Count>0 || pendingFace)
                    {
                        if(sprite?.Group!=null) { sprite.DepthCommands=pendingDepth.Select(c=>c.CloneCommand()).ToList(); sprite.FaceCamera=pendingFace; }
                        else if(sprite!=null && pendingGroup!=null) deferredDepth[sprite]=(pendingDepth.Select(c=>c.CloneCommand()).ToList(),pendingFace,lineNo);
                        else result.Diagnostics.Add(new Diagnostic(lineNo,L.T("parse.spaceMember"),false));
                    }
                    pendingDepth.Clear(); pendingFace=false;
                    pendingComments.Clear();
                    pendingName = pendingDifficulty = null;
                    pendingHidden = false;
                    pendingLocked = null;
                    pendingBackgroundPlaceholder = false;
                    pendingGroup = null;
                    pendingObjectKey = null;
                    continue;
                }

                if (sprite == null)
                {
                    // One message is enough when the declaration above already failed.
                    if (!objectFailed)
                        result.Diagnostics.Add(new Diagnostic(lineNo, L.T("parse.commandWithoutObject"), true));
                    continue;
                }

                string head = parts[0].ToUpperInvariant();
                if (depth == 1 && head == "L")
                {
                    var loop = new SbLoop
                    {
                        Start = Time(Arg(parts, 1, "starttime")),
                        Count = Time(Arg(parts, 2, "loopcount")),
                    };
                    sprite.Nodes.Add(loop);
                    group = loop;
                    if (referenced) loop.VariableSource = new VariableLine(original, expanded, OsbWriter.BlockHeader(loop));
                }
                else if (depth == 1 && head == "T")
                {
                    var trigger = new SbTrigger { Name = Arg(parts, 1, "triggerType") };
                    trigger.Start = parts.Length > 2 && parts[2].Length > 0 ? TriggerBound(parts[2]) : int.MinValue;
                    trigger.End = parts.Length > 3 && parts[3].Length > 0 ? TriggerBound(parts[3]) : int.MaxValue;
                    trigger.Group = parts.Length > 4 && parts[4].Length > 0 ? Time(parts[4]) : 0;
                    sprite.Nodes.Add(trigger);
                    group = trigger;
                    if (referenced) trigger.VariableSource = new VariableLine(original, expanded, OsbWriter.BlockHeader(trigger));
                }
                else
                {
                    var commands = ParseCommands(parts);
                    if (referenced)
                    {
                        var source = new VariableLine(original, expanded, OsbWriter.NodeLines(commands, depth >= 2 && group != null ? 2 : 1), commands.Count);
                        for (int part = 0; part < commands.Count; part++) { commands[part].VariableSource = source; commands[part].VariablePart = part; }
                    }
                    if (depth >= 2 && group != null)
                    {
                        group.Commands.AddRange(commands);
                    }
                    else
                    {
                        if (depth >= 2)
                            result.Diagnostics.Add(new Diagnostic(lineNo, L.T("parse.deepIndent"), false));
                        group = null;
                        sprite.Nodes.AddRange(commands);
                    }
                }
            }
            catch (FormatException ex)
            {
                if (depth == 0) objectFailed = true;
                result.Diagnostics.Add(new Diagnostic(lineNo, ex.Message, true));
            }
        }

        // A group that names one the text does not have, or that would be in itself, stands in the storyboard.
        foreach (var (inner, number) in groupParents)
        {
            if (groupsByNumber.TryGetValue(number, out var outer) && !outer.IsWithin(inner)) inner.Parent = outer;
        }
        foreach(var (member,number) in result.GroupRefs.ToArray())
            if(groupsByNumber.TryGetValue(number,out var owner)) {member.Group=owner;result.GroupRefs.Remove(member);}
        foreach(var (member,meta) in deferredDepth)
        {
            if(member.Group!=null) {member.DepthCommands=meta.Commands;member.FaceCamera=meta.Face;}
            else result.Diagnostics.Add(new Diagnostic(meta.Line,L.T("parse.spaceMember"),false));
        }
        for (int i = 0; i < result.Diagnostics.Count; i++)
        {
            var diagnostic = result.Diagnostics[i];
            result.Diagnostics[i] = diagnostic with
            {
                SourceFile = sourceFile,
                SourceText = diagnostic.Line > 0 && diagnostic.Line <= lines.Length ? lines[diagnostic.Line - 1].TrimEnd('\r') : "",
            };
        }
        var instances=new HashSet<int>();
        int nextInstance=result.Storyboard.Groups.Select(g=>g.Plugin?.Instance??0).DefaultIfEmpty().Max();
        foreach(var owner in result.Storyboard.Groups)
            if(owner.Plugin is {} plugin && !instances.Add(plugin.Instance))
            {
                plugin.Instance=++nextInstance;instances.Add(plugin.Instance);
                result.Diagnostics.Add(new Diagnostic(0,L.T("parse.pluginRenumbered"),false));
            }
        // Repeated variable sections can change a definition halfway through a file. Earlier uses
        // whose meaning no longer agrees with the final definition are kept as their concrete values.
        if (definitionsChangedAfterEvents) foreach (var obj in result.Storyboard.AllObjects)
        {
            bool Current(VariableLine source) => Substitutions(obj.Difficulty).Expand(source.Text) == source.Expanded;
            if (obj.VariableSource is { } declaration && !Current(declaration)) obj.VariableSource = null;
            if (obj is not SbSprite member) continue;
            foreach (var node in member.Nodes)
            {
                if (node.VariableSource is { } source && !Current(source)) node.VariableSource = null;
                if (node is SbGroup block)
                    foreach (var command in block.Commands)
                        if (command.VariableSource is { } child && !Current(child)) command.VariableSource = null;
            }
        }
        return result;
    }

    private static int LastContentLine(string[] lines)
    {
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].TrimEnd('\r', ' ', '\t').TrimStart(' ', '\t', '_');
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("//", StringComparison.Ordinal) && !line.StartsWith(MetaPrefix, StringComparison.Ordinal)) continue;
            return i;
        }
        return -1;
    }

    private static string SectionName(string token, int lineNo, ParseResult result)
    {
        token = token.Trim();
        ReadOnlySpan<char> digits = token.AsSpan();
        if (!digits.IsEmpty && (digits[0] == '+' || digits[0] == '-')) digits = digits[1..];
        if (digits.IsEmpty || digits.ContainsAnyExceptInRange('0', '9')) return token;

        // Numeric section names use the legacy file's flags, not a decoder's enum order.
        string? name = int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number switch
        {
            1 => "General", 2 => "Colours", 4 => "Editor", 8 => "Metadata", 16 => "TimingPoints",
            32 => "Events", 64 => "HitObjects", 128 => "Difficulty", 256 => "Variables",
            _ => null,
        } : null;
        if (name != null) return name;
        result.Diagnostics.Add(new Diagnostic(lineNo, L.T("parse.numericSection", token), true));
        return token;
    }

    private static SbObject? ParseObject(string[] parts, int lineNo, ParseResult result, bool backgroundPlaceholder)
    {
        switch (parts[0].ToLowerInvariant())
        {
            case "sprite":
            case "4":
            {
                var s = new SbSprite();
                ReadSpriteHead(s, parts, backgroundPlaceholder);
                return s;
            }
            case "animation":
            case "6":
            {
                var s = new SbSprite { IsAnimation = true };
                ReadSpriteHead(s, parts);
                s.FrameCount = Math.Max(1, Time(Arg(parts, 6, "frameCount")));
                s.FrameDelay = Number(Arg(parts, 7, "frameDelay"));
                s.LoopType = parts.Length > 8 && parts[8].Length > 0 ? ParseEnum<SbLoopType>(parts[8], "looptype") : SbLoopType.LoopForever;
                return s;
            }
            case "sample":
            case "5":
            {
                return new SbSample
                {
                    Time = Time(Arg(parts, 1, "time")),
                    Layer = ParseEnum<SbLayer>(Arg(parts, 2, "layer"), "layer"),
                    Path = CleanPath(Arg(parts, 3, "filepath")),
                    Volume = parts.Length > 4 && parts[4].Length > 0 ? Number(parts[4]) : 100,
                };
            }
            // A video is told from the background picture by its file, as osu! does, whichever of the two
            // the line calls itself. The picture is not a storyboard object.
            case "0": case "1":
            case "background": case "video":
                if (parts.Length > 2 && Project.KindOf(CleanPath(parts[2])) == AssetKind.Video)
                    return new SbVideo { Time = Time(Arg(parts, 1, "time")), Path = CleanPath(parts[2]) };
                return null;
            // Beatmap events that are not storyboard objects either: break, colour.
            case "2": case "3":
            case "break": case "colour":
                return null;
            default:
                result.Diagnostics.Add(new Diagnostic(lineNo, L.T("parse.unknownObject", parts[0]), true));
                return null;
        }
    }

    private static void ReadSpriteHead(SbSprite s, string[] parts, bool backgroundPlaceholder = false)
    {
        s.Layer = ParseEnum<SbLayer>(Arg(parts, 1, "layer"), "layer");
        s.Origin = ParseEnum<SbOrigin>(Arg(parts, 2, "origin"), "origin");
        // An unquoted empty static image name resolves to .png in legacy files. Quoted empty
        // paths and explicit editor background placeholders retain their existing meaning.
        s.Path = !s.IsAnimation && parts.Length >= 6 && parts[3].Length == 0
            ? (backgroundPlaceholder ? "" : ".png")
            : CleanPath(Arg(parts, 3, "filepath"));
        s.X = Number(Arg(parts, 4, "x"));
        s.Y = Number(Arg(parts, 5, "y"));
    }

    /// <param name="curves">
    /// The command may have a transition drawn by hand where the number of its easing stands, as
    /// <see cref="EasingCurve.ToToken"/> writes it. That is so for the commands of a group of sprites alone.
    /// </param>
    private static List<SbCommand> ParseCommands(string[] parts, bool curves = false)
    {
        if (!Enum.TryParse<CommandKind>(parts[0], true, out var kind) || !Enum.IsDefined(kind) || char.IsDigit(parts[0][0]) || (!curves && SbInfo.IsEditorOnly(kind)))
            throw new FormatException(L.T("parse.unknownCommand", parts[0]));

        EasingCurve? curve = null;
        int easing = 0;
        if (!curves || !EasingCurve.TryParse(Arg(parts, 1, "easing"), out curve))
        {
            easing = Time(Arg(parts, 1, "easing"));
            if (easing < 0 || easing >= Easing.Count) throw new FormatException(L.T("parse.easingRange", easing, Easing.Count - 1));
        }
        int start = Time(Arg(parts, 2, "starttime"));
        int end = parts.Length > 3 && parts[3].Length > 0 ? Time(parts[3]) : start;
        var list = new List<SbCommand>();

        if (kind == CommandKind.P)
        {
            string p = Arg(parts, 4, "parameter").Trim('"').ToUpperInvariant();
            if (p != "H" && p != "V" && p != "A") throw new FormatException(L.T("parse.paramLetter", p));
            list.Add(new SbCommand { Kind = kind, Easing = easing, Start = start, End = end, Param = p[0] });
            return list;
        }

        int per = SbInfo.ValueCount(kind);
        int count = parts.Length - 4;
        if (count < per) throw new FormatException(L.T("parse.tooFewValues", kind, per, Math.Max(0, count)));
        if (count % per != 0) throw new FormatException(L.T("parse.valueMultiple", kind, per, count));

        int sets = count / per;
        var values = new double[sets][];
        for (int s = 0; s < sets; s++)
        {
            values[s] = new double[per];
            for (int v = 0; v < per; v++) values[s][v] = Number(parts[4 + s * per + v]);
        }

        if (sets == 1)
        {
            list.Add(new SbCommand(kind, easing, start, end, values[0]) { Curve = curve });
            return list;
        }

        // values 1,2,3,... chain into commands of equal duration placed one after another.
        int duration = end - start;
        for (int s = 0; s + 1 < sets; s++)
            list.Add(new SbCommand(kind, easing, start + s * duration, end + s * duration, values[s], values[s + 1]) { Curve = curve?.Clone() });
        return list;
    }

    private static string Arg(string[] parts, int index, string name)
    {
        if (index >= parts.Length || parts[index].Length == 0) throw new FormatException(L.T("parse.missingArg", name));
        return parts[index];
    }

    private static double Number(string s)
    {
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || double.IsNaN(v) || double.IsInfinity(v))
            throw new FormatException(L.T("parse.notNumber", s));
        return v;
    }

    private static int Time(string s)
    {
        double v = Number(s);
        if (v > int.MaxValue / 2 || v < int.MinValue / 2) throw new FormatException(L.T("parse.numberTooLarge", s));
        return (int)Math.Round(v);
    }

    // Preserve the unbounded sentinels written by OsbWriter without widening command time limits.
    private static int TriggerBound(string s) => s switch
    {
        "-2147483648" => int.MinValue,
        "2147483647" => int.MaxValue,
        _ => Time(s),
    };

    private static T ParseEnum<T>(string s, string name) where T : struct, Enum
    {
        if (Enum.TryParse<T>(s, true, out var value) && Enum.IsDefined(value)) return value;
        throw new FormatException(L.T("parse.invalidValue", name, s));
    }

    public static string CleanPath(string s) => s.Trim().Trim('"').Replace('\\', '/');
}
