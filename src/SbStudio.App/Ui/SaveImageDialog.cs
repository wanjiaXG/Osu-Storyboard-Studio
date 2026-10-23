using System.Runtime.InteropServices;

namespace SbStudio.App.Ui;

/// <summary>
/// The system's "save as" window for a picture, with a file name filled in and the one file type the
/// picture is saved as. The file dialogs used elsewhere cannot suggest a name, which is why this one
/// talks to Windows itself.
/// </summary>
internal static class SaveImageDialog
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public nint Owner;
        public nint Instance;
        public nint Filter;
        public nint CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public nint File;
        public int MaxFile;
        public nint FileTitle;
        public int MaxFileTitle;
        public nint InitialDir;
        public nint Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public nint DefaultExtension;
        public nint CustomData;
        public nint Hook;
        public nint TemplateName;
        public nint Reserved;
        public int ReservedFlags;
        public int FlagsEx;
    }

    private const int OverwritePrompt = 0x2, HideReadOnly = 0x4, NoChangeDir = 0x8, PathMustExist = 0x800, Explorer = 0x80000;
    private const int MaxPath = 1024;

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetSaveFileNameW(ref OpenFileName dialog);

    [DllImport("user32.dll")]
    private static extern nint GetActiveWindow();

    /// <param name="name">The file name to suggest, without extension.</param>
    /// <param name="folder">The folder to start in, if it still exists.</param>
    /// <param name="typeLabel">What the file type is called in the window.</param>
    /// <param name="extension">"png" or "jpg": the extension the name gets.</param>
    /// <returns>
    /// The chosen file, ending in .png or .jpg: a name typed with the other extension keeps it, so that
    /// what is in a file always is what its name says. Null when the window was cancelled.
    /// </returns>
    public static string? Show(string title, string name, string? folder, string typeLabel, string extension)
    {
        if (!OperatingSystem.IsWindows())
        {
            // No suggested name here, but a place to save all the same.
            var result = NativeFileDialogSharp.Dialog.FileSave(extension, Directory.Exists(folder) ? folder : null);
            return result.IsOk && !string.IsNullOrEmpty(result.Path) ? WithExtension(result.Path, extension) : null;
        }

        nint file = Marshal.AllocHGlobal(MaxPath * 2);
        string pattern = extension == "jpg" ? "*.jpg;*.jpeg" : "*." + extension;
        nint filter = Marshal.StringToHGlobalUni($"{typeLabel} (*.{extension})\0{pattern}\0");
        nint titleText = Marshal.StringToHGlobalUni(title);
        nint defaultExtension = Marshal.StringToHGlobalUni(extension);
        try
        {
            // The folder goes in with the name: on its own Windows may pass it over for the folder the
            // program used last, whatever that was for.
            if (Directory.Exists(folder)) name = Path.Combine(folder, name);
            if (name.Length >= MaxPath) name = name.Substring(0, MaxPath - 1);
            Marshal.Copy(name.ToCharArray(), 0, file, name.Length);
            Marshal.WriteInt16(file, name.Length * 2, 0);

            var dialog = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                // Whichever window of the program is in front: the main one, or the preview on its own.
                Owner = GetActiveWindow(),
                Filter = filter,
                FilterIndex = 1,
                File = file,
                MaxFile = MaxPath,
                Title = titleText,
                Flags = OverwritePrompt | HideReadOnly | NoChangeDir | PathMustExist | Explorer,
                DefaultExtension = defaultExtension,
            };
            if (!GetSaveFileNameW(ref dialog)) return null;
            string? path = Marshal.PtrToStringUni(file);
            return string.IsNullOrEmpty(path) ? null : WithExtension(path, extension);
        }
        finally
        {
            Marshal.FreeHGlobal(file);
            Marshal.FreeHGlobal(filter);
            Marshal.FreeHGlobal(titleText);
            Marshal.FreeHGlobal(defaultExtension);
        }
    }

    /// <summary>The extension decides the format; a name without one of the two gets the given one.</summary>
    private static string WithExtension(string path, string extension)
    {
        string typed = Path.GetExtension(path).ToLowerInvariant();
        return typed is ".png" or ".jpg" or ".jpeg" ? path : path + "." + extension;
    }
}
