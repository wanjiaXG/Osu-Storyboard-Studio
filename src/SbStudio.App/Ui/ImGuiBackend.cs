using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using SbStudio.App.Gfx;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Glfw = Silk.NET.GLFW.Glfw;
using GlfwWindow = Silk.NET.GLFW.WindowHandle;
using WindowAttributeGetter = Silk.NET.GLFW.WindowAttributeGetter;
using WindowAttributeSetter = Silk.NET.GLFW.WindowAttributeSetter;

namespace SbStudio.App.Ui;

/// <summary>
/// Connects Dear ImGui to the windows, the input devices and OpenGL. It takes the place of the controller
/// that comes with Silk.NET, which can only draw into one window: here a panel dragged out of the main
/// window becomes a window of its own (an ImGui "viewport") that can sit on another screen, and merges
/// back when it is dragged in again.
/// </summary>
public sealed unsafe class ImGuiBackend : IDisposable
{
    /// <summary>A window ImGui draws into: the main one, or one made for a viewport.</summary>
    private sealed class Host
    {
        public required IWindow Window;
        public IInputContext? Input;
        /// <summary>Made here for a viewport, and closed again when that viewport goes away.</summary>
        public bool Owned;
        public uint Viewport;
        public GCHandle Handle;
        /// <summary>Frames in which ImGui itself moved or resized the window; what the system reports back then is not news.</summary>
        public int MovedInFrame = -1, ResizedInFrame = -1;
        /// <summary>The window this one stays in front of (Windows only).</summary>
        public nint Owner;
        public readonly HashSet<Key> KeysDown = new();

        public GlfwWindow* Native => (GlfwWindow*)Window.Handle;
    }

    /// <summary>What a viewport window showed in a captured frame. The position is relative to the main window.</summary>
    public readonly record struct Shot(int X, int Y, int Width, int Height, byte[] Pixels);

    private const string VertexSource = @"#version 330 core
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec4 aColor;
uniform mat4 uProjection;
out vec2 vUv;
out vec4 vColor;
void main()
{
    vUv = aUv;
    vColor = aColor;
    gl_Position = uProjection * vec4(aPos, 0.0, 1.0);
}";

    private const string FragmentSource = @"#version 330 core
in vec2 vUv;
in vec4 vColor;
uniform sampler2D uTex;
out vec4 oColor;
void main()
{
    oColor = vColor * texture(uTex, vUv);
}";

    // GLFW 3.4 window attribute that the binding has no name for yet.
    private const WindowAttributeSetter MousePassthrough = (WindowAttributeSetter)0x0002000D;

    /// <summary>The one backend of the program, for the callbacks ImGui makes into static functions.</summary>
    private static ImGuiBackend? current;

    private readonly GL gl;
    private readonly Glfw glfw;
    private readonly nint context;
    private readonly Host main;
    private readonly List<Host> hosts = new();
    private readonly List<Shot> shots = new();
    private readonly bool realInput;
    private readonly bool viewports;
    /// <summary>Windows can let the mouse through, which is what telling ImGui the window under the mouse needs.</summary>
    private readonly bool passthrough;

    private readonly ShaderProgram shader;
    private readonly int projectionLocation, textureLocation;
    private readonly uint vertexBuffer, indexBuffer;
    private uint fontTexture;

    private ImGuiMouseCursor shownCursor = ImGuiMouseCursor.Arrow;
    private int buttonsDown;
    private double monitorsAge;
    private Exception? failure;
    private bool disposed;

    /// <summary>Files were dropped on any of the windows.</summary>
    public event Action<string[]>? FilesDropped;

    /// <summary>The secondary windows of the last frame that was drawn with capture on.</summary>
    public IReadOnlyList<Shot> Shots => shots;

    /// <summary>
    /// Where the main window's content starts in ImGui's coordinates: the screen position while panels can
    /// leave the window, zero otherwise.
    /// </summary>
    public Vector2 Origin
    {
        get
        {
            if (!viewports) return Vector2.Zero;
            var position = main.Window.Position;
            return new Vector2(position.X, position.Y);
        }
    }

    /// <param name="realInput">Feed ImGui from the mouse and keyboard. Off, the caller queues the input itself before each frame.</param>
    /// <param name="configure">Called once the ImGui context exists and before the font atlas is built.</param>
    public ImGuiBackend(GL gl, IWindow window, IInputContext input, bool realInput, Action configure)
    {
        this.gl = gl;
        this.realInput = realInput;
        glfw = Silk.NET.GLFW.GlfwProvider.GLFW.Value;
        current = this;

        context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);
        configure();

        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.HasMouseCursors;

        shader = new ShaderProgram(gl, VertexSource, FragmentSource);
        projectionLocation = shader.Uniform("uProjection");
        textureLocation = shader.Uniform("uTex");
        vertexBuffer = gl.GenBuffer();
        indexBuffer = gl.GenBuffer();
        fontTexture = CreateFontTexture(io);

        main = new Host { Window = window, Input = input };
        Bind(main, ImGui.GetMainViewport());
        if (realInput) Listen(main);

