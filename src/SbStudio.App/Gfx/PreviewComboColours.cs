using System.Numerics;
using SbStudio.Core;

namespace SbStudio.App.Gfx;

internal static class PreviewComboColours
{
    public static Vector3 Resolve(OsuBeatmap map, int comboWithOffsets)
    {
        // Combo colours and colour skips belong to the .osu file, independently of image skin.
        IReadOnlyList<(byte R, byte G, byte B)> colours = map.ComboColours.Count > 0 ? map.ComboColours : OsuBeatmap.DefaultComboColours;
        var colour = colours[Math.Abs(comboWithOffsets) % colours.Count];
        return new Vector3(colour.R, colour.G, colour.B) / 255;
    }
}
