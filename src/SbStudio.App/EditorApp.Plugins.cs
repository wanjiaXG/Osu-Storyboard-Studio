using System.Collections.Concurrent;
using System.Diagnostics;
using SbStudio.Core;

namespace SbStudio.App;

public enum PluginStatus { Ready, Importing, Failed, LinkMissing, Unsupported }

public interface IPluginSource
{
    bool Supported { get; }
    bool Available(Project project,PluginLink link);
    string? MainFile(Project project,PluginLink link);
    IEnumerable<string> WatchFolders(Project project,PluginLink link);
    string? ComputeKey(Project project,PluginLink link,CancellationToken token);
    PluginImportResult Generate(Project project,PluginLink link,CancellationToken token);
    string Description { get; }
    void DrawProperties(EditorApp app,SbObjectGroup group);
    void Reveal(Project project,PluginLink link);
}

internal sealed class LinkedPluginSource : IPluginSource
{
    public bool Supported=>true;
    public bool Available(Project project,PluginLink link)=>File.Exists(LinkedImport.SourceFile(project,link));
    public string? MainFile(Project project,PluginLink link)=>LinkedImport.SourceFile(project,link);
    public string Description=>L.T("plugin.linked");
    public IEnumerable<string> WatchFolders(Project project,PluginLink link)
    {
        return new[]{Path.GetDirectoryName(LinkedImport.SourceFile(project,link))!,LinkedImport.SourceRoot(project,link)}
            .Select(ExistingParent).Distinct(StringComparer.OrdinalIgnoreCase);
        static string ExistingParent(string path)
        {while(!Directory.Exists(path) && Path.GetDirectoryName(path) is {} parent) path=parent;return path;}
    }
    public string? ComputeKey(Project project,PluginLink link,CancellationToken token)=>LinkedImport.Import(project,link,token,false).Output?.Key;
    public PluginImportResult Generate(Project project,PluginLink link,CancellationToken token)=>LinkedImport.Import(project,link,token);
    public void DrawProperties(EditorApp app,SbObjectGroup group)=>Ui.PropertiesPanel.DrawLinkedSource(app,group);
    public void Reveal(Project project,PluginLink link)=>Ui.AssetsPanel.OpenInExplorer(LinkedImport.SourceFile(project,link),true);
}
internal sealed class UnsupportedPluginSource : IPluginSource
{
    public bool Supported=>false;
    public bool Available(Project project,PluginLink link)=>false;
    public string? MainFile(Project project,PluginLink link)=>null;
    public string Description=>L.T("plugin.unsupported");
    public IEnumerable<string> WatchFolders(Project project,PluginLink link)=>Array.Empty<string>();
    public string? ComputeKey(Project project,PluginLink link,CancellationToken token)=>link.Key;
    public PluginImportResult Generate(Project project,PluginLink link,CancellationToken token)=>new(null);
    public void DrawProperties(EditorApp app,SbObjectGroup group) { }
    public void Reveal(Project project,PluginLink link) { }
}

public sealed class PluginRuntime : IDisposable
{
    public PluginStatus Status;
    public string? Error;
    public PluginOutput? Output;
    public double ElapsedMs;
    public IReadOnlyList<SbStudio.Effects.EffectDiagnostic> Diagnostics = [];
    public string Stage = "";
    public double Progress;
    internal SbStudio.Effects.EffectRequestIdentity? RequestIdentity;
    internal readonly ConcurrentQueue<SbStudio.Effects.HostResponse> Messages = new();
    internal string Identity="";
    internal string InputSignature="";
    internal long InputCheck;
    internal readonly List<FileSystemWatcher> Watchers=new();
    internal CancellationTokenSource? Cancel;
    internal Task<PluginImportResult>? Task;
    internal readonly ConcurrentQueue<long> Changes=new();
    internal readonly ConcurrentQueue<string> WatchErrors=new();
    internal long Due;
    internal int Retries;
    internal string? WatchError;
    public void Dispose() {Cancel?.Cancel();Cancel?.Dispose();foreach(var w in Watchers) w.Dispose();Watchers.Clear();}
}

