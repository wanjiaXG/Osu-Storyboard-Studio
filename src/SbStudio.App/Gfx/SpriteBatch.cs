using System.Numerics;
using System.Runtime.InteropServices;
using SbStudio.Core;
using Silk.NET.OpenGL;

namespace SbStudio.App.Gfx;

/// <summary>Maps storyboard coordinates (640x480 space) to pixels of the render target.</summary>
public struct ViewTransform
{
    public float Scale;
    public Vector2 Offset;

    public readonly Vector2 ToPixels(Vector2 sb) => sb * Scale + Offset;
    public readonly Vector2 ToStoryboard(Vector2 px) => (px - Offset) / Scale;
}

public static class SpriteGeometry
{
    /// <summary>Corners of a sprite in storyboard space: top-left, top-right, bottom-right, bottom-left of the image.</summary>
    public static void Corners(in SpriteState st, SbOrigin origin, float texWidth, float texHeight, Span<Vector2> corners)
    {
        var (ox, oy) = SbInfo.OriginFraction(origin);
        // A negative vector scale mirrors the image in place; only its magnitude sizes the quad.
        double w = texWidth * st.Scale * Math.Abs(st.ScaleX);
        double h = texHeight * st.Scale * Math.Abs(st.ScaleY);
        double x0 = -ox * w, y0 = -oy * h, x1 = x0 + w, y1 = y0 + h;
        double cos = Math.Cos(st.Rotation), sin = Math.Sin(st.Rotation);
        double px = st.X, py = st.Y;

        corners[0] = Rotate(x0, y0);
        corners[1] = Rotate(x1, y0);
        corners[2] = Rotate(x1, y1);
        corners[3] = Rotate(x0, y1);

        Vector2 Rotate(double x, double y) => new((float)(px + x * cos - y * sin), (float)(py + x * sin + y * cos));
    }

    public static bool MirrorX(in SpriteState st) => st.FlipH ^ (st.ScaleX < 0);
    public static bool MirrorY(in SpriteState st) => st.FlipV ^ (st.ScaleY < 0);

    /// <summary>Position of a point inside the quad as 0..1 image coordinates (outside that range when off the image).</summary>
    public static Vector2 ToImageSpace(ReadOnlySpan<Vector2> quad, Vector2 p, bool mirrorX, bool mirrorY)
    {
        Vector2 across = quad[1] - quad[0], down = quad[3] - quad[0];
        float lenAcross = across.LengthSquared(), lenDown = down.LengthSquared();
        if (lenAcross < 1e-9f || lenDown < 1e-9f) return new Vector2(-1, -1);
        float u = Vector2.Dot(p - quad[0], across) / lenAcross;
        float v = Vector2.Dot(p - quad[0], down) / lenDown;
        return new Vector2(mirrorX ? 1 - u : u, mirrorY ? 1 - v : v);
    }

    public static bool Contains(ReadOnlySpan<Vector2> quad, Vector2 p)
    {
        bool positive = false, negative = false;
        for (int i = 0; i < 4; i++)
        {
            Vector2 a = quad[i], b = quad[(i + 1) % 4];
            float cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            if (cross > 0) positive = true;
            else if (cross < 0) negative = true;
            if (positive && negative) return false;
        }
        return true;
    }
}

/// <summary>Batches textured quads; flushes whenever the texture or blend mode changes so draw order is kept.</summary>
public sealed class SpriteBatch : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector2 Position;
        public Vector2 Uv;
        public Vector4 Color;
    }

    private const int MaxQuads = 4096;

    private const string VertexSource = @"#version 330 core
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec4 aColor;
uniform vec4 uView;
out vec2 vUv;
out vec4 vColor;
void main()
{
    vUv = aUv;
    vColor = aColor;
    gl_Position = vec4(aPos * uView.xy + uView.zw, 0.0, 1.0);
}";

    private const string FragmentSource = @"#version 330 core