        viewports = (io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0;
        if (!viewports) return;

        glfw.GetVersion(out int major, out int minor, out _);
        passthrough = realInput && (major > 3 || (major == 3 && minor >= 4));
        io.BackendFlags |= ImGuiBackendFlags.PlatformHasViewports | ImGuiBackendFlags.RendererHasViewports;
        if (passthrough) io.BackendFlags |= ImGuiBackendFlags.HasMouseHoveredViewport;

        var platform = ImGui.GetPlatformIO();
        platform.Platform_CreateWindow = (nint)(delegate* unmanaged[Cdecl]<ImGuiViewport*, void>)&OnCreateWindow;
        platform.Platform_DestroyWindow = (nint)(delegate* unmanaged[Cdecl]<ImGuiViewport*, void>)&OnDestroyWindow;
        platform.Platform_ShowWindow = (nint)(delegate* unmanaged[Cdecl]<ImGuiViewport*, void>)&OnShowWindow;
        platform.Platform_SetWindowPos = (nint)(delegate* unmanaged[Cdecl]<ImGuiViewport*, Vector2, void>)&OnSetWindowPos;
        platform.Platform_SetWindowSize = (nint)(delegate* unmanaged[Cdecl]<ImGuiViewport*, Vector2, void>)&OnSetWindowSize;
        platform.Platform_SetWindowFocus = (nint)(delegate* unmanaged[Cdecl]<ImGuiViewport*, void>)&OnSetWindowFocus;
        platform.Platform_GetWindowFocus = (nint)(delegate* unmanaged[Cdecl]<ImGuiViewport*, byte>)&OnGetWindowFocus;
        platform.Platform_GetWindowMinimized = (nint)(delegate* unmanaged[Cdecl]<ImGuiViewport*, byte>)&OnGetWindowMinimized;
        platform.Platform_SetWindowTitle = (nint)(delegate* unmanaged[Cdecl]<ImGuiViewport*, byte*, void>)&OnSetWindowTitle;
        // These two return a vector, which C++ and C hand back differently; cimgui takes them in a form that is safe.
        ImGuiNative.ImGuiPlatformIO_Set_Platform_GetWindowPos(platform.NativePtr,
            (nint)(delegate* unmanaged[Cdecl]<ImGuiViewport*, Vector2*, void>)&OnGetWindowPos);
        ImGuiNative.ImGuiPlatformIO_Set_Platform_GetWindowSize(platform.NativePtr,
            (nint)(delegate* unmanaged[Cdecl]<ImGuiViewport*, Vector2*, void>)&OnGetWindowSize);
        ReadMonitors();
    }

    // ---------------------------------------------------------------- frame

    /// <summary>Called between frames; shared viewport contexts use the replacement atlas too.</summary>
    public void RebuildFonts(Action rebuild)
    {
        rebuild();
        uint previous = fontTexture;
        fontTexture = CreateFontTexture(ImGui.GetIO());
        gl.DeleteTexture(previous);
    }

    /// <summary>Starts an ImGui frame. With real input off, the caller has queued the frame's input already.</summary>
    public void NewFrame(float delta)
    {
        var io = ImGui.GetIO();
        var size = main.Window.Size;
        var pixels = main.Window.FramebufferSize;
        io.DisplaySize = new Vector2(size.X, size.Y);
        if (size.X > 0 && size.Y > 0) io.DisplayFramebufferScale = new Vector2((float)pixels.X / size.X, (float)pixels.Y / size.Y);
        io.DeltaTime = delta;

        if (viewports)
        {
            // Screens come and go, and the taskbar moves, rarely: looking once a second is plenty.
            monitorsAge += delta;
            if (monitorsAge >= 1) ReadMonitors();
        }
        if (realInput)
        {
            ReadModifiers(io);
            ReadMouse(io);
        }

        ImGui.NewFrame();
        Rethrow();
    }

    /// <summary>
    /// Ends the frame and draws it: the main window's part into the bound framebuffer, then every viewport
    /// into its own window. The main window's context is current again afterwards.
    /// </summary>
    /// <param name="capture">Keep what the viewport windows show in <see cref="Shots"/>.</param>
    public void Render(bool capture = false)
    {
        ShowCursor();
        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData(), true);
        shots.Clear();
        if (!viewports) return;

        ImGui.UpdatePlatformWindows();
        Rethrow();

        var origin = Origin;
        var list = ImGui.GetPlatformIO().Viewports;
        bool switched = false;
        for (int i = 1; i < list.Size; i++)
        {
            var viewport = list[i];
            if ((viewport.Flags & ImGuiViewportFlags.IsMinimized) != 0 || HostOf(viewport) is not { } host) continue;
            SetOwner(host, viewport);

            // What the main context drew this frame (the preview) must have reached the textures the others read.
            if (!switched) gl.Flush();
            switched = true;
            host.Window.GLContext!.MakeCurrent();
            var area = host.Window.FramebufferSize;
            gl.Viewport(0, 0, (uint)area.X, (uint)area.Y);
            gl.Disable(EnableCap.ScissorTest);
            gl.ClearColor(0.08f, 0.08f, 0.09f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            RenderDrawData(viewport.DrawData, false);
            if (capture && area.X > 0 && area.Y > 0)
                shots.Add(new Shot((int)(viewport.Pos.X - origin.X), (int)(viewport.Pos.Y - origin.Y), area.X, area.Y, ReadBackBuffer(area)));
            host.Window.GLContext.SwapBuffers();
        }
        if (switched) main.Window.GLContext!.MakeCurrent();
    }

