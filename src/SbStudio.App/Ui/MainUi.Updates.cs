using System.Numerics;
using ImGuiNET;
using SbStudio.App.Updates;
using SbStudio.Core;
using SbStudio.Core.Updates;

namespace SbStudio.App.Ui;

public sealed partial class MainUi
{
    private const string UpdateDialog = "###updates";
    private readonly PortableUpdateSession updates = new();
    private Task<UpdateRelease?>? updateCheck;
    private Task? updateDownload, helperStart;
    private UpdateRelease? release;
    private bool updatePrepared, automaticCheck, announceUpdate;
    private string updateMessage = "";

    public void StartAutomaticUpdateCheck()
    {
        // Development and screenshot runs do not poll GitHub. Retry automatically at most every six hours.
        if (AppSettings.ReadOnly || !updates.CanInstall || !app.Settings.CheckUpdatesOnStartup ||
            DateTime.UtcNow - app.Settings.LastUpdateCheckUtc < TimeSpan.FromHours(6)) return;
        BeginUpdateCheck(true);
    }

    public void StopUpdates() => updates.Dispose();

    private bool UpdateBusy => updateCheck != null || updateDownload != null || helperStart != null;

    private void ShowUpdates()
    {
        openPopup = UpdateDialog;
        if (!UpdateBusy && release == null) BeginUpdateCheck(false);
    }

    private void BeginUpdateCheck(bool automatic)
    {
        automaticCheck = automatic;
        app.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
        if (!AppSettings.ReadOnly) app.Settings.Save();
        updateMessage = "";
        release = null;
        updatePrepared = false;
        updateCheck = updates.CheckAsync();
    }

    private void PollUpdates()
    {
        if (updateCheck is { IsCompleted: true } check)
        {
            updateCheck = null;
            try
            {
                release = check.GetAwaiter().GetResult();
                updateMessage = release == null ? L.T("update.latest", EditorApp.Version) : "";
                announceUpdate = automaticCheck && release != null;
            }
            catch (Exception ex)
            {
                updateMessage = L.T("update.failed", ex.Message);
                if (automaticCheck) AppSettings.Log(updateMessage);
            }
        }
        if (updateDownload is { IsCompleted: true } download)
        {
            updateDownload = null;
            try { download.GetAwaiter().GetResult(); updatePrepared = true; updateMessage = ""; }
            catch (OperationCanceledException) { updateMessage = L.T("update.cancelled"); }
            catch (Exception ex) { updateMessage = L.T("update.failed", ex.Message); }
        }
        if (helperStart is { IsCompleted: true } helper)
        {
            helperStart = null;
            try { helper.GetAwaiter().GetResult(); QuitRequested = true; }
            catch (Exception ex) { updateMessage = L.T("update.failed", ex.Message); openPopup = UpdateDialog; }
        }
        if (announceUpdate && openPopup == null && !app.IsLoading && !app.Exporting &&
            !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
        {
            announceUpdate = false;
            openPopup = UpdateDialog;
        }
    }

    private void DrawUpdateDialog(Vector2 centre)
    {
        if (!BeginModal(L.T("update.title") + UpdateDialog, centre, 600, helperStart == null)) return;
        Theme.TextDimmed(L.T("update.current", EditorApp.Version));
        bool betaBuild = GithubUpdates.ParseVersion(EditorApp.Version).IsBeta;
        Theme.TextWrapped(L.T(betaBuild ? "update.channelBeta" : "update.channelStable"));
        bool auto = app.Settings.CheckUpdatesOnStartup;
        if (ImGui.Checkbox(L.T("update.automatic"), ref auto))
        {
            app.Settings.CheckUpdatesOnStartup = auto;
            app.Settings.Save();
        }
        if (release is { } next)
        {
            ImGui.TextUnformatted(L.T("update.available", next.Version));
            Theme.TextDimmed(L.T("update.size", next.Size / (1024d * 1024d)));
            ImGui.BeginChild("##releaseNotes", new Vector2(0, Theme.Px(190)), ImGuiChildFlags.Border);
            Theme.TextWrapped(next.Notes);
            ImGui.EndChild();
        }
        if (!updates.CanInstall) Theme.TextWrapped(L.T("update.development"));
        if (updateCheck != null) Theme.TextDimmed(L.T("update.checking"));
        if (updateDownload != null)
        {
            Theme.TextDimmed(L.T("update.downloading"));
            ImGui.ProgressBar(updates.Fraction, new Vector2(-1, 0));
        }
        if (helperStart != null) Theme.TextWrapped(L.T("update.starting"));
        if (updateMessage.Length > 0) Theme.TextWrapped(updateMessage);
        if (updatePrepared) Theme.TextWrapped(L.T("update.ready"));
        ImGui.Spacing();
        ImGui.BeginDisabled(UpdateBusy);
        if (Theme.Button(L.T("update.checkAgain"), 110)) BeginUpdateCheck(false);
        if (release != null && updates.CanInstall && !updatePrepared)
        {
            ImGui.SameLine();
            if (Theme.Button(L.T("update.download"), 110))
            {
                updateMessage = "";
                updateDownload = updates.PrepareAsync(release);
            }
        }
        ImGui.EndDisabled();
        if (updatePrepared)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(UpdateBusy || app.IsLoading || app.Exporting || app.PreviewingEdit);
            if (Theme.Button(L.T("update.install"), 160))
            {
                app.Pause();
                app.CommitEdits();
                ImGui.CloseCurrentPopup();
                Guard(() =>
                {
                    helperStart = updates.StartHelperAsync(L.Current.Code);
                    openPopup = UpdateDialog;
                });
            }
            ImGui.EndDisabled();
        }
        if (updateDownload != null)
        {
            ImGui.SameLine();
            if (Theme.Button(L.T("common.cancel"), 100)) updates.Cancel();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(helperStart != null);
        if (Theme.Button(L.T("common.close"), 100)) ImGui.CloseCurrentPopup();
        ImGui.EndDisabled();
        ImGui.EndPopup();
    }
}
