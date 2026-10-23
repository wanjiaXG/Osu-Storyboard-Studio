using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace SbStudio.Core;

public sealed record PluginImportResult(PluginOutput? Output,string? Error=null,bool Busy=false,bool Missing=false,
    double ParseMs=0,double HashMs=0,double CopyMs=0,EffectBinding? Effect=null,IReadOnlyList<SbStudio.Effects.EffectDiagnostic>? Diagnostics=null,
    SbStudio.Effects.EffectRequestIdentity? RequestIdentity=null,IDisposable? OutputLease=null);

public static class LinkedImport
{
    public static string SourceFile(Project project,PluginLink link) => Path.GetFullPath(Path.Combine(project.Folder,link.File));
    public static string SourceRoot(Project project,PluginLink link) => link.Root==null ? Path.GetDirectoryName(SourceFile(project,link))! : Path.GetFullPath(Path.Combine(project.Folder,link.Root));
    public static bool Allows(string root,string relative)
    {
        relative=relative.Replace('\\','/');
        if(Path.IsPathRooted(relative)||relative.Contains(':')||relative.Split('/').Contains("..")) return false;
        try
        {
            string boundary=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            string full=Path.GetFullPath(Path.Combine(boundary,relative));
            if(!full.StartsWith(boundary+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) return false;
            for(string? p=full;p!=null;p=Path.GetDirectoryName(p))
            {
                if((File.Exists(p)||Directory.Exists(p)) && (File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0) return false;
                if(string.Equals(p,boundary,StringComparison.OrdinalIgnoreCase)) break;
            }
            return true;
        }
        catch(Exception ex) when(ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) {return false;}
    }
    private static byte[] Read(string file,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();using var f=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        using var b=new MemoryStream();f.CopyTo(b);token.ThrowIfCancellationRequested();return b.ToArray();
    }
    public static PluginImportResult Import(Project project,PluginLink link,CancellationToken token=default,bool copyAssets=true)
    {
        try
        {
            string file=SourceFile(project,link),root=SourceRoot(project,link);
            if(!File.Exists(file)) return new(null,L.T("plugin.missing"),Missing:true);
            var timer=Stopwatch.StartNew();byte[] bytes=Read(file,token);
            var parsed=OsbParser.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'),sourceFile:file);
            if(parsed.HasErrors) {var d=parsed.Diagnostics.First(d=>d.IsError);return new(null,L.T("plugin.parseError",d.Line,d.Message));}
            var doc=parsed.Storyboard;
            var warnings=parsed.Diagnostics.Where(d=>!d.IsError)
                .Select(d=>L.T("plugin.parseError",d.Line,d.Message)).Distinct().ToList();
            int skipped=doc.Objects.RemoveAll(o=>o is not SbSprite || o is SbSprite {BeatmapBackgroundPlaceholder:true});
            if(skipped>0) warnings.Add(L.T("plugin.skipped",skipped));
            foreach(var g in doc.Groups)
            {
                if(g.Camera!=null||g.NearClip!=100||g.NearFade!=200||g.Fog!=FogMode.None||g.FogStart!=2000||g.FogEnd!=6000) warnings.Add(L.T("plugin.spaceRemoved"));
                g.Camera=null;g.NearClip=100;g.NearFade=200;g.Fog=FogMode.None;g.FogStart=2000;g.FogEnd=6000;g.Plugin=null;
            }
            foreach(var obj in doc.Objects)
            {
                if(obj.Difficulty!=null) warnings.Add(L.T("plugin.difficultyRemoved"));
                obj.Difficulty=null;obj.LockOverride=null;
            }
            doc.PruneGroups();double parseMs=timer.Elapsed.TotalMilliseconds;timer.Restart();
            var sourceFiles=new SortedDictionary<string,byte[]>(StringComparer.Ordinal);
            var copies=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
            var remapped=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var sprite in doc.Sprites)
            {
                token.ThrowIfCancellationRequested();string original=sprite.Path.Replace('\\','/');
                if(!Allows(root,original)) return new(null,L.T("plugin.pathError",original));
                string signature=original+"|"+sprite.FrameCount+"|"+sprite.IsAnimation;
                if(remapped.TryGetValue(signature,out var mapped)) {sprite.Path=mapped;continue;}
                var resolved=new List<(string Relative,byte[] Bytes)>();
                foreach(string name in AssetUsage.FilesOf(sprite))
                {
                    if(!Allows(root,name)) return new(null,L.T("plugin.pathError",name));
                    string? found=ImageAssets.Find(name,p=>File.Exists(Path.Combine(root,p)));
                    if(found!=null)
                    {
                        if(!Allows(root,found)) return new(null,L.T("plugin.pathError",found));
                        if(!sourceFiles.TryGetValue(found,out var content)) sourceFiles[found]=content=Read(Path.Combine(root,found),token);
                        resolved.Add((found,content));
                    }
                    else
                    {
                        if(!Allows(project.AssetsDir,name)) return new(null,L.T("plugin.pathError",name));
                        if(ImageAssets.Find(name,project.AssetExists)==null) warnings.Add(L.T("plugin.assetMissing",name));
                    }
                }
                if(resolved.Count==0) continue;
                using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach(var entry in resolved)
                {
                    if(sprite.IsAnimation) hash.AppendData(Encoding.UTF8.GetBytes(entry.Relative+"\n"));
                    hash.AppendData(entry.Bytes);
                }
                string dir="generated/linked-"+link.Instance+"/"+Convert.ToHexString(hash.GetHashAndReset())[..12].ToLowerInvariant()+"/";
                foreach(var entry in resolved) copies[dir+entry.Relative.Replace('\\','/')]=entry.Bytes;
                sprite.Path=dir+original;remapped[signature]=sprite.Path;
            }
            using var keyHash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            keyHash.AppendData(Encoding.UTF8.GetBytes("sbstudio-plugin-1\nlinked\n"));keyHash.AppendData(bytes);
            foreach(var entry in sourceFiles)
            {
                keyHash.AppendData(Encoding.UTF8.GetBytes(entry.Key.Replace('\\','/')+"\n"));keyHash.AppendData(SHA256.HashData(entry.Value));
            }
            string key=Convert.ToHexString(keyHash.GetHashAndReset())[..16].ToLowerInvariant();
            double hashMs=timer.Elapsed.TotalMilliseconds;timer.Restart();
            if(copyAssets) foreach(var entry in copies)
            {
                token.ThrowIfCancellationRequested();
                if(!Allows(project.AssetsDir,entry.Key)) return new(null,L.T("plugin.pathError",entry.Key));
                string target=project.AssetFullPath(entry.Key);
                if(File.Exists(target)&&new FileInfo(target).Length==entry.Value.Length) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
                try {File.WriteAllBytes(temp,entry.Value);File.Move(temp,target,true);} finally {if(File.Exists(temp)) File.Delete(temp);}
            }
            return new(new PluginOutput(key,doc,warnings,sourceFiles.Keys.Select(p=>Path.GetFullPath(Path.Combine(root,p))).Prepend(file)),
                ParseMs:parseMs,HashMs:hashMs,CopyMs:timer.Elapsed.TotalMilliseconds);
        }
        catch(OperationCanceledException) {throw;}
        catch(IOException) {return new(null,L.T("plugin.busy"),Busy:true);}
        catch(Exception ex) when(ex is UnauthorizedAccessException or ArgumentException or NotSupportedException) {return new(null,ex.Message);}
    }

    public static void Cleanup(Project project,Action<string>? log=null)
    {
        string generated=Path.Combine(project.AssetsDir,"generated");
        if(!Directory.Exists(generated)||(File.GetAttributes(generated)&FileAttributes.ReparsePoint)!=0) return;
        var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(Storyboard doc)
        {
            foreach(var obj in doc.AllObjects)
                foreach(string p in AssetUsage.FilesOf(obj))
                {
                    if(!Allows(project.AssetsDir,p)) continue;
                    string? found=ImageAssets.Find(p,project.AssetExists);if(found!=null) used.Add(project.AssetFullPath(found));
                }
        }
        Collect(project.Storyboard);
        string autosave=Path.Combine(project.Folder,"storyboard.autosave.osb");
        try
        {
            if(File.Exists(autosave))
            {
                var saved=OsbParser.Parse(File.ReadAllText(autosave));
                if(saved.HasErrors) {log?.Invoke(string.Join("\n",saved.Diagnostics.Select(d=>d.Message)));return;}
                Collect(saved.Storyboard);
            }
        }
        catch(Exception ex) {log?.Invoke(ex.Message);return;}
        // Only folders laid out the way an import writes them are touched: <owner>-<instance>/<12 hex digits>/.
        // A "generated" folder that came with the beatmap keeps everything else in it.
        static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        void Visit(string dir)
        {
            foreach(string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                if(!used.Contains(file) && !IsLink(file))
                    try {File.Delete(file);} catch(Exception ex) {log?.Invoke(ex.Message);}
            foreach(string child in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                if(!Directory.EnumerateFileSystemEntries(child).Any()) Directory.Delete(child);
            if(!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        try
        {
            foreach(string owner in Directory.EnumerateDirectories(generated))
            {
                if(IsLink(owner) || !OwnerFolder.IsMatch(Path.GetFileName(owner))) continue;
                foreach(string content in Directory.EnumerateDirectories(owner))
                    if(!IsLink(content) && ContentFolder.IsMatch(Path.GetFileName(content)) &&
                        !Directory.EnumerateFileSystemEntries(content, "*", SearchOption.AllDirectories).Any(IsLink))
                        Visit(content);
                if(!Directory.EnumerateFileSystemEntries(owner).Any()) Directory.Delete(owner);
            }
        }
        catch(Exception ex) {log?.Invoke(ex.Message);}
    }

    private static readonly System.Text.RegularExpressions.Regex OwnerFolder = new("^[a-z0-9_-]+-[0-9]+$");
    private static readonly System.Text.RegularExpressions.Regex ContentFolder = new("^[0-9a-f]{12}$");
}
