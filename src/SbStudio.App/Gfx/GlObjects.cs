using SbStudio.Core;
using Silk.NET.OpenGL;

namespace SbStudio.App.Gfx;

public sealed class ShaderProgram : IDisposable
{
    private readonly GL gl;
    public uint Handle { get; }

    public ShaderProgram(GL gl, string vertexSource, string fragmentSource)
    {
        this.gl = gl;
        uint vs = Compile(ShaderType.VertexShader, vertexSource);
        uint fs = Compile(ShaderType.FragmentShader, fragmentSource);
        Handle = gl.CreateProgram();
        gl.AttachShader(Handle, vs);
        gl.AttachShader(Handle, fs);
        gl.LinkProgram(Handle);
        gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException(L.T("error.shaderLink", gl.GetProgramInfoLog(Handle)));
        gl.DetachShader(Handle, vs);
        gl.DetachShader(Handle, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
    }

    private uint Compile(ShaderType type, string source)
    {
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException(L.T("error.shaderCompile", type, gl.GetShaderInfoLog(shader)));
        return shader;
    }

    public int Uniform(string name) => gl.GetUniformLocation(Handle, name);

    public void Dispose() => gl.DeleteProgram(Handle);
}

/// <summary>An RGBA texture with an explicit alpha representation and sampling bias.</summary>
public sealed class Texture : IDisposable
{
    private readonly GL gl;
    public uint Handle { get; }
    public int Width { get; }
    public int Height { get; }
    public bool Premultiplied { get; } = true;
    public float LodBias { get; }

    public nint ImGuiId => (nint)Handle;

    private const int MaskSize = 64;
    private readonly byte[] mask;
    private readonly int maskWidth, maskHeight;

    /// <summary>
    /// Whether the image has visible pixels around a point given in 0..1 image coordinates. Uses a small
    /// alpha map, so clicks pass through the empty parts of rings, glows and other mostly transparent images.
    /// </summary>
    public bool IsOpaqueAt(float u, float v)
    {
        if (u < 0 || v < 0 || u > 1 || v > 1) return false;
        int x = Math.Min(maskWidth - 1, (int)(u * maskWidth));
        int y = Math.Min(maskHeight - 1, (int)(v * maskHeight));
        return mask[y * maskWidth + x] > 12;
    }

    /// <summary>
    /// The alpha map <see cref="IsOpaqueAt"/> looks things up in. It reads every pixel, which takes
    /// milliseconds for a large image, so whoever decodes images away from the render thread builds it
    /// there and passes it to the constructor.
    /// </summary>
    public static byte[] BuildMask(int width, int height, ReadOnlySpan<byte> rgba)
    {
        int maskWidth = Math.Min(MaskSize, width), maskHeight = Math.Min(MaskSize, height);
        // Each cell keeps the strongest alpha of the pixels it covers.
        var result = new byte[maskWidth * maskHeight];
        for (int y = 0; y < height; y++)
        {
            int row = y * maskHeight / height * maskWidth;
            int offset = y * width * 4 + 3;
            for (int x = 0; x < width; x++)
            {
                byte a = rgba[offset + x * 4];
                int cell = row + x * maskWidth / width;
                if (a > result[cell]) result[cell] = a;
            }
        }
        return result;
    }

    /// <param name="mask">What <see cref="BuildMask"/> gave for these pixels, when it has been built already.</param>
    public unsafe Texture(GL gl, int width, int height, ReadOnlySpan<byte> rgba, bool mipmaps = true, byte[]? mask = null,
        bool premultiplied = true, float lodBias = 0, int maxMipmapLevel = int.MaxValue)
    {
        this.gl = gl;
        Width = width;
        Height = height;
        Premultiplied = premultiplied;
        LodBias = lodBias;
        maskWidth = Math.Min(MaskSize, width);
        maskHeight = Math.Min(MaskSize, height);
        this.mask = mask ?? BuildMask(width, height, rgba);
        Handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Handle);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = rgba)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)(mipmaps ? GLEnum.LinearMipmapLinear : GLEnum.Linear));
        if (mipmaps) gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel,
            Math.Min(maxMipmapLevel, System.Numerics.BitOperations.Log2((uint)Math.Max(width, height))));
        if (mipmaps) gl.GenerateMipmap(TextureTarget.Texture2D);
    }

    /// <summary>A texture whose picture is replaced again and again with <see cref="Update"/>: a video. Opaque all over.</summary>
    public unsafe Texture(GL gl, int width, int height)
    {
        this.gl = gl;
        Width = width;
        Height = height;
        maskWidth = maskHeight = 1;
        mask = new byte[] { 255 };
        Handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Handle);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
    }

    /// <summary>Replaces the whole picture. The pixels are top-down RGBA rows of the texture's size.</summary>
    public unsafe void Update(ReadOnlySpan<byte> rgba)
    {
        gl.BindTexture(TextureTarget.Texture2D, Handle);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = rgba)
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)Width, (uint)Height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        gl.GenerateMipmap(TextureTarget.Texture2D);
    }

    public void Dispose() => gl.DeleteTexture(Handle);
}

/// <summary>
/// Off-screen colour buffer the storyboard preview is drawn into. It has a depth buffer too, which only
/// the bodies of sliders use.
/// </summary>
public sealed class RenderTarget : IDisposable
{
    private readonly GL gl;
    private uint depth;
    public uint Framebuffer { get; private set; }
    public uint Texture { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public nint ImGuiId => (nint)Texture;

    public RenderTarget(GL gl) => this.gl = gl;

    public unsafe void Resize(int width, int height)
    {
        width = Math.Clamp(width, 1, 8192);
        height = Math.Clamp(height, 1, 8192);
        if (width == Width && height == Height && Framebuffer != 0) return;

        Release();
        Width = width;
        Height = height;

        Texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Texture);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        depth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)width, (uint)height);
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

        Framebuffer = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, Framebuffer);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, Texture, 0);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>
    /// Reads the buffer as it lies in memory: RGBA rows from the bottom one up. For whoever turns the rows
    /// round anyway while working on them, such as the video encoder.
    /// </summary>
    public unsafe void ReadPixelsRaw(byte[] raw)
    {
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, Framebuffer);
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        fixed (byte* p = raw)
            gl.ReadPixels(0, 0, (uint)Width, (uint)Height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>Reads the buffer back as top-down RGBA rows.</summary>
    public unsafe byte[] ReadPixels()
    {
        var raw = new byte[Width * Height * 4];
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, Framebuffer);
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        fixed (byte* p = raw)
            gl.ReadPixels(0, 0, (uint)Width, (uint)Height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return FlipRows(raw, Width, Height);
    }

    public static byte[] FlipRows(byte[] raw, int width, int height)
    {
        var flipped = new byte[raw.Length];
        int stride = width * 4;
        for (int y = 0; y < height; y++)
            System.Buffer.BlockCopy(raw, (height - 1 - y) * stride, flipped, y * stride, stride);
        return flipped;
    }

    private void Release()
    {
        if (Framebuffer != 0) gl.DeleteFramebuffer(Framebuffer);
        if (Texture != 0) gl.DeleteTexture(Texture);
        if (depth != 0) gl.DeleteRenderbuffer(depth);
        Framebuffer = Texture = depth = 0;
    }

    public void Dispose() => Release();
}
