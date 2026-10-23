using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>Fonts, colours and small drawing helpers shared by the panels.</summary>
public static unsafe class Theme
{
    public static ImFontPtr MainFont;
    public static ImFontPtr MonoFont;
    /// <summary>Large bold digits, for the numbers on hit circles; see <see cref="DigitGlyphs"/>.</summary>
    private static ImFontPtr digitFont;
    public static float Scale = 1f;
    public static bool PersistLayout = true;
    private static readonly List<nint> fontRanges = new();

    public static readonly uint Accent = Rgba(64, 150, 255);
    public static readonly uint AccentDim = Rgba(64, 150, 255, 90);
    // The same blue as the player's progress bar, so it is not taken for a red line.
    public static readonly uint Playhead = Accent;
    /// <summary>Everything that belongs to object groups: rows, tags, pivot.</summary>
    public static readonly uint GroupColor = Rgba(255, 160, 70);
    public static readonly uint TextDim = Rgba(150, 150, 155);
    public static readonly uint TextError = Rgba(255, 110, 110);
    public static readonly uint TextWarning = Rgba(255, 200, 90);
    public static readonly uint PanelDark = Rgba(24, 24, 26);
    public static readonly uint RowAlt = Rgba(255, 255, 255, 8);
    public static readonly uint GridLine = Rgba(255, 255, 255, 18);
    public static readonly uint GridLineStrong = Rgba(255, 255, 255, 40);

    public static readonly uint[] LayerColors =
    {
        Rgba(110, 130, 160), // Background
        Rgba(200, 95, 95),   // Fail
        Rgba(95, 180, 110),  // Pass
        Rgba(80, 140, 220),  // Foreground
        Rgba(170, 110, 220), // Overlay
    };

    private static readonly Localized<string[]> LayerNameList = new(() => new[]
    {
        L.T("layer.background"), L.T("layer.fail"), L.T("layer.pass"), L.T("layer.foreground"), L.T("layer.overlay"),
    });

    public static string[] LayerNames => LayerNameList.Value;

    public static string SectionLabel(TimelineSection section) => section switch
    {
        TimelineSection.Samples => L.T("layer.samples"),
        TimelineSection.Groups => L.T("layer.groups"),
        TimelineSection.Videos => L.T("layer.video"),
        TimelineSection.HitSounds => L.T("hitsound.group"),
        TimelineSection.Timing => L.T("timing.group"),
        TimelineSection.Waveform => L.T("timeline.waveformLayer"),
        TimelineSection.HitObjects => L.T("timeline.hitObjectLayer"),
        TimelineSection.BeatmapBackground => L.T("props.beatmapBg"),
        _ => LayerNames[(int)section],
    };

    public static uint Rgba(int r, int g, int b, int a = 255) => (uint)(a << 24 | b << 16 | g << 8 | r);

    public static uint WithAlpha(uint color, float alpha) =>
        (color & 0x00FFFFFF) | ((uint)(Math.Clamp(alpha, 0, 1) * (color >> 24)) << 24);

    public static uint KindColor(CommandKind kind) => kind switch
    {
        CommandKind.F => Rgba(210, 210, 215),
        CommandKind.M or CommandKind.MX or CommandKind.MY => Rgba(110, 200, 130),
        CommandKind.S or CommandKind.V => Rgba(235, 200, 90),
        CommandKind.R => Rgba(240, 150, 80),
        CommandKind.C => Rgba(230, 120, 190),
        _ => Rgba(130, 190, 240),
    };

    /// <summary>
    /// Tick colour of a beat snap divisor: the colours of the osu! editor's timeline, slightly see-through
    /// as they are there.
    /// </summary>
    public static uint SnapColor(int divisor) => divisor switch
    {
        1 => Rgba(255, 255, 255, 220),
        2 => Rgba(255, 0, 0, 225),
        3 => Rgba(200, 0, 200, 225),
        4 => Rgba(50, 128, 255, 225),
        6 => Rgba(144, 64, 144, 225),
        // Fifths, sevenths and ninths share the yellow of eighths.
        5 or 7 or 8 or 9 => Rgba(255, 255, 0, 225),
        // Twelfths, sixteenths and anything else.
        _ => Rgba(160, 160, 160, 225),
    };

    /// <summary>Ruler tick height as a fraction of the tick area: coarser divisors are taller.</summary>
    public static float SnapTickHeight(int divisor) => divisor switch
    {
        1 => 0.8f,
        2 => 0.64f,
        3 or 4 => 0.5f,
        6 or 8 => 0.38f,
        _ => 0.28f,
    };