in vec2 vUv;
in vec4 vColor;
uniform sampler2D uTex;
uniform float uLodBias;
uniform int uPremultiplied;
out vec4 oColor;
void main()
{
    vec4 texel = texture(uTex, vUv, uLodBias);
    if (uPremultiplied == 0) texel.rgb *= texel.a;
    oColor = texel * vColor;
}";

    private readonly GL gl;
    private readonly ShaderProgram shader;
    private readonly uint vao, vbo;
    private readonly int viewLocation;
    private readonly int lodBiasLocation, premultipliedLocation;
    private readonly Vertex[] vertices = new Vertex[MaxQuads * 6];
    private int count;
    private uint currentTexture;
    private float currentLodBias;
    private bool currentPremultiplied;
    private bool currentAdditive;

    public Texture White { get; }
    public int DrawCalls { get; private set; }
    public int Quads { get; private set; }

    public unsafe SpriteBatch(GL gl)
    {
        this.gl = gl;
        shader = new ShaderProgram(gl, VertexSource, FragmentSource);
        viewLocation = shader.Uniform("uView");
        lodBiasLocation = shader.Uniform("uLodBias");
        premultipliedLocation = shader.Uniform("uPremultiplied");

        vao = gl.GenVertexArray();
        vbo = gl.GenBuffer();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(Vertex)), null, BufferUsageARB.StreamDraw);
        uint stride = (uint)sizeof(Vertex);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)8);
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)16);
        gl.BindVertexArray(0);

        White = new Texture(gl, 1, 1, new byte[] { 255, 255, 255, 255 }, false);
    }

    /// <summary>Binds the target and sets the state every draw relies on.</summary>
    public void Begin(RenderTarget target, ViewTransform view, Vector4 clearColor)
    {
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, target.Framebuffer);
        gl.Viewport(0, 0, (uint)target.Width, (uint)target.Height);
        gl.Disable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.StencilTest);
        gl.Enable(EnableCap.Blend);
        gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
        gl.ClearColor(clearColor.X, clearColor.Y, clearColor.Z, clearColor.W);
        gl.Clear(ClearBufferMask.ColorBufferBit);

        gl.UseProgram(shader.Handle);
        // Storyboard y grows downward; the top of the picture ends up in the top row of the texture.
        gl.Uniform4(viewLocation,
            view.Scale * 2f / target.Width, -view.Scale * 2f / target.Height,
            view.Offset.X * 2f / target.Width - 1f, 1f - view.Offset.Y * 2f / target.Height);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.Uniform1(shader.Uniform("uTex"), 0);
        gl.BindVertexArray(vao);

        count = 0;
        currentTexture = 0;
        currentAdditive = false;
        DrawCalls = 0;
        Quads = 0;
    }

    /// <param name="corners">Top-left, top-right, bottom-right, bottom-left in storyboard space.</param>
    /// <param name="color">Straight RGB plus opacity; premultiplied here.</param>
    public void Draw(Texture texture, ReadOnlySpan<Vector2> corners, Vector4 color, bool additive, bool mirrorX = false, bool mirrorY = false)
    {
        if (texture.Handle != currentTexture || additive != currentAdditive || count + 6 > vertices.Length)
        {
            Flush();
            currentTexture = texture.Handle;
            currentLodBias = texture.LodBias;
            currentPremultiplied = texture.Premultiplied;
            currentAdditive = additive;
        }

        float u0 = mirrorX ? 1 : 0, u1 = 1 - u0;
        float v0 = mirrorY ? 1 : 0, v1 = 1 - v0;
        var c = new Vector4(color.X * color.W, color.Y * color.W, color.Z * color.W, color.W);

        Add(corners[0], u0, v0, c);
        Add(corners[1], u1, v0, c);
        Add(corners[2], u1, v1, c);
        Add(corners[0], u0, v0, c);
        Add(corners[2], u1, v1, c);
        Add(corners[3], u0, v1, c);
        Quads++;
    }

    public void DrawRect(Vector2 min, Vector2 max, Vector4 color)
    {
        Span<Vector2> corners = stackalloc Vector2[4];
        corners[0] = min;
        corners[1] = new Vector2(max.X, min.Y);
        corners[2] = max;
        corners[3] = new Vector2(min.X, max.Y);
        Draw(White, corners, color, false);
    }

    /// <summary>
    /// Draws what has been gathered so far, so that something of another kind can be drawn in between with
    /// a shader of its own. <see cref="Resume"/> takes up again afterwards.
    /// </summary>
    public void Suspend() => Flush();

    /// <summary>Sets up again what <see cref="Begin"/> set up, after other drawing has changed it. Nothing is cleared.</summary>
    public void Resume()
    {
        gl.Disable(EnableCap.DepthTest);
        gl.Enable(EnableCap.Blend);
        gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
        gl.UseProgram(shader.Handle);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindVertexArray(vao);
        currentTexture = 0;
    }

    private void Add(Vector2 position, float u, float v, Vector4 color)
    {
        ref var vertex = ref vertices[count++];
        vertex.Position = position;
        vertex.Uv = new Vector2(u, v);
        vertex.Color = color;
    }

    private unsafe void Flush()
    {
        if (count == 0) return;
        // Destination alpha stays opaque so the result can be shown as a plain image.
        if (currentAdditive) gl.BlendFuncSeparate(BlendingFactor.One, BlendingFactor.One, BlendingFactor.Zero, BlendingFactor.One);
        else gl.BlendFuncSeparate(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.Zero, BlendingFactor.One);

        gl.BindTexture(TextureTarget.Texture2D, currentTexture);
        gl.Uniform1(lodBiasLocation, currentLodBias);
        gl.Uniform1(premultipliedLocation, currentPremultiplied ? 1 : 0);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (Vertex* p = vertices)
            gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(count * sizeof(Vertex)), p);
        gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)count);
        DrawCalls++;
        count = 0;
    }

    public void End()
    {
        Flush();
        gl.BindVertexArray(0);
        gl.UseProgram(0);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Dispose()
    {
        White.Dispose();
        shader.Dispose();
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);
    }
}
