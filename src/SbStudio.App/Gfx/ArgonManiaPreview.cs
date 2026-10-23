using System.Numerics;

namespace SbStudio.App.Gfx;

/// <summary>Layout and colours from lazer's ManiaArgonSkinTransformer and its Argon components.</summary>
internal static class ArgonManiaPreview
{
    public const float Scale = 480f / 768;
    public const float StageTop = 30 * Scale;
    public const float StageBottom = 480 - 30 * Scale;
    public const float HitPosition = StageBottom - 110 * Scale;
    public const float KeyAreaTop = StageBottom - (110 + 3.4f * 2) * Scale;
    public const float NoteHeight = 42 * Scale;
    public const float AccentHeight = NoteHeight * 0.82f;
    public const float Radius = 3.4f * Scale;
    public const float ColumnMargin = Scale;
    public const float ScrollRangeScale = (768 - 110f) / (768 - (480 - 402) * 1.6f);

    private static readonly Vector3 Yellow = new(255, 197, 40), Orange = new(252, 109, 1), Pink = new(213, 35, 90),
        Purple = new(203, 60, 236), Cyan = new(72, 198, 255), Green = new(100, 192, 92), Special = new(169, 106, 255);
    private static readonly Vector3[][] Colours =
    {
        new[] { Yellow },
        new[] { Green, Cyan },
        new[] { Green, Special, Cyan },
        new[] { Yellow, Orange, Pink, Purple },
        new[] { Pink, Orange, Yellow, Green, Cyan },
        new[] { Pink, Orange, Green, Cyan, Orange, Pink },
        new[] { Pink, Orange, Pink, Special, Pink, Orange, Pink },
        new[] { Purple, Pink, Orange, Green, Cyan, Orange, Pink, Purple },
        new[] { Purple, Pink, Orange, Yellow, Special, Yellow, Orange, Pink, Purple },
        new[] { Purple, Pink, Orange, Yellow, Green, Cyan, Yellow, Orange, Pink, Purple },
    };

    public static Vector3 Colour(int keys, int column)
    {
        int stageKeys = keys > 10 ? keys / 2 : keys;
        return Colours[Math.Clamp(stageKeys, 1, 10) - 1][column % stageKeys] / 255;
    }

    public static float ColumnWidth(int keys, int column) => 60 * Scale * (OsuPreviewSkin.ManiaColumnType(keys, column) == "S" ? 2 : 1);
}
