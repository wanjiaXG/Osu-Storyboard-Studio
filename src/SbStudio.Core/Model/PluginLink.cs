using System.Text.Json;
using System.Text.RegularExpressions;

namespace SbStudio.Core;

public enum PluginKind { Linked, CSharp, Command, Unknown, Effect }

/// <summary>Persistent source identity. Generated descendants belong to its output.</summary>
public sealed class PluginLink
{
    public PluginKind Kind;
    public string Head = "linked";
    public int Instance;
    public string File = "";
    public string? Root;
    public int Offset;
    public string? Key, Version;
    public EffectBinding? Effect;
    public List<string> Fields = new();
    public List<string> Parameters = new();
    public bool Hidden {get=>Fields.Contains("hidden=true");set {Fields.Remove("hidden=true");if(value) Fields.Add("hidden=true");}}
    public bool Locked {get=>Fields.Contains("locked=true");set {Fields.Remove("locked=true");if(value) Fields.Add("locked=true");}}
    public PluginLink Clone() => new() { Kind=Kind, Head=Head, Instance=Instance, File=File, Root=Root,
        Offset=Offset, Key=Key, Version=Version, Fields=new(Fields), Parameters=new(Parameters), Effect=Effect?.Clone() };
    public static bool IsGenerated(SbObject obj) => obj is SbSprite s && s.Group?.PluginRoot != null;
    public static bool IsGenerated(SbObjectGroup group) => group.Parent?.PluginRoot != null;

    public static PluginLink Parse(string text)
    {
        var tokens = Regex.Matches(text, "[^\\s=]+=(?:\"(?:\\\\.|[^\"\\\\])*\"|[^\\s]+)|[^\\s]+").Select(m=>m.Value).ToArray();
        var link = new PluginLink { Head=tokens.FirstOrDefault() ?? "", Kind=PluginKind.Unknown };
        foreach (var field in tokens.Skip(1))
        {
            int eq=field.IndexOf('='); if(eq<1) {link.Fields.Add(field);continue;}
            string name=field[..eq], raw=field[(eq+1)..];
            string value;
            try { value=raw.StartsWith('"') ? JsonSerializer.Deserialize<string>(raw)! : raw; }
            catch(JsonException) { throw new FormatException(L.T("parse.pluginInstance")); }
            switch(name)
            {
                case "inst": if(!int.TryParse(value,out link.Instance)||link.Instance<1) throw new FormatException(L.T("parse.pluginInstance")); break;
                case "file": link.File=value; break;
                case "root": link.Root=value; break;
                case "offset": if(!int.TryParse(value,out link.Offset)) throw new FormatException(L.T("parse.pluginInstance")); break;
                case "key": link.Key=value; break;
                case "version": link.Version=value; break;
                case "data" when link.Head=="effect":
                    try { link.Effect=SbStudio.Effects.EffectJson.Deserialize<EffectBinding>(value); }
                    catch(Exception ex) when(ex is JsonException or NotSupportedException) { link.Fields.Add(field); }
                    break;
                default: link.Fields.Add(field); break;
            }
        }
        if(link.Instance<1) throw new FormatException(L.T("parse.pluginInstance"));
        link.Kind=link.Head=="effect" ? PluginKind.Effect : link.Head=="linked" ? PluginKind.Linked : link.Fields.Contains("kind=csharp") ? PluginKind.CSharp : link.Fields.Contains("kind=command") ? PluginKind.Command : PluginKind.Unknown;
        return link;
    }
    public string ToLine()
    {
        var fields=new List<string>{Head,"inst="+Instance};
        if(Kind==PluginKind.Linked || File.Length>0) fields.Add("file="+JsonSerializer.Serialize(File));
        if(Root!=null && !string.Equals(Root.Replace('\\','/').TrimEnd('/'),Path.GetDirectoryName(File)?.Replace('\\','/').TrimEnd('/'),StringComparison.OrdinalIgnoreCase)) fields.Add("root="+JsonSerializer.Serialize(Root));
        if(Offset!=0) fields.Add("offset="+Offset);
        if(Key!=null) fields.Add("key="+Key);
        if(Version!=null) fields.Add("version="+Version);
        if(Effect!=null) fields.Add("data="+JsonSerializer.Serialize(SbStudio.Effects.EffectJson.Serialize(Effect)));
        fields.AddRange(Fields);return string.Join(' ',fields);
    }
    public static bool ValidParameter(string text)
    {
        int eq=text.IndexOf('=');if(eq<1 || !Regex.IsMatch(text[..eq],"^[A-Za-z0-9_-]+$")) return false;
        try {using var json=JsonDocument.Parse(text[(eq+1)..]);return true;} catch(JsonException){return false;}
    }
}
