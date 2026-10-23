using System.Numerics;
using SbStudio.Core;
using Silk.NET.OpenGL;

namespace SbStudio.App.Gfx;

public sealed unsafe partial class HitObjectRenderer
{
    private readonly Dictionary<string, SkinSprite[]> rulesetImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Texture> rulesetShapes = new();
    private Texture? rulesetDisc, rulesetRing, rulesetRounded;
    private ArgonBlobShape? argonCatchBlobs;
    private OsuBeatmap? catchMap;
    private List<CatchPreviewObject> catchObjects = new();
    private List<CatchPreviewMovement.Frame> catchMovement = new();
    private bool LegacyComponent(string name) => !lazerStyle || skin.HasBeatmapImage(name);

    private void ModeSprite(Texture texture, Vector2 centre, Vector2 size, float rotation, Vector3 colour, float alpha, bool additive = false, bool mirrorY = false)
        => Sprite(texture, centre, size, rotation, colour, alpha, additive, mirrorY);

    private SkinSprite? RulesetImage(string name, double elapsed = 0)
    {
        if (!rulesetImages.TryGetValue(name, out var frames))
            rulesetImages[name] = frames = skin.ReadAnimation(name).Select(CreateSkinSprite).ToArray();
        return frames.Length == 0 ? null : frames[OsuPreviewSkin.AnimationFrame(elapsed, skin.AnimationRate, frames.Length)];
    }

    private bool RulesetSprite(string name, Vector2 centre, Vector2 size, Vector3 colour, double elapsed = 0, float alpha = 1, float rotation = 0)
    {
        if (RulesetImage(name, elapsed) is not { } image) return false;
        ModeSprite(image.Texture, centre, size, rotation, colour, alpha);
        return true;
    }

    private bool NativeRulesetSprite(string name, Vector2 centre, float scale, Vector3 colour, double elapsed = 0,
        float alpha = 1, float rotation = 0, float maximum = float.PositiveInfinity, bool additive = false)
    {
        if (RulesetImage(name, elapsed) is not { } image) return false;
        // WithMaximumSize shrinks oversized images without stretching smaller or non-square assets.
        Vector2 size = image.Size * MathF.Min(1, maximum / MathF.Max(image.Size.X, image.Size.Y)) * scale;
        ModeSprite(image.Texture, centre, size, rotation, colour, alpha, additive);
        return true;
    }

    private void EnsureRulesetShapes()
    {
        if (rulesetDisc != null) return;
        rulesetDisc = Keep(Paint(128, (x, y, p) => new(1, 1, 1, Cover((1 - MathF.Sqrt(x * x + y * y)) * p))));
        rulesetRing = Keep(Paint(128, (x, y, p) =>
        {
            float d = MathF.Sqrt(x * x + y * y);
            return new(1, 1, 1, Cover((1 - d) * p) * Cover((d - 0.86f) * p));
        }));
        rulesetRounded = Keep(Paint(128, (x, y, p) =>
        {
            float dx = MathF.Max(MathF.Abs(x) - 0.8f, 0), dy = MathF.Max(MathF.Abs(y) - 0.8f, 0);
            return new(1, 1, 1, Cover((0.2f - MathF.Sqrt(dx * dx + dy * dy)) * p));
        }));

        Texture Keep(Texture texture) { rulesetShapes.Add(texture); return texture; }
    }

    private void DrawOtherRuleset(OsuBeatmap map, double time, RenderTarget target, ViewTransform view, bool widescreen)
    {
        EnsureRulesetShapes();
        switch (map.Mode)
        {
            case 1: DrawTaiko(map, time, target, view, widescreen); break;
            case 2: DrawCatch(map, time, target, view); break;
            case 3: DrawMania(map, time, target, view, widescreen); break;
        }
    }

    private void ModeRectangle(float x, float y, float width, float height, Vector4 colour)
    {
        if (width > 0 && height > 0) batch.DrawRect(new(x, y), new(x + width, y + height), colour);
    }

