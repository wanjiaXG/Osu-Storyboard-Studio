using System.Globalization;
using SbStudio.App.Audio;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using SbStudio.App.Gfx;
using SbStudio.App.Ui;
using SbStudio.Core;
using SbStudio.Core.Updates;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace SbStudio.App;

public static class Program
{
    /// <summary>
    /// Command line:
    ///   [project]                 project folder or project.sbproj to open
    ///   --demo &lt;folder&gt;           create (if needed) and open a demo project in that folder
    ///   --time &lt;ms&gt;               start at this time
    ///   --select &lt;index&gt;          select and unfold the object with this index
    ///   --lang &lt;code&gt;             interface language for this run, such as zh-Hans or en
    /// For automated checks (these never touch the user's settings or layout, and run in zh-Hans unless --lang says otherwise):
    ///   --screenshot &lt;png&gt;        save the whole window to a PNG after a few frames and exit
    ///   --preview-shot &lt;png&gt;      same for the preview image only
    ///   --frames &lt;n&gt;              how many frames to wait before the screenshot (default 40)
    ///   --script &lt;file&gt;           play back scripted input, see <see cref="InputScript"/>
    ///   --size &lt;w&gt;x&lt;h&gt;            window size
    /// </summary>
    private sealed class Options
    {
        public string? Project, Demo, Screenshot, PreviewShot, Script, Language;
        public double? Time;
        public int? Select;
        public int Width = 1600, Height = 900;
        public int Frames = 40;
        public bool Automated => Screenshot != null || PreviewShot != null || Script != null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint thread, uint to, bool attach);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    /// <summary>
    /// Puts the window in front of all others and gives it the keyboard. Left to itself, Windows only does
    /// that for a program started by the one in front. Started by a script that first spent a while building,
    /// or from another program's terminal, the window would open behind what the user is looking at.
    /// </summary>
    private static void BringToFront(IntPtr window)
    {
        const uint noSize = 0x0001, noMove = 0x0002;
        IntPtr topmost = new(-1), notTopmost = new(-2);
        // Being topmost for a moment raises the window whatever the rules about the keyboard say.
        SetWindowPos(window, topmost, 0, 0, 0, 0, noMove | noSize);
        SetWindowPos(window, notTopmost, 0, 0, 0, 0, noMove | noSize);

        // The keyboard may be taken by a thread that shares its input with the one that has it now.
        uint self = GetCurrentThreadId(), front = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        bool attached = front != 0 && front != self && AttachThreadInput(self, front, true);
        BringWindowToTop(window);
        SetForegroundWindow(window);
        if (attached) AttachThreadInput(self, front, false);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        var options = Parse(args);
        // The same per-directory mutex protects the editor and the external updater from each other.
        string mutexName = PortablePackage.MutexName(AppContext.BaseDirectory);
        using var instance = new Mutex(false, mutexName);
        bool held;
        try { held = instance.WaitOne(0); }
        catch (AbandonedMutexException) { held = true; }
        if (!held)
        {
            // A project double-clicked while the program is open: that window opens it.
            if (!options.Automated && !string.IsNullOrEmpty(options.Project) && ProjectHandOff.Send(mutexName, options.Project)) return 0;
            if (!options.Automated && OperatingSystem.IsWindows())
                MessageBoxW(IntPtr.Zero, L.T("update.alreadyRunning"), "osu! Storyboard Studio", 0x40);
            return 1;
        }
        EditorApp? app = null;
        try
        {
            PortablePackage.Recover(AppContext.BaseDirectory);
            Run(options, mutexName, a => app = a);
            return 0;
        }
        catch (Exception ex)
        {
            AppSettings.Log(L.T("log.crash", ex));
            string recovered = "";
            try
            {
                // Whatever happened, do not lose the storyboard that was being edited.
                if (app?.Project != null && app.Dirty)
                {
                    app.WriteAutosave();
                    recovered = "\n\n" + L.T("crash.recovered");
                }
            }
            catch (Exception)
            {
            }

            if (options.Automated)
                File.WriteAllText((options.Screenshot ?? options.PreviewShot ?? options.Script) + ".error.txt", ex.ToString());
            else if (OperatingSystem.IsWindows())
                MessageBoxW(IntPtr.Zero, L.T("crash.message", ex.Message, recovered, AppSettings.LogFile), "osu! Storyboard Studio", 0x10);
            return 1;
        }
        finally { instance.ReleaseMutex(); }
    }

