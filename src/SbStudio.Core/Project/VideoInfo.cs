namespace SbStudio.Core;

/// <summary>
/// The size and length of a beatmap's video, and how osu! shows it: behind the
/// storyboard, in the middle, fading in over its first second and out over its last.
/// </summary>
/// <param name="DurationMs">Length in milliseconds; infinite when the file does not say.</param>
public sealed record VideoInfo(int Width, int Height, double DurationMs)
{
    private const double FadeMs = 1000;

    /// <summary>How visible the video is at a time into it. Zero before it begins and after it ends.</summary>
    public double OpacityAt(double ms)
    {
        if (ms < 0 || ms > DurationMs) return 0;
        if (ms >= DurationMs - FadeMs) return Math.Clamp((DurationMs - ms) / FadeMs, 0, 1);
        return Math.Clamp(ms / FadeMs, 0, 1);
    }

    /// <summary>
    /// The factor from the video's pixels to storyboard units. A wide video fills the picture, cut off
    /// where it sticks out. One of 4:3 or narrower does the same next to a widescreen storyboard or
    /// without a storyboard at all, and is otherwise fitted to the height.
    /// </summary>
    /// <param name="hasStoryboard">Whether the storyboard shows any sprite.</param>
    public double ScaleFor(bool widescreen, bool hasStoryboard)
    {
        double fitHeight = 480.0 / Height;
        double fill = Math.Max(fitHeight, (widescreen ? 854.0 : 640.0) / Width);
        return (double)Width / Height > 1.34 || widescreen || !hasStoryboard ? fill : fitHeight;
    }
}
