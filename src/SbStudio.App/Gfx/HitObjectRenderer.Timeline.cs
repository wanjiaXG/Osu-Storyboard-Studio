using System.Numerics;
using SbStudio.Core;

namespace SbStudio.App.Gfx;

public sealed unsafe partial class HitObjectRenderer
{
    private readonly Dictionary<string, Texture?> timelineImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Vector2> timelineImageSizes = new(StringComparer.OrdinalIgnoreCase);
    private bool timelineLazerStyle;
    internal bool TimelineLazerStyle => timelineLazerStyle;
    internal bool TimelineOverlayAboveNumber => skin.OverlayAboveNumber;
    internal float TimelineNumberOverlap => skin.NumberOverlap;
    internal Vector3 TimelineColour(OsuBeatmap map, HitObject obj) => map.Mode switch
    {
        0 or 2 => Colour(map, obj),
        1 => skin.CustomColour((obj.HitSound & 10) != 0 ? "TaikoNoteColourRim" : "TaikoNoteColour")
            ?? ((obj.HitSound & 10) != 0 ? new Vector3(0.25f, 0.7f, 0.85f) : new Vector3(0.95f, 0.3f, 0.28f)),
        3 => lazerStyle ? ArgonManiaPreview.Colour(map.Columns, map.ColumnOf(obj)) : Vector3.One,
        _ => Colour(map, obj)
    };
    internal Texture? TimelineImage(string name)
    {
        if (timelineImages.TryGetValue(name, out var texture)) return texture;
        var image = name.StartsWith("digit:") ? skin.ReadDigit(int.Parse(name[6..])) : skin.ReadAnimation(name, 1).FirstOrDefault();
        if (image != null)
        {
            // Skin bitmaps are premultiplied for the preview renderer. ImGui uses straight alpha.
            var pixels = (byte[])image.Pixels.Clone();
            for (int p = 0; p < pixels.Length; p += 4)
                if (pixels[p + 3] > 0)
                    for (int c = 0; c < 3; c++) pixels[p + c] = (byte)Math.Min(255, pixels[p + c] * 255 / pixels[p + 3]);
            texture = new Texture(gl, image.Width, image.Height, pixels, premultiplied: false);
            timelineImageSizes[name] = image.Size;
        }
        timelineImages[name] = texture;
        return texture;
    }
    internal Vector2 TimelineImageSize(string name) => timelineImageSizes.GetValueOrDefault(name);
    internal string TimelineNoteImage(OsuBeatmap map, HitObject obj)
    {
        int column = map.ColumnOf(obj);
        return skin.ManiaValue(map.Columns, "NoteImage" + column, "mania-note" + OsuPreviewSkin.ManiaColumnType(map.Columns, column));
    }
    private void ReleaseTimelineImages()
    {
        foreach (var texture in timelineImages.Values) texture?.Dispose();
        timelineImages.Clear();
        timelineImageSizes.Clear();
    }
}
