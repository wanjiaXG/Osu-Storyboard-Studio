using SbStudio.Effects;

namespace SbStudio.Effects.BuiltIn;

public sealed class Snow : IEffectModule
{
    private ParameterKey<AssetReference> image = null!;
    private ParameterKey<int> count = null!, seed = null!;
    private ParameterKey<double> speed = null!, direction = null!, sizeMin = null!, sizeMax = null!;
    private ParameterKey<ColorValue> color = null!;
    public void Define(EffectDefinitionBuilder d)
    {
        d.Name("Snow").Category("Particles").Description("Drifting particles with a reproducible seed.").AdjustableDuration(10000);
        image = d.Image("image").Default(AssetReference.Package("assets/particle.png")).Label("Image").Key;
        count = d.Integer("count").Default(200).Range(1, 50000).Label("Particle count").Key;
        speed = d.Number("speed").Default(80).Range(1, 2000).Unit("px/s").Key;
        direction = d.Number("direction").Default(90).Range(-360, 360).Unit("°").Key;
        sizeMin = d.Number("sizeMin").Default(.1).Range(.001, 10).Label("Minimum scale").Key;
        sizeMax = d.Number("sizeMax").Default(.4).Range(.001, 10).Label("Maximum scale").Key;
        color = d.Color("color").Default(ColorValue.White).Key;
        seed = d.Integer("seed").Default(1).Key;
    }
    public void Generate(EffectContext c, StoryboardBuilder b)
    {
        var p = c.Parameters; var random = c.Random(p.Get(seed)); var asset = p.Get(image);
        double min = p.Get(sizeMin), max = p.Get(sizeMax);
        if (min > max) throw new ArgumentException("Minimum scale must not exceed maximum scale.");
        double angle = p.Get(direction) * Math.PI / 180, distance = p.Get(speed) * c.Duration / 1000;
        for (int i = 0; i < p.Get(count); i++)
        {
            c.Cancellation.ThrowIfCancellationRequested();
            double x = random.Between(-200, 840), y = random.Between(-300, 480);
            b.Sprite(asset).Name("Snow " + (i + 1)).Move(0, c.Duration, x, y, x + Math.Cos(angle) * distance, y + Math.Sin(angle) * distance)
                .Scale(0, random.Between(min, max)).Color(0, p.Get(color)).Fade(0, Math.Min(300, c.Duration / 2), 0, 1)
                .Fade(Math.Max(c.Duration / 2, c.Duration - 300), c.Duration, 1, 0);
        }
    }
}
public sealed class BackgroundPush : IEffectModule
{
    private ParameterKey<AssetReference> image = null!;
    private ParameterKey<double> fromScale = null!, toScale = null!;
    private ParameterKey<Point2> from = null!, to = null!;
    private ParameterKey<string> ease = null!;
    public void Define(EffectDefinitionBuilder d)
    {
        d.Name("Background push").Category("Motion").AdjustableDuration(5000);
        image = d.Image("image").Default(AssetReference.Package("assets/background.png")).Key;
        fromScale = d.Number("fromScale").Default(1).Range(.001, 100).Label("Start zoom").Key;
        toScale = d.Number("toScale").Default(1.1).Range(.001, 100).Label("End zoom").Key;
        from = d.Point2("from").Default(new(320, 240)).Label("Start position").Key;
        to = d.Point2("to").Default(new(320, 240)).Label("End position").Key;
        ease = d.Choice("ease", Enum.GetNames<Ease>()).Default(nameof(Ease.SineInOut)).Key;
    }
    public void Generate(EffectContext c, StoryboardBuilder b)
    {
        var p = c.Parameters; var asset = p.Get(image); var size = c.Assets.ImageSize(asset);
        double fit = Math.Max(854d / size.Width, 480d / size.Height); var easing = Enum.Parse<Ease>(p.Get(ease));
        b.Sprite(asset).Move(0, c.Duration, p.Get(from), p.Get(to), easing)
            .Scale(0, c.Duration, fit * p.Get(fromScale), fit * p.Get(toScale), easing).Fade(0, c.Duration, 1);
    }
}
public sealed class BeatFlash : IEffectModule
{
    private ParameterKey<AssetReference> image = null!;
    private ParameterKey<ColorValue> color = null!;
    private ParameterKey<double> strength = null!, flash = null!;
    private ParameterKey<int> subdivision = null!;
    public void Define(EffectDefinitionBuilder d)
    {
        d.Name("Beat flash").Category("Rhythm").AdjustableDuration(10000).Uses(EffectInputs.Timing | EffectInputs.AbsoluteTime);
        image = d.Image("image").Default(AssetReference.Package("assets/pixel.png")).Key;
        color = d.Color("color").Default(ColorValue.White).Key;
        strength = d.Number("strength").Default(.3).Range(0, 1).Key;
        subdivision = d.Integer("subdivision").Default(1).Range(1, 16).Key;
        flash = d.Time("flash").Default(100).Range(1, 10000).Unit("ms").Label("Flash duration").Key;
    }
    public void Generate(EffectContext c, StoryboardBuilder b)
    {
        var p = c.Parameters; var asset = p.Get(image); var size = c.Assets.ImageSize(asset);
        var timing = c.Beatmap.Timing.OrderBy(t => t.Time).ToArray(); if (timing.Length == 0) timing = [new(0, 500, 4, false)];
        int count = 0;
        for (int i = 0; i < timing.Length; i++)
        {
            var t = timing[i]; double step = t.BeatLength / p.Get(subdivision);
            double start = Math.Max(c.PlacementTime, i == 0 ? double.NegativeInfinity : t.Time);
            double end = Math.Min(c.PlacementTime + c.Duration, i + 1 < timing.Length ? timing[i + 1].Time : double.PositiveInfinity);
            if (!double.IsFinite(step) || step <= 0) throw new ArgumentException("Invalid beat length.");
            for (double time = t.Time + Math.Ceiling((start - t.Time) / step) * step; time < end; time += step)
            {
                c.Cancellation.ThrowIfCancellationRequested(); if (++count > 100000) throw new ArgumentException("Too many beat flashes.");
                double local = c.LocalTime(time);
                b.Sprite(asset, Layer.Foreground).ScaleXY(local, 854d / size.Width, 480d / size.Height).Color(local, p.Get(color))
                    .Fade(local, Math.Min(c.Duration, local + p.Get(flash)), p.Get(strength), 0);
            }
        }
    }
}