    private void Rethrow()
    {
        if (failure is not { } error) return;
        failure = null;
        ExceptionDispatchInfo.Capture(error).Throw();
    }

    private byte[] ReadBackBuffer(Vector2D<int> size)
    {
        var raw = new byte[size.X * size.Y * 4];
        gl.ReadBuffer(ReadBufferMode.Back);
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        fixed (byte* p = raw)
            gl.ReadPixels(0, 0, (uint)size.X, (uint)size.Y, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        return RenderTarget.FlipRows(raw, size.X, size.Y);
    }

    // ---------------------------------------------------------------- input

    private void Listen(Host host)
    {
        foreach (var keyboard in host.Input!.Keyboards)
        {
            keyboard.KeyDown += (_, key, _) => OnKey(host, key, true);
            keyboard.KeyUp += (_, key, _) => OnKey(host, key, false);
            keyboard.KeyChar += (_, character) => ImGui.GetIO().AddInputCharacterUTF16(character);
        }
        foreach (var mouse in host.Input.Mice)
        {
            mouse.MouseDown += (_, button) => OnButton(button, true);
            mouse.MouseUp += (_, button) => OnButton(button, false);
            mouse.Scroll += (_, wheel) => ImGui.GetIO().AddMouseWheelEvent(wheel.X, wheel.Y);
            ShowCursor(host, shownCursor);
        }
        host.Window.FocusChanged += focused => ImGui.GetIO().AddFocusEvent(focused);
        host.Window.FileDrop += paths => FilesDropped?.Invoke(paths);
    }

    private static void OnKey(Host host, Key key, bool down)
    {
        if (down) host.KeysDown.Add(key);
        else host.KeysDown.Remove(key);
        var mapped = MapKey(key);
        if (mapped != ImGuiKey.None) ImGui.GetIO().AddKeyEvent(mapped, down);
    }

    private void OnButton(MouseButton button, bool down)
    {
        int index = button switch
        {
            MouseButton.Left => 0,
            MouseButton.Right => 1,
            MouseButton.Middle => 2,
            MouseButton.Button4 => 3,
            MouseButton.Button5 => 4,
            _ => -1,
        };
        if (index < 0) return;
        buttonsDown = down ? buttonsDown | 1 << index : buttonsDown & ~(1 << index);
        ImGui.GetIO().AddMouseButtonEvent(index, down);
    }

    /// <summary>
    /// ImGui derives Ctrl/Shift/Alt from dedicated modifier key events, which the key events above do not
    /// include. Without them no Ctrl shortcut would work, so they are reported here every frame, from the
    /// window that has the keyboard.
    /// </summary>
    private void ReadModifiers(ImGuiIOPtr io)
    {
        IKeyboard? keyboard = null;
        foreach (var host in hosts)
            if (host.Input is { Keyboards.Count: > 0 } input && glfw.GetWindowAttrib(host.Native, WindowAttributeGetter.Focused))
                keyboard = input.Keyboards[0];

        bool Down(Key left, Key right) => keyboard != null && (keyboard.IsKeyPressed(left) || keyboard.IsKeyPressed(right));
        io.AddKeyEvent(ImGuiKey.ModCtrl, Down(Key.ControlLeft, Key.ControlRight));
        io.AddKeyEvent(ImGuiKey.ModShift, Down(Key.ShiftLeft, Key.ShiftRight));
        io.AddKeyEvent(ImGuiKey.ModAlt, Down(Key.AltLeft, Key.AltRight));
        io.AddKeyEvent(ImGuiKey.ModSuper, Down(Key.SuperLeft, Key.SuperRight));
    }

    private void ReadMouse(ImGuiIOPtr io)
    {
        uint hovered = 0;
        bool over = false;
        var list = ImGui.GetPlatformIO().Viewports;
        for (int i = 0; i < list.Size; i++)
        {
            var viewport = list[i];
            if (HostOf(viewport) is not { } host) continue;
            // A window that is being dragged lets the mouse through, so that the window under it is the
            // one found here: that is where the dragged one can be docked.
            if (passthrough) glfw.SetWindowAttrib(host.Native, MousePassthrough, (viewport.Flags & ImGuiViewportFlags.NoInputs) != 0);
            if (!glfw.GetWindowAttrib(host.Native, WindowAttributeGetter.Hovered)) continue;
            hovered = viewport.ID;
            over = true;
        }
        if (passthrough) io.AddMouseViewportEvent(hovered);

        // Away from the windows the mouse is nowhere, unless it is in the middle of dragging something.
        if ((over || buttonsDown != 0) && main.Input!.Mice.Count > 0)
        {
            var position = main.Input.Mice[0].Position + Origin;
            io.AddMousePosEvent(MathF.Floor(position.X), MathF.Floor(position.Y));
        }
        else
        {
            io.AddMousePosEvent(-float.MaxValue, -float.MaxValue);
        }
    }

    /// <summary>Shows the cursor shape the UI asked for (resize arrows over edges and splitters, I-beam over text).</summary>
    private void ShowCursor()
    {
        if (!realInput || (ImGui.GetIO().ConfigFlags & ImGuiConfigFlags.NoMouseCursorChange) != 0) return;
        var wanted = ImGui.GetMouseCursor();
        if (wanted == shownCursor) return;
        shownCursor = wanted;
        foreach (var host in hosts) ShowCursor(host, wanted);
    }

    private static void ShowCursor(Host host, ImGuiMouseCursor cursor)
    {
        if (host.Input == null || host.Input.Mice.Count == 0) return;
        host.Input.Mice[0].Cursor.CursorMode = cursor == ImGuiMouseCursor.None ? CursorMode.Hidden : CursorMode.Normal;
        if (cursor == ImGuiMouseCursor.None) return;
        host.Input.Mice[0].Cursor.StandardCursor = cursor switch
        {
            ImGuiMouseCursor.TextInput => StandardCursor.IBeam,
            ImGuiMouseCursor.ResizeAll => StandardCursor.ResizeAll,
            ImGuiMouseCursor.ResizeEW => StandardCursor.HResize,
            ImGuiMouseCursor.ResizeNS => StandardCursor.VResize,
            ImGuiMouseCursor.ResizeNESW => StandardCursor.NeswResize,
            ImGuiMouseCursor.ResizeNWSE => StandardCursor.NwseResize,
            ImGuiMouseCursor.Hand => StandardCursor.Hand,
            ImGuiMouseCursor.NotAllowed => StandardCursor.NotAllowed,
            _ => StandardCursor.Arrow,
        };
    }

    private static ImGuiKey MapKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ImGuiKey.A + (key - Key.A),
        >= Key.Number0 and <= Key.Number9 => ImGuiKey._0 + (key - Key.Number0),
        >= Key.F1 and <= Key.F24 => ImGuiKey.F1 + (key - Key.F1),
        >= Key.Keypad0 and <= Key.Keypad9 => ImGuiKey.Keypad0 + (key - Key.Keypad0),
        Key.Tab => ImGuiKey.Tab,
        Key.Left => ImGuiKey.LeftArrow,
        Key.Right => ImGuiKey.RightArrow,
        Key.Up => ImGuiKey.UpArrow,
        Key.Down => ImGuiKey.DownArrow,
        Key.PageUp => ImGuiKey.PageUp,
        Key.PageDown => ImGuiKey.PageDown,
        Key.Home => ImGuiKey.Home,
        Key.End => ImGuiKey.End,
        Key.Insert => ImGuiKey.Insert,
        Key.Delete => ImGuiKey.Delete,
        Key.Backspace => ImGuiKey.Backspace,
        Key.Space => ImGuiKey.Space,
        Key.Enter => ImGuiKey.Enter,
        Key.Escape => ImGuiKey.Escape,
        Key.Apostrophe => ImGuiKey.Apostrophe,
        Key.Comma => ImGuiKey.Comma,
        Key.Minus => ImGuiKey.Minus,
        Key.Period => ImGuiKey.Period,
        Key.Slash => ImGuiKey.Slash,
        Key.Semicolon => ImGuiKey.Semicolon,
        Key.Equal => ImGuiKey.Equal,
        Key.LeftBracket => ImGuiKey.LeftBracket,
        Key.BackSlash => ImGuiKey.Backslash,
        Key.RightBracket => ImGuiKey.RightBracket,
        Key.GraveAccent => ImGuiKey.GraveAccent,
        Key.CapsLock => ImGuiKey.CapsLock,
        Key.ScrollLock => ImGuiKey.ScrollLock,
        Key.NumLock => ImGuiKey.NumLock,
        Key.PrintScreen => ImGuiKey.PrintScreen,
        Key.Pause => ImGuiKey.Pause,
        Key.KeypadDecimal => ImGuiKey.KeypadDecimal,
        Key.KeypadDivide => ImGuiKey.KeypadDivide,
        Key.KeypadMultiply => ImGuiKey.KeypadMultiply,
        Key.KeypadSubtract => ImGuiKey.KeypadSubtract,
        Key.KeypadAdd => ImGuiKey.KeypadAdd,
        Key.KeypadEnter => ImGuiKey.KeypadEnter,
        Key.KeypadEqual => ImGuiKey.KeypadEqual,
        Key.ShiftLeft => ImGuiKey.LeftShift,
        Key.ControlLeft => ImGuiKey.LeftCtrl,
        Key.AltLeft => ImGuiKey.LeftAlt,
        Key.SuperLeft => ImGuiKey.LeftSuper,
        Key.ShiftRight => ImGuiKey.RightShift,
        Key.ControlRight => ImGuiKey.RightCtrl,
        Key.AltRight => ImGuiKey.RightAlt,
        Key.SuperRight => ImGuiKey.RightSuper,
        Key.Menu => ImGuiKey.Menu,
        _ => ImGuiKey.None,
    };

