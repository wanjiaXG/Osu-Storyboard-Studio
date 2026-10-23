using System.Numerics;
using SbStudio.Core;

namespace SbStudio.App.Gfx;

/// <summary>A snapshot of a submitted picture, in back-to-front order.</summary>
public readonly record struct RenderRow(SbObject? Object, int ObjectNumber, SbLayer Layer, string Path,
    Texture? Texture, SpriteState State, SbOrigin Origin, int Frame, int Frames, string Group, double Load, bool Simulated)
{
    public bool IsSprite => Object is SbSprite;
}

/// <summary>Reused only when the preview inspector is open.</summary>
public sealed class RenderRecord : IReadOnlyList<RenderRow>
{
    public readonly List<RenderRow> Rows = new();
    public readonly List<RenderRow> Missing = new();
    public readonly int[] Alive = new int[SbInfo.LayerCount], Drawn = new int[SbInfo.LayerCount], Hidden = new int[SbInfo.LayerCount];
    private readonly Dictionary<SbObject, (int Number, string Group)> objects = new();
    private Storyboard? document;
    private int revision = -1;
    public int SpriteCount { get; private set; }
    public int Animations { get; private set; }
    public int Sprites { get; private set; }
    public int Samples { get; private set; }
    public int Videos { get; private set; }
    public int Commands { get; private set; }
    public int Objects => objects.Count;
    public int Count => Rows.Count + Missing.Count;
    public RenderRow this[int index] => index < Rows.Count ? Rows[index] : Missing[index - Rows.Count];
    public IEnumerator<RenderRow> GetEnumerator() => Rows.Concat(Missing).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public void Prepare(Storyboard doc, int version)
    {
        if (document == doc && revision == version) return;
        document = doc;
        revision = version;
        objects.Clear();
        Animations = Sprites = Samples = Videos = Commands = 0;
        for (int i = 0; i < doc.Objects.Count; i++)
        {
            var obj = doc.Objects[i];
            string group = "";
            if (obj is SbSprite sprite)
            {
                var names = new List<string>();
                for (var g = sprite.Group; g != null && names.Count < SbObjectGroup.MaxDepth; g = g.Parent) names.Add(g.Name);
                names.Reverse();
                group = string.Join(" › ", names);
                if (sprite.IsAnimation) Animations++; else Sprites++;
                foreach (var node in sprite.Nodes) Commands += node is SbCommand ? 1 : node is SbGroup block ? block.Commands.Count : 0;
            }
            else if (obj is SbSample) Samples++;
            else if (obj is SbVideo) Videos++;
            objects[obj] = (i + 1, group);
        }
        foreach (var group in doc.Groups) Commands += group.Commands.Count;
    }

    public void Observe(int layer, bool hidden)
    {
        Alive[layer]++;
        if (hidden) Hidden[layer]++;
    }

    public void Begin()
    {
        Rows.Clear();
        Missing.Clear();
        Array.Clear(Alive);
        Array.Clear(Drawn);
        Array.Clear(Hidden);
        SpriteCount = 0;
    }

    public void Add(SbObject? obj, SbLayer layer, string path, Texture? texture, SpriteState state, SbOrigin origin,
        double load, bool simulated = false, int frame = 0, int frames = 1)
    {
        var meta = obj != null && objects.TryGetValue(obj, out var found) ? found : default;
        var row = new RenderRow(obj, meta.Number, layer, path, texture, state, origin, frame, frames, meta.Group ?? "", load, simulated);
        if (texture == null) { Missing.Add(row); return; }
        Rows.Add(row);
        if (obj is SbSprite)
        {
            SpriteCount++;
            Drawn[(int)layer]++;
        }
    }

    /// <summary>Debug picking shares the editor's corner transform, alpha mask and box fallback.</summary>
    public void At(Vector2 point, List<int> hits)
    {
        hits.Clear();
        Span<Vector2> corners = stackalloc Vector2[4];
        bool pixels = false;
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            var row = Rows[i];
            if (!row.IsSprite || row.Texture is not { } texture) continue;
            SpriteGeometry.Corners(row.State, row.Origin, texture.Width, texture.Height, corners);
            if (!SpriteGeometry.Contains(corners, point)) continue;
            var uv = SpriteGeometry.ToImageSpace(corners, point, SpriteGeometry.MirrorX(row.State), SpriteGeometry.MirrorY(row.State));
            bool opaque = texture.IsOpaqueAt(uv.X, uv.Y);
            if (opaque && !pixels) { hits.Clear(); pixels = true; }
            if (opaque || !pixels) hits.Add(i);
        }
    }
}
