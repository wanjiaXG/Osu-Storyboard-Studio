namespace SbStudio.Core;

/// <summary>
/// osu!'s "SB Load", the number its editor shows for how much a storyboard draws at one moment: the
/// size of every picture that is drawn, added up and divided by the size of the 640×480 screen. One
/// picture filling that screen is 1.00x. osu! shows the number in red above 5.
///
/// What a picture counts for is the size it is shown at, as if it were not turned and wherever it lies:
/// what sticks out of the screen counts too, a picture that is hardly visible counts in full, and one
/// picture never counts for more than a 16:9 screen. A picture that is not turned and lies wholly
/// outside the screen is not drawn by osu! and so counts for nothing.
/// </summary>
public static class StoryboardLoad
{
    /// <summary>Above this osu! shows the load in red.</summary>
    public const double High = 5;

    /// <summary>Below this opacity osu! does not draw a sprite.</summary>
    public const double LeastOpacity = 0.001;

    private const double Screen = 640.0 * 480;
    private const double MostPerPicture = 480.0 * 16 / 9 * 480;
    /// <summary>How far a widescreen storyboard reaches to either side of the 640 wide screen.</summary>
    private const double WideMargin = (480.0 * 16 / 9 - 640) / 2;

    /// <summary>What one drawn picture adds to the load, from the size it is shown at in storyboard units.</summary>
    public static double Of(double width, double height) => Math.Min(Math.Abs(width * height), MostPerPicture) / Screen;

    /// <summary>
    /// What a sprite adds to the load: nothing when osu! does not draw it, else what its size comes to.
    /// </summary>
    /// <param name="left">Left edge of the picture as it lies when it is not turned, in storyboard units.</param>
    /// <param name="rotation">How far it is turned, in radians.</param>
    /// <param name="widescreen">Whether the storyboard is a widescreen one, which is drawn beyond the 640 wide screen.</param>
    public static double OfSprite(double left, double top, double width, double height, double rotation, double opacity, bool widescreen)
    {
        if (opacity <= LeastOpacity) return 0;
        // A turned picture is drawn wherever it is. One that is not, only when some of it is on the screen.
        if (rotation == 0)
        {
            double margin = widescreen ? WideMargin : 0;
            if (left + width < -margin || left > 640 + margin || top + height < 0 || top > 480) return 0;
        }
        return Of(width, height);
    }
}