    public static void DrawCameraRig(ImDrawListPtr dl,Vector2 center,float size,uint color)
    {
        dl.AddRect(center-new Vector2(size/2,size/3),center+new Vector2(size/2,size/3),color,2);
        dl.AddCircle(center,size/5,color,12,1.5f);
        dl.AddLine(center+new Vector2(size/2,0),center+new Vector2(size*0.8f,-size/3),color);
        dl.AddLine(center+new Vector2(size/2,0),center+new Vector2(size*0.8f,size/3),color);
    }
    public static string KindLabel(CommandKind kind) => kind switch
    {
        CommandKind.F => L.T("kind.F"),
        CommandKind.M => L.T("kind.M"),
        CommandKind.MX => L.T("kind.MX"),
        CommandKind.MY => L.T("kind.MY"),
        CommandKind.S => L.T("kind.S"),
        CommandKind.V => L.T("kind.V"),
        CommandKind.R => L.T("kind.R"),
        CommandKind.C => L.T("kind.C"),
        CommandKind.Z => L.T("kind.Z"),
        CommandKind.RX => L.T("kind.RX"),
        CommandKind.RY => L.T("kind.RY"),
        CommandKind.CT => L.T("kind.CT"),
        CommandKind.CD => L.T("kind.CD"),
        CommandKind.CO => L.T("kind.CO"),
        CommandKind.CI => L.T("kind.CI"),
        CommandKind.CL => L.T("kind.CL"),
        CommandKind.CF => L.T("kind.CF"),
        _ => L.T("kind.P"),
    };

    /// <summary>Called while the ImGui context is being set up, before the font atlas is built.</summary>
    /// <param name="persistLayout">Load and save the window layout; off for screenshot runs.</param>
    public static void Configure(float scale, bool persistLayout)
    {
        Scale = scale;
        PersistLayout = persistLayout;
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        // A panel dragged out of the main window becomes a window of its own, free to go to another screen;
        // dragged back in, it docks again. Such windows belong to the main one and stay off the taskbar.
        io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
        io.ConfigViewportsNoTaskBarIcon = true;
        io.ConfigWindowsMoveFromTitleBarOnly = true;
        io.ConfigDockingWithShift = false;
        // Every remaining drag field (project settings and the like) also takes a typed value on a plain click.
        io.ConfigDragClickToInputText = true;
        io.NativePtr->IniFilename = persistLayout ? (byte*)Marshal.StringToCoTaskMemUTF8(AppSettings.LayoutFile) : null;

        ReleaseFontRanges();
        MainFont = MonoFont = digitFont = default;
        LoadFonts(io, scale);
        // After the others: the first font of all is the one the interface is written in.
        LoadDigitFont(io);
        ApplyStyle(scale);
    }

    /// <summary>Rebuild between frames, after all windows have finished drawing with the old atlas.</summary>
    public static void ApplyScale(float scale)
    {
        var io = ImGui.GetIO();
        io.NativePtr->FontDefault = null;
        io.Fonts.Clear();
        ReleaseFontRanges();
        MainFont = MonoFont = digitFont = default;
        Scale = scale;
        LoadFonts(io, scale);
        LoadDigitFont(io);
        ApplyStyle(scale);
    }

    /// <summary>Glyph ranges belong to this atlas and stay valid until it is cleared or destroyed.</summary>
    public static void ReleaseFontRanges()
    {
        foreach (nint ranges in fontRanges) ImGui.MemFree(ranges);
        fontRanges.Clear();
    }

    private static void LoadFonts(ImGuiIOPtr io, float scale)
    {
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string? cjk = new[] { "msyh.ttc", "msyh.ttf", "msjh.ttc", "meiryo.ttc", "malgun.ttf", "simhei.ttf", "simsun.ttc" }
            .Select(f => Path.Combine(fonts, f)).FirstOrDefault(File.Exists);
        string? main = cjk ?? new[] { "segoeui.ttf", "tahoma.ttf", "arial.ttf" }
            .Select(f => Path.Combine(fonts, f)).FirstOrDefault(File.Exists);
        string? mono = new[] { "consola.ttf", "cour.ttf" }
            .Select(f => Path.Combine(fonts, f)).FirstOrDefault(File.Exists);
        float size = MathF.Round(17f * scale);

        // Full CJK set for the UI so any file name can be shown, plus a few symbols used by the panels.
        var builder = new ImFontGlyphRangesBuilderPtr(ImGuiNative.ImFontGlyphRangesBuilder_ImFontGlyphRangesBuilder());
        builder.AddRanges(io.Fonts.GetGlyphRangesChineseFull());
        builder.AddText("●○◆◇■□▲△▼▽★☆→←↑↓…—·×÷°±");
        // Whatever else the language files use, such as accented or Cyrillic letters.
        foreach (var language in L.Languages) AddText(builder, language);
        ImVector fullRanges = BuildRanges(builder);

        ImFontConfigPtr config = ImGuiNative.ImFontConfig_ImFontConfig();
        config.OversampleH = 1;
        config.OversampleV = 1;
        config.PixelSnapH = true;
        config.SizePixels = size;
        // Continue merging script fonts even on Windows installations without Chinese fonts.
        MainFont = main == null ? io.Fonts.AddFontDefault(config)
            : io.Fonts.AddFontFromFileTTF(main, size, config, fullRanges.Data);
        ImGuiNative.ImFontConfig_destroy(config.NativePtr);

        MergeLanguageFonts(io, fonts, size);

        if (mono == null)
        {
            MonoFont = MainFont;
            return;
        }

        ImFontConfigPtr monoConfig = ImGuiNative.ImFontConfig_ImFontConfig();
        monoConfig.OversampleH = 2;
        monoConfig.OversampleV = 1;
        MonoFont = io.Fonts.AddFontFromFileTTF(mono, MathF.Round(15f * scale), monoConfig, io.Fonts.GetGlyphRangesDefault());
        ImGuiNative.ImFontConfig_destroy(monoConfig.NativePtr);

        ImFontConfigPtr merge = ImGuiNative.ImFontConfig_ImFontConfig();
        merge.MergeMode = true;
        merge.OversampleH = 1;
        merge.OversampleV = 1;
        merge.PixelSnapH = true;
        if (main != null)
            io.Fonts.AddFontFromFileTTF(main, MathF.Round(15f * scale), merge, fullRanges.Data);
        ImGuiNative.ImFontConfig_destroy(merge.NativePtr);
        MergeLanguageFonts(io, fonts, MathF.Round(15f * scale));
    }

