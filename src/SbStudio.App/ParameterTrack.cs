using System.Collections;
using SbStudio.Core;

namespace SbStudio.App;

/// <summary>Independent parameter lanes backed by ordinary P commands in their original container.</summary>
internal static class ParameterTrack
{
    internal static List<SbCommand> Commands(IList nodes, char parameter) =>
        KeyTrack.Commands(nodes, CommandKind.P).Where(c => char.ToUpperInvariant(c.Param) == parameter).ToList();

    internal static (SbCommand Command, bool Added) Add(IList nodes, char parameter, int start, int end)
    {
        if (!"HVA".Contains(parameter)) throw new ArgumentOutOfRangeException(nameof(parameter));
        if (end < start) throw new ArgumentOutOfRangeException(nameof(end));
        var existing = Commands(nodes, parameter).FirstOrDefault(c => c.Start == start && c.End == end);
        if (existing != null) return (existing, false);
        var command = SbCommand.Parameter(start, end, parameter);
        nodes.Add(command);
        return (command, true);
    }
}