    private static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--demo": o.Demo = Next(); break;
                case "--time": o.Time = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--select": o.Select = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--screenshot": o.Screenshot = Next(); break;
                case "--preview-shot": o.PreviewShot = Next(); break;
                case "--script": o.Script = Next(); break;
                case "--lang": o.Language = Next(); break;
                case "--frames": o.Frames = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--size":
                    var parts = Next().Split('x');
                    o.Width = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    o.Height = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    break;
                default:
                    if (!args[i].StartsWith("--", StringComparison.Ordinal)) o.Project = args[i];
                    break;
            }
        }
        return o;
    }

    private static void Run(Options options, string mutexName, Action<EditorApp> created)
    {
        var settings = AppSettings.Load();
        AppSettings.ReadOnly = options.Automated;
        // Automated checks start out the same whatever the user has chosen; a script can still switch it in the menu.
        if (options.Automated)
        {
            settings.VSync = true;
            settings.WaveformStyle = TimelineWaveformStyle.Classic;
            settings.TimelineAutoJump = false;
            settings.SoundOffsetMs = 0;
        }

        // Languages beyond the built-in ones are files in the "lang" folder next to the program. They have
        // to be known before the window exists: the font atlas is built from the characters they use.
        foreach (string problem in L.LoadFolder(Path.Combine(AppContext.BaseDirectory, "lang"))) AppSettings.Log(problem);
        UiLanguage.Apply(options.Language ?? (options.Automated ? "zh-Hans" : settings.Language));

        var windowOptions = WindowOptions.Default;
        windowOptions.Title = EditorApp.WindowTitle;
        windowOptions.Size = new Vector2D<int>(options.Width, options.Height);
        windowOptions.WindowState = options.Automated ? WindowState.Normal : WindowState.Maximized;
        windowOptions.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3));
        windowOptions.VSync = settings.VSync;

        using var window = Window.Create(windowOptions);
        GL gl = null!;
        IInputContext input = null!;
        ImGuiBackend imgui = null!;
        EditorApp app = null!;
        MainUi ui = null!;
        RenderTarget target = null!;
        InputScript? script = null;
        string title = windowOptions.Title;
        int frame = 0;
        bool released = false;

        window.Load += () =>
        {
            gl = window.CreateOpenGL();
            input = window.CreateInput();
            SetWindowIcon(window);

            if (settings.FirstRun)
            {
                // Pick a readable size on high-resolution screens the first time.
                int height = window.Monitor?.VideoMode.Resolution?.Y ?? 1080;
                settings.UiScale = height >= 2000 ? 1.75f : height >= 1400 ? 1.25f : 1f;
            }
            if (options.Automated) settings.UiScale = 1f;
            float scale = settings.UiScale;

            // A script plays its own mouse and keyboard; the real ones must not get in its way.
            imgui = new ImGuiBackend(gl, window, input, options.Script == null, () => Theme.Configure(scale, !options.Automated));
            app = new EditorApp(gl, settings);
            app.HitObjects.Digits = Theme.DigitGlyphs;
            created(app);
            target = new RenderTarget(gl);
            ui = new MainUi(app, target) { FullscreenWindow = new FullscreenWindow(new SilkFullscreenWindow(window), options.Automated) };
            if (!options.Automated) ui.StartAutomaticUpdateCheck();
            // Scripted double clicks inspect the generated URI without opening another application.
            if (options.Automated)
            {
                app.ConfigureEffectsForTest(Path.Combine(Path.GetDirectoryName(options.Script ?? options.Screenshot ?? "") ?? Path.GetTempPath(), "effect-profile"), Path.Combine(AppContext.BaseDirectory, "effects", "host", "SbStudio.Effects.Host.exe"));
                ui.OsuEditLinkOpener = _ => { };
                ui.HitObjectClipboardWriter = _ => { }; // Inspect copied text without replacing the user's clipboard.
                ui.TimingClipboardWriter = _ => { };
                ui.ClipboardRevision = () => 0;
                ui.ClipboardText = () => "";
            }
            imgui.FilesDropped += ui.OnFilesDropped;
            if (options.Script != null) script = new InputScript(File.ReadAllLines(options.Script));

            void Opened()
            {
                if (options.Time is { } time) app.Seek(time);
                if (options.Select is { } index && index >= 0 && index < app.Doc.Objects.Count)
                {
                    app.SelectOnly(app.Doc.Objects[index]);
                    app.Doc.Objects[index].Expanded = true;
                    app.TouchFolds();
                }
            }

            if (!string.IsNullOrEmpty(options.Demo))
            {
                string folder = Path.GetFullPath(options.Demo);
                if (File.Exists(Path.Combine(folder, Project.ProjectFileName))) app.OpenProject(folder, Opened);
                else if (app.NewProject(Path.GetDirectoryName(folder)!, Path.GetFileName(folder), true, null)) Opened();
            }
            else if (!string.IsNullOrEmpty(options.Project))
            {
                app.OpenProject(options.Project, Opened);
            }

            // Automated checks count frames, so the project is all there before the first one.
            if (options.Automated) app.FinishLoading();

            // Automated checks stay behind: they run while the user is busy with something else.
            if (!options.Automated && OperatingSystem.IsWindows() && window.Native?.Win32 is { } win32) BringToFront(win32.Hwnd);
        };

        // What went wrong while a frame was drawn. Thrown out of the window's own loop it would be lost: the
        // window cannot be let go of from inside that loop, and its complaint about that would be all
        // that is left to report.
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;

        window.Closing += () =>
        {
            if (failure == null && !ui.RequestClose())
            {
                window.IsClosing = false;
                return;
            }
            // GL objects must go while the context is still alive.
            if (released) return;
            released = true;
            ui.StopUpdates();
            app.Dispose();
            target.Dispose();
            imgui.Dispose();
            input.Dispose();
        };

        // Automated runs neither take projects from other copies nor touch the user's file types.
        using var handOff = options.Automated ? null : new ProjectHandOff(mutexName);
        if (!options.Automated && OperatingSystem.IsWindows()) ApplyFileAssociation(settings);

        window.Render += delta =>
        {
            if (released || failure != null) return;
            try
            {
                Frame(delta);
            }
            catch (Exception ex)
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                window.Close();
            }
        };

        void Frame(double delta)
        {
            frame++;
            // Switched in the View menu. An export does not wait for the screen: every wait would be time
            // in which no frame of the video gets made.
            bool vsync = settings.VSync && !app.Exporting;
            if (window.VSync != vsync) window.VSync = vsync;
            AudioTap.Flush();
            app.Update();
            if (handOff?.Next() is { } handedPath)
            {
                ui.OnFilesDropped(new[] { handedPath });
                if (OperatingSystem.IsWindows() && window.Native?.Win32 is { } win32) BringToFront(win32.Hwnd);
            }
            script?.BeginFrame(frame, app, ui, imgui.Origin);
            if (Math.Abs(Theme.Scale - settings.UiScale) > 0.001f)
                imgui.RebuildFonts(() => Theme.ApplyScale(settings.UiScale));
            imgui.NewFrame((float)Math.Max(delta, 1e-4));
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ui.Draw();
            UiMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            app.EndFrame(ImGui.IsAnyItemActive() || ImGui.IsMouseDown(ImGuiMouseButton.Left));

            var size = window.FramebufferSize;
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
            gl.Disable(EnableCap.ScissorTest);
            gl.ClearColor(0.08f, 0.08f, 0.09f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            var shots = script?.TakeShots(frame).ToList();
            // The frame a run without a script ends on, once its images are in.
            bool last = script == null && options.Automated && frame >= options.Frames &&
                        (app.Textures.PendingCount == 0 || frame > options.Frames + 600);
            imgui.Render(shots is { Count: > 0 } || last);
            DrawMs = System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds;

            if (app.Title != title)
            {
                title = app.Title;
                window.Title = title;
            }

            if (script != null)
            {
                foreach (string file in shots!) SaveWindow(gl, file, size, imgui.Shots);
                foreach (string file in script.TakeShots(frame, "preview"))
                    if (target.Framebuffer != 0) PngWriter.Write(file, target.Width, target.Height, target.ReadPixels());
                if (script.Finished(frame)) window.Close();
            }
            else if (last)
            {
                if (options.Screenshot != null) SaveWindow(gl, options.Screenshot, size, imgui.Shots);
                if (options.PreviewShot != null && target.Framebuffer != 0)
                    PngWriter.Write(options.PreviewShot, target.Width, target.Height, target.ReadPixels());
                window.Close();
            }

            if (ui.QuitRequested) window.Close();
        }

        window.Run();
        AudioTap.Start(null);
        failure?.Throw();
        if (!options.Automated) settings.Save();
    }

    /// <summary>Registers .sbproj for this exe, or removes the registration, as the Preferences menu says.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void ApplyFileAssociation(AppSettings settings)
    {
        try { ProjectFileAssociation.Apply(settings.OpenProjectFiles, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OsuStoryboardStudio.exe")); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            AppSettings.Log(L.T("log.fileAssociation", e.Message));
        }
    }

    /// <summary>CPU time of the last frame's UI build and of submitting its draw data.</summary>
    public static double UiMs { get; private set; }
    private static double DrawMs;

    /// <summary>
    /// Saves what the main window shows. Panels and popups that have left it for windows of their own are
    /// added where they sit on the screen, the picture growing to the right and down to hold them, so a
    /// point in the picture is still the point a script moves the mouse to.
    /// </summary>
    private static unsafe void SaveWindow(GL gl, string file, Vector2D<int> size, IReadOnlyList<ImGuiBackend.Shot> others)
    {
        var raw = new byte[size.X * size.Y * 4];
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.ReadBuffer(ReadBufferMode.Back);
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        fixed (byte* p = raw)
            gl.ReadPixels(0, 0, (uint)size.X, (uint)size.Y, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        byte[] pixels = RenderTarget.FlipRows(raw, size.X, size.Y);

        int width = size.X, height = size.Y;
        foreach (var other in others)
        {
            width = Math.Clamp(other.X + other.Width, width, 8192);
            height = Math.Clamp(other.Y + other.Height, height, 8192);
        }
        if (others.Count > 0)
        {
            var canvas = new byte[width * height * 4];
            Paste(pixels, 0, 0, size.X, size.Y);
            foreach (var other in others) Paste(other.Pixels, other.X, other.Y, other.Width, other.Height);
            pixels = canvas;

            void Paste(byte[] source, int x, int y, int w, int h)
            {
                int x0 = Math.Max(0, x), x1 = Math.Min(width, x + w);
                if (x1 <= x0) return;
                for (int row = Math.Max(0, y); row < Math.Min(height, y + h); row++)
                    System.Buffer.BlockCopy(source, ((row - y) * w + x0 - x) * 4, canvas, (row * width + x0) * 4, (x1 - x0) * 4);
            }
        }
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        PngWriter.Write(file, width, height, pixels);
    }

    /// <summary>
    /// Scripted input for automated checks. One step per line, "frame command arguments":
    ///   move x y | down [button] | up [button] | wheel amount | key Name | mod ctrl|shift|alt on|off
    ///   text some text | popup id | open folder | drop file | select index | selectadd index | seek ms | skin id | hitobjects on|off | dump file | dumpsel file | shot file.png | quit
    ///   frame height file.png|jpg (the storyboard's picture alone, as the screenshot button saves it)
    /// popup takes a dialog id: new, export, settings, import, shortcuts, about. open opens a project the way
    /// the menu does, loading screen included, while a project named on the command line is loaded before frame 1.
    /// move counts from the top left corner of the main window; a point outside it reaches the panels that
    /// have been dragged out into windows of their own, which shot shows next to the main window.
    /// Lines starting with # are ignored.
    /// </summary>
    private sealed class InputScript
    {
        private readonly List<(int Frame, string Command, string Argument)> steps = new();
        // Left, right, middle, and the back and forward buttons on the side of a mouse.
        private readonly bool[] buttons = new bool[5];
        private readonly Dictionary<ImGuiKey, bool> modifiers = new();
        private Vector2 mouse = new(-1, -1);
        private readonly List<(int Frame, ImGuiKey Key)> keyReleases = new();
        private readonly int lastFrame;
        private int skippedFrames, appliedFrame;
        private double waitUntil, waitStarted;
        private bool waitForHidden,waitForPlugins,waitForEffects;

        public InputScript(IEnumerable<string> lines)
        {
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                steps.Add((int.Parse(parts[0], CultureInfo.InvariantCulture), parts[1], parts.Length > 2 ? parts[2] : ""));
            }
            lastFrame = steps.Count > 0 ? steps.Max(s => s.Frame) : 0;
        }

        public bool Finished(int frame) => appliedFrame >= 0 && (appliedFrame > lastFrame + 2 || steps.Any(s => s.Frame == appliedFrame && s.Command == "quit"));

        public IEnumerable<string> TakeShots(int frame, string command = "shot") =>
            steps.Where(s => s.Frame == appliedFrame && s.Command == command).Select(s => s.Argument);

        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private double lastFrameMs, worstFrameMs;

        /// <summary>Queues this frame's scripted input; nothing else feeds the ImGui frame that follows.</summary>
        /// <param name="origin">The main window's top left corner in ImGui's coordinates. The script counts from that corner.</param>
        public void BeginFrame(int frame, EditorApp app, MainUi ui, Vector2 origin)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (frame > 20) worstFrameMs = Math.Max(worstFrameMs, now - lastFrameMs);
            frameMs = now - lastFrameMs;
            lastFrameMs = now;
            bool waiting = now < waitUntil || (waitForHidden && !ui.FullscreenControlsHidden) || (waitForPlugins && app.ImportingPlugins>0) || (waitForEffects && ui.EffectsBusy);
            if (waiting && now - waitStarted > 15000) throw new TimeoutException("Script wait exceeded 15 seconds.");
            if (waiting) { skippedFrames++; appliedFrame = -1; }
            else { waitForHidden = false;waitForPlugins=false;waitForEffects=false; appliedFrame = frame - skippedFrames; }
            Apply(appliedFrame, app, ui, origin);
            // QPC timestamp, playing, playhead, played output frames, produced output frames: for comparing
            // the playback clock with a recording of the output.
            clockLog?.WriteLine(FormattableString.Invariant(
                $"{System.Diagnostics.Stopwatch.GetTimestamp()},{(app.Playing ? 1 : 0)},{app.Audio.CurrentTimeMs:0.###},{app.Audio.ClockState}"));
        }

        private StreamWriter? clockLog;

        private double frameMs;

        private void Apply(int frame, EditorApp app, MainUi ui, Vector2 origin)
        {
            var io = ImGui.GetIO();
            foreach (var (stepFrame, command, argument) in steps)
            {
                if (stepFrame != frame) continue;
                var args = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                AudioTap.Command(frame, command, argument);
                switch (command)
                {
                    case "move":
                        mouse = new Vector2(float.Parse(args[0], CultureInfo.InvariantCulture), float.Parse(args[1], CultureInfo.InvariantCulture));
                        break;
                    case "down":
                        buttons[args.Length > 0 ? int.Parse(args[0]) : 0] = true;
                        break;
                    case "up":
                        buttons[args.Length > 0 ? int.Parse(args[0]) : 0] = false;
                        break;
                    case "wheel":
                        io.AddMouseWheelEvent(0, float.Parse(args[0], CultureInfo.InvariantCulture));
                        break;
                    case "key":
                    {
                        var key = Enum.Parse<ImGuiKey>(args[0], true);
                        io.AddKeyEvent(key, true);
                        keyReleases.Add((frame + 1, key));
                        break;
                    }
                    case "mod":
                    {
                        var key = args[0] switch { "ctrl" => ImGuiKey.ModCtrl, "shift" => ImGuiKey.ModShift, _ => ImGuiKey.ModAlt };
                        modifiers[key] = args[1] == "on";
                        break;
                    }
                    case "text":
                        io.AddInputCharactersUTF8(argument.Replace("\\n", "\n"));
                        break;
                    case "popup":
                        ui.OpenDialog(argument);
                        break;
                    case "open":
                        ui.OpenProject(argument);
                        break;
                    case "seamless":
                        app.SetSeamlessPlaybackRefresh(argument == "on");
                        break;
                    case "hitlane":
                        app.SetHitSoundLaneHidden(int.Parse(args[0], CultureInfo.InvariantCulture), args[1] == "on");
                        break;
                    case "audiotap":
                        AudioTap.Start(argument == "off" ? null : argument);
                        break;
                    case "skin":
                        app.HitObjects.SkinLibrary.Refresh();
                        app.HitObjects.SelectSkin(argument);
                        app.Settings.PreviewSkin = app.HitObjects.SkinId;
                        if (app.Settings.SeamlessPlaybackRefresh) app.InvalidateHitSoundEvents();
                        app.RefreshPlayback();
                        break;
                    case "hitobjects":
                        app.RenderOptions.ShowHitObjects = argument == "on";
                        app.RefreshPlayback();
                        break;
                    case "previewoptions":
                    {
                        var options = System.Text.Json.JsonSerializer.Deserialize<RenderOptions>(File.ReadAllText(argument),
                            new System.Text.Json.JsonSerializerOptions { IncludeFields = true })!;
                        app.RenderOptions.FailState = options.FailState;
                        app.RenderOptions.BackgroundDim = options.BackgroundDim;
                        app.RenderOptions.ShowBackground = options.ShowBackground;
                        app.RenderOptions.ShowVideo = options.ShowVideo;
                        app.RenderOptions.ShowHitObjects = options.ShowHitObjects;
                        break;
                    }
                    case "exportpreview":
                        app.Settings.VideoExport.ShowRenderPreview = argument == "on";
                        break;
                    case "exportvideo":
                    {
                        var request = System.Text.Json.JsonSerializer.Deserialize<VideoExportRequest>(File.ReadAllText(argument))!;
                        string? error = app.StartVideoExport(request);
                        if (error != null) throw new InvalidOperationException(error);
                        break;
                    }
                    case "markdirty":
                        app.MarkSettingsChanged();
                        break;
                    case "closeproject":
                        app.CloseProject();
                        break;
                    case "tryexportvideo":
                    {
                        var request = System.Text.Json.JsonSerializer.Deserialize<VideoExportRequest>(File.ReadAllText(argument))!;
                        string? error = app.StartVideoExport(request);
                        File.WriteAllText(argument + ".result.json", System.Text.Json.JsonSerializer.Serialize(new
                        {
                            Error = error, app.Dirty, app.Exporting, OutputExists = File.Exists(request.Path), PartExists = File.Exists(request.Path + ".part"),
                        }));
                        break;
                    }
                    case "dumpvideoexport":
                        File.WriteAllText(argument, System.Text.Json.JsonSerializer.Serialize(new
                        {
                            app.VideoExport?.Finished, app.VideoExport?.Error, app.VideoExport?.Frame,
                            app.Settings.VideoExport.ShowRenderPreview,
                            PreviewHitSounds = app.ScheduledSamples().Count(s => s.HitSound),
                            ExportHitSounds = app.ScheduledSamples(forExport: true).Count(s => s.HitSound),
                        }));
                        break;
                    case "recheck":
                        app.SetHitSoundRecheck(argument == "on");
                        break;
                    case "timingcheck":
                        app.SetTimingRecheck(argument == "on");
                        break;
                    case "timingsnap":
                        app.SetTimingSnapSound(argument == "on");
                        break;
                    case "timingbpm":
                        if (app.CurrentTimingOffset is { } point) app.SetTimingBpm(point, double.Parse(argument, CultureInfo.InvariantCulture));
                        break;
                    case "timingbrightness":
                        app.Settings.TimingBrightnessFeedback = argument == "on";
                        break;
                    case "timingmute":
                        app.SetTimingLaneHidden(argument == "on");
                        break;
                    case "timingsound":
                        app.SetTimingAssistSound(argument == "on");
                        break;
                    case "timingtempo":
                        app.SetTimingSoundFollowsTempo(argument == "on");
                        break;
                    case "timingslow":
                        app.SetTimingSlowPlayback(argument == "clock" ? TimingSlowPlaybackStyle.PlaybackClock : TimingSlowPlaybackStyle.Current);
                        break;
                    case "jumptiming":
                        app.JumpTimingOffset(int.Parse(argument, CultureInfo.InvariantCulture));
                        break;
                    case "audioreference":
                    {
                        var samples = app.ScheduledSamples().Where(s => (s.LaneMask & app.Audio.MutedLanes) == 0
                            && (!s.BypassTempo || app.Audio.ClickGain > 0)).ToArray();
                        File.WriteAllText(argument + ".schedule.json", System.Text.Json.JsonSerializer.Serialize(samples));
                        using var reference = app.Audio.OpenMixdown(0, samples);
                        using var file = File.Create(argument);
                        var data = new float[44100 * 2];
                        for (int second = 0; second < 90; second++)
                        {
                            reference.Read(data, 44100);
                            file.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()));
                        }
                        break;
                    }
                    case "dumptiming":
                        File.WriteAllText(argument, System.Text.Json.JsonSerializer.Serialize(new
                        {
                            Enabled = app.Settings.ShowTimingRecheck, HitSoundEnabled = app.Settings.ShowHitSoundRecheck,
                            Feedback = app.Settings.TimingBrightnessFeedback, Hidden = app.TimingLaneHidden,
                            SoundEnabled = app.Settings.TimingAssistSound,
                            app.Settings.SoundOffsetMs,
                            PreviewSamples = app.ScheduledSamples(),
                            app.PlaybackEndMs,
                            app.Settings.TimingSoundFollowsTempo,
                            app.Audio.PlayingTimingSoundFollowsTempo,
                            TimingSlowPlayback = app.Settings.TimingSlowPlayback.ToString(),
                            app.Audio.PlayingClockTimingSlowdown,
                            app.Audio.PlayingTrialTiming, app.Audio.TrialTimingTriggers, app.Audio.TrialTimingLastTrigger,
                            app.Rate,
                            SeamlessRefresh = app.Settings.SeamlessPlaybackRefresh,
                            app.Audio.LiveUpdates, app.Audio.Rebuilds, app.Audio.MutedLanes, app.Audio.ClickGain,
                            app.Audio.OutputBufferMilliseconds, app.Audio.DeviceLatencyMilliseconds, app.Audio.PeakAudioCallbackMilliseconds,
                            app.Audio.TimingMusicReady, app.Audio.TimingMusicBytes, app.Audio.UsingTimingMusicCache,
                            app.Time, Bpm = app.HasTiming ? 60000 / app.BeatLength : (double?)null,
                            PreviousOffset = app.TimingOffsetTarget(-1), NextOffset = app.TimingOffsetTarget(1),
                            PlayheadOffset = app.TimingOffsetAtPlayhead?.Offset,
                            TimingAction = app.TimingOffsetAtPlayhead == null ? "Add" : "Delete",
                            Offsets = app.Project?.Settings.Timing.Select(p => p.Offset).ToArray(),
                            app.Playing, app.Audio.Error, app.Audio.AuditionCount,
                            Audition = app.Audio.LastAudition,
                            AuditionActive = app.Audio.IsAuditioning,
                            Scheduled = app.ScheduledSamples().Where(s => s.HitSound).ToArray(),
                            Active = app.Audio.ScheduledHitSoundCount,
                            PlayingClicks = app.Audio.ScheduledClickTimes,
                            Export = app.ScheduledSamples(forExport: true),
                            Events = app.TimingRecheckEvents.Events,
                        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                        break;
                    case "dumpplayback":
                        File.WriteAllText(argument, $"playing={app.Playing}\nvisible={app.RenderOptions.ShowHitObjects}\nrecheck={app.Settings.ShowHitSoundRecheck}\n"
                            + $"scheduled={app.ScheduledSamples().Count(s => s.HitSound)}\nactive={app.Audio.ScheduledHitSoundCount}\nexport={app.ScheduledSamples(forExport: true).Count(s => s.HitSound)}\n"
                            + $"storyboard={app.ScheduledSamples().Count(s => !s.HitSound)}\nactiveStoryboard={app.Audio.ScheduledStoryboardSampleCount}\nexportStoryboard={app.ScheduledSamples(forExport: true).Count(s => !s.HitSound)}\n");
                        break;
                    case "dumprecheck":
                        File.WriteAllText(argument, ui.TimelineRecheckState);
                        break;
                    case "difficulty":
                        app.SelectDifficulty(argument);
                        break;
                    case "wait":
                        waitStarted = clock.Elapsed.TotalMilliseconds;
                        waitUntil = waitStarted + double.Parse(argument, CultureInfo.InvariantCulture) * 1000;
                        break;
                    case "waitfullscreenhidden":
                        waitStarted = clock.Elapsed.TotalMilliseconds;
                        waitForHidden = true;
                        break;
                    case "linkosb":
                    {
                        var paths=System.Text.RegularExpressions.Regex.Matches(argument,"\"([^\"]*)\"|([^ ]+)").Select(m=>m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value).ToArray();
                        app.LinkOsb(paths[0],paths.Length>1?paths[1]:null);break;
                    }
                    case "waitplugins": waitStarted=clock.Elapsed.TotalMilliseconds;waitForPlugins=true;break;
                    case "waiteffects": waitStarted=clock.Elapsed.TotalMilliseconds;waitForEffects=true;break;
                    case "effectprofile": app.ConfigureEffectsForTest(argument, Path.Combine(AppContext.BaseDirectory,"effects","host","SbStudio.Effects.Host.exe")); app.RefreshEffectLibrary(); break;
                    case "effectadd": app.RefreshEffectLibrary(); app.AddEffect(app.EffectLibrary.Single(e=>e.Definition.EffectId==argument)); break;
                    case "effectparam":
                        if(app.SelectedGroup is {} effectGroup)
                        { int split=argument.IndexOf(' '); using var json=System.Text.Json.JsonDocument.Parse(argument[(split+1)..]); app.SetEffectParameter(effectGroup,argument[..split],json.RootElement); }
                        break;
                    case "effectsource": File.WriteAllText(argument,app.CreateDevelopmentEffect()); break;
                    case "effectdialog":
                        { int split = argument.IndexOf(' '); EffectFileDialogs.Enqueue(argument[..split], argument[(split + 1)..]); break; }
                    case "pointui":
                        mouse = UiScriptTargets.Point(argument) - origin; break;
                    case "variablespanel": ui.ShowVariables(); break;
                    case "dumpvariables":
                        File.WriteAllText(argument, System.Text.Json.JsonSerializer.Serialize(new { Variables = app.Doc.Variables,
                            Names = app.Doc.Variables.Select(StoryboardVariables.DisplayName), Positions = app.Doc.Sprites.Select(s => new { s.X, s.Y }),
                            app.Dirty, app.HistoryPosition })); break;
                    case "offsetmouse":
                        { var xy = argument.Split(' '); mouse += new Vector2(float.Parse(xy[0], CultureInfo.InvariantCulture), float.Parse(xy[1], CultureInfo.InvariantCulture)); break; }
                    case "effectload": app.AddDevelopmentEntries(app.LoadDevelopmentEffectAsync(argument, true).GetAwaiter().GetResult()); break;
                    case "effectpack": app.PackEffectAsync(app.EffectLibrary.Single(e => e.SourceKind == "development"), argument).GetAwaiter().GetResult(); break;
                    case "exportosb":
                        if (app.Project != null) Exporter.Export(app.Project.ExportSnapshot(), new() { OutputFolder = argument, FileName = "storyboard.osb", Zip = true });
                        break;
                    case "dumpeffects": File.WriteAllText(argument,System.Text.Json.JsonSerializer.Serialize(app.EffectLibrary)); break;
                    case "selectplugin": app.SelectGroup(app.Doc.Groups.Single(g=>g.Plugin?.Instance==int.Parse(argument)));break;
                    case "dumpplugins": File.WriteAllText(argument,app.DumpPlugins());break;
                    case "resetperformance": worstFrameMs=0; break;
                    case "dumpperformance":
                        using (var process = System.Diagnostics.Process.GetCurrentProcess())
                            File.WriteAllText(argument,System.Text.Json.JsonSerializer.Serialize(new { frameMs,worstFrameMs,UiMs,DrawMs,
                                Sections=Perf.Report(),ManagedBytes=GC.GetTotalMemory(false),ProcessBytes=process.WorkingSet64,Handles=process.HandleCount }));
                        break;
                    case "pluginaction":
                        if(app.SelectedGroup is {} pluginGroup)
                        {
                            if(argument=="detach") app.DetachPlugin(pluginGroup);
                            else if(argument=="detachcopy") app.DetachPluginCopy(pluginGroup);
                            else if(argument=="reimport") app.ReimportPlugin(pluginGroup);
                        }
                        break;
                    case "fullscreen":
                        ui.SetFullscreen(argument == "on");
                        break;
                    case "dumprows":
                        File.WriteAllText(argument, ui.TimelineRowsState);
                        break;
                    case "dumpspace":
                        app.DumpSpace(argument);
                        break;
                    case "dumpfullscreen":
                        File.WriteAllText(argument, ui.FullscreenState);
                        break;
                    case "dumplayout":
                        File.WriteAllText(argument, ImGui.SaveIniSettingsToMemory());
                        break;
                    case "dumpsettings":
                        File.WriteAllText(argument, System.Text.Json.JsonSerializer.Serialize(new { Settings = app.Project?.Settings, Dirty = app.Dirty, app.Settings.FullscreenEditing, app.Settings.FullscreenDebugPanel, app.Settings.TimelineAutoJump, app.Settings.OutlineScope, app.Settings.RecentProjects }));
                        break;
                    case "clipboardtime":
                        ui.SetScriptedClipboard(argument);
                        break;
                    case "osuindicator":
                        app.Settings.OsuStyleTimelineIndicator = argument == "on";
                        break;
                    case "snap":
                        app.Settings.Snap = argument == "on";
                        break;
                    case "divisor":
                        if (app.Project != null) app.Project.Settings.BeatDivisor = int.Parse(argument, CultureInfo.InvariantCulture);
                        break;
                    case "volume":
                        app.Audio.Volume = float.Parse(argument, CultureInfo.InvariantCulture);
                        break;
                    case "brightness":
                        app.Settings.HitSoundBrightnessFeedback = argument == "on";
                        break;
                    case "clocklog":
                        clockLog?.Dispose();
                        clockLog = argument == "off" ? null : new StreamWriter(argument) { AutoFlush = true };
                        break;
                    case "play":
                        if (argument == "on") app.Play(); else app.Pause();
                        break;
                    case "rate":
                        app.SetRate(double.Parse(argument, CultureInfo.InvariantCulture));
                        break;
                    case "dumpviewport":
                        File.WriteAllText(argument, ui.TimelineViewportState);
                        break;
                    case "dumptimingcopy":
                        File.WriteAllText(argument, ui.CopiedTimingCode);
                        break;
                    case "timinglistimport":
                        ui.ImportTimingListFile(argument);
                        break;
                    case "dumphitselection":
                        File.WriteAllText(argument, ui.TimelineHitObjectSelectionState);
                        break;
                    case "dumphitsounds":
                        File.WriteAllText(argument, System.Text.Json.JsonSerializer.Serialize(app.HitSoundEvents.Events,
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true,
                                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
                        break;
                    case "drop":
                        ui.OnFilesDropped(new[] { argument });
                        break;
                    case "select":
                        if (app.Project != null)
                        {
                            int index = int.Parse(args[0], CultureInfo.InvariantCulture);
                            app.SelectOnly(app.Doc.Objects[index]);
                        }
                        break;
                    case "selectadd":
                        if (app.Project != null)
                            app.AddToSelection(app.Doc.Objects[int.Parse(args[0], CultureInfo.InvariantCulture)]);
                        break;
                    case "seek":
                        app.Seek(double.Parse(args[0], CultureInfo.InvariantCulture));
                        break;
                    case "frame":
                        // frame height file: what the screenshot button saves, without the window that asks where.
                        app.SaveFrame(argument.Substring(args[0].Length).Trim(), int.Parse(args[0], CultureInfo.InvariantCulture));
                        break;
                    case "dumpsel":
                        // The selected objects as script, with the selected keyframes: enough to check an edit by.
                        if (app.Project != null)
                            File.WriteAllText(argument,
                                $"time={app.Time:0}\nkeys={string.Join(" ", app.SelectedKeys.Select(k => k.Kind + "#" + k.Index))}\n" +
                                $"track={app.ActiveTrack?.Kind}\ncommands={app.SelectedCommands.Count}\ncanUndo={app.CanUndo}\n" + app.SelectionScript());
                        break;
                    case "history":
                        app.GoToHistory(int.Parse(argument, CultureInfo.InvariantCulture));
                        break;
                    case "simulation":
                        ui.ShowTriggerSimulation();
                        break;
                    case "uiscale":
                        ui.SetUiScale(float.Parse(argument, CultureInfo.InvariantCulture));
                        break;
                    case "dumpscale":
                        File.WriteAllText(argument, System.Text.Json.JsonSerializer.Serialize(new
                        {
                            Requested = app.Settings.UiScale,
                            Actual = Theme.Scale,
                            MainFont = Theme.MainFont.FontSize,
                            MonoFont = Theme.MonoFont.FontSize,
                            Fonts = ImGui.GetIO().Fonts.Fonts.Size,
                            Padding = ImGui.GetStyle().FramePadding.X,
                            CellPadding = ImGui.GetStyle().CellPadding.X,
                            Viewports = ImGui.GetPlatformIO().Viewports.Size,
                            app.Dirty,
                            app.HistoryPosition,
                            Message = app.PendingMessage?.Text,
                        }));
                        break;
                    case "dumpsimulation":
                        if (app.Project != null)
                            File.WriteAllText(argument, System.Text.Json.JsonSerializer.Serialize(new
                            {
                                app.Time,
                                Enabled = app.Project.Settings.TriggerSimulationRules.Any(r => r.Enabled),
                                EventCount = app.TriggerSimulation.Stream.Events.Count,
                                HasStateRules = app.TriggerSimulation.Stream.HasStateRules,
                                Events = app.TriggerSimulation.Stream.Events,
                                Reports = app.SimulationReports.Select(r => new { r.Sprite.Path, r.Trigger.Name, Status = r.Status.ToString(), r.Fires, Notes = r.Notes.Select(n => new { Note = n.Note.ToString(), n.Detail }) }),
                                Rules = app.Project.Settings.TriggerSimulationRules,
                                app.Dirty,
                                app.CanUndo,
                                SelectedTrigger = (app.SelectedBlock as SbTrigger)?.Name,
                                Manager = ui.TriggerSimulationUiState,
                                Samples = app.ScheduledSamples().Select(s => new { s.TimeMs, s.File }),
                                Fail = app.TriggerSimulation.Stream.HasStateRules ? app.TriggerSimulation.FailStateAt(app.Time) : app.RenderOptions.FailState,
                                States = app.Doc.Sprites.Select(s => new
                                {
                                    s.Path,
                                    Active = s.Timeline.IsActive(app.Time),
                                    State = app.PreviewState(s, app.Time),
                                }).ToArray(),
                            }, new System.Text.Json.JsonSerializerOptions { IncludeFields = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
                        break;
                    case "historylimit":
                        app.SetHistoryLimit(int.Parse(argument, CultureInfo.InvariantCulture));
                        break;
                    case "dumphistory":
                        File.WriteAllText(argument, System.Text.Json.JsonSerializer.Serialize(new
                        {
                            Position = app.HistoryPosition,
                            Count = app.HistoryCount,
                            app.HistoryLimit,
                            app.CanUndo,
                            app.CanRedo,
                            app.Dirty,
                            app.Title,
                            Entries = app.HistoryEntries,
                        }));
                        break;
                    case "dump":
                        if (app.Project != null)
                            File.WriteAllText(argument,
                                $"time={app.Time:0}\nselection={string.Join(",", app.Selection.Select(o => app.Doc.Objects.IndexOf(o)))}" +
                                $" group={(app.SelectedGroup == null ? -1 : app.Doc.Groups.IndexOf(app.SelectedGroup))}\n" +
                                $"objects={app.Doc.Objects.Count}\ncommands={app.SelectedCommands.Count}\ncanUndo={app.CanUndo}\nplaying={app.Playing}\n" +
                                $"beatmapDirectory={app.Project.BeatmapDirectory}\ndifficulty={app.Project.Settings.Difficulty}\n" +
                                $"difficulties={string.Join("|", app.Project.Difficulties.Select(d => d.File))}\n" +
                                $"beatmapFirst={app.Beatmap?.Objects.FirstOrDefault()?.StartTime}\n" +
                                $"beatmapObjects={app.Beatmap?.Objects.Count ?? 0}\n" +
                                $"audioOutputDevice={app.Audio.OutputDeviceId}\naudioDefaultOutput={app.Audio.OutputDevices.FirstOrDefault(d => d.IsDefault)?.Name}\naudioError={app.Audio.Error}\n" +
                                $"hitSoundVolume={app.Audio.HitSoundVolume}\nmusicVolume={app.Audio.MusicVolume}\npreviewSkin={app.HitObjects.SkinId}\n" +
                                $"hitSoundRecheck={app.Settings.ShowHitSoundRecheck}\n" +
                                $"waveformStyle={app.Settings.WaveformStyle}\n" +
                                $"timingRecheck={app.Settings.ShowTimingRecheck}\ntimingBrightnessFeedback={app.Settings.TimingBrightnessFeedback}\n" +
                                $"hitSoundBrightnessFeedback={app.Settings.HitSoundBrightnessFeedback}\n" +
                                $"auditions={app.Audio.AuditionCount}\nauditionActive={app.Audio.IsAuditioning}\n" +
                                $"auditionFiles={string.Join("|", app.Audio.LastAudition.Select(s => s.File))}\n" +
                                $"insertionLayer={app.InsertionLayer}\n" +
                                $"timelineSection={app.SelectedTimelineSection}\n" +
                                $"frameMs={frameMs:0.0} worstFrameMs={worstFrameMs:0.0} uiMs={UiMs:0.00} drawMs={DrawMs:0.00} {Perf.Report()}\n" +
                                OsbWriter.WriteFile(app.Doc, new OsbWriteOptions { IncludeEditorMeta = true }));
                        break;
                }
            }

            foreach (var (releaseFrame, key) in keyReleases)
                if (releaseFrame == frame) io.AddKeyEvent(key, false);

            foreach (var (key, on) in modifiers) io.AddKeyEvent(key, on);
            io.AddMousePosEvent(mouse.X + origin.X, mouse.Y + origin.Y);
            for (int b = 0; b < buttons.Length; b++) io.AddMouseButtonEvent(b, buttons[b]);
        }
    }

    /// <summary>Gives the window the program's icon, in the sizes Windows picks from for the title bar and taskbar.</summary>
    private static void SetWindowIcon(IWindow window)
    {
        var assembly = typeof(Program).Assembly;
        var images = new List<Silk.NET.Core.RawImage>();
        foreach (int size in new[] { 16, 32, 48, 256 })
        {
            using Stream? stream = assembly.GetManifestResourceStream($"AppIcon/icon-{size}.png");
            if (stream == null) continue;
            var image = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            images.Add(new Silk.NET.Core.RawImage(image.Width, image.Height, image.Data));
        }
        if (images.Count > 0) window.SetWindowIcon(images.ToArray());
    }
}
