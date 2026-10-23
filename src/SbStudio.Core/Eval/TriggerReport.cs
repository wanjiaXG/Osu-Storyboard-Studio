namespace SbStudio.Core;

public enum TriggerStatus { Fires, FiresHidden, NeverFires, Invalid }
public enum TriggerNote { InvalidName, NoMatchingEvent, OutsideWindow, FiresOutsideLifetime, TriggerOnlySprite,
    CancelsNormalCommands, GroupNumberUnused, ZeroWindow, HiddenLayer, ApproximateMode }
public sealed record TriggerFire(int EventIndex, double Time, bool Alive);
public sealed record TriggerReport(SbSprite Sprite, SbTrigger Trigger, bool Shared, TriggerStatus Status,
    IReadOnlyList<TriggerFire> Fires, IReadOnlyList<(TriggerNote Note, string Detail)> Notes);
