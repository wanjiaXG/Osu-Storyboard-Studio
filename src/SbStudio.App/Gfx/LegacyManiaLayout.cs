namespace SbStudio.App.Gfx;

/// <summary>stable StageMania horizontal placement in the 640/854 by 480 game frame.</summary>
internal sealed class LegacyManiaLayout
{
    public float[] Widths { get; }
    public float[] ColumnLefts { get; }
    public float[] StageLefts { get; }
    public float[] StageWidths { get; }
    public int Stages => StageLefts.Length;
    public int PrimaryKeys { get; }
    public float WidthScale { get; }
    public float Left => StageLefts[0];
    public float Right => StageLefts[^1] + StageWidths[^1];
    public int StageOf(int column) => Stages == 2 && column >= PrimaryKeys ? 1 : 0;
    public int KeysInStage(int column) => StageOf(column) == 0 ? PrimaryKeys : Widths.Length - PrimaryKeys;
    public int LocalColumn(int column) => StageOf(column) == 0 ? column : column - PrimaryKeys;

    public LegacyManiaLayout(OsuPreviewSkin skin, int keys, bool widescreen)
    {
        bool split = keys > 1 && skin.ManiaValue(keys, "SplitStages", keys >= 10 ? "1" : "0") == "1";
        PrimaryKeys = split ? (keys + 1) / 2 : keys;
        StageLefts = new float[split ? 2 : 1];
        StageWidths = new float[StageLefts.Length];
        Widths = new float[keys];
        ColumnLefts = new float[keys];
        var spaces = new float[keys];
        float separation = split ? MathF.Max(5, skin.ManiaNumber(keys, "StageSeparation", 40)) : 0;
        float total = separation;
        for (int i = 0; i < keys; i++)
        {
            Widths[i] = Math.Clamp(skin.ManiaNumber(keys, "ColumnWidth", 30, i), 5, 100);
            spaces[i] = i == 0 ? 0 : MathF.Max(-Widths[i], skin.ManiaNumber(keys, "ColumnSpacing", 0, i - 1));
            total += Widths[i] + spaces[i];
        }
        float frameWidth = widescreen ? 854 : 640, frameLeft = widescreen ? -107 : 0;
        float start = skin.ManiaNumber(keys, "ColumnStart", 136), right = skin.ManiaNumber(keys, "ColumnRight", 19);
        // Match stable's handling of overlapping offsets, then fit columns/spaces/separation as a unit.
        float left = MathF.Min(start, frameWidth - right);
        right = MathF.Min(right, frameWidth - start);
        float overflow = MathF.Max(0, left + total + right - frameWidth);
        WidthScale = total > 0 ? Math.Clamp((total - overflow) / total, 0, 1) : 1;
        StageLefts[0] = frameLeft + left;
        float cursor = StageLefts[0];
        for (int i = 0; i < keys; i++)
        {
            if (split && i == PrimaryKeys)
            {
                cursor += separation * WidthScale;
                StageLefts[1] = cursor;
            }
            int stage = StageOf(i);
            float spacing = spaces[i] * WidthScale;
            Widths[i] *= WidthScale;
            cursor += spacing;
            ColumnLefts[i] = cursor;
            cursor += Widths[i];
            StageWidths[stage] += Widths[i] + spacing;
        }
    }
}