    // ---------------------------------------------------------------- viewport windows

    private static Host? HostOf(ImGuiViewportPtr viewport) =>
        viewport.PlatformUserData == 0 ? null : (Host?)GCHandle.FromIntPtr(viewport.PlatformUserData).Target;

    private void Bind(Host host, ImGuiViewportPtr viewport)
    {
        host.Viewport = viewport.ID;
        host.Handle = GCHandle.Alloc(host);
        viewport.PlatformUserData = GCHandle.ToIntPtr(host.Handle);
        viewport.PlatformHandle = host.Window.Handle;
        // The native handle is what ImGui places the input method's candidate list with.
        if (OperatingSystem.IsWindows() && host.Window.Native?.Win32 is { } win32) viewport.PlatformHandleRaw = win32.Hwnd;
        hosts.Add(host);
    }

    private void CreateWindow(ImGuiViewportPtr viewport)
    {
        // Same kind of context as the main window, sharing its textures, buffers and shaders.
        var options = WindowOptions.Default;
        options.API = main.Window.API;
        options.SharedContext = main.Window.GLContext;
        options.IsVisible = false;
        options.WindowBorder = (viewport.Flags & ImGuiViewportFlags.NoDecoration) != 0 ? WindowBorder.Hidden : WindowBorder.Resizable;
        options.TopMost = (viewport.Flags & ImGuiViewportFlags.TopMost) != 0;
        options.Position = new Vector2D<int>((int)viewport.Pos.X, (int)viewport.Pos.Y);
        options.Size = new Vector2D<int>(Math.Max(1, (int)viewport.Size.X), Math.Max(1, (int)viewport.Size.Y));
        options.Title = "";
        options.VSync = false;
        options.ShouldSwapAutomatically = false;

        var window = Window.Create(options);
        try
        {
            window.Initialize();
            // Its context is current now. The main window sets the pace; this one never waits for the screen.
            window.GLContext!.SwapInterval(0);
        }
        finally
        {
            main.Window.GLContext!.MakeCurrent();
        }

        var host = new Host { Window = window, Owned = true };
        // ImGui decides which window has the keyboard; showing one must not take it.
        glfw.SetWindowAttrib(host.Native, WindowAttributeSetter.FocusOnShow, false);
        Bind(host, viewport);

        window.Move += _ =>
        {
            if (ImGui.GetFrameCount() > host.MovedInFrame + 1 && Find(host) is { } moved) moved.PlatformRequestMove = true;
        };
        window.Resize += _ =>
        {
            if (ImGui.GetFrameCount() > host.ResizedInFrame + 1 && Find(host) is { } resized) resized.PlatformRequestResize = true;
        };
        window.Closing += () =>
        {
            if (Find(host) is { } closed) closed.PlatformRequestClose = true;
        };
        if (!realInput) return;
        host.Input = window.CreateInput();
        Listen(host);
    }

