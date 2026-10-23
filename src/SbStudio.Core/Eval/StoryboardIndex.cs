namespace SbStudio.Core;

/// <summary>
/// What every frame asks about the storyboard as a whole: the sprites of each layer and when they are
/// alive, the samples, the overall time range and the members of each group. Answering by walking all
/// objects costs milliseconds per frame once there are tens of thousands of them, so the answers are
/// collected here in one pass and only collected again after an edit.
/// </summary>
public sealed class StoryboardIndex
{
    /// <summary>Sprites of one layer in file order: later ones are drawn in front.</summary>
    public sealed class LayerSprites
    {
        private SbSprite[] sprites = Array.Empty<SbSprite>();
        // Kept apart from the sprites so the many that are not alive are skipped without touching them.
        private int[] starts = Array.Empty<int>();
        private int[] ends = Array.Empty<int>();

        public int Count { get; private set; }

        public SbSprite this[int index] => sprites[index];

        /// <summary>The same answer as <see cref="SpriteTimeline.IsActive"/> of that sprite.</summary>
        public bool IsActive(int index, double time) => time >= starts[index] && time <= ends[index];

        internal void Clear()
        {
            Array.Clear(sprites, 0, Count);
            Count = 0;
        }

        internal void Add(SbSprite sprite, SpriteTimeline timeline)
        {
            if (Count == sprites.Length)
            {
                int capacity = Math.Max(256, Count * 2);
                Array.Resize(ref sprites, capacity);
                Array.Resize(ref starts, capacity);
                Array.Resize(ref ends, capacity);
            }
            sprites[Count] = sprite;
            // A sprite without commands is never shown: an empty span keeps it out.
            starts[Count] = timeline.HasCommands ? timeline.StartTime : int.MaxValue;
            ends[Count] = timeline.HasCommands ? timeline.EndTime : int.MinValue;
            Count++;
        }
    }

    private readonly LayerSprites[] layers = new LayerSprites[SbInfo.LayerCount];
    private readonly List<SbSample> samples = new();
    private readonly List<SbVideo> videos = new();
    private readonly Dictionary<SbObjectGroup, List<SbSprite>> members = new();
    private readonly Dictionary<SbObjectGroup, Content> contents = new();

    /// <summary>What is in a group, however deep, among the objects that are shown.</summary>
    public sealed class Content
    {
        /// <summary>The sprites, in file order.</summary>
        public readonly List<SbSprite> Sprites = new();
        /// <summary>From the first moment one of them is shown to the last; Start is past End when none ever is.</summary>
        public int Start = int.MaxValue, End = int.MinValue;
        /// <summary>How many of them are switched off with the eye, and how many are locked.</summary>
        public int Hidden, Locked;

        public bool HasSpan => Start <= End;
    }

    private static readonly Content Nothing = new();
    private string? imageAsked;
    private bool imageUsed;

    public StoryboardIndex()
    {
        for (int i = 0; i < layers.Length; i++) layers[i] = new LayerSprites();
    }

    /// <summary>
    /// Indexed by <see cref="SbLayer"/>: the sprites of that layer in the order they are drawn in, the
    /// hindmost first. That is the order of the exported files: first the sprites that are in no group,
    /// as the storyboard has them, then on top of them those that are in groups, in the order
    /// <see cref="Storyboard.GroupedInDrawOrder"/> gives them. And as the game reads a difficulty's own
    /// file before the shared one, all that the shown difficulty has of a layer, grouped or not, comes
    /// before what is shared.
    /// </summary>
    public IReadOnlyList<LayerSprites> Layers => layers;

    public IReadOnlyList<SbSample> Samples => samples;

    /// <summary>The videos, in the order of the script: a later one plays over an earlier one.</summary>
    public IReadOnlyList<SbVideo> Videos => videos;

    /// <summary>The same as <see cref="Storyboard.TimeRange"/>.</summary>
    public (int Start, int End)? TimeRange { get; private set; }

    /// <summary>The same sprites as <see cref="Storyboard.MembersOf"/>. The list stays as it is when the index is built again.</summary>
    public IReadOnlyList<SbSprite> MembersOf(SbObjectGroup group) =>
        members.TryGetValue(group, out var list) ? list : Array.Empty<SbSprite>();

