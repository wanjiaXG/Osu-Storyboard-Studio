using System.Diagnostics;
using System.Numerics;
using System.Text.RegularExpressions;
using ImGuiNET;
using SbStudio.App.Gfx;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <param name="FrameCount">Above 1 for an animation made of numbered frame files.</param>
public sealed record AssetItem(string Path, AssetKind Kind, long Size, int FrameCount)
{
    public string Folder { get; } = System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? "";
    public string Name { get; } = System.IO.Path.GetFileName(Path);
    public string Extension { get; } = System.IO.Path.GetExtension(Path);
    /// <summary>Tells an animation from a file of the same name.</summary>
    public string Key { get; } = Path + "|" + FrameCount;
    /// <summary>The file that stands for it: the first frame of an animation.</summary>
    public string ShownFile { get; } = FrameCount > 1 ? SbSprite.FramePath(Path, 0) : Path;
}

/// <summary>
/// The project panel, laid out like a file manager: the assets folder is walked one folder at a time, with
/// an address bar and back, forward and up, and what is in a folder is shown as icons or as a list. Files
/// carry thumbnails and usage counts and are drag sources.
/// </summary>
public sealed class AssetsPanel
{
    private const string RootName = "assets";
    private const string Ellipsis = "…";
    private const int IconStep = 16;
    private const string FormatsPopup = "formats";
    /// <summary>What may stand between extensions typed in one go, the commas of a Chinese keyboard among them.</summary>
    private static readonly char[] ExtensionSeparators = { ' ', ',', ';', '\uFF0C', '\uFF1B', '\u3001' };

    private static readonly Regex NumberedFile = new(@"^(.+?)(\d+)(\.[^./\\]+)$", RegexOptions.Compiled);

    /// <summary>The asset being dragged out of the panel; the payload itself carries no data.</summary>
    public static AssetItem? Dragged;

    /// <summary>A folder below assets. The assets folder itself has the empty path.</summary>
    private sealed class FolderNode
    {
        public readonly string Path, Name, Key;
        public readonly List<FolderNode> Folders = new();
        public readonly List<AssetItem> Files = new();
        /// <summary>The files in it and below it.</summary>
        public int FileCount;
        /// <summary>The first image in it or below it, shown on its icon.</summary>
        public string? Preview;

        public FolderNode(string path)
        {
            Path = path;
            Name = path[(path.LastIndexOf('/') + 1)..];
            Key = path + "|";
        }

        public string Parent => Path.Length == Name.Length ? "" : Path[..^(Name.Length + 1)];
    }

    /// <summary>One thing in the view: a folder or a file.</summary>
    private readonly record struct Entry(FolderNode? Folder, AssetItem? Item)
    {
        public string Key => Folder?.Key ?? Item!.Key;
    }

    /// <summary>A name as it is written under an icon: on two lines at most.</summary>
    private readonly record struct NameLines(string First, float FirstWidth, string Second, float SecondWidth);

    private readonly EditorApp app;
    private readonly Dictionary<string, FolderNode> folders = new(StringComparer.OrdinalIgnoreCase);
    private FolderNode current = new("");
    private Project? builtProject;
    private int builtRevision = -1;

    /// <summary>The folders that were open, for back and forward.</summary>
    private readonly List<string> history = new() { "" };
    private int historyIndex;

    private string filter = "";
    private string? selectedKey;
    private readonly List<Entry> view = new();

    /// <summary>An asset whose folder is to be opened once the list of assets has it.</summary>
    private string? toReveal;
    private bool scrollToTop;
    /// <summary>The entry to scroll to: into the middle of the view, or to its top edge.</summary>
    private string? scrollKey;
    private bool scrollKeyToTop;
    /// <summary>
    /// The frames for which that still holds. Content that has just changed is first laid out with the scroll
    /// bar of what was there before; with its own, the number of columns may be another, and only then is
    /// the place of an entry final.
    /// </summary>
    private int scrollFrames;
    private int firstVisible;

    private readonly Dictionary<string, NameLines> nameLines = new();
    private float nameLinesWidth;
    private float nameLinesScale;

    /// <summary>The extensions of the usable formats in the settings, to be found whatever their case.</summary>
    private readonly HashSet<string> usable = new(StringComparer.OrdinalIgnoreCase);
    private string newExtension = "";

    private static readonly Localized<string> WindowTitle = new(() => L.T("panel.assets") + "###assets");

    public bool Open = true;

    public AssetsPanel(EditorApp app)
    {
        this.app = app;
        usable.UnionWith(app.Settings.AssetExtensions);
    }

    private static float Pad => Theme.Px(5);

    private void Rebuild(Project project)
    {
        builtRevision = app.AssetsRevision;
        if (builtProject != project)
        {
            // Another project starts at its assets folder.
            builtProject = project;
            history.Clear();
            history.Add("");
            historyIndex = 0;
            filter = "";
            selectedKey = null;
            scrollToTop = true;
        }

        var list = new List<AssetItem>();
        var sequences = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in app.Assets)
        {
            list.Add(new AssetItem(asset.Path, asset.Kind, asset.Size, 1));
            if (asset.Kind != AssetKind.Image) continue;
            var match = NumberedFile.Match(asset.Path);
            if (!match.Success) continue;
            // Only "name0.png" style counts, not "name00.png".
            if (match.Groups[2].Value.Length > 1 && match.Groups[2].Value[0] == '0') continue;
            string prefix = match.Groups[1].Value;
            if (prefix.EndsWith('/')) continue;
            string key = prefix + match.Groups[3].Value;
            if (!sequences.TryGetValue(key, out var set)) sequences[key] = set = new SortedSet<int>();
            set.Add(int.Parse(match.Groups[2].Value));
        }

