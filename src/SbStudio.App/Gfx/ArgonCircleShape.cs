using System.Numerics;
using Silk.NET.OpenGL;

namespace SbStudio.App.Gfx;

/// <summary>Resizable Argon rings keep their border thickness while Size animates in lazer.</summary>
internal sealed unsafe class ArgonCircleShape : IDisposable
{
    private const string VertexSource = @"#version 330 core
layout(location = 0) in vec2 aUnit;
uniform vec4 uView;
uniform vec2 uCentre;
uniform float uDiameter;
out vec2 vUnit;
void main() {
    vUnit = aUnit;
    vec2 p = uCentre + aUnit * uDiameter * 0.5;
    gl_Position = vec4(p * uView.xy + uView.zw, 0.0, 1.0);
}";

    private const string FragmentSource = @"#version 330 core
in vec2 vUnit;
uniform float uDiameter;
uniform float uThickness;
uniform vec4 uTop;
uniform vec4 uBottom;
uniform float uAlpha;
out vec4 oColor;
void main() {
    float radius = uDiameter * 0.5;
    float distance = length(vUnit) * radius;
    float aa = max(fwidth(distance), 0.0001);
    float coverage = clamp((radius - distance) / aa + 0.5, 0.0, 1.0);
    if (uThickness >= 0.0)
        coverage *= clamp((distance - radius + uThickness) / aa + 0.5, 0.0, 1.0);
    vec4 c = mix(uTop, uBottom, (vUnit.y + 1.0) * 0.5);
    float alpha = c.a * coverage * uAlpha;
    oColor = vec4(c.rgb * alpha, alpha);
}";

    private readonly GL gl;
    private readonly SpriteBatch batch;
    private readonly ShaderProgram shader;
    private readonly uint vao, vbo;
    private readonly int viewLocation, centreLocation, diameterLocation, thicknessLocation, topLocation, bottomLocation, alphaLocation;
    private Vector4 projection;

    public ArgonCircleShape(GL gl, SpriteBatch batch)
    {
        this.gl = gl;
        this.batch = batch;
        shader = new(gl, VertexSource, FragmentSource);
        viewLocation = shader.Uniform("uView");
        centreLocation = shader.Uniform("uCentre");
        diameterLocation = shader.Uniform("uDiameter");
        thicknessLocation = shader.Uniform("uThickness");
        topLocation = shader.Uniform("uTop");
        bottomLocation = shader.Uniform("uBottom");
        alphaLocation = shader.Uniform("uAlpha");
        vao = gl.GenVertexArray();
        vbo = gl.GenBuffer();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        float[] vertices = { -1, -1, 1, -1, 1, 1, -1, -1, 1, 1, -1, 1 };
        fixed (float* p = vertices)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        gl.BindVertexArray(0);
    }

    public void SetView(RenderTarget target, ViewTransform view) => projection = new(
        view.Scale * 2 / target.Width, -view.Scale * 2 / target.Height,
        view.Offset.X * 2 / target.Width - 1, 1 - view.Offset.Y * 2 / target.Height);

    /// <param name="thickness">Negative for a filled circle; otherwise the ring's thickness in storyboard space.</param>
    public void Draw(Vector2 centre, float diameter, float thickness, Vector4 top, Vector4 bottom, float alpha)
    {
        if (alpha <= 0 || diameter <= 0) return;
        batch.Suspend();
        gl.UseProgram(shader.Handle);
        gl.BindVertexArray(vao);
        gl.Disable(EnableCap.DepthTest);
        gl.Enable(EnableCap.Blend);
        gl.BlendFuncSeparate(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.Zero, BlendingFactor.One);
        gl.Uniform4(viewLocation, projection.X, projection.Y, projection.Z, projection.W);
        gl.Uniform2(centreLocation, centre.X, centre.Y);
        gl.Uniform1(diameterLocation, diameter);
        gl.Uniform1(thicknessLocation, thickness);
        gl.Uniform4(topLocation, top.X, top.Y, top.Z, top.W);
        gl.Uniform4(bottomLocation, bottom.X, bottom.Y, bottom.Z, bottom.W);
        gl.Uniform1(alphaLocation, alpha);
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
