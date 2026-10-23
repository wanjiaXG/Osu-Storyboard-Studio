using SbStudio.Core;
using SbStudio.Effects;

namespace SbStudio.App.Effects;

internal static class EffectDisplay
{
    internal static string Name(EffectDefinition d) => d.EffectId switch
    { "sbstudio.snow" => L.T("effects.snow"), "sbstudio.background-push" => L.T("effects.backgroundPush"), "sbstudio.beat-flash" => L.T("effects.beatFlash"), _ => d.Name };
    internal static string Parameter(EffectDefinition d, ParameterDefinition p)
    {
        if (!d.EffectId.StartsWith("sbstudio.", StringComparison.Ordinal)) return p.Label;
        return p.Id switch
        {
            "image" => L.T("effects.image"), "count" => L.T("effects.count"), "speed" => L.T("effects.speed"),
            "direction" => L.T("effects.direction"), "sizeMin" => L.T("effects.sizeMin"), "sizeMax" => L.T("effects.sizeMax"),
            "color" => L.T("effects.color"), "seed" => L.T("effects.seed"), "fromScale" => L.T("effects.fromScale"),
            "toScale" => L.T("effects.toScale"), "from" => L.T("effects.fromPosition"), "to" => L.T("effects.toPosition"),
            "ease" => L.T("effects.ease"), "strength" => L.T("effects.strength"), "subdivision" => L.T("effects.subdivision"),
            "flash" => L.T("effects.flash"), _ => p.Label
        };
    }
}