    // Supply missing scripts in both interface and script-editor fonts.
    private static void MergeLanguageFonts(ImGuiIOPtr io, string fonts, float size)
    {
        foreach (var language in L.Languages)
        {
            string extraFont = Path.Combine(fonts, Path.GetFileName(language.Font));
            if (language.Font.Length == 0 || !File.Exists(extraFont)) continue;
            var builder = new ImFontGlyphRangesBuilderPtr(ImGuiNative.ImFontGlyphRangesBuilder_ImFontGlyphRangesBuilder());
            AddText(builder, language);
            ImVector ranges = BuildRanges(builder);
            ImFontConfigPtr config = ImGuiNative.ImFontConfig_ImFontConfig();
            config.MergeMode = true;
            config.OversampleH = 1;
            config.OversampleV = 1;
            config.PixelSnapH = true;
            io.Fonts.AddFontFromFileTTF(extraFont, size, config, ranges.Data);
            ImGuiNative.ImFontConfig_destroy(config.NativePtr);
        }
    }

    private static ImVector BuildRanges(ImFontGlyphRangesBuilderPtr builder)
    {
        // Native BuildRanges expects a zero-initialized vector. The binding's out parameter can reuse
        // stack contents between calls, causing later fonts to overwrite earlier fonts' range buffers.
        ImVector ranges = default;
        ImGuiNative.ImFontGlyphRangesBuilder_BuildRanges(builder.NativePtr, &ranges);
        ImGuiNative.ImFontGlyphRangesBuilder_destroy(builder.NativePtr);
        fontRanges.Add(ranges.Data);
        return ranges;
    }

    /// <summary>
    /// The digits once more, bold and large enough for a picture of any size: the numbers on hit circles
    /// are cut out of the font texture, which is the one place here where fonts are drawn.
    /// </summary>
    private static void LoadDigitFont(ImGuiIOPtr io)
    {
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string? file = new[] { "segoeuib.ttf", "arialbd.ttf", "tahomabd.ttf", "msyhbd.ttc", "msyh.ttc" }
            .Select(f => Path.Combine(fonts, f)).FirstOrDefault(File.Exists);
        if (file == null) return;
        var builder = new ImFontGlyphRangesBuilderPtr(ImGuiNative.ImFontGlyphRangesBuilder_ImFontGlyphRangesBuilder());
        builder.AddText("0123456789");
        ImVector ranges = BuildRanges(builder);
        ImFontConfigPtr config = ImGuiNative.ImFontConfig_ImFontConfig();
        config.OversampleH = 1;
        config.OversampleV = 1;
        config.PixelSnapH = true;
        digitFont = io.Fonts.AddFontFromFileTTF(file, 128f, config, ranges.Data);
        ImGuiNative.ImFontConfig_destroy(config.NativePtr);
    }

