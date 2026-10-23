using SbStudio.Core;

namespace SbStudio.App;

public sealed partial class EditorApp
{
    private Task<BeatmapReferenceSnapshot>? beatmapRefresh;
    private string beatmapFingerprint = "", beatmapPendingFingerprint = "";
    private string? beatmapRelink;
    private long beatmapPollAt, beatmapPendingAt;
    private bool beatmapResume;
    private int beatmapReadFailures;

    private static string BeatmapFingerprint(Project project)
    {
        return DirectoryFingerprint(project.BeatmapDirectory);
    }

    private static string DirectoryFingerprint(string? folder)
    {
        if (folder == null) return "";
        if (!Directory.Exists(folder)) return folder + "|missing";
        return folder + "|" + string.Join('|', BeatmapImporter.Difficulties(folder).Select(path =>
        {
            var file = new FileInfo(path);
            return $"{file.Name}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
        }));
    }

    private void ResetBeatmapReference()
    {
        beatmapRefresh = null;
        beatmapRelink = null;
        beatmapPendingFingerprint = "";
        beatmapReadFailures = 0;
        beatmapResume = false;
        beatmapPollAt = 0;
        try { beatmapFingerprint = Project == null ? "" : BeatmapFingerprint(Project); }
        catch (IOException) { beatmapFingerprint = ""; }
        catch (UnauthorizedAccessException) { beatmapFingerprint = ""; }
    }

    public bool ReferenceBeatmap(string folder)
    {
        if (Project == null || IsLoading) return false;
        try
        {
            string full = Path.GetFullPath(folder);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException(L.T("import.noFolder", full));
            BeginBeatmapRefresh(full, relink: true);
            return true;
        }
        catch (Exception ex)
        {
            ShowMessage(L.T("reference.failed"), ex.Message);
            return false;
        }
    }

    public void UnlinkBeatmap()
    {
        if (Project == null || IsLoading) return;
        CommitEdits();
        string audio = Project.Settings.Audio;
        Project.UnlinkBeatmap();
        ResetBeatmapReference();
        beatmapFile = null;
        ClearSelection();
        Touch();
        AfterDifficultyChange(audio);
        SetStatus(L.T("reference.unlinked"));
    }

    private void BeginBeatmapRefresh(string folder, bool relink)
    {
        CommitEdits();
        beatmapResume = Playing;
        if (beatmapResume) Pause();
        beatmapRelink = relink ? folder : null;
        if (relink) beatmapPendingFingerprint = DirectoryFingerprint(folder);
        loadingName = Project!.Settings.Name;
        beatmapRefresh = Task.Run(() => BeatmapReferenceSnapshot.Read(folder));
    }

    private void UpdateBeatmapReference()
    {
        if (Project == null || opening != null || Exporting) return;
        if (beatmapRefresh is { IsCompleted: true } task)
        {
            beatmapRefresh = null;
            string previousFolder = Project.Settings.BeatmapFolder;
            try
            {
                var snapshot = task.GetAwaiter().GetResult();
                if (beatmapRelink != null && !snapshot.Exists)
                    throw new DirectoryNotFoundException(L.T("import.noFolder", snapshot.Directory));
                string audio = Project.Settings.Audio;
                if (beatmapRelink != null) Project.Settings.BeatmapFolder = beatmapRelink;
                Project.RefreshBeatmaps(snapshot);
                if (beatmapRelink != null && Project.CurrentDifficulty == null && Project.Difficulties.Count > 0)
                    Project.SelectDifficulty(Project.Difficulties[0].File);
                beatmapFile = null;
                ClearSelection();
                Touch();
                RefreshAssets();
                AfterDifficultyChange(audio);
                LoadImages();
                beatmapFingerprint = beatmapPendingFingerprint;
                // Read again if the directory changed while the background read was under way.
                beatmapReadFailures = 0;
                SetStatus(Project.PreservedDifficultyEdits.Count > 0
                    ? L.T("reference.preserved", string.Join(", ", Project.PreservedDifficultyEdits))
                    : L.T("reference.updated", Project.Difficulties.Count));
            }
            catch (Exception ex)
            {
                if (beatmapRelink != null) Project.Settings.BeatmapFolder = previousFolder;
                SetStatus(L.T("reference.readFailed", ex.Message));
                if (++beatmapReadFailures >= 3) beatmapFingerprint = beatmapPendingFingerprint;
            }
            beatmapRelink = null;
            beatmapPendingFingerprint = "";
            beatmapPollAt = Environment.TickCount64 + 500;
        }
        if (beatmapResume && !IsLoading)
        {
            beatmapResume = false;
            Play();
        }
        long now = Environment.TickCount64;
        if (IsLoading || pendingCommit || now < beatmapPollAt) return;
        beatmapPollAt = now + 500;
        try
        {
            string fingerprint = BeatmapFingerprint(Project);
            if (fingerprint == beatmapFingerprint) { beatmapPendingFingerprint = ""; return; }
            if (fingerprint != beatmapPendingFingerprint)
            {
                beatmapPendingFingerprint = fingerprint;
                beatmapPendingAt = now;
                return;
            }
            if (now - beatmapPendingAt >= 300 && Project.BeatmapDirectory is { } folder)
                BeginBeatmapRefresh(folder, relink: false);
        }
        catch (IOException) { /* An editor may be replacing a file; retry the next poll. */ }
        catch (UnauthorizedAccessException ex) { SetStatus(L.T("reference.readFailed", ex.Message)); }
    }
}
