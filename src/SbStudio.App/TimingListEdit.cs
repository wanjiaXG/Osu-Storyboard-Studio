using SbStudio.Core;

namespace SbStudio.App;

/// <summary>A private timing draft; confirming it only changes the project's timing sections.</summary>
internal sealed class TimingListEdit
{
    private readonly List<TimingPoint> original;
    public Project Project { get; }
    public List<TimingPoint> Timing { get; set; }

    public TimingListEdit(Project project)
    {
        Project = project;
        original = Copy(project.Settings.Timing);
        Timing = Copy(project.Settings.Timing);
    }

    internal static List<TimingPoint> Copy(IEnumerable<TimingPoint> points) =>
        points.Select(p => new TimingPoint { Offset = p.Offset, BeatLength = p.BeatLength, Meter = p.Meter }).ToList();

    public bool Apply() => ApplyTo(Project, original, Timing);

    internal static bool ApplyTo(Project project, List<TimingPoint> original, List<TimingPoint> edited)
    {
        static IEnumerable<(double, double, int)> Values(List<TimingPoint> points) =>
            points.OrderBy(p => p.Offset).Select(p => (p.Offset, p.BeatLength, p.Meter));
        if (Values(edited).SequenceEqual(Values(original)) || Values(project.Settings.Timing).SequenceEqual(Values(edited))) return false;
        project.Settings.Timing = Copy(edited.OrderBy(p => p.Offset));
        return true;
    }
}
