// CircularBlob algorithm: Copyright (c) ppy Pty Ltd <contact@ppy.sh>, MIT.
// See src/SbStudio.Resources/Assets/Argon/osu-MIT-LICENCE.txt and RulesetPreviewSources.md.
using System.Numerics;
using Silk.NET.OpenGL;

namespace SbStudio.App.Gfx;

internal sealed unsafe class ArgonBlobShape : IDisposable
{
    private const string VertexSource = @"#version 330 core
layout(location = 0) in vec2 aUnit;
uniform vec4 uView;
uniform vec4 uShape;
out vec2 vUnit;
void main() {
    vUnit = aUnit;
    float c = cos(uShape.w), s = sin(uShape.w);
    vec2 local = (aUnit - 0.5) * uShape.z;
    vec2 p = uShape.xy + vec2(local.x*c-local.y*s, local.x*s+local.y*c);
    gl_Position = vec4(p * uView.xy + uView.zw, 0.0, 1.0);
}";
    private const string FragmentSource = @"#version 330 core
in vec2 vUnit;
uniform vec4 uColour;
uniform vec4 uBlob;
uniform vec2 uNoise;
out vec4 oColor;
float randomValue(vec2 st) { return fract(sin(dot(st, vec2(12.9898,78.233))) * 43758.5453123); }
float noiseValue(vec2 st) {
    vec2 i = floor(st), f = fract(st);
    float a = randomValue(i), b = randomValue(i + vec2(1,0));
    float c = randomValue(i + vec2(0,1)), d = randomValue(i + vec2(1,1));
    vec2 u = f*f*(3.0-2.0*f);
    return mix(a,b,u.x) + (c-a)*u.y*(1.0-u.x) + (d-b)*u.x*u.y;
}
void main() {
    float pixelAngle = atan(0.5-vUnit.y, 0.5-vUnit.x) - 1.57079632679;
    if (pixelAngle < 0) pixelAngle += 6.28318530718;
    float complexity = (uBlob.z + uBlob.w)*0.5 + 1.0;
    int points = int(ceil(5.0*complexity));
    float searchRange = 0.1*complexity;
    float pathRadius = uBlob.x*0.25;
    float shortestDistance = 1.0;
    float startAngle = pixelAngle - searchRange*0.5;
    for (int i=0; i<points; i++) {
        float angle = startAngle + searchRange*float(i)/float(points);
        vec2 cs = vec2(cos(angle-1.57079632679), sin(angle-1.57079632679));
        float n = noiseValue(uNoise + cs*uBlob.z);
        vec2 pos = vec2(0.5) + cs*(0.5-pathRadius-uBlob.y-n*0.5*uBlob.w);
        shortestDistance = min(shortestDistance, distance(vUnit,pos));
    }
    // Reversed-edge smoothstep in framework, expressed without undefined GLSL behaviour.
    float coverage = 1.0-smoothstep(0.0,uBlob.y,shortestDistance-pathRadius);
    float alpha = coverage*uColour.a;
    oColor = vec4(uColour.rgb*alpha,alpha);
}";
    private readonly GL gl;
    private readonly SpriteBatch batch;
    private readonly ShaderProgram shader;
    private readonly uint vao, vbo;
    private readonly int viewLocation, shapeLocation, colourLocation, blobLocation, noiseLocation;
    private Vector4 projection;
    private float pixelScale;
    public ArgonBlobShape(GL gl, SpriteBatch batch)
    {
        this.gl = gl;
        this.batch = batch;
        shader = new(gl, VertexSource, FragmentSource);
        viewLocation = shader.Uniform("uView");
        shapeLocation = shader.Uniform("uShape");
        colourLocation = shader.Uniform("uColour");
        blobLocation = shader.Uniform("uBlob");
        noiseLocation = shader.Uniform("uNoise");
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
    public void SetView(RenderTarget target, ViewTransform view)
    {
        projection = new(view.Scale * 2 / target.Width, -view.Scale * 2 / target.Height,
            view.Offset.X * 2 / target.Width - 1, 1 - view.Offset.Y * 2 / target.Height);
        pixelScale = view.Scale;
    }
    public void Draw(Vector2 centre, float diameter, float rotation, Vector4 colour, float innerRadius, int seed)
    {
        if (diameter <= 0 || colour.W <= 0) return;
        var random = new Random(seed);
        float nx = (float)(random.NextDouble() * 1000), ny = (float)(random.NextDouble() * 1000);
        batch.Suspend();
        gl.UseProgram(shader.Handle);
        gl.BindVertexArray(vao);
        gl.Disable(EnableCap.DepthTest);
        gl.Enable(EnableCap.Blend);
        gl.BlendFuncSeparate(BlendingFactor.One, BlendingFactor.One, BlendingFactor.Zero, BlendingFactor.One);
        gl.Uniform4(viewLocation, projection.X, projection.Y, projection.Z, projection.W);
        gl.Uniform4(shapeLocation, centre.X, centre.Y, diameter, rotation);
        gl.Uniform4(colourLocation, colour.X, colour.Y, colour.Z, colour.W);
        gl.Uniform4(blobLocation, innerRadius, 1.5f / (diameter * pixelScale), 1.5f, 0.3f);
        gl.Uniform2(noiseLocation, nx, ny);
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
