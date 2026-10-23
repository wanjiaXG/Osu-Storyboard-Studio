using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace SbStudio.Core;

public enum TriggerKind { HitSound, Passing, Failing, HitObjectHit }
public enum TriggerSampleBank { Any, Normal, Soft, Drum }
public enum TriggerAddition { Any, Whistle, Clap, Finish }

/// <summary>Trigger condition grammar and bank mapping from lazer's StoryboardTriggerController.</summary>
public sealed record TriggerCondition(TriggerKind Kind, TriggerSampleBank NormalBank = TriggerSampleBank.Any,
    TriggerSampleBank AdditionBank = TriggerSampleBank.Any, TriggerAddition Addition = TriggerAddition.Any, string Suffix = "")
{
    private static readonly Regex hitSoundPattern = new(
        @"^HitSound(?<bank1>(All|Normal|Soft|Drum))?(?<bank2>(All|Normal|Soft|Drum))?(?<name>(Whistle|Clap|Finish))?(?<suffix>\d+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParse(string name, [NotNullWhen(true)] out TriggerCondition? condition)
    {
        if (name == null) { condition = null; return false; }
        condition = name switch
        {
            "Passing" => new(TriggerKind.Passing),
            "Failing" => new(TriggerKind.Failing),
            "HitObjectHit" => new(TriggerKind.HitObjectHit),
            _ => null,
        };
        if (condition != null) return true;
        var match = hitSoundPattern.Match(name);
        if (!match.Success) return false;
        string bank1 = match.Groups["bank1"].Value, bank2 = match.Groups["bank2"].Value;
        string addition = match.Groups["name"].Value;
        // A single bank followed by an addition name filters the addition bank, as in lazer.
        bool singleAdditionBank = bank1.Length > 0 && bank2.Length == 0 && addition.Length > 0;
        condition = new(TriggerKind.HitSound,
            Bank(singleAdditionBank ? "" : bank1), Bank(singleAdditionBank ? bank1 : bank2),
            Enum.TryParse<TriggerAddition>(addition, true, out var parsed) ? parsed : TriggerAddition.Any,
            match.Groups["suffix"].Value);
        return true;
    }

    public static bool IsValidSuffix(string suffix) => suffix.All(char.IsDigit);

    public string ToName()
    {
        if (Kind != TriggerKind.HitSound) return Kind.ToString();
        if (!IsValidSuffix(Suffix)) throw new ArgumentException("A sample suffix must contain digits only.", nameof(Suffix));
        // The shortest spelling that reads back the same: a single bank before an addition name is the
        // addition's bank, so "All" is only written where the other reading would be meant otherwise.
        bool normal = NormalBank != TriggerSampleBank.Any, additionBank = AdditionBank != TriggerSampleBank.Any;
        bool named = Addition != TriggerAddition.Any;
        string banks = (normal, additionBank) switch
        {
            (false, false) => "",
            (true, false) => BankName(NormalBank) + (named ? "All" : ""),
            (false, true) => (named ? "" : "All") + BankName(AdditionBank),
            _ => BankName(NormalBank) + BankName(AdditionBank),
        };
        return "HitSound" + banks + (Addition == TriggerAddition.Any ? "" : Addition.ToString()) + Suffix;
    }

    private static TriggerSampleBank Bank(string name) =>
        Enum.TryParse<TriggerSampleBank>(name, true, out var bank) ? bank : TriggerSampleBank.Any;

    private static string BankName(TriggerSampleBank bank) => bank == TriggerSampleBank.Any ? "All" : bank.ToString();
}
