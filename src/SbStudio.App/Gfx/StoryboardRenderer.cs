using System.Numerics;
using SbStudio.Core;

namespace SbStudio.App.Gfx;

public sealed class RenderOptions
{
    public TriggerSimulation? Simulation;
    /// <summary>Show the Fail layer instead of the Pass layer.</summary>
    public bool FailState;
    public bool ShowBackground = true;
    /// <summary>Play the videos behind the storyboard, as osu! does with its video option on.</summary>
    public bool ShowVideo = true;
    /// <summary>
    /// osu!'s background dim: 0 = untouched, 1 = black. Despite its name it darkens the storyboard just
    /// as much as the beatmap background.
    /// </summary>
    public float BackgroundDim;
    /// <summary>
    /// Draw the hit objects of the difficulty that is shown, where the game has them: over the Foreground
    /// layer and under the Overlay layer.
    /// </summary>
    public bool ShowHitObjects;
    /// <summary>Ignore editor-only eye switches when writing a complete storyboard video.</summary>
    public bool IncludeHidden;

    public static RenderOptions ForVideoExport() => new()
    {
        FailState = false,
        ShowBackground = true,
        ShowVideo = true,
        BackgroundDim = 0,
        ShowHitObjects = false,
        IncludeHidden = true,
    };
}

/// <summary>Draws the storyboard as osu! would show it at one moment in time.</summary>
public sealed class StoryboardRenderer
{
    private readonly SpriteBatch batch;
    private readonly TextureCache textures;
    private readonly VideoPlayer videos;
    private readonly HitObjectRenderer hitObjects;

    public int VisibleSprites { get; private set; }

    /// <summary>osu!'s "SB Load" of the picture that was drawn last; see <see cref="StoryboardLoad"/>.</summary>
    public double Load { get; private set; }

    public StoryboardRenderer(SpriteBatch batch, TextureCache textures, VideoPlayer videos, HitObjectRenderer hitObjects)
    {
        this.batch = batch;
        this.textures = textures;
        this.videos = videos;
        this.hitObjects = hitObjects;
    }

    /// <param name="index">The index of the project's storyboard, up to date with its last edit.</param>
    /// <param name="beatmap">The hit objects to draw when the options ask for them; null when there are none to draw.</param>
    /// <param name="widescreen">The picture format to draw for, when it is not to be the project's own: a video may be exported in the other one.</param>
    public void Render(Project project, StoryboardIndex index, double time, RenderTarget target, ViewTransform view, RenderOptions options,
        OsuBeatmap? beatmap = null, bool? widescreen = null, RenderRecord? record = null)
    {
        bool wide = widescreen ?? project.Settings.Widescreen;
        // Sprites that follow a group are drawn as the commands they are exported with have them, which
        // is what the game plays: as near to the motion as each group asks for.

        batch.Begin(target, view, new Vector4(0, 0, 0, 1));
        VisibleSprites = 0;
        Load = 0;
        record?.Begin();

        // As in osu!, the dim scales the colour of everything that is drawn and leaves opacity alone.
        float light = 1 - Math.Clamp(options.BackgroundDim, 0, 1);
        if (options.ShowBackground) DrawBackground(project, index, light, record);
        if (options.ShowVideo) DrawVideos(project, index, time, light, wide, options.IncludeHidden, record);

        Span<Vector2> corners = stackalloc Vector2[4];
        bool failState = options.Simulation is { Stream.HasStateRules: true } sim ? sim.FailStateAt(time) : options.FailState;
        for (int layer = 0; layer < SbInfo.LayerCount; layer++)
        {
            bool layerShown = !(layer == (int)SbLayer.Fail && !failState) && !(layer == (int)SbLayer.Pass && failState);
            if (!layerShown && record == null) continue;
            // The game draws what is played between the Foreground and the Overlay. The background dim
            // leaves it as it is.
            if (layer == (int)SbLayer.Overlay && options.ShowHitObjects && beatmap != null) hitObjects.Draw(beatmap, time, target, view, wide);

            var sprites = index.Layers[layer];
            for (int i = 0; i < sprites.Count; i++)
            {
                if (!sprites.IsActive(i, time)) continue;
                var sprite = sprites[i];
                record?.Observe(layer, sprite.Hidden && !options.IncludeHidden);
                if (!layerShown || (sprite.Hidden && !options.IncludeHidden)) continue;
                var timeline = sprite.Timeline;
                if ((options.Simulation == null || !timeline.HasTriggers) && timeline.OpacityAt(time) <= 0) continue;

                var state = options.Simulation?.Evaluate(sprite, time) ?? sprite.ShownState(time);
                float opacity = (float)Math.Clamp(state.Opacity, 0, 1);
                if (opacity <= 0) continue;

                string frameFile = FrameFile(sprite, timeline, time);
                var texture = textures.Require(frameFile);
                if (texture == null)
                {
                    record?.Add(sprite, sprite.Layer, frameFile, null, state, sprite.Origin, 0);
                    continue;
                }

                SpriteGeometry.Corners(state, sprite.Origin, texture.Width, texture.Height, corners);
                double contribution;
                {
                    // The size it is shown at and where it lies, both as if it were not turned.
                    double width = texture.Width * state.Scale * Math.Abs(state.ScaleX), height = texture.Height * state.Scale * Math.Abs(state.ScaleY);
                    var (originX, originY) = SbInfo.OriginFraction(sprite.Origin);
                    contribution = StoryboardLoad.OfSprite(state.X - originX * width, state.Y - originY * height, width, height,
                        state.Rotation, opacity, wide);
                }
                Load += contribution;
                record?.Add(sprite, sprite.Layer, frameFile, texture, state, sprite.Origin, contribution,
                    options.Simulation != null && timeline.HasTriggers, sprite.IsAnimation ? timeline.FrameAt(time, sprite) : 0, sprite.FrameCount);
                var color = new Vector4(
                    (float)Math.Clamp(state.R / 255, 0, 1) * light,
                    (float)Math.Clamp(state.G / 255, 0, 1) * light,
                    (float)Math.Clamp(state.B / 255, 0, 1) * light,
                    opacity);
                batch.Draw(texture, corners, color, state.Additive, SpriteGeometry.MirrorX(state), SpriteGeometry.MirrorY(state));
                VisibleSprites++;
            }
        }

        batch.End();
    }

