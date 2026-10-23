namespace SbStudio.App.Ui;

internal static class TimelineViewport
{
    public const double OsuAnchor = .28;
    public static double FixedStart(double time, double width, double pixelsPerMs) => time - width * OsuAnchor / pixelsPerMs;
    public static double FixedFitScale(double start, double end, double time, double width) =>
        Math.Clamp(width / (Math.Max(1000, Math.Max((time - start) / OsuAnchor, (end - time) / (1 - OsuAnchor))) * 1.08), .0005, 5);
    public static double ScrubTime(double viewStart, double pixelsPerMs, double left, double mouseX) => viewStart + (mouseX - left) / pixelsPerMs;
}