public sealed partial class EditorApp
{
    private readonly Dictionary<PluginKind,IPluginSource> pluginSources=new() {[PluginKind.Linked]=new LinkedPluginSource()};
    private readonly IPluginSource unsupportedPluginSource=new UnsupportedPluginSource();
    private readonly Dictionary<int,PluginRuntime> plugins=new();
    private readonly LinkedList<PluginOutput> releasedOutputs=new();
    private readonly Dictionary<int,(string Identity,PluginOutput Output)> releasedInstances=new();
    private bool pluginDirty,pluginsLoading,cleanupPlugins;
    private string effectProjectSession = Guid.NewGuid().ToString("N");
    private long effectGeneration;
    public IReadOnlyDictionary<int,PluginRuntime> Plugins=>plugins;
    public int ImportingPlugins=>plugins.Values.Count(r=>r.Status==PluginStatus.Importing);
    public IPluginSource SourceOf(PluginLink link)=>link.Kind==PluginKind.Effect
        ? link.Effect?.Supported==true ? effectSource??=new(this) : unsupportedPluginSource
        : pluginSources.GetValueOrDefault(link.Kind,unsupportedPluginSource);
    internal void RegisterPluginSource(PluginKind kind,IPluginSource source)=>pluginSources[kind]=source;
    private static string PluginIdentity(PluginLink p)=>p.Effect is {} e ? SbStudio.Effects.EffectJson.Serialize(new { e.InstanceGuid,e.PackageHash,e.EffectId,e.SourceKind,e.SourceLocation,e.DurationMs,e.TimeScale,e.TimeOffset,e.InputDifficulty,e.OutputDifficulty,Parameters=p.Parameters.Order().ToArray(),Offset=e.Definition?.Inputs.HasFlag(SbStudio.Effects.EffectInputs.AbsoluteTime)==true?p.Offset:0 }) : p.Kind+"|"+p.Head+"|"+p.File+"|"+p.Root;
    private IEnumerable<SbObject> HistoryObjects=>Doc.Objects.Where(o=>!PluginLink.IsGenerated(o));
    private List<SbObjectGroup> HistoryGroups=>Doc.Groups.Where(g=>!PluginLink.IsGenerated(g)).ToList();
    private bool RejectPluginEdit(bool condition)
    {
        if(!condition) return false;SetStatus(L.T("status.pluginLocked"));return true;
    }
    private void ReleasePlugins()
    {
        effectLifetime.Cancel(); effectLifetime.Dispose(); effectLifetime = new();
        effectProjectSession = Guid.NewGuid().ToString("N");
        foreach(var runtime in plugins.Values) runtime.Dispose();plugins.Clear();releasedOutputs.Clear();releasedInstances.Clear();
        pluginDirty=false;pluginsLoading=false;cleanupPlugins=false;
        effectHosts?.Dispose();effectHosts=null;
        effectHistoryPins?.Dispose(); effectHistoryPins = null;
        if (effectCollection is { IsCompleted: false }) try { effectCollection.GetAwaiter().GetResult(); } catch (Exception ex) { AppSettings.Log(ex.Message); }
        if (Project is {} project) Effects.EffectResultPins.Collect(project);
        nextEffectCollection = 0;
    }
    private void RememberOutput(PluginOutput? output)
    {
        if(output==null) return;
        // Large effects restore from disk; small recent results also survive a damaged disk cache.
        if (output.Key.StartsWith("effect-", StringComparison.Ordinal) && output.SpriteCount > 5000) return;
        var existing=releasedOutputs.FirstOrDefault(o=>o.Key==output.Key);
        if(existing!=null) releasedOutputs.Remove(existing);
        releasedOutputs.AddFirst(output);while(releasedOutputs.Count>8) releasedOutputs.RemoveLast();
        while (releasedOutputs.Count > 1 && releasedOutputs.Where(o => o.Key.StartsWith("effect-", StringComparison.Ordinal)).Sum(o => o.SpriteCount) > 5000)
            releasedOutputs.RemoveLast();
    }
    private void SyncPlugins(bool restore=false)
    {
        if(Project==null) return;
        var groups=Doc.Groups.Where(g=>g.Plugin!=null && !PluginLink.IsGenerated(g)).ToArray();
        var instances=groups.Select(g=>g.Plugin!.Instance).ToHashSet();
        foreach(int id in plugins.Keys.Where(id=>!instances.Contains(id)).ToArray())
        {
            if(plugins[id].Output is {} output) releasedInstances[id]=(plugins[id].Identity,output);
            RememberOutput(plugins[id].Output);plugins[id].Dispose();plugins.Remove(id);
            foreach(var expired in releasedInstances.Where(e=>!releasedOutputs.Contains(e.Value.Output)).Select(e=>e.Key).ToArray()) releasedInstances.Remove(expired);
        }
        foreach(var group in groups)
        {
            var link=group.Plugin!;string identity=PluginIdentity(link);
            if(plugins.TryGetValue(link.Instance,out var old) && old.Identity!=identity)
            {RememberOutput(old.Output);old.Dispose();plugins.Remove(link.Instance);}
            if(!plugins.TryGetValue(link.Instance,out var runtime))
            {
                runtime=new PluginRuntime {Identity=identity};plugins[link.Instance]=runtime;
                runtime.Output=PluginOutput.FromMembers(Doc,group);
                if(link.Kind==PluginKind.Effect && runtime.Output.SpriteCount==0 && link.Key!=null)
                    runtime.Output=Effects.EffectOutputStore.Restore(Project,link.Key)??runtime.Output;
                if(restore && runtime.Output.SpriteCount==0)
                    runtime.Output=releasedInstances.TryGetValue(link.Instance,out var saved) && saved.Identity==identity ? saved.Output : releasedOutputs.FirstOrDefault(o=>o.Key==link.Key);
                var source=SourceOf(link);var project=Project;
                if(!source.Supported) runtime.Status=PluginStatus.Unsupported;
                else
                {
                    try
                    {
                        foreach(string folder in source.WatchFolders(Project,link))
                        {
                            var watcher=new FileSystemWatcher(folder) {IncludeSubdirectories=true,NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName|NotifyFilters.LastWrite|NotifyFilters.Size};
                            void Changed(string path,bool created)
                            {
                                if(link.Kind==PluginKind.Effect)
                                {
                                    if(path.Split(Path.DirectorySeparatorChar).Any(n=>n is "bin" or "obj" or ".git" or ".build")) return;
                                    string effectRoot=Effects.EffectPluginSource.Root(project,link);
                                    if(link.Effect?.SourceKind=="development" && path.StartsWith(effectRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || runtime.Output?.SourceFiles.Contains(path,StringComparer.OrdinalIgnoreCase)==true)
                                        runtime.Changes.Enqueue(Environment.TickCount64);
                                    return;
                                }
                                if(created || runtime.Output?.SourceFiles.Contains(path,StringComparer.OrdinalIgnoreCase)==true || path.Equals(source.MainFile(project,link),StringComparison.OrdinalIgnoreCase)) runtime.Changes.Enqueue(Environment.TickCount64);
                            }
                            watcher.Changed+=(_,e)=>Changed(e.FullPath,false);watcher.Created+=(_,e)=>Changed(e.FullPath,true);
                            watcher.Deleted+=(_,e)=>Changed(e.FullPath,false);watcher.Renamed+=(_,e)=>Changed(e.FullPath,true);
                            watcher.Error+=(_,e)=>runtime.WatchErrors.Enqueue(e.GetException().Message);
                            runtime.Watchers.Add(watcher);watcher.EnableRaisingEvents=true;
                        }
                    }
                    catch(Exception ex) {runtime.WatchError=L.T("plugin.watchError")+" "+ex.Message;runtime.Error=runtime.WatchError;AppSettings.Log(runtime.Error);}
                    StartPluginImport(group,runtime);
                }
            }
            if((restore || link.Kind==PluginKind.Effect && Doc.SpritesIn(group,true).Any()==false) && runtime.Output!=null) PutPluginOutput(group,runtime.Output);
        }
        RefreshEffectPins();
    }
    private void StartPluginImport(SbObjectGroup group,PluginRuntime runtime)
    {
        if(Project==null || group.Plugin==null) return;
        runtime.Cancel?.Cancel();runtime.Cancel?.Dispose();runtime.Cancel=new();
        var token=runtime.Cancel.Token;var link=group.Plugin.Clone();var project=Project;var source=SourceOf(link);
        if(!source.Supported) {runtime.Status=PluginStatus.Unsupported;return;}
        runtime.Status=PluginStatus.Importing;runtime.Due=0;
        runtime.Stage = ""; runtime.Progress = 0; runtime.Diagnostics = [];
        runtime.RequestIdentity = link.Effect is {} binding
            ? new(effectProjectSession, binding.InstanceGuid, ++effectGeneration, "",
                SbStudio.Effects.PackageFiles.Hash(System.Text.Encoding.UTF8.GetBytes(PluginIdentity(link)))) : null;
        if (link.Effect != null) runtime.InputSignature = Effects.EffectPluginSource.InputSignature(project, link.Effect);
        var identity = runtime.RequestIdentity;
        var generate=source is Effects.EffectPluginSource effect ? effect.Prepare(project,link,identity,runtime.Messages.Enqueue) : new Func<CancellationToken,PluginImportResult>(t=>source.Generate(project,link,t));
        runtime.Task=Task.Run(()=>{var sw=Stopwatch.StartNew();var result=generate(token);runtime.ElapsedMs=sw.Elapsed.TotalMilliseconds;return result;},token);
    }
    public void ReimportPlugin(SbObjectGroup group)
    {
        SyncPlugins();if(group.Plugin!=null && plugins.TryGetValue(group.Plugin.Instance,out var r)) {r.Retries=0;StartPluginImport(group,r);}
    }
    public void ReimportPlugins() {if(Project!=null) foreach(var g in Doc.Groups.Where(g=>g.Plugin!=null).ToArray()) ReimportPlugin(g);}
    internal void PumpPlugins()=>UpdatePlugins();
    private void UpdatePlugins()
    {
        if(Project==null) return;
        if(Doc.Groups.Any(g=>g.Plugin?.Kind==PluginKind.Effect && (!plugins.TryGetValue(g.Plugin.Instance,out var r) || r.Identity!=PluginIdentity(g.Plugin)))) SyncPlugins();
        foreach(var entry in plugins.ToArray())
        {
            var runtime=entry.Value;var group=Doc.Groups.FirstOrDefault(g=>g.Plugin?.Instance==entry.Key);
            if(group==null) continue;
            if (group.Plugin?.Effect is {} binding && Environment.TickCount64 >= runtime.InputCheck)
            {
                runtime.InputCheck = Environment.TickCount64 + 300;
                string signature = Effects.EffectPluginSource.InputSignature(Project, binding);
                if (signature != runtime.InputSignature)
                { runtime.InputSignature = signature; runtime.Changes.Enqueue(Environment.TickCount64); binding.DesiredInputKey = "pending"; }
            }
            while(runtime.Changes.TryDequeue(out long changed))
            {
                runtime.Due=changed+300;runtime.Retries=0;
                if(group.Plugin?.Kind==PluginKind.Effect) {runtime.Cancel?.Cancel();runtime.Task=null;runtime.RequestIdentity=null;runtime.Status=PluginStatus.Importing;group.Plugin.Effect!.DesiredInputKey="pending";}
            }
            while(runtime.WatchErrors.TryDequeue(out string? error))
            {runtime.WatchError=L.T("plugin.watchError")+" "+error;runtime.Error=runtime.WatchError;runtime.Status=PluginStatus.Failed;AppSettings.Log(runtime.WatchError);DocRevision++;}
            if(runtime.Due!=0 && Environment.TickCount64>=runtime.Due) {StartPluginImport(group,runtime);}
            while (runtime.Messages.TryDequeue(out var message))
            {
                if (!AcceptEffectMessage(runtime, message.Identity)) continue;
                if (message.Kind == SbStudio.Effects.HostMessageKind.Progress) { runtime.Stage = message.Stage; runtime.Progress = message.Progress; }
                else runtime.Diagnostics = runtime.Diagnostics.Concat(message.Diagnostics).Take(201).ToArray();
            }
            if(runtime.Task is not {IsCompleted:true} task) continue;
            runtime.Task=null;
            PluginImportResult result;
            try {result=task.GetAwaiter().GetResult();}
            catch(OperationCanceledException) {continue;}
            catch(Exception ex) {result=new(null,ex.Message);}
            using var outputLease = result.OutputLease;
            if (result.RequestIdentity != null && !AcceptEffectMessage(runtime, result.RequestIdentity)) continue;
            runtime.Diagnostics = result.Diagnostics ?? [];
            if(result.Busy && ++runtime.Retries<10) {runtime.Due=Environment.TickCount64+300;continue;}
            if(result.Output is not {} output)
            {runtime.Status=result.Missing ? PluginStatus.LinkMissing : PluginStatus.Failed;runtime.Error=result.Error;DocRevision++;continue;}
            runtime.Retries=0;
            var shared=plugins.Values.Select(r=>r.Output).Concat(releasedOutputs).FirstOrDefault(o=>o?.Key==output.Key && o.SourceFiles.Count>0);
            output=shared??output;
            bool changedKey=group.Plugin!.Key!=output.Key;
            bool missingAssets=Doc.SpritesIn(group,true).SelectMany(AssetUsage.FilesOf).Any(p=>ImageAssets.Find(p,Project.AssetExists)==null);
            if(shared==null && !changedKey && !missingAssets && runtime.Output is {} savedOutput && savedOutput.SourceFiles.Count==0)
                output=savedOutput.WithMetadata(output.Warnings,output.SourceFiles);
            var previousOutput = runtime.Output;
            var previousBinding = group.Plugin!.Effect;
            RememberOutput(runtime.Output);runtime.Output=output;runtime.Status=runtime.WatchError==null?PluginStatus.Ready:PluginStatus.Failed;runtime.Error=runtime.WatchError;
            if(result.Effect!=null)
            {
                if (SbStudio.Effects.EffectJson.Serialize(group.Plugin!.Effect) != SbStudio.Effects.EffectJson.Serialize(result.Effect)) pluginDirty = true;
                group.Plugin.Effect=result.Effect;
            }
            // The import has just written its files: the list of assets has them now, not once the watcher
            // gets round to it, or every new member would be reported missing in the meantime.
            if(changedKey || missingAssets)
            {
                try { RefreshAssets(); PutPluginOutput(group,output); pluginDirty=true; }
                catch (Exception ex) when (ex is InvalidOperationException or OverflowException)
                {
                    runtime.Output=previousOutput; group.Plugin!.Effect=previousBinding;
                    runtime.Status=PluginStatus.Failed; runtime.Error=ex.Message; DocRevision++; continue;
                }
            }
            if(group.Plugin?.Kind==PluginKind.Effect) UpdateCommittedEffectResult(group);
            DocRevision++;
        }
        if(pluginsLoading && ImportingPlugins==0) {pluginsLoading=false;LoadImages();}
        if(cleanupPlugins && !pluginsLoading && imagesToLoad==0) {cleanupPlugins=false;LinkedImport.Cleanup(Project,AppSettings.Log);}
        CollectEffectCache();
    }
    private bool AcceptEffectMessage(PluginRuntime runtime, SbStudio.Effects.EffectRequestIdentity identity)
    {
        if (runtime.RequestIdentity is not {} expected || identity.ProjectSessionId != effectProjectSession ||
            identity.ProjectSessionId != expected.ProjectSessionId || identity.InstanceGuid != expected.InstanceGuid ||
            identity.Generation != expected.Generation || identity.DesiredInputKey != expected.DesiredInputKey ||
            expected.CodeRevision.Length > 0 && expected.CodeRevision != identity.CodeRevision) return false;
        runtime.RequestIdentity = identity;
        return true;
    }
    internal void PutPluginOutput(SbObjectGroup group,PluginOutput output)
    {
        var content=output.Materialize(group,group.Plugin!.Offset);
        var removed=Doc.SpritesIn(group,true).ToHashSet();
        Doc.Objects.RemoveAll(o=>o is SbSprite s && removed.Contains(s));Doc.Parked.RemoveAll(o=>o is SbSprite s && removed.Contains(s));
        foreach(var s in removed) s.Group=null;
        foreach(var g in Doc.GroupsIn(group).ToArray()) {Doc.Groups.Remove(g);g.Parent=null;}
        Doc.Groups.InsertRange(Doc.Groups.IndexOf(group)+1,content.Groups);Doc.Objects.AddRange(content.Sprites);
        group.Plugin.Key=output.Key;Arrange();DocRevision++;
        foreach(var s in content.Sprites) foreach(string path in AssetUsage.FilesOf(s)) Textures?.Preload(path);
    }
    public SbObjectGroup? LinkOsb(string file,string? root=null)
    {
        if(Project==null || RejectPluginEdit(OpenGroup?.PluginRoot!=null)) return null;
        CommitEdits();
        string Stored(string path) {string relative=Path.GetRelativePath(Project.Folder,Path.GetFullPath(path));return relative.StartsWith("..")||Path.IsPathRooted(relative) ? Path.GetFullPath(path) : relative.Replace('\\','/');}
        var group=new SbObjectGroup {Name=Path.GetFileNameWithoutExtension(file),Parent=OpenGroup,
            Plugin=new PluginLink {Kind=PluginKind.Linked,Instance=Doc.Groups.Select(g=>g.Plugin?.Instance??0).DefaultIfEmpty().Max()+1,File=Stored(file),Root=root==null||Path.GetFullPath(root)==Path.GetDirectoryName(Path.GetFullPath(file)) ? null : Stored(root)}};
        Doc.Groups.Add(group);SelectGroup(group);Touch();CommitEdits();SetPluginHistory(HistoryAction.LinkPlugin);SyncPlugins();return group;
    }
    private void SetPluginHistory(HistoryAction action) {if(committed!=null) committed=committed with {HistoryLabel=new(action)};HistoryRevision++;}
    internal (SbObjectGroup Group, bool Copy)? PendingEffectConversion;
    private bool CanConvertEffect(SbObjectGroup group, bool copy, bool useLast)
    {
        if (group.Plugin?.Kind != PluginKind.Effect) return true;
        if (string.IsNullOrEmpty(group.Plugin.Key)) { SetStatus(L.T("effects.noResult")); return false; }
        if (!useLast && plugins.TryGetValue(group.Plugin.Instance, out var runtime) && runtime.Status != PluginStatus.Ready)
        { PendingEffectConversion = (group, copy); return false; }
        if (Project == null || Doc.SpritesIn(group, true).SelectMany(AssetUsage.FilesOf).Any(p => ImageAssets.Find(p, Project.AssetExists) == null))
        { SetStatus(L.T("effects.missingAssets")); return false; }
        return true;
    }
    public void DetachPlugin(SbObjectGroup group, bool useLast = false)
    {
        if(group.Plugin==null) return;
        if (!CanConvertEffect(group, false, useLast)) return;
        CommitEdits();
        foreach(var s in Doc.SpritesIn(group,true)) {if(group.Plugin.Hidden) s.Hidden=true;if(group.Plugin.Locked) s.Locked=true;}
        group.Plugin=null;SyncPlugins();Touch();CommitEdits();SetPluginHistory(HistoryAction.DetachPlugin);SetStatus(L.T("status.pluginDetached"));
    }
    public void DetachPluginCopy(SbObjectGroup group, bool useLast = false)
    {
        if(group.Plugin==null) return;
        if (!CanConvertEffect(group, true, useLast)) return;
        CommitEdits();
        var copy=Storyboard.CloneContent(Doc.SpritesIn(group,true).Cast<SbObject>().ToList(),Array.Empty<SbObject>(),Doc.Groups.Where(g=>g.IsWithin(group)).ToList());
        var top=copy.Groups[0];top=copy.Groups[Doc.Groups.Where(g=>g.IsWithin(group)).ToList().IndexOf(group)];foreach(var s in copy.Objects.OfType<SbSprite>()) {if(top.Plugin!.Hidden) s.Hidden=true;if(top.Plugin.Locked) s.Locked=true;}
        // The copy stands where the plugin group stands, also inside another group.
        top.Plugin=null;top.Parent=group.Parent;top.Name+=" "+L.T("plugin.copy");
        Doc.Groups.InsertRange(Doc.Groups.IndexOf(group)+1,copy.Groups);Doc.Objects.AddRange(copy.Objects);Arrange();SelectGroup(top);Touch();CommitEdits();SetPluginHistory(HistoryAction.DetachPluginCopy);
    }
    public void SetPluginOffset(SbObjectGroup group,int offset)
    {
        if(group.Plugin==null || group.Plugin.Offset==offset) return;CommitEdits();
        long delta=(long)offset-group.Plugin.Offset;if(delta<int.MinValue||delta>int.MaxValue) {SetStatus(L.T("plugin.offsetInvalid"));return;}
        if(group.Plugin.Kind==PluginKind.Effect) group.ShiftTime((int)delta);
        foreach(var s in Doc.SpritesIn(group,true)) s.ShiftTime((int)delta);
        foreach(var g in Doc.GroupsIn(group)) g.ShiftTime((int)delta);
        group.Plugin.Offset=offset;TouchGroup(group);CommitEdits();
    }
    private string StorePluginPath(string path)
    {
        string full=Path.GetFullPath(Path.Combine(Project!.Folder,path));string relative=Path.GetRelativePath(Project.Folder,full);
        return relative==".." || relative.StartsWith(".."+Path.DirectorySeparatorChar) || Path.IsPathRooted(relative) ? full : relative.Replace('\\','/');
    }
    public void SetPluginPath(SbObjectGroup group,string file,string? root)
    {
        if(group.Plugin==null) return;CommitEdits();group.Plugin.File=StorePluginPath(file);group.Plugin.Root=root==null?null:StorePluginPath(root);TouchGroup(group);CommitEdits();SyncPlugins();
    }
    private void PreparePastedPlugins(IEnumerable<SbObjectGroup> groups)
    {
        int id=Doc.Groups.Select(g=>g.Plugin?.Instance??0).DefaultIfEmpty().Max();
        foreach(var g in groups.Where(g=>g.Plugin!=null))
        {
            if(g.Plugin!.Kind==PluginKind.Effect) {g.Plugin.Instance=++id;if(g.Plugin.Effect!=null) g.Plugin.Effect.InstanceGuid=Guid.NewGuid();continue;}
            if(!SourceOf(g.Plugin!).Supported || !SourceOf(g.Plugin!).Available(Project!,g.Plugin!))
            {g.Plugin=null;SetStatus(L.T("status.pluginPastedNormal"));}
            else g.Plugin!.Instance=++id;
        }
    }
    public string PluginStatusText(PluginStatus status)=>status switch
    {
        PluginStatus.Ready=>L.T("plugin.ready"),PluginStatus.Importing=>L.T("plugin.importing"),PluginStatus.Failed=>L.T("plugin.failed"),PluginStatus.LinkMissing=>L.T("plugin.missing"),_=>L.T("plugin.unsupported")
    };
    public void SetGroupVisibility(SbObjectGroup group,bool hidden)
    {
        foreach(var g in Doc.Groups.Where(g=>g.IsWithin(group)&&g.Plugin!=null)) g.Plugin!.Hidden=hidden;
        foreach(var s in Doc.SpritesIn(group,true).Where(s=>!PluginLink.IsGenerated(s))) s.Hidden=hidden;
        TouchGroup(group);
    }
    public void SetGroupLocked(SbObjectGroup group,bool locked)
    {
        foreach(var g in Doc.Groups.Where(g=>g.IsWithin(group)&&g.Plugin!=null)) g.Plugin!.Locked=locked;
        foreach(var s in Doc.SpritesIn(group,true).Where(s=>!PluginLink.IsGenerated(s))) s.Locked=locked;
        TouchGroup(group);
    }
    public int StalePlugins=>plugins.Values.Count(r=>r.Status is PluginStatus.Failed or PluginStatus.LinkMissing or PluginStatus.Unsupported);
    public IEnumerable<Problem> PluginProblems()
    {
        foreach(var group in Doc.Groups.Where(g=>g.Plugin!=null))
        {
            if(!plugins.TryGetValue(group.Plugin!.Instance,out var r)) continue;
            ProblemKind? kind=r.Status switch {PluginStatus.Failed=>ProblemKind.PluginFailed,PluginStatus.LinkMissing=>ProblemKind.PluginLinkMissing,PluginStatus.Unsupported=>ProblemKind.PluginUnsupported,_=>null};
            if(kind is {} k) yield return new Problem(k,null,group,CommandKind.M,0,0,r.Error??"");
            if(r.Output?.Warnings.Count>0) yield return new Problem(ProblemKind.PluginWarnings,null,group,CommandKind.M,0,0,string.Join("\n",r.Output.Warnings),r.Output.Warnings.Count);
        }
    }
    public string DumpPlugins()
    {
        var data=Doc.Groups.Where(g=>g.Plugin!=null).Select(g=>{
            var p=g.Plugin!;plugins.TryGetValue(p.Instance,out var r);var span=GroupClip.Span(Doc,g);
            return new {Index=Doc.Groups.IndexOf(g),g.Name,p.Instance,Kind=p.Kind.ToString(),p.File,p.Root,p.Offset,FileKey=p.Key,CurrentKey=r?.Output?.Key,
                Effect=p.Effect,Parameters=p.Kind==PluginKind.Effect?EffectBinding.Parameters(p):null,
                Status=r?.Status.ToString(),r?.Error,g.X,g.Y,Warnings=r?.Output?.Warnings.Count??0,Sprites=Doc.SpritesIn(g,true).Count(),Groups=Doc.GroupsIn(g).Count(),
                Span=span==null?null:new[]{span.Value.Start,span.Value.End},Selected=IsSelected(g),OpenInside=OpenGroup?.IsWithin(g)==true};});
        string generated=Path.Combine(Project!.AssetsDir,"generated");
        return System.Text.Json.JsonSerializer.Serialize(new {Plugins=data,Dirty,Status,HistoryCount,HistoryPosition,Groups=Doc.Groups.Count,GeneratedFiles=Directory.Exists(generated)?Directory.GetFiles(generated,"*",SearchOption.AllDirectories).Length:0},new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
    }
}