    /// <summary>The sprites in a group and in the groups in it, with the time they take up together.</summary>
    public Content ContentOf(SbObjectGroup group) => contents.TryGetValue(group, out var content) ? content : Nothing;

    /// <summary>
    /// Whether a sprite of the Background layer shows this file. That is when osu! leaves out the beatmap
    /// background: it compares the names as written, so a different case does not count, and neither
    /// does a sprite of another layer.
    /// </summary>
    public bool BackgroundLayerUses(string path)
    {
        // Asked every frame about the same file (the beatmap background), so the last answer is kept.
        if (imageAsked == path) return imageUsed;
        imageAsked = path;
        imageUsed = false;
        var sprites = layers[(int)SbLayer.Background];
        for (int i = 0; i < sprites.Count && !imageUsed; i++)
            imageUsed = sprites[i].Path == path;
        return imageUsed;
    }

    /// <summary>Collects everything again. Call after any change to the storyboard; null empties the index.</summary>
    public void Build(Storyboard? source)
    {
        foreach (var layer in layers) layer.Clear();
        samples.Clear();
        videos.Clear();
        members.Clear();
        contents.Clear();
        TimeRange = null;
        imageAsked = null;
        if (source == null) return;

        // With sprites in groups, the layers are put together at the end: in each, those that are in
        // groups go on top of those that are not, part by part the way the game reads them, a
        // difficulty's own file before the shared one. Without any, the storyboard's own order is it.
        bool anyGrouped = false;
        if (source.Groups.Count > 0)
        {
            foreach (var obj in source.Objects)
            {
                if (obj is not SbSprite { Group: not null }) continue;
                anyGrouped = true;
                break;
            }
        }
        // The sprites of each layer that are in no group: the difficulty's own first, then the shared ones.
        var free = anyGrouped ? new List<SbSprite>[2, layers.Length] : null;

        int start = int.MaxValue, end = int.MinValue;
        foreach (var obj in source.Objects)
        {
            if (obj is SbSprite sprite)
            {
                var timeline = sprite.Timeline;
                if ((uint)sprite.Layer < (uint)layers.Length && sprite.Group == null)
                {
                    if (free == null) layers[(int)sprite.Layer].Add(sprite, timeline);
                    else (free[sprite.Difficulty != null ? 0 : 1, (int)sprite.Layer] ??= new List<SbSprite>()).Add(sprite);
                }
                if (sprite.Group != null)
                {
                    if (!members.TryGetValue(sprite.Group, out var list)) members[sprite.Group] = list = new List<SbSprite>();
                    list.Add(sprite);
                    int steps = 0;
                    for (var g = sprite.Group; g != null && steps < SbObjectGroup.MaxDepth; g = g.Parent, steps++)
                    {
                        if (!contents.TryGetValue(g, out var content)) contents[g] = content = new Content();
                        content.Sprites.Add(sprite);
                        if (sprite.Hidden) content.Hidden++;
                        if (sprite.Locked) content.Locked++;
                        if (!timeline.HasCommands) continue;
                        content.Start = Math.Min(content.Start, timeline.StartTime);
                        content.End = Math.Max(content.End, timeline.EndTime);
                    }
                }
                if (!timeline.HasCommands) continue;
                start = Math.Min(start, timeline.StartTime);
                end = Math.Max(end, timeline.EndTime);
            }
            else if (obj is SbSample sample)
            {
                samples.Add(sample);
                start = Math.Min(start, sample.Time);
                end = Math.Max(end, sample.Time);
            }
            else if (obj is SbVideo video)
            {
                videos.Add(video);
                start = Math.Min(start, video.Time);
                end = Math.Max(end, video.Time);
            }
        }
        if (start != int.MaxValue) TimeRange = (start, end);

        if (free == null) return;
        var ranks = source.GroupRanks();
        for (int part = 0; part < 2; part++)
        {
            bool own = part == 0;
            var grouped = Storyboard.GroupedInDrawOrder(source.Objects.Where(o => o.Difficulty != null == own), ranks);
            for (int layer = 0; layer < layers.Length; layer++)
            {
                if (free[part, layer] is { } list)
                    foreach (var sprite in list) layers[layer].Add(sprite, sprite.Timeline);
                foreach (var sprite in grouped)
                    if ((int)sprite.Layer == layer) layers[layer].Add(sprite, sprite.Timeline);
            }
        }
    }
}