        // name0.png, name1.png, ... can also be used as one animation "name.png".
        foreach (var (key, set) in sequences)
        {
            int frames = 0;
            while (set.Contains(frames)) frames++;
            if (frames >= 2) list.Add(new AssetItem(key, AssetKind.Image, 0, frames));
        }

        folders.Clear();
        folders[""] = new FolderNode("");
        foreach (string folder in app.AssetFolders) NodeOf(folder);
        foreach (var item in list) NodeOf(item.Folder).Files.Add(item);
        foreach (var node in folders.Values)
        {
            node.Folders.Sort((a, b) => Project.CompareNames(a.Name, b.Name));
            // Animations come before the files.
            node.Files.Sort((a, b) =>
            {
                int c = b.FrameCount.CompareTo(a.FrameCount);
                return c != 0 ? c : Project.CompareNames(a.Name, b.Name);
            });
        }
        Summarise(folders[""]);
        current = Existing(history[historyIndex]);
    }

    private FolderNode NodeOf(string path)
    {
        if (folders.TryGetValue(path, out var node)) return node;
        node = new FolderNode(path);
        folders[path] = node;
        NodeOf(node.Parent).Folders.Add(node);
        return node;
    }

    private static void Summarise(FolderNode node)
    {
        foreach (var item in node.Files)
        {
            if (item.FrameCount == 1) node.FileCount++;
            if (item.Kind == AssetKind.Image) node.Preview ??= item.ShownFile;
        }
        foreach (var folder in node.Folders)
        {
            Summarise(folder);
            node.FileCount += folder.FileCount;
            node.Preview ??= folder.Preview;
        }
    }

    /// <summary>The folder of a path, or the nearest one above it that is still there.</summary>
    private FolderNode Existing(string path)
    {
        FolderNode? node;
        while (!folders.TryGetValue(path, out node))
            path = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        return node;
    }

    // ---------------------------------------------------------------- navigation

    private void Navigate(string path)
    {
        var target = Existing(path);
        if (target == current) return;
        history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
        history.Add(target.Path);
        historyIndex++;
        Show(target);
    }

    /// <summary>Back (-1) or forward (1) through the folders that were open.</summary>
    private void Step(int direction)
    {
        int index = historyIndex + direction;
        if (index < 0 || index >= history.Count) return;
        historyIndex = index;
        Show(Existing(history[index]));
    }

    private void Show(FolderNode target)
    {
        string left = current.Path;
        current = target;
        filter = "";
        scrollToTop = true;
        scrollKey = null;
        // Going back up, the folder that was left is found again: selected and in sight.
        if (left.Length > target.Path.Length && (target.Path.Length == 0 || left.StartsWith(target.Path + "/", StringComparison.OrdinalIgnoreCase)))
        {
            int end = left.IndexOf('/', target.Path.Length == 0 ? 0 : target.Path.Length + 1);
            selectedKey = (end < 0 ? left : left[..end]) + "|";
            ScrollTo(selectedKey, false);
        }
    }

    private void ScrollTo(string key, bool toTop)
    {
        scrollKey = key;
        scrollKeyToTop = toTop;
        scrollFrames = 3;
    }

    /// <summary>Keeps the entry at the top of the view where it is while the size of the entries changes.</summary>
    private void KeepPlace()
    {
        if (firstVisible < view.Count) ScrollTo(view[firstVisible].Key, true);
    }

    /// <summary>Opens the folder of the first of these assets and selects it. Called with what has just been imported.</summary>
    public void Reveal(IReadOnlyList<string> assetPaths)
    {
        if (assetPaths.Count > 0) toReveal = assetPaths[0];
    }

    private void HandleKeys()
    {
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
        {
            // The back and forward buttons on the side of a mouse.
            if (ImGui.IsMouseClicked((ImGuiMouseButton)3)) Step(-1);
            if (ImGui.IsMouseClicked((ImGuiMouseButton)4)) Step(1);
        }
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows) && !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Backspace, false))
            Step(-1);
    }

    // ---------------------------------------------------------------- the panel

    public void Draw()
    {
        if (!Open) return;
        if (!ImGui.Begin(WindowTitle.Value, ref Open) || app.Project == null)
        {
            ImGui.End();
            return;
        }
        var project = app.Project;
        if (builtRevision != app.AssetsRevision || builtProject != project) Rebuild(project);

        if (toReveal != null)
        {
            string path = toReveal;
            toReveal = null;
            Navigate(path.Contains('/') ? path[..path.LastIndexOf('/')] : "");
            filter = "";
            var item = current.Files.Find(i => i.FrameCount == 1 && i.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                selectedKey = item.Key;
                ScrollTo(item.Key, false);
            }
        }

        DrawToolbar(project);
        HandleKeys();

        // A drag edits the storyboard every frame but only moves things; the counts catch up when the button is let go.
        bool recount = !ImGui.IsMouseDown(ImGuiMouseButton.Left);
        BuildView();

        if (scrollToTop) ImGui.SetNextWindowScroll(Vector2.Zero);
        scrollToTop = false;
        ImGui.BeginChild("asset_list", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()));
        DrawView(project, recount);
        DrawViewMenu(project);
        ImGui.EndChild();

        DrawFooter();
        ImGui.End();
    }

    private void DrawToolbar(Project project)
    {
        float inner = ImGui.GetStyle().ItemInnerSpacing.X;

        if (ImGui.Button(L.T("assets.import"))) ImportDialog();
        Theme.Tooltip(L.T("assets.import.tip"));
        ImGui.SameLine();
        if (ImGui.Button(L.T("assets.openFolder"))) OpenInExplorer(project.AssetFullPath(current.Path), false);

        if (NavButton("##back", ImGuiDir.Left, historyIndex > 0, L.T("assets.back"))) Step(-1);
        ImGui.SameLine(0, inner);
        if (NavButton("##forward", ImGuiDir.Right, historyIndex < history.Count - 1, L.T("assets.forward"))) Step(1);
        ImGui.SameLine(0, inner);
        if (NavButton("##up", ImGuiDir.Up, current.Path.Length > 0, L.T("assets.up"))) Navigate(current.Parent);
        ImGui.SameLine();
        DrawAddress();

        float button = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(-(button + inner) * 2);
        if (Theme.SearchInput("##filter", L.T("assets.filterHint"), ref filter, 128)) scrollToTop = true;
        ImGui.SameLine(0, inner);
        if (Theme.IconButton("##icons", button, DrawIconsSign, !app.Settings.AssetList)) SetListView(false);
        Theme.Tooltip(L.T("assets.viewIcons.tip"));
        ImGui.SameLine(0, inner);
        if (Theme.IconButton("##rows", button, DrawListSign, app.Settings.AssetList)) SetListView(true);
        Theme.Tooltip(L.T("assets.viewList"));
    }

    private static bool NavButton(string id, ImGuiDir direction, bool enabled, string tip)
    {
        ImGui.BeginDisabled(!enabled);
        bool clicked = ImGui.ArrowButton(id, direction);
        ImGui.EndDisabled();
        Theme.Tooltip(tip);
        return clicked;
    }

    /// <summary>The address bar: the folders from assets down to the open one, each a button that goes there.</summary>
    private void DrawAddress()
    {
        var style = ImGui.GetStyle();
        string[] names = current.Path.Length == 0 ? new[] { RootName } : (RootName + "/" + current.Path).Split('/');
        float arrow = Theme.Px(12), height = ImGui.GetFrameHeight();
        float Width(string name) => ImGui.CalcTextSize(name).X + style.FramePadding.X * 2;

        // In a panel too narrow for all of them, the first folders make way for an ellipsis.
        float total = -arrow;
        foreach (string name in names) total += arrow + Width(name);
        float available = ImGui.GetContentRegionAvail().X, more = Width(Ellipsis) + arrow;
        int first = 0;
        while (first < names.Length - 1 && total + (first > 0 ? more : 0) > available)
        {
            total -= Width(names[first]) + arrow;
            first++;
        }

        var dl = ImGui.GetWindowDrawList();
        int target = -1;
        ImGui.PushStyleColor(ImGuiCol.Button, 0u);
        for (int i = Math.Max(0, first - 1); i < names.Length; i++)
        {
            // The ellipsis goes to the last folder it stands for.
            string name = i < first ? Ellipsis : names[i];
            ImGui.PushID(i);
            Vector2 pos = ImGui.GetCursorScreenPos();
            // Drawn rather than given as the label: a folder may have "##" in its name.
            if (ImGui.Button("##crumb", new Vector2(Width(name), 0))) target = i;
            dl.AddText(pos + style.FramePadding, ImGui.GetColorU32(ImGuiCol.Text), name);
            ImGui.PopID();
            if (i == names.Length - 1) break;

            ImGui.SameLine(0, 0);
            pos = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(arrow, height));
            Theme.FoldArrow(dl, pos + new Vector2(arrow, height) / 2, Theme.Px(7), false, Theme.TextDim);
            ImGui.SameLine(0, 0);
        }
        ImGui.PopStyleColor();
        if (target >= 0) Navigate(string.Join('/', names, 1, target));
    }

    private void DrawFooter()
    {
        var settings = app.Settings;
        string label = L.T("assets.usableOnly");
        float inner = ImGui.GetStyle().ItemInnerSpacing.X, button = ImGui.GetFrameHeight();
        float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        float width = button + inner + ImGui.CalcTextSize(label).X + inner + button;
        ImGui.AlignTextToFramePadding();
        Theme.TextDimmed(L.T("assets.items", view.Count));
        ImGui.SameLine();
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), right - width));

        bool usableOnly = settings.AssetUsableOnly;
        if (ImGui.Checkbox(label, ref usableOnly))
        {
            settings.AssetUsableOnly = usableOnly;
            settings.Save();
            KeepPlace();
        }
        Theme.Tooltip(L.T("assets.usableOnly.tip"));
        ImGui.SameLine(0, inner);
        if (Theme.IconButton("##formats", button, DrawGearSign))
        {
            newExtension = "";
            ImGui.OpenPopup(FormatsPopup);
        }
        Theme.Tooltip(L.T("assets.formats.tip"));
        DrawFormats();
    }

    /// <summary>The extensions that make a file a usable format, and what it takes to change them.</summary>
    private void DrawFormats()
    {
        if (!ImGui.BeginPopup(FormatsPopup)) return;
        var settings = app.Settings;
        float inner = ImGui.GetStyle().ItemInnerSpacing.X, padding = ImGui.GetStyle().FramePadding.X * 2;
        float width = Theme.Px(300);

        ImGui.TextUnformatted(L.T("assets.formats.title"));
        // One button each, as many on a row as there is room for.
        string? removed = null;
        float used = 0;
        foreach (string ext in settings.AssetExtensions)
        {
            string chip = ext + "  ×";
            float chipWidth = ImGui.CalcTextSize(chip).X + padding;
            if (used > 0 && used + chipWidth <= width) ImGui.SameLine(0, inner);
            else used = 0;
            if (ImGui.Button(chip)) removed = ext;
            used += chipWidth + inner;
        }
        if (settings.AssetExtensions.Count == 0) TooltipNote(L.T("common.none"));

        // The popup is as wide as what is in it, so the note is told where to wrap.
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextUnformatted(L.T("assets.formats.note"));
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        ImGui.SetNextItemWidth(Theme.Px(130));
        bool add = ImGui.InputTextWithHint("##extension", L.T("assets.formats.hint"), ref newExtension, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        // Enter leaves the field; this way the next extension can be typed right away.
        if (add) ImGui.SetKeyboardFocusHere(-1);
        ImGui.SameLine(0, inner);
        add |= ImGui.Button(L.T("common.add"));
        string reset = L.T("assets.formats.reset");
        ImGui.SameLine();
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetStyle().WindowPadding.X + width - ImGui.CalcTextSize(reset).X - padding));
        if (ImGui.Button(reset))
        {
            settings.AssetExtensions = new List<string>(AppSettings.DefaultAssetExtensions);
            FormatsChanged();
        }

        // What was not understood stays in the field to be put right.
        if (add && AddExtensions(newExtension)) newExtension = "";
        if (removed != null)
        {
            settings.AssetExtensions.Remove(removed);
            FormatsChanged();
        }
        ImGui.EndPopup();
    }

    /// <summary>Adds the extensions in a text, one or several of them, with or without their dots.</summary>
    /// <returns>False when some of the text is not an extension.</returns>
    private bool AddExtensions(string text)
    {
        var extensions = app.Settings.AssetExtensions;
        bool understood = true;
        foreach (string part in text.Split(ExtensionSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            string? extension = AppSettings.NormalizeExtension(part);
            if (extension == null) understood = false;
            else if (!extensions.Contains(extension)) extensions.Add(extension);
        }
        FormatsChanged();
        return understood;
    }

    private void FormatsChanged()
    {
        app.Settings.Save();
        usable.Clear();
        usable.UnionWith(app.Settings.AssetExtensions);
    }

    /// <summary>The menu of the empty space around the entries.</summary>
    private void DrawViewMenu(Project project)
    {
        if (!ImGui.BeginPopupContextWindow("view", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems)) return;
        if (ImGui.MenuItem(L.T("assets.import"))) ImportDialog();
        if (ImGui.MenuItem(L.T("assets.openFolder"))) OpenInExplorer(project.AssetFullPath(current.Path), false);
        ImGui.Separator();
        if (ImGui.MenuItem(L.T("assets.viewIcons"), null, !app.Settings.AssetList)) SetListView(false);
        if (ImGui.MenuItem(L.T("assets.viewList"), null, app.Settings.AssetList)) SetListView(true);
        ImGui.EndPopup();
    }

    private void SetListView(bool list)
    {
        if (app.Settings.AssetList == list) return;
        app.Settings.AssetList = list;
        app.Settings.Save();
        KeepPlace();
    }

    private void SetIconSize(int size)
    {
        size = Math.Clamp(size, AppSettings.MinAssetIconSize, AppSettings.MaxAssetIconSize);
        if (app.Settings.AssetIconSize == size) return;
        app.Settings.AssetIconSize = size;
        app.Settings.Save();
        KeepPlace();
    }

    // ---------------------------------------------------------------- the entries

    /// <summary>
    /// Fills <see cref="view"/>: what is in the open folder, or, while searching, whatever matches in it and
    /// below it.
    /// </summary>
    private void BuildView()
    {
        view.Clear();
        if (filter.Length > 0)
        {
            int foundFolders = 0;
            Search(current, ref foundFolders);
            return;
        }
        foreach (var folder in current.Folders) view.Add(new Entry(folder, null));
        foreach (var item in current.Files)
            if (Listed(item)) view.Add(new Entry(null, item));
    }

    private void Search(FolderNode node, ref int foundFolders)
    {
        foreach (var item in node.Files)
            if (Listed(item) && Matches(item.Name)) view.Add(new Entry(null, item));
        foreach (var folder in node.Folders)
        {
            // Folders stay ahead of the files, as they are in a folder.
            if (Matches(folder.Name)) view.Insert(foundFolders++, new Entry(folder, null));
            Search(folder, ref foundFolders);
        }
    }

    /// <summary>Whether a file is listed: every file is, unless only the usable formats are asked for.</summary>
    private bool Listed(AssetItem item) => !app.Settings.AssetUsableOnly || usable.Contains(item.Extension);

    private bool Matches(string name) => name.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private void DrawView(Project project, bool recount)
    {
        if (view.Count == 0)
        {
            if (filter.Length > 0) Theme.TextDimmed(L.T("assets.noMatch"));
            // Files that are there but not listed are not the same as no files.
            else if (current.Files.Count > 0) Theme.TextDimmed(L.T("assets.noUsable"));
            else if (current.Path.Length > 0) Theme.TextDimmed(L.T("assets.emptyFolder"));
            else Theme.TextDimmed(L.T("assets.empty"));
            scrollKey = null;
            firstVisible = 0;
            return;
        }

        bool list = app.Settings.AssetList;
        float width = ImGui.GetContentRegionAvail().X;
        float icon = Theme.Px(list ? 22 : app.Settings.AssetIconSize), gap = list ? 0 : Theme.Px(2);
        int columns = 1;
        var cell = new Vector2(width, Theme.Px(26));
        if (!list)
        {
            float narrowest = icon + Pad * 2;
            columns = Math.Max(1, (int)((width + gap) / (narrowest + gap)));
            // The icons are spread over the whole width, which leaves their names more room.
            cell = new Vector2(MathF.Max(narrowest, MathF.Floor((width + gap) / columns) - gap),
                Pad + icon + Theme.Px(3) + ImGui.GetTextLineHeight() * 2 + Pad);
        }
        float step = cell.Y + gap;
        int rows = (view.Count + columns - 1) / columns;

        if (scrollKey != null)
        {
            string key = scrollKey;
            int index = view.FindIndex(e => e.Key == key);
            if (index >= 0)
            {
                float y = index / columns * step;
                ImGui.SetScrollY(scrollKeyToTop ? y : MathF.Max(0, y - (ImGui.GetWindowHeight() - step) / 2));
            }
            if (index < 0 || --scrollFrames <= 0) scrollKey = null;
        }

        // Only the rows in sight are laid out.
        Vector2 origin = ImGui.GetCursorScreenPos();
        float top = ImGui.GetScrollY();
        int firstRow = Math.Clamp((int)(top / step), 0, rows - 1);
        int lastRow = Math.Min(rows - 1, (int)((top + ImGui.GetWindowHeight()) / step));
        firstVisible = firstRow * columns;
        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int column = 0; column < columns && row * columns + column < view.Count; column++)
            {
                ImGui.SetCursorScreenPos(origin + new Vector2(column * (cell.X + gap), row * step));
                DrawEntry(project, view[row * columns + column], cell, icon, list, recount);
            }
        }
        // The rest only takes up its room.
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(0, rows * step - gap));

        var io = ImGui.GetIO();
        if (!list && io.KeyCtrl && io.MouseWheel != 0 && ImGui.IsWindowHovered())
            SetIconSize(app.Settings.AssetIconSize + (io.MouseWheel > 0 ? IconStep : -IconStep));
    }

    private void DrawEntry(Project project, Entry entry, Vector2 size, float icon, bool list, bool recount)
    {
        ImGui.PushID(entry.Key);
        Vector2 pos = ImGui.GetCursorScreenPos();
        bool opened = false;
        // A selectable grows by half the item spacing on every side; without spacing its highlight is the cell.
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        if (ImGui.Selectable("##entry", selectedKey == entry.Key, ImGuiSelectableFlags.AllowDoubleClick, size))
        {
            selectedKey = entry.Key;
            // Going down several folders in quick double clicks, ImGui counts the later ones as a third and a
            // fourth click. Every second click opens, as it does in Explorer; these arrive when the button is let go.
            int clicks = ImGui.GetIO().MouseClickedLastCount[0];
            opened = ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) ||
                     (clicks > 2 && clicks % 2 == 0 && ImGui.IsMouseReleased(ImGuiMouseButton.Left));
        }
        ImGui.PopStyleVar();
        bool hovered = ImGui.IsItemHovered();

        Vector2 iconMin = list
            ? pos + new Vector2(Theme.Px(2), MathF.Round((size.Y - icon) / 2))
            : pos + new Vector2(MathF.Round((size.X - icon) / 2), Pad);
        if (entry.Folder != null) DrawFolder(project, entry.Folder, pos, size, iconMin, icon, list, opened, hovered);
        else DrawFile(project, entry.Item!, pos, size, iconMin, icon, list, opened, hovered, recount);
        ImGui.PopID();
    }

    private void DrawFolder(Project project, FolderNode folder, Vector2 pos, Vector2 size, Vector2 iconMin, float icon, bool list, bool opened, bool hovered)
    {
        if (opened) Navigate(folder.Path);

        if (ImGui.BeginPopupContextItem("ctx"))
        {
            selectedKey = folder.Key;
            if (ImGui.MenuItem(L.T("assets.open"))) Navigate(folder.Path);
            ImGui.Separator();
            if (ImGui.MenuItem(L.T("assets.copyPath"))) ImGui.SetClipboardText(folder.Path);
            if (ImGui.MenuItem(L.T("assets.reveal"))) OpenInExplorer(project.AssetFullPath(folder.Path), true);
            ImGui.EndPopup();
        }

        var dl = ImGui.GetWindowDrawList();
        // The icon of a list row is too small to show what is inside.
        DrawFolderSign(dl, iconMin, icon, list || folder.Preview == null ? null : app.Textures.Get(folder.Preview));
        uint color = ImGui.GetColorU32(ImGuiCol.Text);
        string displayName=folder.Path=="generated"?L.T("plugin.generatedAssets"):folder.Name;
        if (list) DrawRowName(dl, displayName, "", pos, size, icon, color);
        else DrawName(dl, displayName, pos, size, icon, color);

        if (hovered && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal) && !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(folder.Path);
            TooltipNote(L.T("assets.folderFiles", folder.FileCount));
            ImGui.EndTooltip();
        }
    }

    private void DrawFile(Project project, AssetItem item, Vector2 pos, Vector2 size, Vector2 iconMin, float icon, bool list, bool opened, bool hovered, bool recount)
    {
        if (opened) AddToStoryboard(item);

        if (ImGui.BeginDragDropSource())
        {
            Dragged = item;
            ImGui.SetDragDropPayload(ViewerPanel.AssetPayload, IntPtr.Zero, 0);
            ImGui.TextUnformatted(item.FrameCount > 1 ? L.T("assets.dragAnimation", item.Name, item.FrameCount) : item.Name);
            ImGui.EndDragDropSource();
        }

        if (ImGui.BeginPopupContextItem("ctx"))
        {
            selectedKey = item.Key;
            if (item.Kind == AssetKind.Image && ImGui.MenuItem(item.FrameCount > 1 ? L.T("assets.addAnimation") : L.T("assets.addSprite"))) AddToStoryboard(item);
            if (item.Kind == AssetKind.Audio && ImGui.MenuItem(L.T("assets.addSample"))) AddToStoryboard(item);
            if (item.Kind == AssetKind.Audio && ImGui.MenuItem(L.T("assets.setAudio")))
            {
                project.Settings.Audio = item.Path;
                app.MarkSettingsChanged();
                app.ReloadMusic();
            }
            if (item.Kind == AssetKind.Image && item.FrameCount == 1 && ImGui.MenuItem(L.T("assets.setBackground")))
            {
                project.Settings.Background = item.Path;
                app.MarkSettingsChanged();
            }
            if (item.Kind == AssetKind.Video && ImGui.MenuItem(L.T("assets.addVideo"))) AddToStoryboard(item);
            ImGui.Separator();
            if (ImGui.MenuItem(L.T("assets.copyPath"))) ImGui.SetClipboardText(item.Path);
            if (item.FrameCount == 1 && ImGui.MenuItem(L.T("assets.reveal"))) OpenInExplorer(project.AssetFullPath(item.Path), true);
            ImGui.EndPopup();
        }

        int uses = app.UsageOf(item.ShownFile, recount);
        var dl = ImGui.GetWindowDrawList();
        DrawThumbnail(dl, item, iconMin, icon);

        string tag = "";
        if (item.FrameCount > 1) tag = L.T("assets.tagAnimation", item.FrameCount);
        else if (item.Path.Equals(project.Settings.Audio, StringComparison.OrdinalIgnoreCase)) tag = L.T("assets.tagAudio");
        else if (item.Path.Equals(project.Settings.Background, StringComparison.OrdinalIgnoreCase)) tag = L.T("assets.tagBackground");
        string count = uses > 0 ? "×" + uses : "";
        uint color = ImGui.GetColorU32(uses > 0 || project.IsMainFile(item.Path) ? ImGuiCol.Text : ImGuiCol.TextDisabled);

        if (list)
        {
            DrawRowName(dl, tag.Length > 0 ? item.Name + "  [" + tag + "]" : item.Name, count, pos, size, icon, color);
        }
        else
        {
            DrawName(dl, item.Name, pos, size, icon, color);
            Vector2 iconMax = iconMin + new Vector2(icon, icon);
            if (count.Length > 0)
            {
                // The usage count sits in the top right corner of the picture.
                Vector2 text = ImGui.CalcTextSize(count);
                Vector2 plateMin = new(iconMax.X - text.X - Theme.Px(6), iconMin.Y);
                dl.AddRectFilled(plateMin, new Vector2(iconMax.X, iconMin.Y + text.Y + Theme.Px(2)), PlateColor, 2);
                dl.AddText(plateMin + new Vector2(Theme.Px(3), Theme.Px(1)), Theme.Accent, count);
            }
            if (tag.Length > 0)
            {
                // What the file is, along the bottom edge of the picture.
                Vector2 text = ImGui.CalcTextSize(tag);
                Vector2 plateMin = new(iconMin.X, iconMax.Y - text.Y - Theme.Px(2));
                dl.AddRectFilled(plateMin, iconMax, PlateColor, 2);
                dl.PushClipRect(plateMin, iconMax, true);
                dl.AddText(new Vector2(iconMin.X + MathF.Max(Theme.Px(2), MathF.Round((icon - text.X) / 2)), plateMin.Y + Theme.Px(1)), ImGui.GetColorU32(ImGuiCol.Text), tag);
                dl.PopClipRect();
            }
        }

        if (hovered && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal) && !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.BeginTooltip();
            if (item.Kind == AssetKind.Image)
            {
                var texture = app.Textures.Get(item.ShownFile);
                if (texture != null)
                {
                    float fit = MathF.Min(1, MathF.Min(Theme.Px(260) / texture.Width, Theme.Px(200) / texture.Height));
                    ImGui.Image(texture.ImGuiId, new Vector2(texture.Width, texture.Height) * fit);
                    ImGui.TextUnformatted(L.T("assets.pixels", texture.Width, texture.Height));
                }
            }
            ImGui.TextUnformatted(item.Path);
            if (item.FrameCount == 1) TooltipNote(Theme.FormatSize(item.Size));
            TooltipNote(uses > 0 ? L.T("assets.usedBy", uses) : L.T("assets.unused"));
            TooltipNote(L.T("assets.addTip"));
            ImGui.EndTooltip();
        }
    }

    /// <summary>
    /// A dimmed line of a tooltip. It is not wrapped: a tooltip is as wide as its longest line, and wrapped
    /// text would be broken to the width of the lines above it, however short they are.
    /// </summary>
    private static void TooltipNote(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>Behind the texts that lie on a picture, so that they can be read on any of them.</summary>
    private static readonly uint PlateColor = Theme.Rgba(0, 0, 0, 190);

    private void DrawThumbnail(ImDrawListPtr dl, AssetItem item, Vector2 min, float size)
    {
        dl.AddRectFilled(min, min + new Vector2(size, size), Theme.PanelDark, 2);
        Vector2 centre = min + new Vector2(size, size) / 2;
        if (item.Kind == AssetKind.Image)
        {
            if (app.Textures.Get(item.ShownFile) is { } texture) DrawFitted(dl, texture, min, min + new Vector2(size, size));
        }
        else if (item.Kind == AssetKind.Audio)
        {
            // A few bars stand in for a waveform.
            for (int i = 0; i < 5; i++)
            {
                float h = size * (0.25f + 0.5f * MathF.Abs(MathF.Sin(i * 1.7f + 0.6f)));
                float x = min.X + size * (0.18f + i * 0.16f);
                dl.AddLine(new Vector2(x, min.Y + (size - h) / 2), new Vector2(x, min.Y + (size + h) / 2), Theme.Rgba(235, 200, 90), MathF.Max(2, size * 0.06f));
            }
        }
        else if (item.Kind == AssetKind.Video)
        {
            // The triangle of a play button.
            float r = size * 0.28f;
            dl.AddTriangleFilled(centre + new Vector2(-r * 0.8f, -r), centre + new Vector2(-r * 0.8f, r), centre + new Vector2(r, 0), Theme.Rgba(120, 200, 235));
        }
        else
        {
            dl.AddText(centre - ImGui.CalcTextSize("?") / 2, Theme.TextDim, "?");
        }
    }

    /// <summary>Draws a whole image as large as a box allows, in the middle of it.</summary>
    private static void DrawFitted(ImDrawListPtr dl, Texture texture, Vector2 min, Vector2 max)
    {
        Vector2 box = max - min;
        float fit = MathF.Min(box.X / texture.Width, box.Y / texture.Height);
        Vector2 half = new Vector2(texture.Width, texture.Height) * fit / 2;
        Vector2 centre = (min + max) / 2;
        dl.AddImage(texture.ImGuiId, centre - half, centre + half);
    }

    /// <summary>A folder as file managers draw it. Given an image from inside, it stands open with that image in it.</summary>
    private static void DrawFolderSign(ImDrawListPtr dl, Vector2 min, float size, Texture? inside)
    {
        float w = MathF.Round(size * 0.88f), h = MathF.Round(size * 0.7f);
        Vector2 o = new(MathF.Round(min.X + (size - w) / 2), MathF.Round(min.Y + (size - h) / 2 + size * 0.03f));
        float tab = MathF.Round(h * 0.15f), round = MathF.Max(1, MathF.Round(size * 0.05f));
        uint back = Theme.Rgba(206, 152, 44), front = Theme.Rgba(242, 198, 86);

        dl.AddRectFilled(o, o + new Vector2(MathF.Round(w * 0.42f), tab + round), back, round, ImDrawFlags.RoundCornersTop);
        dl.AddRectFilled(o + new Vector2(0, tab), o + new Vector2(w, h), back, round);
        // Closed, the front covers nearly all of it; open, it is folded down to show the sheet behind it.
        float frontTop = MathF.Round(h * (inside != null ? 0.68f : 0.3f));
        if (inside != null)
        {
            float margin = MathF.Round(w * 0.1f);
            Vector2 sheetMin = o + new Vector2(margin, MathF.Round(tab * 0.4f)), sheetMax = o + new Vector2(w - margin, h - round);
            dl.AddRectFilled(sheetMin, sheetMax, Theme.PanelDark, 1);
            DrawFitted(dl, inside, sheetMin + new Vector2(1, 1), new Vector2(sheetMax.X - 1, o.Y + frontTop - 1));
        }
        dl.AddRectFilled(o + new Vector2(0, frontTop), o + new Vector2(w, h), front, round);
    }

    /// <summary>Four squares: the sign of the icon view.</summary>
    private static void DrawIconsSign(ImDrawListPtr dl, Vector2 c, float s, uint color)
    {
        c = new Vector2(MathF.Round(c.X), MathF.Round(c.Y));
        float outer = MathF.Round(s * 0.26f), inner = MathF.Max(1, MathF.Round(s * 0.05f));
        for (int i = 0; i < 4; i++)
        {
            var side = new Vector2(i % 2 == 0 ? -1 : 1, i < 2 ? -1 : 1);
            Vector2 a = c + side * inner, b = c + side * outer;
            dl.AddRectFilled(Vector2.Min(a, b), Vector2.Max(a, b), color);
        }
    }

    /// <summary>A gear wheel: the sign of options.</summary>
    private static void DrawGearSign(ImDrawListPtr dl, Vector2 c, float s, uint color)
    {
        float r = s * 0.2f;
        for (int i = 0; i < 8; i++)
        {
            var spoke = new Vector2(MathF.Cos(i * MathF.PI / 4), MathF.Sin(i * MathF.PI / 4));
            dl.AddLine(c + spoke * r, c + spoke * r * 1.55f, color, MathF.Max(2, s * 0.11f));
        }
        dl.AddCircle(c, r * 0.85f, color, 20, r * 0.6f);
    }

    /// <summary>Three lines, each behind a dot: the sign of the list view.</summary>
    private static void DrawListSign(ImDrawListPtr dl, Vector2 c, float s, uint color)
    {
        c = new Vector2(MathF.Round(c.X), MathF.Round(c.Y));
        float half = MathF.Round(s * 0.26f), thick = MathF.Max(1, MathF.Round(s * 0.04f)), dot = thick * 2;
        for (int i = -1; i <= 1; i++)
        {
            float y = c.Y + i * MathF.Round(s * 0.2f);
            dl.AddRectFilled(new Vector2(c.X - half, y - thick), new Vector2(c.X - half + dot, y + thick), color);
            dl.AddRectFilled(new Vector2(c.X - half + dot * 2, y - thick), new Vector2(c.X + half, y + thick), color);
        }
    }

    /// <summary>The name of a list row, cut off where the text on the right begins.</summary>
    private static void DrawRowName(ImDrawListPtr dl, string name, string right, Vector2 pos, Vector2 size, float icon, uint color)
    {
        float textY = pos.Y + (size.Y - ImGui.GetFontSize()) / 2;
        float rightWidth = right.Length > 0 ? ImGui.CalcTextSize(right).X + Theme.Px(8) : 0;
        dl.PushClipRect(pos, pos + new Vector2(size.X - rightWidth, size.Y), true);
        dl.AddText(new Vector2(pos.X + icon + Theme.Px(8), textY), color, name);
        dl.PopClipRect();
        if (right.Length > 0)
            dl.AddText(new Vector2(pos.X + size.X - rightWidth + Theme.Px(4), textY), Theme.Accent, right);
    }

    /// <summary>The name under an icon, centred.</summary>
    private void DrawName(ImDrawListPtr dl, string name, Vector2 pos, Vector2 size, float icon, uint color)
    {
        var lines = Wrap(name, size.X - Theme.Px(4));
        float y = pos.Y + Pad + icon + Theme.Px(3);
        dl.AddText(new Vector2(MathF.Round(pos.X + (size.X - lines.FirstWidth) / 2), y), color, lines.First);
        if (lines.Second.Length > 0)
            dl.AddText(new Vector2(MathF.Round(pos.X + (size.X - lines.SecondWidth) / 2), y + ImGui.GetTextLineHeight()), color, lines.Second);
    }

    /// <summary>Breaks a name into the two lines there are under an icon. What does not fit ends in an ellipsis.</summary>
    private NameLines Wrap(string name, float width)
    {
        if (width != nameLinesWidth || nameLinesScale != Theme.Scale || nameLines.Count > 4096)
        {
            nameLines.Clear();
            nameLinesWidth = width;
            nameLinesScale = Theme.Scale;
        }
        if (nameLines.TryGetValue(name, out var lines)) return lines;

        string first = name, second = "";
        int cut = Fit(name, 0, width);
        if (cut < name.Length)
        {
            // The extension stays in one piece where the break would fall inside it, and no line is a single letter.
            int dot = name.LastIndexOf('.');
            if (dot > 0 && dot < cut && name.Length - dot <= 6) cut = dot;
            else if (name.Length - cut == 1 && cut > 2) cut--;
            first = name[..cut];
            second = name[cut..];
            if (Fit(name, cut, width) < name.Length)
                second = name[cut..Fit(name, cut, width - ImGui.CalcTextSize(Ellipsis).X)] + Ellipsis;
        }
        lines = new NameLines(first, ImGui.CalcTextSize(first).X, second, ImGui.CalcTextSize(second).X);
        nameLines[name] = lines;
        return lines;
    }

    /// <summary>How far a text gets within a width, counting from <paramref name="start"/>: the index it ends at.</summary>
    private static int Fit(string text, int start, float width)
    {
        var font = ImGui.GetFont();
        float scale = ImGui.GetFontSize() / font.FontSize, x = 0;
        int end = start;
        for (; end < text.Length; end++)
        {
            x += font.GetCharAdvance(text[end]) * scale;
            if (x > width) break;
        }
        // Never between the two halves of one character, and never without getting anywhere.
        if (end > start && end < text.Length && char.IsLowSurrogate(text[end])) end--;
        return Math.Max(end, Math.Min(start + 1, text.Length));
    }

    // ---------------------------------------------------------------- actions

    private void AddToStoryboard(AssetItem item)
    {
        if (item.Kind == AssetKind.Audio) app.AddSample(item.Path);
        else if (item.Kind == AssetKind.Video) app.AddVideo(item.Path);
        else if (item.Kind == AssetKind.Image) app.AddSprite(item.Path, 320, 240, item.FrameCount);
    }

    private void ImportDialog()
    {
        string start = Directory.Exists(app.Settings.LastImportFolder) ? app.Settings.LastImportFolder : "";
        var result = NativeFileDialogSharp.Dialog.FileOpenMultiple("png,jpg,jpeg,mp3,ogg,wav,avi,flv,mpg,wmv,m4v,mp4", start.Length > 0 ? start : null);
        if (!result.IsOk || result.Paths == null || result.Paths.Count == 0) return;
        app.Settings.LastImportFolder = Path.GetDirectoryName(result.Paths[0]) ?? "";
        app.Settings.Save();
        // Into the folder that is open. From the assets folder itself, each file goes where its kind belongs.
        Reveal(app.ImportFiles(result.Paths, current.Path.Length > 0 ? current.Path : null));
    }

    public static void OpenInExplorer(string path, bool select)
    {
        try
        {
            string args = select ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppSettings.Log(L.T("log.explorerFailed", ex.Message));
        }
    }
}