    public static string FrameFile(SbSprite sprite, SpriteTimeline timeline, double time) =>
        sprite.IsAnimation ? SbSprite.FramePath(sprite.Path, timeline.FrameAt(time, sprite)) : sprite.Path;

    /// <summary>
    /// The beatmap background sits behind everything, unless a sprite of the Background layer shows the
    /// same file, in which case osu! removes it.
    /// </summary>
    private void DrawBackground(Project project, StoryboardIndex index, float light, RenderRecord? record)
    {
        string background = project.Settings.Background;
        if (background.Length == 0 || index.BackgroundLayerUses(background)) return;

        var texture = textures.Require(background);
        if (texture == null) return;

        float scale = 480f / texture.Height;
        float halfW = texture.Width * scale / 2, halfH = 240;
        // osu! counts the background as it does any picture.
        Load += StoryboardLoad.Of(halfW * 2, halfH * 2);
        Span<Vector2> corners = stackalloc Vector2[4];
        corners[0] = new Vector2(320 - halfW, 240 - halfH);
        corners[1] = new Vector2(320 + halfW, 240 - halfH);
        corners[2] = new Vector2(320 + halfW, 240 + halfH);
        corners[3] = new Vector2(320 - halfW, 240 + halfH);
        batch.Draw(texture, corners, new Vector4(light, light, light, 1), false);
        record?.Add(null, SbLayer.Background, background, texture,
            new SpriteState { X = 320, Y = 240, Scale = scale, ScaleX = 1, ScaleY = 1, Opacity = 1, R = 255, G = 255, B = 255 },
            SbOrigin.Centre, StoryboardLoad.Of(halfW * 2, halfH * 2));
    }

    /// <summary>
    /// The videos play over the background and under every layer, in the middle of the picture, each from
    /// the time its line gives. Dimmed like everything else.
    /// </summary>
    private void DrawVideos(Project project, StoryboardIndex index, double time, float light, bool wide, bool includeHidden, RenderRecord? record)
    {
        if (index.Videos.Count == 0) return;
        bool hasStoryboard = false;
        for (int layer = 0; layer < SbInfo.LayerCount && !hasStoryboard; layer++) hasStoryboard = index.Layers[layer].Count > 0;

        Span<Vector2> corners = stackalloc Vector2[4];
        foreach (var video in index.Videos)
        {
            if ((video.Hidden && !includeHidden) || video.Path.Length == 0) continue;
            string file = project.AssetFullPath(video.Path);
            var info = videos.Info(file);
            if (info == null) continue;

            double at = time - video.Time;
            float opacity = (float)info.OpacityAt(at);
            if (opacity <= 0) continue;
            var texture = videos.Frame(file, at);
            if (texture == null) continue;

            float scale = (float)info.ScaleFor(wide, hasStoryboard);
            float halfW = info.Width * scale / 2, halfH = info.Height * scale / 2;
            if (opacity > StoryboardLoad.LeastOpacity) Load += StoryboardLoad.Of(halfW * 2, halfH * 2);
            corners[0] = new Vector2(320 - halfW, 240 - halfH);
            corners[1] = new Vector2(320 + halfW, 240 - halfH);
            corners[2] = new Vector2(320 + halfW, 240 + halfH);
            corners[3] = new Vector2(320 - halfW, 240 + halfH);
            batch.Draw(texture, corners, new Vector4(light, light, light, opacity), false);
            record?.Add(video, SbLayer.Background, video.Path, texture,
                new SpriteState { X = 320, Y = 240, Scale = scale, ScaleX = 1, ScaleY = 1, Opacity = opacity, R = 255, G = 255, B = 255 },
                SbOrigin.Centre, opacity > StoryboardLoad.LeastOpacity ? StoryboardLoad.Of(halfW * 2, halfH * 2) : 0);
        }
    }
}