    private static ImGuiViewportPtr? Find(Host host)
    {
        var viewport = ImGui.FindViewportByID(host.Viewport);
        return viewport.NativePtr == null ? null : viewport;
    }

    private void DestroyWindow(ImGuiViewportPtr viewport)
    {
        var host = HostOf(viewport);
        viewport.PlatformUserData = 0;
        viewport.PlatformHandle = 0;
        if (host == null) return;
        host.Handle.Free();
        hosts.Remove(host);
        if (!host.Owned) return;
        viewport.PlatformHandleRaw = 0;

        // A window that goes away with keys held never reports them released.
        var io = ImGui.GetIO();
        foreach (var key in host.KeysDown)
            if (MapKey(key) is var mapped and not ImGuiKey.None) io.AddKeyEvent(mapped, false);

        if (OperatingSystem.IsWindows() && host.Window.Native?.Win32 is { } win32)
        {
            // Nor would a drag that began in it ever end: the main window takes over the mouse.
            if (GetCapture() == win32.Hwnd && main.Window.Native?.Win32 is { } mainWindow)
            {
                ReleaseCapture();
                SetCapture(mainWindow.Hwnd);
            }
            // Windows closes whatever a closing window owns; those are closed here, in their own time.
            foreach (var other in hosts)
            {
                if (other.Owner != win32.Hwnd || other.Window.Native?.Win32 is not { } owned) continue;
                SetWindowLongPtr(owned.Hwnd, OwnerIndex, 0);
                other.Owner = 0;
            }
        }

        host.Input?.Dispose();
        host.Window.Dispose();
        // Had its context been the current one, none would be now.
        main.Window.GLContext!.MakeCurrent();
    }

    private void ShowWindow(ImGuiViewportPtr viewport)
    {
        if (HostOf(viewport) is not { } host) return;
        if (OperatingSystem.IsWindows() && viewport.PlatformHandleRaw != 0 && (viewport.Flags & ImGuiViewportFlags.NoTaskBarIcon) != 0)
        {
            nint style = GetWindowLongPtr(viewport.PlatformHandleRaw, ExtendedStyleIndex);
            SetWindowLongPtr(viewport.PlatformHandleRaw, ExtendedStyleIndex, style & ~AppWindowStyle | ToolWindowStyle);
        }
        SetOwner(host, viewport);
        host.Window.IsVisible = true;
    }