    private void ClipRuleset(RenderTarget target, ViewTransform view, Vector2 min, Vector2 max)
    {
        batch.Suspend();
        Vector2 a = view.ToPixels(min), b = view.ToPixels(max);
        int left = Math.Clamp((int)MathF.Floor(a.X), 0, target.Width), right = Math.Clamp((int)MathF.Ceiling(b.X), 0, target.Width);
        int top = Math.Clamp((int)MathF.Floor(a.Y), 0, target.Height), bottom = Math.Clamp((int)MathF.Ceiling(b.Y), 0, target.Height);
        gl.Enable(EnableCap.ScissorTest);
        gl.Scissor(left, target.Height - bottom, (uint)Math.Max(0, right - left), (uint)Math.Max(0, bottom - top));
        batch.Resume();
    }

    private void EndRulesetClip()
    {
        batch.Suspend();
        gl.Disable(EnableCap.ScissorTest);
        batch.Resume();
    }

    // Mania: notes align their bottom edge with the hit target. Hold tails remain above the target
    // until release. The default speed is lazer's 11485 / 8 ms; SV changes are integrated over time.
    private void DrawMania(OsuBeatmap map, double time, RenderTarget target, ViewTransform view, bool widescreen)
    {
        if (lazerStyle)
        {
            DrawArgonMania(map, time, target, view, widescreen);
            return;
        }
        int keys = map.Columns;
        var layout = new LegacyManiaLayout(skin, keys, widescreen);
        int stages = layout.Stages;
        var widths = layout.Widths;
        var x = layout.ColumnLefts;
        float StageWidth(int stage) => layout.StageWidths[stage];
        float StageLeft(int stage) => layout.StageLefts[stage];
        float left = layout.Left;
        float hit = Math.Clamp(skin.ManiaNumber(keys, "HitPosition", 402), 240, 480);
        bool up = false;
        if (up) hit = 480 - hit;
        float sign = up ? -1 : 1;
        float top = up ? hit : 0, bottom = up ? 480 : hit;
        for (int i = 0; i < keys; i++)
        {
            float cursor = x[i];
            Vector4 background = skin.ManiaColour(keys, "Colour" + (i + 1), new(0, 0, 0, 1));
            ModeRectangle(cursor, 0, widths[i], 480, background);
            Vector4 lineColour = skin.ManiaColour(keys, "ColourColumnLine", Vector4.One);
            float line = skin.ManiaNumber(keys, "ColumnLineWidth", 2, i) * 0.74f;
            ModeRectangle(cursor, 0, line, hit, lineColour);
        }
        for (int stage = 0; stage < stages; stage++)
        {
            float stageLeft = StageLeft(stage), stageWidth = StageWidth(stage);
            StageEdge("StageLeft", "mania-stage-left", stageLeft, -1);
            StageEdge("StageRight", "mania-stage-right", stageLeft + stageWidth, 1);
            string targetName = skin.ManiaValue(keys, "StageHint", "mania-stage-hint");
            if (RulesetImage(targetName) is { } hint)
                ModeSprite(hint.Texture, new(stageLeft + stageWidth / 2, hit), new(stageWidth, hint.Size.Y * 0.9f * 1.6025f * 0.625f), 0, Vector3.One, 1);
            if (skin.ManiaValue(keys, "JudgementLine", "1") != "0")
                ModeRectangle(stageLeft, hit, stageWidth, 0.625f, skin.ManiaColour(keys, "ColourJudgementLine", new(1, 1, 1, 0.9f)));
        }
        float clipRight = StageLeft(stages - 1) + StageWidth(stages - 1);
        var pressing = new bool[keys];
        foreach (var obj in map.Objects)
            if (time >= obj.StartTime && time <= obj.EndTime + 80) pressing[map.ColumnOf(obj)] = true;
        for (int i = 0; i < keys; i++)
        {
            string type = ColumnType(i);
            {
                string name = skin.ManiaValue(keys, "KeyImage" + i + (pressing[i] ? "D" : ""), "mania-key" + type + (pressing[i] ? "D" : ""));
                if (RulesetImage(name) is { } key)
                {
                    float height = key.Size.Y * widths[i] / key.Size.X;
                    ModeSprite(key.Texture, new(x[i] + widths[i] / 2, 480 - height / 2), new(widths[i], height), 0, Vector3.One, 1);
                }
            }
        }
        ClipRuleset(target, view, new(left, top), new(clipRight, bottom));
        foreach (var bar in map.BarLines)
        {
            if (bar.Time < time) continue;
            float y = hit - (float)(map.ScrollDistance(time, bar.Time) / map.TimePreempt * 402);
            if (y < top) break;
            for (int stage = 0; stage < stages; stage++)
                ModeRectangle(StageLeft(stage), y - 0.75f, StageWidth(stage), skin.ManiaNumber(keys, "BarlineHeight", 1.2f) * 0.625f, skin.ManiaColour(keys, "ColourBarline", Vector4.One));
        }
        for (int index = map.Objects.Count - 1; index >= 0; index--)
        {
            var obj = map.Objects[index];
            if (time > obj.EndTime + 200 || obj.StartTime > time + map.TimePreempt * 12) continue;
            int column = map.ColumnOf(obj);
            float width = widths[column], centreX = x[column] + width / 2;
            float head = hit - sign * (float)(map.ScrollDistance(time, obj.StartTime) / map.TimePreempt * 402);
            float tail = hit - sign * (float)(map.ScrollDistance(time, obj.EndTime) / map.TimePreempt * 402);
            if (MathF.Max(head, tail) < top - 50 || MathF.Min(head, tail) > bottom + 50) continue;
            float alpha = time <= obj.EndTime ? 1 : (float)(1 - Along(time, obj.EndTime, 200));
            Vector3 accent = ManiaAccent(column);
            string type = ColumnType(column);
            bool hold = obj.EndTime > obj.StartTime;
            float height = width * 0.3f;
            string noteName = skin.ManiaValue(keys, "NoteImage" + column + (hold ? "H" : ""), "mania-note" + type + (hold ? "H" : ""));
            var note = RulesetImage(noteName, time - obj.StartTime + map.TimePreempt) ?? RulesetImage("mania-note" + type);
            if (note != null) height = note.Size.Y * skin.ManiaNumber(keys, "WidthForNoteHeightScale", width) / note.Size.X;
            if (hold)
            {
                if (time >= obj.StartTime) head = hit;
                float bodyTop = MathF.Min(head, tail), bodyHeight = MathF.Abs(head - tail);
                string bodyName = skin.ManiaValue(keys, "NoteImage" + column + "L", "mania-note" + type + "L");
                var body = RulesetImage(bodyName, Math.Max(0, time - obj.StartTime));
                if (body != null)
                {
                    // lazer currently treats every repeat style alike, and defaults to Stretch before v2.5.
                    int bodyStyle = (int)skin.ManiaNumber(keys, "NoteBodyStyle", skin.Version < 2.5f ? 0 : 3);
                    if (bodyStyle == 0)
                        ModeSprite(body.Texture, new(centreX, bodyTop + bodyHeight / 2), new(width, bodyHeight), 0, Vector3.One, alpha, mirrorY: up);
                    else
                    {
                        float tileHeight = MathF.Max(2, body.Size.Y * width / body.Size.X);
                        float shownTop = MathF.Max(top, bodyTop), shownBottom = MathF.Min(bottom, bodyTop + bodyHeight);
                        if (shownBottom > shownTop)
                        {
                            ClipRuleset(target, view, new(x[column], shownTop), new(x[column] + width, shownBottom));
                            float anchor = bodyTop;
                            float firstTile = anchor + MathF.Floor((shownTop - anchor) / tileHeight) * tileHeight;
                            for (float tileY = firstTile; tileY < shownBottom; tileY += tileHeight)
                                ModeSprite(body.Texture, new(centreX, tileY + tileHeight / 2), new(width, tileHeight), 0, Vector3.One, alpha, mirrorY: up);
                            ClipRuleset(target, view, new(left, top), new(clipRight, bottom));
                        }
                    }
                }
                else
                {
                    ModeRectangle(x[column] + width * 0.2f, bodyTop, width * 0.6f, bodyHeight, new(accent, alpha * 0.4f));
                    ModeRectangle(centreX - 1.8f, bodyTop, 3.6f, bodyHeight, new(accent, alpha));
                }
                DrawNote(tail, true);
            }
            DrawNote(head, false);

            void DrawNote(float y, bool isTail)
            {
                var piece = isTail ? RulesetImage(skin.ManiaValue(keys, "NoteImage" + column + "T", "mania-note" + type + "T")) ?? note : note;
                float pieceHeight = piece != null ? piece.Size.Y * skin.ManiaNumber(keys, "WidthForNoteHeightScale", width) / piece.Size.X : height;
                Vector2 centre = new(centreX, y + (isTail ? sign : -sign) * pieceHeight / 2);
                if (piece != null) ModeSprite(piece.Texture, centre, new(width, pieceHeight), 0, Vector3.One, alpha, mirrorY: up ^ isTail);
                else
                {
                    ModeSprite(rulesetRounded!, centre, new(width - 1, height), 0, accent, alpha);
                    ModeRectangle(x[column] + 1, y - sign * 3 - (up ? 3 : 0), width - 2, 3, new(1, 1, 1, alpha));
                    if (!isTail) Chevron(centre, 7, sign > 0 ? MathF.PI / 2 : -MathF.PI / 2, alpha);
                }
            }
        }
        EndRulesetClip();
        if (RulesetImage(skin.ManiaValue(keys, "StageBottom", "mania-stage-bottom"), time) is { } foreground)
            for (int stage = 0; stage < stages; stage++)
                ModeSprite(foreground.Texture, new(StageLeft(stage) + StageWidth(stage) / 2, 480 - foreground.Size.Y / 2), foreground.Size, 0, Vector3.One, 1);

        string ColumnType(int column)
        {
            int count = layout.KeysInStage(column), local = layout.LocalColumn(column);
            return count % 2 == 1 && local == count / 2 ? "S"
                : Math.Min(local, count - 1 - local) % 2 == 0 ? "1" : "2";
        }
        Vector3 ManiaAccent(int column) => ColumnType(column) == "S" ? new(0.95f, 0.25f, 0.35f)
            : column % 2 == 0 ? new(0.15f, 0.7f, 1) : new(0.95f, 0.95f, 1);
        void StageEdge(string lookup, string fallback, float edge, int side)
        {
            if (RulesetImage(skin.ManiaValue(keys, lookup, fallback)) is { } image)
                ModeSprite(image.Texture, new(edge + side * image.Size.X * 0.625f / 2, 240), new(image.Size.X * 0.625f, 480), 0, Vector3.One, 1);
        }
    }

