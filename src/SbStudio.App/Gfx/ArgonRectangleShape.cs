using System.Numerics;
using Silk.NET.OpenGL;

namespace SbStudio.App.Gfx;

/// <summary>Argon rectangles retain a fixed corner radius and border as their width and height change.</summary>
internal sealed unsafe class ArgonRectangleShape : IDisposable
{
    private const string VertexSource = @"#version 330 core
layout(location = 0) in vec2 aUnit;
uniform vec4 uView;
uniform vec4 uRect;
out vec2 vLocal;
void main() {
    vLocal = aUnit * uRect.zw;
    vec2 p = uRect.xy + vLocal;
    gl_Position = vec4(p * uView.xy + uView.zw, 0.0, 1.0);
}";
    private const string FragmentSource = @"#version 330 core
in vec2 vLocal;
uniform vec4 uRect;
uniform float uRadius;
uniform float uBorder;
uniform vec4 uTop;
uniform vec4 uBottom;
out vec4 oColor;
void main() {
    vec2 halfSize = uRect.zw * 0.5;
    float radius = min(uRadius, min(halfSize.x, halfSize.y));
    vec2 q = abs(vLocal - halfSize) - halfSize + vec2(radius);
    float distance = length(max(q, vec2(0.0))) + min(max(q.x, q.y), 0.0) - radius;
    float aa = max(fwidth(distance), 0.0001);
    float coverage = clamp(0.5 - distance / aa, 0.0, 1.0);
    if (uBorder > 0.0) coverage *= clamp(0.5 + (distance + uBorder) / aa, 0.0, 1.0);
    vec4 colour = mix(uTop, uBottom, clamp(vLocal.y / uRect.w, 0.0, 1.0));
    float alpha = colour.a * coverage;
    oColor = vec4(colour.rgb * alpha, alpha);
}";
    private readonly GL gl;
    private readonly SpriteBatch batch;
    private readonly ShaderProgram shader;
    private readonly uint vao, vbo;
    private readonly int viewLocation, rectLocation, radiusLocation, borderLocation, topLocation, bottomLocation;
    private Vector4 projection;

    public ArgonRectangleShape(GL gl, SpriteBatch batch)
    {
        this.gl = gl;
        this.batch = batch;
        shader = new(gl, VertexSource, FragmentSource);
        viewLocation = shader.Uniform("uView");
        rectLocation = shader.Uniform("uRect");
        radiusLocation = shader.Uniform("uRadius");
        borderLocation = shader.Uniform("uBorder");
        topLocation = shader.Uniform("uTop");
        bottomLocation = shader.Uniform("uBottom");
        vao = gl.GenVertexArray();
        vbo = gl.GenBuffer();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        float[] vertices = { 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1 };
        fixed (float* p = vertices) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        gl.BindVertexArray(0);
    }

    public void SetView(RenderTarget target, ViewTransform view) => projection = new(
        view.Scale * 2 / target.Width, -view.Scale * 2 / target.Height,
        view.Offset.X * 2 / target.Width - 1, 1 - view.Offset.Y * 2 / target.Height);

    public void Draw(Vector4 rect, Vector4 top, Vector4 bottom, float radius = 0, float border = 0, bool additive = false)
    {
        if (rect.Z <= 0 || rect.W <= 0 || (top.W <= 0 && bottom.W <= 0)) return;
        batch.Suspend();
        gl.UseProgram(shader.Handle);
        gl.BindVertexArray(vao);
        gl.Disable(EnableCap.DepthTest);
        gl.Enable(EnableCap.Blend);
        gl.BlendFuncSeparate(BlendingFactor.One, additive ? BlendingFactor.One : BlendingFactor.OneMinusSrcAlpha, BlendingFactor.Zero, BlendingFactor.One);
        gl.Uniform4(viewLocation, projection.X, projection.Y, projection.Z, projection.W);
        gl.Uniform4(rectLocation, rect.X, rect.Y, rect.Z, rect.W);
        gl.Uniform1(radiusLocation, radius);
        gl.Uniform1(borderLocation, border);
        gl.Uniform4(topLocation, top.X, top.Y, top.Z, top.W);
        gl.Uniform4(bottomLocation, bottom.X, bottom.Y, bottom.Z, bottom.W);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        batch.Resume();
    }

    public void Dispose()
    {
        shader.Dispose();
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);
    }
}
