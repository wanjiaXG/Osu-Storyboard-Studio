namespace SbStudio.Core;

public static class TriggerMatcher
{
    public static bool InWindow(SbTrigger trigger, double time) => trigger.Start <= time && time <= trigger.End;
    public static bool Accepts(TriggerCondition condition, SbTrigger trigger, SimEvent e) => e.Effective && InWindow(trigger, e.Time) && Matches(condition, e);
    public static bool Matches(TriggerCondition c, SimEvent e)
    {
        if (c.Kind == TriggerKind.HitObjectHit) return e.Kind == SimEventKind.HitObjectHit;
        if (c.Kind is TriggerKind.Passing or TriggerKind.Failing)
            return e.Kind == SimEventKind.StateChange && e.Effective && e.Failing == (c.Kind == TriggerKind.Failing);
        if (e.Kind != SimEventKind.HitSound) return false;
        bool found = c.Addition == TriggerAddition.Any, bank = c.AdditionBank == TriggerSampleBank.Any;
        string addition = c.Addition switch { TriggerAddition.Whistle => "hitwhistle", TriggerAddition.Finish => "hitfinish", TriggerAddition.Clap => "hitclap", _ => "" };
        foreach (var s in e.Samples)
        {
            if (s.Name == "hitnormal") { if (c.NormalBank != TriggerSampleBank.Any && c.NormalBank != s.Bank) return false; }
            else { if (s.Name == addition) found = true; if (s.Bank == c.AdditionBank) bank = true; }
            if (c.Suffix.Length > 0 && !string.Equals(c.Suffix, s.Suffix, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return found && bank;
    }
}
