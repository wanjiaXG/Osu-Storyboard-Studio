using SbStudio.Core;

namespace SbStudio.App;

/// <summary>A private draft of the fields edited by the project settings dialog.</summary>
internal sealed class ProjectSettingsEdit
{
    private readonly ProjectSettings original;
    private readonly string? originalDirectory;
    public Project Project { get; }
    public ProjectSettings Settings { get; }
    public string? BeatmapDirectory { get; set; }
    public bool ReferenceChanged => !string.Equals(BeatmapDirectory, originalDirectory, StringComparison.OrdinalIgnoreCase);

    public ProjectSettingsEdit(Project project)
    {
        Project = project;
        original = Copy(project.Settings);
        Settings = Copy(project.Settings);
        BeatmapDirectory = originalDirectory = project.BeatmapDirectory;
    }

    private static ProjectSettings Copy(ProjectSettings settings) => new()
    {
        Name = settings.Name, Audio = settings.Audio, Background = settings.Background,
        Widescreen = settings.Widescreen, Timing = TimingListEdit.Copy(settings.Timing),
        Export = new ExportSettings { FileName = settings.Export.FileName },
    };

    /// <summary>Only apply edited fields, preserving unrelated changes from a live .osu refresh.</summary>
    public bool Apply()
    {
        var target = Project.Settings;
        bool changed = false;
        if (Settings.Name != original.Name && target.Name != Settings.Name) { target.Name = Settings.Name; changed = true; }
        if (Settings.Audio != original.Audio && target.Audio != Settings.Audio) { target.Audio = Settings.Audio; changed = true; }
        if (Settings.Background != original.Background && target.Background != Settings.Background) { target.Background = Settings.Background; changed = true; }
        if (Settings.Widescreen != original.Widescreen && target.Widescreen != Settings.Widescreen) { target.Widescreen = Settings.Widescreen; changed = true; }
        if (Settings.Export.FileName != original.Export.FileName && target.Export.FileName != Settings.Export.FileName)
        {
            target.Export.FileName = Settings.Export.FileName;
            changed = true;
        }
        changed |= TimingListEdit.ApplyTo(Project, original.Timing, Settings.Timing);
        return changed;
    }
}
