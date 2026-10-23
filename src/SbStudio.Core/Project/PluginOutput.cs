namespace SbStudio.Core;

/// <summary>A private, immutable template. Every access to content produces independent copies.</summary>
public sealed class PluginOutput
{
    private readonly Storyboard template;
    private readonly double basisScale, basisOffset;
    public string Key { get; }
    public IReadOnlyList<string> Warnings { get; }
    public IReadOnlyList<string> SourceFiles { get; }
    public IReadOnlyList<SbObjectGroup> Groups=>CopyContent().Groups.AsReadOnly();
    public IReadOnlyList<SbSprite> Sprites=>CopyContent().Sprites.ToList().AsReadOnly();
    public int SpriteCount => template.Sprites.Count();
    public int CommandCount => template.Sprites.Sum(s=>s.Nodes.Sum(n=>n is SbGroup b ? b.Commands.Count : 1)+s.DepthCommands.Count)+template.Groups.Sum(g=>g.Commands.Count);
    public PluginOutput(string key, Storyboard content, IEnumerable<string>? warnings=null, IEnumerable<string>? sourceFiles=null, double basisScale=1, double basisOffset=0)
    {
        this.basisScale=basisScale; this.basisOffset=basisOffset;
        Key=key; var copy=content.CloneContent(); template=new Storyboard {Objects=copy.Objects,Parked=copy.Parked,Groups=copy.Groups};
        // Generated content is already concrete; its source's private variables are not project variables.
        StoryboardVariables.DetachReferences(template.AllObjects);
        Warnings=Array.AsReadOnly((warnings??Array.Empty<string>()).ToArray());
        SourceFiles=Array.AsReadOnly((sourceFiles??Array.Empty<string>()).ToArray());
    }
    public (List<SbObjectGroup> Groups,List<SbSprite> Sprites) Materialize(SbObjectGroup pluginGroup,int offset)
    {
        var copy=template.CloneContent();
        var sprites=copy.Objects.OfType<SbSprite>().ToList();
        if (pluginGroup.Plugin?.Effect is {} effect)
        {
            double scale = effect.TimeScale / basisScale, shift = effect.TimeOffset - scale * basisOffset + offset;
            if (scale != 1 || shift != 0)
            {
                var timing = new ClipTimeTransform(scale, shift);
                sprites = sprites.Select(timing.Sprite).ToList();
                foreach (var g in copy.Groups)
                {
                    g.Commands = timing.Commands(g.Commands, true);
                    if (g.Camera != null) g.Camera.Commands = timing.Commands(g.Camera.Commands, true);
                    g.Touch();
                }
            }
            offset = 0;
        }
        foreach(var g in copy.Groups) {g.Parent??=pluginGroup; if(offset!=0) g.ShiftTime(offset);}
        foreach(var s in sprites) {s.Group??=pluginGroup;s.Difficulty=pluginGroup.Plugin?.Effect?.OutputDifficulty; if(offset!=0) s.ShiftTime(offset);}
        return (copy.Groups,sprites);
    }
    public PluginOutput WithMetadata(IEnumerable<string> warnings, IEnumerable<string> sourceFiles) =>
        new(Key, template, warnings, sourceFiles, basisScale, basisOffset);
    public Storyboard CopyContent() {var c=template.CloneContent();return new Storyboard{Objects=c.Objects,Parked=c.Parked,Groups=c.Groups};}
    public static PluginOutput FromMembers(Storyboard doc,SbObjectGroup group)
    {
        var c=Storyboard.CloneContent(doc.SpritesIn(group,true).Cast<SbObject>().ToList(),Array.Empty<SbObject>(),doc.GroupsIn(group).ToList());
        foreach(var s in c.Objects.OfType<SbSprite>()) if(group.Plugin!.Offset!=0) s.ShiftTime(-group.Plugin.Offset);
        foreach(var g in c.Groups) if(group.Plugin!.Offset!=0) g.ShiftTime(-group.Plugin.Offset);
        return new PluginOutput(group.Plugin!.Key??"",new Storyboard{Objects=c.Objects,Groups=c.Groups}, basisScale: group.Plugin.Effect?.TimeScale ?? 1, basisOffset: group.Plugin.Effect?.TimeOffset ?? 0);
    }
}
