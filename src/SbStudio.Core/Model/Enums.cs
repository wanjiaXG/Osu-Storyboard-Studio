namespace SbStudio.Core;

public enum SbLayer
{
    Background = 0,
    Fail = 1,
    Pass = 2,
    Foreground = 3,
    Overlay = 4,
}

public enum SbOrigin
{
    TopLeft = 0,
    Centre = 1,
    CentreLeft = 2,
    TopRight = 3,
    BottomCentre = 4,
    TopCentre = 5,
    Custom = 6,
    CentreRight = 7,
    BottomLeft = 8,
    BottomRight = 9,
}

public enum SbLoopType
{
    LoopForever = 0,
    LoopOnce = 1,
}

public enum CommandKind
{
    F,
    M,
    MX,
    MY,
    S,
    V,
    R,
    C,
    P,
    Z, RX, RY, CT, CD, CO, CI, CL, CF,
}

public static class SbInfo
{
    public const int LayerCount = 5;
    public static readonly CommandKind[] DepthKinds = { CommandKind.Z, CommandKind.RX, CommandKind.RY };
    public static readonly CommandKind[] CameraKinds = { CommandKind.CT, CommandKind.CD, CommandKind.CO, CommandKind.CI, CommandKind.CL, CommandKind.CF };
    public static bool IsEditorOnly(CommandKind kind) => kind >= CommandKind.Z;
    public static bool IsAngle(CommandKind kind) => kind is CommandKind.R or CommandKind.RX or CommandKind.RY or CommandKind.CO or CommandKind.CI or CommandKind.CL;

    public static readonly CommandKind[] AllKinds =
    {
        CommandKind.F, CommandKind.M, CommandKind.MX, CommandKind.MY, CommandKind.S,
        CommandKind.V, CommandKind.R, CommandKind.C, CommandKind.P,
    };

    /// <summary>Number of scalar values at each end of a command (P carries a letter instead).</summary>
    public static int ValueCount(CommandKind kind) => kind switch
    {
        CommandKind.M or CommandKind.V => 2,
        CommandKind.C or CommandKind.CT => 3,
        CommandKind.P => 0,
        _ => 1,
    };

    /// <summary>Origin as a fraction of the image size (0,0 = top left; 1,1 = bottom right).</summary>
    public static (double X, double Y) OriginFraction(SbOrigin origin) => origin switch
    {
        SbOrigin.TopLeft or SbOrigin.Custom => (0, 0),
        SbOrigin.Centre => (0.5, 0.5),
        SbOrigin.CentreLeft => (0, 0.5),
        SbOrigin.TopRight => (1, 0),
        SbOrigin.BottomCentre => (0.5, 1),
        SbOrigin.TopCentre => (0.5, 0),
        SbOrigin.CentreRight => (1, 0.5),
        SbOrigin.BottomLeft => (0, 1),
        SbOrigin.BottomRight => (1, 1),
        _ => (0.5, 0.5),
    };

    public static readonly string[] EasingNames =
    {
        "Linear", "Out", "In",
        "QuadIn", "QuadOut", "QuadInOut",
        "CubicIn", "CubicOut", "CubicInOut",
        "QuartIn", "QuartOut", "QuartInOut",
        "QuintIn", "QuintOut", "QuintInOut",
        "SineIn", "SineOut", "SineInOut",
        "ExpoIn", "ExpoOut", "ExpoInOut",
        "CircIn", "CircOut", "CircInOut",
        "ElasticIn", "ElasticOut", "ElasticHalfOut", "ElasticQuarterOut", "ElasticInOut",
        "BackIn", "BackOut", "BackInOut",
        "BounceIn", "BounceOut", "BounceInOut",
    };
}