    /// <summary>The ten digits as the font texture has them, each cut to the same height. Empty when no font has them.</summary>
    public static Gfx.DigitGlyph[] DigitGlyphs()
    {
        if (digitFont.NativePtr == null) return Array.Empty<Gfx.DigitGlyph>();
        ImGui.GetIO().Fonts.GetTexDataAsRGBA32(out nint pixels, out int atlasWidth, out int atlasHeight, out _);
        // A glyph as ImGui itself keeps it: one word of flags and code point, then the advance, the box
        // it is drawn in (x0, y0, x1, y1) and the part of the texture it is in (u0, v0, u1, v1). The
        // binding's own view of it counts the flags as three words and so reads all of these from the
        // wrong place.
        const int advance = 0, y0 = 2, y1 = 4, u0 = 5, v0 = 6, u1 = 7, v1 = 8;
        var glyphs = new float*[10];
        float top = float.MaxValue, bottom = float.MinValue;
        for (int i = 0; i < 10; i++)
        {
            var glyph = digitFont.FindGlyphNoFallback((ushort)('0' + i));
            if (glyph.NativePtr == null) return Array.Empty<Gfx.DigitGlyph>();
            glyphs[i] = (float*)((byte*)glyph.NativePtr + sizeof(uint));
            top = MathF.Min(top, glyphs[i][y0]);
            bottom = MathF.Max(bottom, glyphs[i][y1]);
        }

        int height = (int)MathF.Ceiling(bottom - top);
        if (height is <= 0 or > 1024) return Array.Empty<Gfx.DigitGlyph>();
        var result = new Gfx.DigitGlyph[10];
        for (int i = 0; i < 10; i++)
        {
            float* glyph = glyphs[i];
            int left = (int)MathF.Round(glyph[u0] * atlasWidth), firstRow = (int)MathF.Round(glyph[v0] * atlasHeight);
            int width = Math.Clamp((int)MathF.Round((glyph[u1] - glyph[u0]) * atlasWidth), 1, 1024);
            int rows = (int)MathF.Round((glyph[v1] - glyph[v0]) * atlasHeight);
            int offset = (int)MathF.Round(glyph[y0] - top);
            var coverage = new byte[width * height];
            for (int row = 0; row < rows && row + offset < height && firstRow + row < atlasHeight; row++)
                for (int column = 0; column < width && left + column < atlasWidth; column++)
                    coverage[(row + offset) * width + column] = ((byte*)pixels)[((firstRow + row) * atlasWidth + left + column) * 4 + 3];
            result[i] = new Gfx.DigitGlyph(width, height, coverage, glyph[advance], digitFont.FontSize);
        }
        return result;
    }

    private static void AddText(ImFontGlyphRangesBuilderPtr builder, Language language)
    {
        builder.AddText(language.Name);
        foreach (string text in language.Strings.Values) builder.AddText(text);
    }

    private static void ApplyStyle(float scale)
    {
        // Start from defaults so repeated scale changes cannot multiply rounded sizes over time.
        var defaults = ImGuiNative.ImGuiStyle_ImGuiStyle();
        *ImGui.GetStyle().NativePtr = *defaults;
        ImGuiNative.ImGuiStyle_destroy(defaults);
        ImGui.StyleColorsDark();
        var style = ImGui.GetStyle();
        style.WindowRounding = 0;
        style.ChildRounding = 0;
        style.FrameRounding = 3;
        style.GrabRounding = 3;
        style.PopupRounding = 4;
        style.TabRounding = 3;
        style.ScrollbarRounding = 6;
        style.WindowBorderSize = 1;
        style.FrameBorderSize = 0;
        style.WindowPadding = new Vector2(8, 8);
        style.FramePadding = new Vector2(6, 3);
        style.ItemSpacing = new Vector2(6, 5);
        style.ItemInnerSpacing = new Vector2(5, 4);
        style.ScrollbarSize = 13;
        style.GrabMinSize = 9;
        style.IndentSpacing = 16;
        style.WindowMenuButtonPosition = ImGuiDir.None;

        var c = style.Colors;
        Vector4 V(int r, int g, int b, int a = 255) => new(r / 255f, g / 255f, b / 255f, a / 255f);

        c[(int)ImGuiCol.Text] = V(222, 222, 226);
        c[(int)ImGuiCol.TextDisabled] = V(125, 125, 130);
        c[(int)ImGuiCol.WindowBg] = V(35, 35, 38);
        c[(int)ImGuiCol.ChildBg] = V(0, 0, 0, 0);
        c[(int)ImGuiCol.PopupBg] = V(42, 42, 46, 250);
        c[(int)ImGuiCol.Border] = V(18, 18, 20);
        c[(int)ImGuiCol.FrameBg] = V(24, 24, 27);
        c[(int)ImGuiCol.FrameBgHovered] = V(52, 52, 58);
        c[(int)ImGuiCol.FrameBgActive] = V(62, 62, 70);
        c[(int)ImGuiCol.TitleBg] = V(26, 26, 28);
        c[(int)ImGuiCol.TitleBgActive] = V(30, 30, 33);
        c[(int)ImGuiCol.TitleBgCollapsed] = V(26, 26, 28);
        c[(int)ImGuiCol.MenuBarBg] = V(30, 30, 33);
        c[(int)ImGuiCol.ScrollbarBg] = V(28, 28, 30);
        c[(int)ImGuiCol.ScrollbarGrab] = V(72, 72, 78);
        c[(int)ImGuiCol.ScrollbarGrabHovered] = V(92, 92, 100);
        c[(int)ImGuiCol.ScrollbarGrabActive] = V(110, 110, 120);
        c[(int)ImGuiCol.CheckMark] = V(90, 170, 255);
        c[(int)ImGuiCol.SliderGrab] = V(64, 150, 255);
        c[(int)ImGuiCol.SliderGrabActive] = V(100, 175, 255);
        // The fill of a progress bar.
        c[(int)ImGuiCol.PlotHistogram] = V(64, 150, 255);
        c[(int)ImGuiCol.Button] = V(58, 58, 64);
        c[(int)ImGuiCol.ButtonHovered] = V(74, 74, 82);
        c[(int)ImGuiCol.ButtonActive] = V(48, 110, 190);
        c[(int)ImGuiCol.Header] = V(48, 100, 170, 150);
        c[(int)ImGuiCol.HeaderHovered] = V(64, 64, 72);
        c[(int)ImGuiCol.HeaderActive] = V(48, 110, 190);
        c[(int)ImGuiCol.Separator] = V(18, 18, 20);
        c[(int)ImGuiCol.SeparatorHovered] = V(64, 150, 255, 160);
        c[(int)ImGuiCol.SeparatorActive] = V(64, 150, 255);
        c[(int)ImGuiCol.ResizeGrip] = V(0, 0, 0, 0);
        c[(int)ImGuiCol.ResizeGripHovered] = V(64, 150, 255, 120);
        c[(int)ImGuiCol.ResizeGripActive] = V(64, 150, 255);
        c[(int)ImGuiCol.Tab] = V(30, 30, 33);
        c[(int)ImGuiCol.TabHovered] = V(60, 60, 68);
        c[(int)ImGuiCol.TabActive] = V(48, 48, 53);
        c[(int)ImGuiCol.TabUnfocused] = V(28, 28, 30);
        c[(int)ImGuiCol.TabUnfocusedActive] = V(40, 40, 44);
        c[(int)ImGuiCol.DockingPreview] = V(64, 150, 255, 110);
        c[(int)ImGuiCol.DockingEmptyBg] = V(20, 20, 22);
        c[(int)ImGuiCol.TableHeaderBg] = V(30, 30, 33);
        c[(int)ImGuiCol.TableBorderStrong] = V(18, 18, 20);
        c[(int)ImGuiCol.TableBorderLight] = V(28, 28, 31);
        c[(int)ImGuiCol.TableRowBgAlt] = V(255, 255, 255, 6);
        c[(int)ImGuiCol.TextSelectedBg] = V(64, 150, 255, 90);
        c[(int)ImGuiCol.DragDropTarget] = V(64, 150, 255);
        c[(int)ImGuiCol.NavHighlight] = V(64, 150, 255);
        c[(int)ImGuiCol.ModalWindowDimBg] = V(0, 0, 0, 140);

        style.ScaleAllSizes(scale);
    }