    private void DrawCatch(OsuBeatmap map, double time, RenderTarget target, ViewTransform view)
    {
        if (catchMap != map)
        {
            catchMap = map;
            catchObjects = CatchPreviewObjects.Build(map);
            catchMovement = CatchPreviewMovement.Build(map, catchObjects);
        }
        var field = RulesetPreviewLayout.CatchField(target.Width, target.Height, view);
        argonCatchBlobs ??= new(gl, batch);
        argonCatchBlobs.SetView(target, view);
        float hitY = field.HitY;
        float catcherX = CatchPreviewMovement.PositionAt(catchMovement, time);
        float catcherScale = 2 * map.Scale * field.Scale, catcherWidth = 106.75f * catcherScale;
        Vector2 catcher = new(field.Left + catcherX * field.Scale, hitY);
        string catcherName = (lazerStyle ? 1 : skin.Version) < 2.3f && LegacyComponent("fruit-ryuuta") && RulesetImage("fruit-ryuuta") != null ? "fruit-ryuuta" : "fruit-catcher-idle";
        if (LegacyComponent(catcherName) && RulesetImage(catcherName, time) is { } catcherImage)
        {
            Vector2 size = catcherImage.Size * (0.35f * catcherScale);
            ModeSprite(catcherImage.Texture, new(catcher.X, hitY + size.Y / 2 - 16 * 0.35f * catcherScale), size, 0, Vector3.One, 1);
        }
        else
        {
            argonManiaRectangles ??= new(gl, batch);
            argonManiaRectangles.SetView(target, view);
            float centreWidth = catcherWidth * 0.8f, plateHeight = 10 * catcherScale;
            argonManiaRectangles.Draw(new(catcher.X - centreWidth / 2, hitY - plateHeight / 2, centreWidth, plateHeight), Vector4.One, Vector4.One, plateHeight / 2);
            float bumper = catcherWidth * 0.1f;
            argonManiaRectangles.Draw(new(catcher.X - catcherWidth / 2, hitY - 2 * catcherScale, bumper, 4 * catcherScale), Vector4.One, Vector4.One, 2 * catcherScale);
            argonManiaRectangles.Draw(new(catcher.X + catcherWidth / 2 - bumper, hitY - 2 * catcherScale, bumper, 4 * catcherScale), Vector4.One, Vector4.One, 2 * catcherScale);
            ModeRectangle(view.ToStoryboard(Vector2.Zero).X, hitY - 0.9f * catcherScale, catcher.X - catcherWidth / 2 - view.ToStoryboard(Vector2.Zero).X, 1.8f * catcherScale, new(1, 1, 1, 0.25f));
            float lineX = catcher.X + catcherWidth / 2;
            ModeRectangle(lineX, hitY - 0.9f * catcherScale, view.ToStoryboard(new(target.Width, 0)).X - lineX, 1.8f * catcherScale, new(1, 1, 1, 0.25f));
        }
        // Catch's visible area is screen-wide; sprites may straddle the 512-wide playable area's edges.
        ClipRuleset(target, view, view.ToStoryboard(Vector2.Zero), view.ToStoryboard(new(target.Width, target.Height)));
        for (int index = FirstCatchObject(time); index < catchObjects.Count; index++)
        {
            var obj = catchObjects[index];
            if (obj.Time > time + map.TimePreempt) break;
            float alpha = 1;
            float visualScale = map.Scale * field.Scale;
            if (obj.Kind == CatchObjectKind.TinyDroplet) visualScale *= 0.5f;
            float diameter = 128 * visualScale;
            Vector2 position = field.Position(obj.X, obj.Time - time, map.TimePreempt);
            Vector3 colour = obj.Kind == CatchObjectKind.Banana ? ((CatchVisualRandom.Value(obj.Time) % 3) switch { 1 => new Vector3(255, 192, 0), 2 => new Vector3(214, 221, 28), _ => new Vector3(255, 240, 0) }) / 255
                : PreviewComboColours.Resolve(map, obj.Combo);
            string name = obj.Kind == CatchObjectKind.Banana ? "fruit-bananas"
                : obj.Kind != CatchObjectKind.Fruit ? "fruit-drop" : "fruit-" + new[] { "pear", "grapes", "apple", "orange" }[obj.Index % 4];
            float progress = (float)(1 - (obj.Time - time) / map.TimePreempt);
            float rotation = obj.Kind is CatchObjectKind.Droplet or CatchObjectKind.TinyDroplet
                ? (CatchVisualRandom.Single(obj.Time, 1) * 20 + 720 * progress * (float)(map.TimePreempt / (map.TimePreempt + 2000))) * MathF.PI / 180
                : (CatchVisualRandom.Single(obj.Time, 1) - 0.5f) * 40 * MathF.PI / 180;
            if (obj.Kind == CatchObjectKind.Banana)
            {
                float startScale = 0.6f + 1.6f * CatchVisualRandom.Single(obj.Time, 3);
                float bananaScale = startScale + (0.6f - startScale) * progress;
                visualScale *= bananaScale;
                diameter *= bananaScale;
                rotation = (CatchVisualRandom.Single(obj.Time, 1) + (CatchVisualRandom.Single(obj.Time, 2) - CatchVisualRandom.Single(obj.Time, 1)) * progress) * MathF.Tau - MathF.PI;
            }
            bool legacyPiece = LegacyComponent(obj.Kind == CatchObjectKind.Fruit ? "fruit-pear" : name)
                && (obj.Kind != CatchObjectKind.Fruit || RulesetImage("fruit-pear") != null);
            float legacyScale = visualScale * (obj.Kind is CatchObjectKind.Droplet or CatchObjectKind.TinyDroplet ? 0.8f : 1);
            if (legacyPiece && RulesetImage(name) != null)
            {
                if (obj.HyperDash) NativeRulesetSprite(name, position, legacyScale * 1.2f, new(1, 0, 0), alpha: 0.7f, rotation: rotation, maximum: 160, additive: true);
                NativeRulesetSprite(name, position, legacyScale, colour, alpha: alpha, rotation: rotation, maximum: 160);
                NativeRulesetSprite(name + "-overlay", position, legacyScale, Vector3.One, alpha: alpha, rotation: rotation, maximum: 160);
            }
            else
            {
                bool drop = obj.Kind is CatchObjectKind.Droplet or CatchObjectKind.TinyDroplet;
                if (drop) diameter *= 0.7f;
                for (int layer = 0; layer < (drop ? 2 : 3); layer++)
                {
                    float rotationRandomness = 0.2f + 0.8f * CatchVisualRandom.Single(obj.Time, 10);
                    float elapsed = (float)(time - obj.Time + map.TimePreempt);
                    float spin = drop ? -elapsed * 0.4f * rotationRandomness * (layer == 1 ? 0.5f : 1)
                        : elapsed * layer * 2 * 0.02f * rotationRandomness * (layer % 2 == 1 ? 1 : -1);
                    float inner = drop ? layer == 0 ? 0.5f : 0.4f : layer == 0 ? 0.5f : layer == 1 ? 0.2f : 0.05f;
                    argonCatchBlobs.Draw(position, diameter * (drop ? layer == 0 ? 1 : 0.7f : layer == 0 ? 1.1f : 1), rotation + spin * MathF.PI / 180,
                        new(colour, alpha * (layer == 0 ? 0.15f : layer == 1 ? 0.5f : 1)), inner, (int)CatchVisualRandom.Value(obj.Time, layer + 4));
                }
                if (obj.HyperDash) argonCatchBlobs.Draw(position, diameter * (drop ? 1 : 1.15f), rotation,
                    new(1, 0, 0, 1), drop ? 0.5f : 0.08f, (int)CatchVisualRandom.Value(obj.Time, 4));
                ModeSprite(rulesetDisc!, position, new(20 * visualScale), 0, Vector3.One, alpha);
                if (obj.Kind == CatchObjectKind.Banana)
                {
                    float flare = 1 - (float)EaseOutQuint(Along(progress, 0.3, 0.5));
                    float flareWidth = 200 * (0.4f * visualScale + 0.6f * field.Scale);
                    ModeSprite(rulesetRounded!, position, new(flareWidth, 8 * visualScale), 0, Vector3.One, 0.4f * flare, true);
                }
            }
        }
        EndRulesetClip();
    }

    private int FirstCatchObject(double time)
    {
        int low = 0, high = catchObjects.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (catchObjects[middle].Time < time) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private void Chevron(Vector2 centre, float size, float rotation, float alpha)
    {
        Vector2 direction = new(MathF.Cos(rotation), MathF.Sin(rotation));
        Vector2 perpendicular = new(-direction.Y, direction.X);
        ModeSprite(batch.White, centre - perpendicular * size * 0.25f, new(size, 2), rotation + MathF.PI / 4, Vector3.One, alpha);
        ModeSprite(batch.White, centre + perpendicular * size * 0.25f, new(size, 2), rotation - MathF.PI / 4, Vector3.One, alpha);
    }

    private void ReleaseRulesetTextures()
    {
        argonManiaArrow?.Dispose();
        argonManiaArrow = null;
        taikoAngle?.Dispose();
        taikoAngle = null;
        foreach (var image in rulesetImages.Values.SelectMany(frames => frames)) image.Texture.Dispose();
        rulesetImages.Clear();
        foreach (var shape in rulesetShapes) shape.Dispose();
        rulesetShapes.Clear();
        rulesetDisc = rulesetRing = rulesetRounded = null;
    }
}
