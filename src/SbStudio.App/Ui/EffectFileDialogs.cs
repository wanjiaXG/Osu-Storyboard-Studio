using NativeFileDialogSharp;

namespace SbStudio.App.Ui;

/// <summary>Effect pickers share the same native dialogs and a deterministic scripted-input boundary.</summary>
internal static class EffectFileDialogs
{
    internal sealed record Selection(bool IsOk, string Path);
    private static readonly Queue<(string Kind, string Path)> scripted = new();
    internal static void Enqueue(string kind, string path)
    {
        if (!AppSettings.ReadOnly) throw new InvalidOperationException("Scripted picker input requires an input-script session.");
        scripted.Enqueue((kind, path));
    }
    internal static Selection Open(string? filter) => Pick("open", () => Dialog.FileOpen(filter, null));
    internal static Selection Save(string? filter) => Pick("save", () => Dialog.FileSave(filter, null));
    internal static Selection Folder() => Pick("folder", () => Dialog.FolderPicker(null));
    private static Selection Pick(string kind, Func<DialogResult> native)
    {
        if (AppSettings.ReadOnly && scripted.TryPeek(out var item))
        {
            if (item.Kind != kind) throw new InvalidOperationException("Unexpected effect picker: " + kind);
            scripted.Dequeue();
            return new(item.Path.Length > 0, item.Path);
        }
        var result = native();
        return new(result.IsOk, result.Path ?? "");
    }
}