    // ---------------------------------------------------------------- helpers

    public static float Px(float value) => MathF.Round(value * Scale);

    /// <summary>mm:ss.fff, with a leading minus for intro time.</summary>
    public static string FormatTime(double ms)
    {
        long total = (long)Math.Round(Math.Abs(ms));
        string text = $"{total / 60000:00}:{total / 1000 % 60:00}.{total % 1000:000}";
        return ms < -0.5 ? "-" + text : text;
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} B";

    public static void TextDimmed(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, TextDim);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    public static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) SetTooltip(text);
    }

    /// <summary>A search field with a drawn magnifying glass, independent of the UI font's symbols.</summary>
    public static bool SearchInput(string id, string hint, ref string value, uint capacity)
    {
        Vector2 padding = ImGui.GetStyle().FramePadding;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(padding.X + Px(20), padding.Y));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Rgba(40, 40, 46));
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Rgba(164, 164, 174));
        bool changed = ImGui.InputTextWithHint(id, hint, ref value, capacity);
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();

        Vector2 min = ImGui.GetItemRectMin(), max = ImGui.GetItemRectMax();
        Vector2 centre = new(min.X + padding.X + Px(6), (min.Y + max.Y) / 2 - Px(1));
        var draw = ImGui.GetWindowDrawList();
        bool active = ImGui.IsItemActive(), hovered = ImGui.IsItemHovered();
        uint border = active ? Accent : hovered ? Rgba(112, 156, 210) : Rgba(78, 80, 91);
        float thickness = Px(active ? 2 : 1);
        draw.AddRect(min + new Vector2(thickness / 2), max - new Vector2(thickness / 2),
            ImGui.GetColorU32(border), ImGui.GetStyle().FrameRounding, ImDrawFlags.RoundCornersAll, thickness);
        uint colour = ImGui.GetColorU32(active || hovered ? Accent : Rgba(160, 170, 184));
        draw.PushClipRect(min, max, true);
        draw.AddCircle(centre, Px(4), colour, 16, Px(1.5f));
        draw.AddLine(centre + new Vector2(Px(3)), centre + new Vector2(Px(7)), colour, Px(1.5f));
        draw.PopClipRect();
        return changed;
    }

    /// <summary>
    /// ImGui reads tooltips and wrapped text as printf formats. Translated text and file names are not
    /// formats, so a % in them is escaped here.
    /// </summary>
    public static string EscapeFormat(string text) => text.Contains('%') ? text.Replace("%", "%%") : text;

    public static void SetTooltip(string text) => ImGui.SetTooltip(EscapeFormat(text));

    public static void TextWrapped(string text) => ImGui.TextWrapped(EscapeFormat(text));

    /// <summary>
    /// printf format of a drag or slider field: the text of <paramref name="key"/> with its {0} replaced
    /// by <paramref name="number"/>, such as "%d".
    /// </summary>
    public static string NumberFormat(string key, string number) => EscapeFormat(L.T(key)).Replace("{0}", number);

    /// <summary>
    /// Room to keep on the right of a field for its label: <paramref name="minimum"/>, or more when one
    /// of the labels is longer than that in the current language.
    /// </summary>
    public static float LabelWidth(float minimum, params ReadOnlySpan<string> labels)
    {
        float width = Px(minimum);
        float spacing = ImGui.GetStyle().ItemInnerSpacing.X * 2;
        foreach (string label in labels) width = MathF.Max(width, ImGui.CalcTextSize(label).X + spacing);
        return width;
    }

    /// <summary>Width of a button that is <paramref name="minimum"/> wide unless its label needs more.</summary>
    public static float ButtonWidth(string label, float minimum) =>
        MathF.Max(Px(minimum), ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2 + Px(12));

    public static bool Button(string label, float minimum) => ImGui.Button(label, new Vector2(ButtonWidth(label, minimum), 0));

    public static void Diamond(ImDrawListPtr dl, Vector2 centre, float radius, uint fill, uint outline)
    {
        var a = centre + new Vector2(0, -radius);
        var b = centre + new Vector2(radius, 0);
        var c = centre + new Vector2(0, radius);
        var d = centre + new Vector2(-radius, 0);
        dl.AddQuadFilled(a, b, c, d, fill);
        dl.AddQuad(a, b, c, d, outline, 1f);
    }

    /// <summary>Small arrow used for fold/unfold in custom-drawn rows.</summary>
    public static void FoldArrow(ImDrawListPtr dl, Vector2 centre, float size, bool open, uint color)
    {
        float h = size * 0.5f;
        if (open)
            dl.AddTriangleFilled(centre + new Vector2(-h, -h * 0.6f), centre + new Vector2(h, -h * 0.6f), centre + new Vector2(0, h * 0.8f), color);
        else
            dl.AddTriangleFilled(centre + new Vector2(-h * 0.6f, -h), centre + new Vector2(h * 0.8f, 0), centre + new Vector2(-h * 0.6f, h), color);
    }

    /// <summary>A button whose icon is drawn with <paramref name="draw"/> (centre, size, colour).</summary>
    /// <summary>
    /// A whole number between a minus button (left) and a plus button (right), typed in the middle. The buttons stay
    /// within <paramref name="min"/>..<paramref name="max"/>; a typed value is left for the caller to accept or refuse.
    /// Returns whether it changed.
    /// </summary>
    public static bool Stepper(string id, ref int value, int min, int max, float fieldWidth)
    {
        bool changed = false;
        float button = ImGui.GetFrameHeight(), gap = ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.PushID(id);
        ImGui.BeginDisabled(value <= min);
        if (ImGui.Button("-", new System.Numerics.Vector2(button, 0))) { value--; changed = true; }
        ImGui.EndDisabled();
        ImGui.SameLine(0, gap);
        ImGui.SetNextItemWidth(fieldWidth);
        int typed = value;
        if (ImGui.InputInt("##value", ref typed, 0, 0)) { value = typed; changed = true; }
        ImGui.SameLine(0, gap);
        ImGui.BeginDisabled(value >= max);
        if (ImGui.Button("+", new System.Numerics.Vector2(button, 0))) { value++; changed = true; }
        ImGui.EndDisabled();
        ImGui.PopID();
        return changed;
    }

    public static bool IconButton(string id, float size, Action<ImDrawListPtr, Vector2, float, uint> draw, bool active = false)
    {
        var pos = ImGui.GetCursorScreenPos();
        if (active) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));
        bool clicked = ImGui.Button(id, new Vector2(size, size));
        if (active) ImGui.PopStyleColor();
        draw(ImGui.GetWindowDrawList(), pos + new Vector2(size, size) * 0.5f, size, ImGui.GetColorU32(ImGuiCol.Text));
        return clicked;
    }

    public static void DrawProgressTrack(ImDrawListPtr dl, Vector2 pos, Vector2 size, float fraction, bool lit)
    {
        float radius = Px(6), x0 = pos.X + radius, x1 = MathF.Max(x0 + 1, pos.X + size.X - radius);
        float middle = MathF.Round(pos.Y + size.Y / 2), half = Px(lit ? 3 : 2);
        float knob = x0 + (x1 - x0) * Math.Clamp(fraction, 0, 1);
        float alpha = ImGui.GetStyle().Alpha;
        dl.AddRectFilled(new Vector2(x0, middle - half), new Vector2(x1, middle + half), WithAlpha(Rgba(255, 255, 255, 38), alpha), half);
        dl.AddRectFilled(new Vector2(x0, middle - half), new Vector2(knob, middle + half), WithAlpha(Accent, alpha), half);
        dl.AddCircleFilled(new Vector2(knob, middle), lit ? radius : radius - 1, WithAlpha(Rgba(236, 236, 240), alpha));
    }

    /// <summary>Three faders: advanced audio controls.</summary>
    public static void DrawAudioSettings(ImDrawListPtr dl, Vector2 c, float s, uint color)
    {
        float half = MathF.Round(s * 0.27f), spacing = MathF.Round(s * 0.22f);
        float thickness = MathF.Max(1, Px(1));
        for (int i = -1; i <= 1; i++)
        {
            float x = MathF.Round(c.X + i * spacing), y = MathF.Round(c.Y + (i == 0 ? -1 : 1) * s * 0.12f);
            dl.AddLine(new Vector2(x, c.Y - half), new Vector2(x, c.Y + half), color, thickness);
            dl.AddRectFilled(new Vector2(x - s * 0.09f, y - thickness), new Vector2(x + s * 0.09f, y + thickness), color, thickness);
        }
    }

    public static void DrawPlay(ImDrawListPtr dl, Vector2 c, float s, uint color)
    {
        float r = s * 0.24f;
        dl.AddTriangleFilled(c + new Vector2(-r * 0.8f, -r), c + new Vector2(r, 0), c + new Vector2(-r * 0.8f, r), color);
    }

    /// <summary>A camera: body, viewfinder bump and lens.</summary>
    public static void DrawFullscreen(ImDrawListPtr dl, Vector2 c, float s, uint color) => DrawScreenCorners(dl, c, s, color, false);
    public static void DrawExitFullscreen(ImDrawListPtr dl, Vector2 c, float s, uint color) => DrawScreenCorners(dl, c, s, color, true);

    private static void DrawScreenCorners(ImDrawListPtr dl, Vector2 c, float s, uint color, bool inward)
    {
        float r = MathF.Round(s * 0.29f), arm = MathF.Round(s * 0.17f);
        foreach (int x in new[] { -1, 1 })
            foreach (int y in new[] { -1, 1 })
            {
                var corner = c + new Vector2(x, y) * (inward ? r - arm : r);
                float direction = inward ? 1 : -1;
                dl.AddLine(corner, corner + new Vector2(direction * x * arm, 0), color, 2);
                dl.AddLine(corner, corner + new Vector2(0, direction * y * arm), color, 2);
            }
    }

    public static void DrawCamera(ImDrawListPtr dl, Vector2 c, float s, uint color)
    {
        c = new Vector2(MathF.Round(c.X), MathF.Round(c.Y));
        float w = MathF.Round(s * 0.28f), top = -MathF.Round(s * 0.14f), bottom = MathF.Round(s * 0.24f);
        dl.AddRectFilled(c + new Vector2(-w, top), c + new Vector2(w, bottom), color, 2f);
        dl.AddRectFilled(c + new Vector2(-MathF.Round(s * 0.11f), top - MathF.Round(s * 0.09f)), c + new Vector2(MathF.Round(s * 0.11f), top), color, 1f);
        // The lens is a ring cut out of the body.
        var lens = c + new Vector2(0, (top + bottom) / 2);
        dl.AddCircleFilled(lens, s * 0.13f, PanelDark, 20);
        dl.AddCircleFilled(lens, s * 0.065f, color, 16);
    }

    public static void DrawPause(ImDrawListPtr dl, Vector2 c, float s, uint color)
    {
        float r = s * 0.22f, w = s * 0.08f;
        dl.AddRectFilled(c + new Vector2(-r, -r), c + new Vector2(-r + w * 2, r), color);
        dl.AddRectFilled(c + new Vector2(r - w * 2, -r), c + new Vector2(r, r), color);
    }

    public static void DrawToStart(ImDrawListPtr dl, Vector2 c, float s, uint color) => DrawJump(dl, c, s, color, -1, false);

    public static void DrawToEnd(ImDrawListPtr dl, Vector2 c, float s, uint color) => DrawJump(dl, c, s, color, 1, false);

    public static void DrawStepBack(ImDrawListPtr dl, Vector2 c, float s, uint color) => DrawJump(dl, c, s, color, -1, true);

    public static void DrawStepForward(ImDrawListPtr dl, Vector2 c, float s, uint color) => DrawJump(dl, c, s, color, 1, true);

    public static void DrawPreviousKey(ImDrawListPtr dl, Vector2 c, float s, uint color) => DrawKeyJump(dl, c, s, color, -1);

    public static void DrawNextKey(ImDrawListPtr dl, Vector2 c, float s, uint color) => DrawKeyJump(dl, c, s, color, 1);

    /// <summary>
    /// The sign for going to a keyframe: an arrow pointing at a diamond, the mark keyframes have
    /// everywhere, to the left (-1) or to the right (1).
    /// </summary>
    private static void DrawKeyJump(ImDrawListPtr dl, Vector2 c, float s, uint color, int direction)
    {
        c = new Vector2(MathF.Round(c.X), MathF.Round(c.Y));
        float h = MathF.Round(s * 0.2f), w = MathF.Round(s * 0.17f), gap = MathF.Round(s * 0.05f), r = MathF.Round(s * 0.17f);
        // Laid out pointing right, then mirrored for the other direction.
        float x = -MathF.Round((w + gap + r * 2) / 2);
        Vector2 back = new(x * direction, 0), tip = new((x + w) * direction, 0);
        if (direction > 0) dl.AddTriangleFilled(c + back + new Vector2(0, -h), c + tip, c + back + new Vector2(0, h), color);
        else dl.AddTriangleFilled(c + tip, c + back + new Vector2(0, -h), c + back + new Vector2(0, h), color);
        Vector2 centre = c + new Vector2((x + w + gap + r) * direction, 0);
        dl.AddQuadFilled(centre + new Vector2(0, -r), centre + new Vector2(r, 0), centre + new Vector2(0, r), centre + new Vector2(-r, 0), color);
    }

    /// <summary>
    /// The transport signs made of a bar and arrows, pointing left (-1) or right (1). Two arrows running
    /// into the bar mean "to the very start or end"; one arrow leaving the bar means "one step".
    /// </summary>
    private static void DrawJump(ImDrawListPtr dl, Vector2 c, float s, uint color, int direction, bool step)
    {
        c = new Vector2(MathF.Round(c.X), MathF.Round(c.Y));
        int arrows = step ? 1 : 2;
        float h = MathF.Round(s * 0.22f), bar = MathF.Max(2, MathF.Round(s * 0.07f));
        float w = MathF.Round(s * (step ? 0.24f : 0.2f)), gap = step ? MathF.Round(s * 0.05f) : 0;

        // Laid out pointing right, then mirrored for the other direction.
        float x = -MathF.Round((w * arrows + gap + bar) / 2);
        if (step)
        {
            Bar(x);
            x += bar + gap;
        }
        for (int i = 0; i < arrows; i++, x += w)
        {
            Vector2 back = new(x * direction, 0), tip = new((x + w) * direction, 0);
            // Clockwise either way, which is what the anti-aliased fill expects.
            if (direction > 0) dl.AddTriangleFilled(c + back + new Vector2(0, -h), c + tip, c + back + new Vector2(0, h), color);
            else dl.AddTriangleFilled(c + tip, c + back + new Vector2(0, -h), c + back + new Vector2(0, h), color);
        }
        if (!step) Bar(x);

        void Bar(float at)
        {
            float x0 = MathF.Min(at * direction, (at + bar) * direction), x1 = MathF.Max(at * direction, (at + bar) * direction);
            dl.AddRectFilled(c + new Vector2(x0, -h), c + new Vector2(x1, h), color);
        }
    }
}