    /// <summary>
    /// Keeps a viewport's window in front of the window it belongs to, the main one for a panel. Without
    /// this, clicking the main window would bury the panels floating over it.
    /// </summary>
    private void SetOwner(Host host, ImGuiViewportPtr viewport)
    {
        if (!OperatingSystem.IsWindows() || viewport.PlatformHandleRaw == 0) return;
        nint owner = 0;
        if (viewport.ParentViewportId != 0)
        {
            var parent = ImGui.FindViewportByID(viewport.ParentViewportId);
            if (parent.NativePtr != null) owner = parent.PlatformHandleRaw;
        }
        if (owner == host.Owner) return;
        host.Owner = owner;
        SetWindowLongPtr(viewport.PlatformHandleRaw, OwnerIndex, owner);
    }

    private void ReadMonitors()
    {
        monitorsAge = 0;
        var monitors = glfw.GetMonitors(out int count);
        if (monitors == null || count <= 0) return;

        var platform = ImGui.GetPlatformIO().NativePtr;
        var data = (ImGuiPlatformMonitor*)platform->Monitors.Data;
        int capacity = platform->Monitors.Capacity;
        if (capacity < count)
        {
            // From ImGui's own allocator, as ImGui is who frees it in the end.
            if (data != null) ImGui.MemFree((nint)data);
            data = (ImGuiPlatformMonitor*)ImGui.MemAlloc((uint)(count * sizeof(ImGuiPlatformMonitor)));
            capacity = count;
        }

        int filled = 0;
        for (int i = 0; i < count; i++)
        {
            var mode = glfw.GetVideoMode(monitors[i]);
            if (mode == null || mode->Width <= 0 || mode->Height <= 0) continue;
            glfw.GetMonitorPos(monitors[i], out int x, out int y);
            glfw.GetMonitorWorkarea(monitors[i], out int workX, out int workY, out int workWidth, out int workHeight);
            glfw.GetMonitorContentScale(monitors[i], out float scale, out _);

            var monitor = data + filled++;
            *monitor = default;
            monitor->MainPos = monitor->WorkPos = new Vector2(x, y);
            monitor->MainSize = monitor->WorkSize = new Vector2(mode->Width, mode->Height);
            if (workWidth > 0 && workHeight > 0)
            {
                monitor->WorkPos = new Vector2(workX, workY);
                monitor->WorkSize = new Vector2(workWidth, workHeight);
            }
            monitor->DpiScale = scale > 0 ? scale : 1f;
        }
        platform->Monitors = new ImVector(filled, capacity, (nint)data);
    }

