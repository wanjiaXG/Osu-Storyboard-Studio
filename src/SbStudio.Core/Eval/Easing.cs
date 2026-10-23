namespace SbStudio.Core;

/// <summary>The 35 storyboard easings, numbered as in the .osb format.</summary>
public static class Easing
{
    public const int Count = 35;

    private const double ElasticConst = 2 * Math.PI / .3;
    private const double ElasticConst2 = .3 / 4;
    private const double BackConst = 1.70158;
    private const double BackConst2 = BackConst * 1.525;
    private const double BounceConst = 1 / 2.75;

    // Offsets that make the expo and elastic curves start and end exactly at 0 and 1.
    private static readonly double ExpoOffset = Math.Pow(2, -10);
    private static readonly double ElasticOffsetFull = Math.Pow(2, -11);
    private static readonly double ElasticOffsetHalf = Math.Pow(2, -10) * Math.Sin((.5 - ElasticConst2) * ElasticConst);
    private static readonly double ElasticOffsetQuarter = Math.Pow(2, -10) * Math.Sin((.25 - ElasticConst2) * ElasticConst);
    private static readonly double InOutElasticOffset = Math.Pow(2, -10) * Math.Sin((1 - ElasticConst2 * 1.5) * ElasticConst / 1.5);

    public static double Apply(int easing, double t)
    {
        switch (easing)
        {
            default:
                return t;

            case 2: // In
            case 3: // QuadIn
                return t * t;

            case 1: // Out
            case 4: // QuadOut
                return t * (2 - t);

            case 5: // QuadInOut
                if (t < .5) return t * t * 2;
                t -= 1;
                return t * t * -2 + 1;

            case 6: // CubicIn
                return t * t * t;

            case 7: // CubicOut
                t -= 1;
                return t * t * t + 1;

            case 8: // CubicInOut
                if (t < .5) return t * t * t * 4;
                t -= 1;
                return t * t * t * 4 + 1;

            case 9: // QuartIn
                return t * t * t * t;

            case 10: // QuartOut
                t -= 1;
                return 1 - t * t * t * t;

            case 11: // QuartInOut
                if (t < .5) return t * t * t * t * 8;
                t -= 1;
                return t * t * t * t * -8 + 1;

            case 12: // QuintIn
                return t * t * t * t * t;

            case 13: // QuintOut
                t -= 1;
                return t * t * t * t * t + 1;

            case 14: // QuintInOut
                if (t < .5) return t * t * t * t * t * 16;
                t -= 1;
                return t * t * t * t * t * 16 + 1;

            case 15: // SineIn
                return 1 - Math.Cos(t * Math.PI * .5);

            case 16: // SineOut
                return Math.Sin(t * Math.PI * .5);

            case 17: // SineInOut
                return .5 - .5 * Math.Cos(Math.PI * t);

            case 18: // ExpoIn
                return Math.Pow(2, 10 * (t - 1)) + ExpoOffset * (t - 1);

            case 19: // ExpoOut
                return -Math.Pow(2, -10 * t) + 1 + ExpoOffset * t;

            case 20: // ExpoInOut
                if (t < .5) return .5 * (Math.Pow(2, 20 * t - 10) + ExpoOffset * (2 * t - 1));
                return 1 - .5 * (Math.Pow(2, -20 * t + 10) + ExpoOffset * (-2 * t + 1));

            case 21: // CircIn
                return 1 - Math.Sqrt(1 - t * t);

            case 22: // CircOut
                t -= 1;
                return Math.Sqrt(1 - t * t);

            case 23: // CircInOut
                t *= 2;
                if (t < 1) return .5 - .5 * Math.Sqrt(1 - t * t);
                t -= 2;
                return .5 * Math.Sqrt(1 - t * t) + .5;

            case 24: // ElasticIn
                return -Math.Pow(2, -10 + 10 * t) * Math.Sin((1 - ElasticConst2 - t) * ElasticConst) + ElasticOffsetFull * (1 - t);

            case 25: // ElasticOut
                return Math.Pow(2, -10 * t) * Math.Sin((t - ElasticConst2) * ElasticConst) + 1 - ElasticOffsetFull * t;

            case 26: // ElasticHalfOut
                return Math.Pow(2, -10 * t) * Math.Sin((.5 * t - ElasticConst2) * ElasticConst) + 1 - ElasticOffsetHalf * t;

            case 27: // ElasticQuarterOut
                return Math.Pow(2, -10 * t) * Math.Sin((.25 * t - ElasticConst2) * ElasticConst) + 1 - ElasticOffsetQuarter * t;

            case 28: // ElasticInOut
                t *= 2;
                if (t < 1)
                    return -.5 * (Math.Pow(2, -10 + 10 * t) * Math.Sin((1 - ElasticConst2 * 1.5 - t) * ElasticConst / 1.5) - InOutElasticOffset * (1 - t));
                t -= 1;
                return .5 * (Math.Pow(2, -10 * t) * Math.Sin((t - ElasticConst2 * 1.5) * ElasticConst / 1.5) - InOutElasticOffset * t) + 1;

            case 29: // BackIn
                return t * t * ((BackConst + 1) * t - BackConst);

            case 30: // BackOut
                t -= 1;
                return t * t * ((BackConst + 1) * t + BackConst) + 1;

            case 31: // BackInOut
                t *= 2;
                if (t < 1) return .5 * t * t * ((BackConst2 + 1) * t - BackConst2);
                t -= 2;
                return .5 * (t * t * ((BackConst2 + 1) * t + BackConst2) + 2);

            case 32: // BounceIn
                return 1 - BounceOut(1 - t);

            case 33: // BounceOut
                return BounceOut(t);

            case 34: // BounceInOut
                if (t < .5) return .5 - .5 * BounceOut(1 - t * 2);
                return BounceOut((t - .5) * 2) * .5 + .5;
        }
    }

    private static double BounceOut(double t)
    {
        if (t < BounceConst) return 7.5625 * t * t;
        if (t < 2 * BounceConst)
        {
            t -= 1.5 * BounceConst;
            return 7.5625 * t * t + .75;
        }
        if (t < 2.5 * BounceConst)
        {
            t -= 2.25 * BounceConst;
            return 7.5625 * t * t + .9375;
        }
        t -= 2.625 * BounceConst;
        return 7.5625 * t * t + .984375;
    }
}