/// <summary>Dock builder functions that ImGui.NET does not wrap.</summary>
internal static unsafe class DockBuilder
{
    private const string Lib = "cimgui";
    private const int DockSpaceFlag = 1 << 10; // ImGuiDockNodeFlags_DockSpace (internal)

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint igDockBuilderAddNode(uint nodeId, int flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderRemoveNode(uint nodeId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderSetNodeSize(uint nodeId, Vector2 size);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint igDockBuilderSplitNode(uint nodeId, int direction, float ratio, uint* outAtDirection, uint* outOpposite);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderDockWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string windowName, uint nodeId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderFinish(uint nodeId);

    public static void Reset(uint dockId, Vector2 size)
    {
        igDockBuilderRemoveNode(dockId);
        igDockBuilderAddNode(dockId, DockSpaceFlag);
        igDockBuilderSetNodeSize(dockId, size);
    }

    /// <summary>Splits a node; returns the part on the given side and leaves the rest in <paramref name="remaining"/>.</summary>
    public static uint Split(uint nodeId, ImGuiDir direction, float ratio, out uint remaining)
    {
        uint at, opposite;
        igDockBuilderSplitNode(nodeId, (int)direction, ratio, &at, &opposite);
        remaining = opposite;
        return at;
    }

    public static void Dock(string windowName, uint nodeId) => igDockBuilderDockWindow(windowName, nodeId);

    public static void Finish(uint dockId) => igDockBuilderFinish(dockId);
}