    // ImGui calls these. An exception must not travel through ImGui's frames, so it is kept and thrown
    // again once ImGui has returned.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCreateWindow(ImGuiViewport* viewport)
    {
        try
        {
            current!.CreateWindow(viewport);
        }
        catch (Exception ex)
        {
            current!.failure ??= ex;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDestroyWindow(ImGuiViewport* viewport)
    {
        try
        {
            current!.DestroyWindow(viewport);
        }
        catch (Exception ex)
        {
            current!.failure ??= ex;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnShowWindow(ImGuiViewport* viewport)
    {
        try
        {
            current!.ShowWindow(viewport);
        }
        catch (Exception ex)
        {
            current!.failure ??= ex;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSetWindowPos(ImGuiViewport* viewport, Vector2 position)
    {
        try
        {
            if (HostOf(viewport) is not { } host) return;
            host.MovedInFrame = ImGui.GetFrameCount();
            host.Window.Position = new Vector2D<int>((int)position.X, (int)position.Y);
        }
        catch (Exception ex)
        {
            current!.failure ??= ex;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnGetWindowPos(ImGuiViewport* viewport, Vector2* position)
    {
        *position = default;
        try
        {
            if (HostOf(viewport) is not { } host) return;
            var value = host.Window.Position;
            *position = new Vector2(value.X, value.Y);
        }
        catch (Exception ex)
        {
            current!.failure ??= ex;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSetWindowSize(ImGuiViewport* viewport, Vector2 size)
    {
        try
        {
            if (HostOf(viewport) is not { } host) return;
            host.ResizedInFrame = ImGui.GetFrameCount();
            host.Window.Size = new Vector2D<int>(Math.Max(1, (int)size.X), Math.Max(1, (int)size.Y));
        }
        catch (Exception ex)
        {
            current!.failure ??= ex;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnGetWindowSize(ImGuiViewport* viewport, Vector2* size)
    {
        *size = default;
        try
        {
            if (HostOf(viewport) is not { } host) return;
            var value = host.Window.Size;
            *size = new Vector2(value.X, value.Y);
        }
        catch (Exception ex)
        {
            current!.failure ??= ex;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSetWindowFocus(ImGuiViewport* viewport)
    {
        try
        {
            HostOf(viewport)?.Window.Focus();
        }
        catch (Exception ex)
        {
            current!.failure ??= ex;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnGetWindowFocus(ImGuiViewport* viewport)
    {
        try
        {
            return HostOf(viewport) is { } host && current!.glfw.GetWindowAttrib(host.Native, WindowAttributeGetter.Focused) ? (byte)1 : (byte)0;
        }
        catch (Exception ex)
        {
            current!.failure ??= ex;
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnGetWindowMinimized(ImGuiViewport* viewport)
    {
        try
        {
            return HostOf(viewport) is { } host && current!.glfw.GetWindowAttrib(host.Native, WindowAttributeGetter.Iconified) ? (byte)1 : (byte)0;
        }
        catch (Exception ex)
        {
            current!.failure ??= ex;
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSetWindowTitle(ImGuiViewport* viewport, byte* title)
    {
        try
        {
            if (HostOf(viewport) is { } host) host.Window.Title = Marshal.PtrToStringUTF8((nint)title) ?? "";
        }
        catch (Exception ex)
        {
            current!.failure ??= ex;
        }
    }

    private const int ExtendedStyleIndex = -20, OwnerIndex = -8;
    private const nint ToolWindowStyle = 0x80, AppWindowStyle = 0x40000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint GetCapture();

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseCapture();

    // ---------------------------------------------------------------- drawing

    private uint CreateFontTexture(ImGuiIOPtr io)
    {
        io.Fonts.GetTexDataAsRGBA32(out nint pixels, out int width, out int height, out _);
        gl.GetInteger(GetPName.TextureBinding2D, out int lastTexture);
        uint texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, texture);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)pixels);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.BindTexture(TextureTarget.Texture2D, (uint)lastTexture);
        io.Fonts.SetTexID((nint)texture);
        return texture;
    }

    /// <param name="restore">
    /// Put the OpenGL state back as it was. The main window's context is shared with the preview renderer;
    /// the context of a viewport window is used for nothing else.
    /// </param>
    private void RenderDrawData(ImDrawDataPtr data, bool restore)
    {
        int width = (int)(data.DisplaySize.X * data.FramebufferScale.X);
        int height = (int)(data.DisplaySize.Y * data.FramebufferScale.Y);
        if (width <= 0 || height <= 0) return;

        int lastActiveTexture = 0, lastProgram = 0, lastTexture = 0, lastSampler = 0, lastArrayBuffer = 0, lastVertexArray = 0;
        int lastBlendSrcRgb = 0, lastBlendDstRgb = 0, lastBlendSrcAlpha = 0, lastBlendDstAlpha = 0, lastBlendEquationRgb = 0, lastBlendEquationAlpha = 0;
        Span<int> lastPolygonMode = stackalloc int[2];
        Span<int> lastViewport = stackalloc int[4];
        Span<int> lastScissorBox = stackalloc int[4];
        bool lastBlend = false, lastCullFace = false, lastDepthTest = false, lastStencilTest = false, lastScissorTest = false, lastPrimitiveRestart = false;
        if (restore)
        {
            gl.GetInteger(GetPName.ActiveTexture, out lastActiveTexture);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.GetInteger(GetPName.CurrentProgram, out lastProgram);
            gl.GetInteger(GetPName.TextureBinding2D, out lastTexture);
            gl.GetInteger(GetPName.SamplerBinding, out lastSampler);
            gl.GetInteger(GetPName.ArrayBufferBinding, out lastArrayBuffer);
            gl.GetInteger(GetPName.VertexArrayBinding, out lastVertexArray);
            gl.GetInteger(GetPName.PolygonMode, lastPolygonMode);
            gl.GetInteger(GetPName.Viewport, lastViewport);
            gl.GetInteger(GetPName.ScissorBox, lastScissorBox);
            gl.GetInteger(GetPName.BlendSrcRgb, out lastBlendSrcRgb);
            gl.GetInteger(GetPName.BlendDstRgb, out lastBlendDstRgb);
            gl.GetInteger(GetPName.BlendSrcAlpha, out lastBlendSrcAlpha);
            gl.GetInteger(GetPName.BlendDstAlpha, out lastBlendDstAlpha);
            gl.GetInteger(GetPName.BlendEquationRgb, out lastBlendEquationRgb);
            gl.GetInteger(GetPName.BlendEquationAlpha, out lastBlendEquationAlpha);
            lastBlend = gl.IsEnabled(EnableCap.Blend);
            lastCullFace = gl.IsEnabled(EnableCap.CullFace);
            lastDepthTest = gl.IsEnabled(EnableCap.DepthTest);
            lastStencilTest = gl.IsEnabled(EnableCap.StencilTest);
            lastScissorTest = gl.IsEnabled(EnableCap.ScissorTest);
            lastPrimitiveRestart = gl.IsEnabled(EnableCap.PrimitiveRestart);
        }

        gl.ActiveTexture(TextureUnit.Texture0);
        gl.Enable(EnableCap.Blend);
        gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
        gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.StencilTest);
        gl.Enable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.PrimitiveRestart);
        gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        gl.Viewport(0, 0, (uint)width, (uint)height);

        // ImGui's coordinates are the screen's while panels can leave the main window: each window shows
        // the part of them that starts at its own position.
        float left = data.DisplayPos.X, right = data.DisplayPos.X + data.DisplaySize.X;
        float top = data.DisplayPos.Y, bottom = data.DisplayPos.Y + data.DisplaySize.Y;
        Span<float> projection =
        [
            2f / (right - left), 0, 0, 0,
            0, 2f / (top - bottom), 0, 0,
            0, 0, -1, 0,
            (right + left) / (left - right), (top + bottom) / (bottom - top), 0, 1,
        ];
        gl.UseProgram(shader.Handle);
        gl.Uniform1(textureLocation, 0);
        gl.UniformMatrix4(projectionLocation, 1, false, projection);
        gl.BindSampler(0, 0);

        // Vertex arrays belong to one context, unlike the buffers, so each draw makes its own.
        uint vertexArray = gl.GenVertexArray();
        gl.BindVertexArray(vertexArray);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vertexBuffer);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, indexBuffer);
        uint stride = (uint)sizeof(ImDrawVert);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)8);
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, (void*)16);

        Vector2 clipOffset = data.DisplayPos, clipScale = data.FramebufferScale;
        for (int n = 0; n < data.CmdListsCount; n++)
        {
            var list = data.CmdLists[n];
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(list.VtxBuffer.Size * sizeof(ImDrawVert)), (void*)list.VtxBuffer.Data, BufferUsageARB.StreamDraw);
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(list.IdxBuffer.Size * sizeof(ushort)), (void*)list.IdxBuffer.Data, BufferUsageARB.StreamDraw);

            for (int i = 0; i < list.CmdBuffer.Size; i++)
            {
                var command = list.CmdBuffer[i];
                // Nothing in this program draws through callbacks.
                if (command.UserCallback != 0) continue;

                float x0 = (command.ClipRect.X - clipOffset.X) * clipScale.X, y0 = (command.ClipRect.Y - clipOffset.Y) * clipScale.Y;
                float x1 = (command.ClipRect.Z - clipOffset.X) * clipScale.X, y1 = (command.ClipRect.W - clipOffset.Y) * clipScale.Y;
                if (x0 >= width || y0 >= height || x1 < 0 || y1 < 0 || x1 <= x0 || y1 <= y0) continue;

                gl.Scissor((int)x0, (int)(height - y1), (uint)(x1 - x0), (uint)(y1 - y0));
                gl.BindTexture(TextureTarget.Texture2D, (uint)command.TextureId);
                gl.DrawElementsBaseVertex(PrimitiveType.Triangles, command.ElemCount, DrawElementsType.UnsignedShort,
                    (void*)(command.IdxOffset * sizeof(ushort)), (int)command.VtxOffset);
            }
        }

        gl.BindVertexArray(0);
        gl.DeleteVertexArray(vertexArray);
        if (!restore) return;

        gl.UseProgram((uint)lastProgram);
        gl.BindTexture(TextureTarget.Texture2D, (uint)lastTexture);
        gl.BindSampler(0, (uint)lastSampler);
        gl.ActiveTexture((TextureUnit)lastActiveTexture);
        gl.BindVertexArray((uint)lastVertexArray);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, (uint)lastArrayBuffer);
        gl.BlendEquationSeparate((BlendEquationModeEXT)lastBlendEquationRgb, (BlendEquationModeEXT)lastBlendEquationAlpha);
        gl.BlendFuncSeparate((BlendingFactor)lastBlendSrcRgb, (BlendingFactor)lastBlendDstRgb, (BlendingFactor)lastBlendSrcAlpha, (BlendingFactor)lastBlendDstAlpha);
        Set(EnableCap.Blend, lastBlend);
        Set(EnableCap.CullFace, lastCullFace);
        Set(EnableCap.DepthTest, lastDepthTest);
        Set(EnableCap.StencilTest, lastStencilTest);
        Set(EnableCap.ScissorTest, lastScissorTest);
        Set(EnableCap.PrimitiveRestart, lastPrimitiveRestart);
        gl.PolygonMode(TriangleFace.FrontAndBack, (PolygonMode)lastPolygonMode[0]);
        gl.Viewport(lastViewport[0], lastViewport[1], (uint)lastViewport[2], (uint)lastViewport[3]);
        gl.Scissor(lastScissorBox[0], lastScissorBox[1], (uint)lastScissorBox[2], (uint)lastScissorBox[3]);

        void Set(EnableCap capability, bool on)
        {
            if (on) gl.Enable(capability);
            else gl.Disable(capability);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Closes the viewport windows; the main one belongs to the caller.
        if (viewports) ImGui.DestroyPlatformWindows();
        gl.DeleteBuffer(vertexBuffer);
        gl.DeleteBuffer(indexBuffer);
        gl.DeleteTexture(fontTexture);
        shader.Dispose();
        ImGui.DestroyContext(context);
        Theme.ReleaseFontRanges();
        current = null;
    }
}
