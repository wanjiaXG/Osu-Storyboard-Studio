namespace SbStudio.Effects;

public abstract class CommandBuilder<TSelf> where TSelf : CommandBuilder<TSelf>
{
    private readonly Action<OutputCommand> append;
    internal CommandBuilder(Action<OutputCommand> append) => this.append = append;
    private TSelf Add(string kind, double start, double end, double[] from, double[] to, Ease ease = Ease.Linear, char parameter = 'A')
    {
        Guard.Time(start, end);
        if (!Enum.IsDefined(ease)) throw new ArgumentOutOfRangeException(nameof(ease));
        foreach (double v in from.Concat(to)) Guard.Finite(v);
        append(new(kind, start, end, Array.AsReadOnly(from), Array.AsReadOnly(to), ease, parameter));
        return (TSelf)this;
    }
    public TSelf Fade(double start, double end, double from, double to, Ease ease = Ease.Linear) => Add("F", start, end, [from], [to], ease);
    public TSelf Fade(double start, double end, double value) => Fade(start, end, value, value);
    public TSelf Fade(double time, double value) => Fade(time, time, value, value);
    public TSelf Move(double start, double end, double fromX, double fromY, double toX, double toY, Ease ease = Ease.Linear) => Add("M", start, end, [fromX, fromY], [toX, toY], ease);
    public TSelf Move(double start, double end, Point2 from, Point2 to, Ease ease = Ease.Linear) => Move(start, end, from.X, from.Y, to.X, to.Y, ease);
    public TSelf Move(double start, double end, double x, double y) => Move(start, end, x, y, x, y);
    public TSelf Move(double time, double x, double y) => Move(time, time, x, y, x, y);
    public TSelf MoveX(double start, double end, double from, double to, Ease ease = Ease.Linear) => Add("MX", start, end, [from], [to], ease);
    public TSelf MoveX(double start, double end, double value) => MoveX(start, end, value, value);
    public TSelf MoveX(double time, double value) => MoveX(time, time, value, value);
    public TSelf MoveY(double start, double end, double from, double to, Ease ease = Ease.Linear) => Add("MY", start, end, [from], [to], ease);
    public TSelf MoveY(double start, double end, double value) => MoveY(start, end, value, value);
    public TSelf MoveY(double time, double value) => MoveY(time, time, value, value);
    public TSelf Scale(double start, double end, double from, double to, Ease ease = Ease.Linear) => Add("S", start, end, [from], [to], ease);
    public TSelf Scale(double start, double end, double value) => Scale(start, end, value, value);
    public TSelf Scale(double time, double value) => Scale(time, time, value, value);
    public TSelf ScaleXY(double start, double end, double fromX, double fromY, double toX, double toY, Ease ease = Ease.Linear) => Add("V", start, end, [fromX, fromY], [toX, toY], ease);
    public TSelf ScaleXY(double start, double end, double x, double y) => ScaleXY(start, end, x, y, x, y);
    public TSelf ScaleXY(double time, double x, double y) => ScaleXY(time, time, x, y, x, y);
    public TSelf Rotate(double start, double end, double from, double to, Ease ease = Ease.Linear) => RotateRad(start, end, from * Math.PI / 180, to * Math.PI / 180, ease);
    public TSelf Rotate(double start, double end, double value) => Rotate(start, end, value, value);
    public TSelf Rotate(double time, double value) => Rotate(time, time, value, value);
    public TSelf RotateRad(double start, double end, double from, double to, Ease ease = Ease.Linear) => Add("R", start, end, [from], [to], ease);
    public TSelf RotateRad(double start, double end, double value) => RotateRad(start, end, value, value);
    public TSelf RotateRad(double time, double value) => RotateRad(time, time, value, value);
    public TSelf Color(double start, double end, ColorValue from, ColorValue to, Ease ease = Ease.Linear) => Add("C", start, end, [from.R, from.G, from.B], [to.R, to.G, to.B], ease);
    public TSelf Color(double start, double end, ColorValue value) => Color(start, end, value, value);
    public TSelf Color(double time, ColorValue value) => Color(time, time, value, value);
    public TSelf FlipX(double start, double end) => Add("P", start, end, [], [], parameter: 'H');
    public TSelf FlipX(double time) => FlipX(time, time);
    public TSelf FlipY(double start, double end) => Add("P", start, end, [], [], parameter: 'V');
    public TSelf FlipY(double time) => FlipY(time, time);
    public TSelf Additive(double start, double end) => Add("P", start, end, [], [], parameter: 'A');
    public TSelf Additive(double time) => Additive(time, time);
    public TSelf Z(double start, double end, double from, double to, Ease ease = Ease.Linear) => Add("Z", start, end, [from], [to], ease);
    public TSelf Z(double start, double end, double value) => Z(start, end, value, value);
    public TSelf Z(double time, double value) => Z(time, time, value, value);
    public TSelf RotX(double start, double end, double from, double to, Ease ease = Ease.Linear) => Add("RX", start, end, [from * Math.PI / 180], [to * Math.PI / 180], ease);
    public TSelf RotX(double start, double end, double value) => RotX(start, end, value, value);
    public TSelf RotX(double time, double value) => RotX(time, time, value, value);
    public TSelf RotY(double start, double end, double from, double to, Ease ease = Ease.Linear) => Add("RY", start, end, [from * Math.PI / 180], [to * Math.PI / 180], ease);
    public TSelf RotY(double start, double end, double value) => RotY(start, end, value, value);
    public TSelf RotY(double time, double value) => RotY(time, time, value, value);
}

public sealed class BlockBuilder : CommandBuilder<BlockBuilder>
{
    internal BlockBuilder(Action<OutputCommand> append) : base(c =>
    {
        if (c.Kind is "Z" or "RX" or "RY") throw new InvalidOperationException("Depth commands cannot be inside a loop or trigger.");
        append(c);
    }) { }
}
