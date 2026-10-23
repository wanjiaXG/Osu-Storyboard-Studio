using System.Globalization;

namespace SbStudio.Core;

/// <summary>
/// What the numbers of a video export may be. The user types them freely; what a video file or its
/// encoders cannot take is turned down here, with the reason in the user's language. The sound is AAC
/// in every kind of file but AVI, where it is MP3, which is far more particular.
/// </summary>
public static class VideoLimits
{
    public const int MinHeight = 144, MaxHeight = 4320;
    public const int MinFps = 1, MaxFps = 1000;
    public const int MinVideoKbps = 100, MaxVideoKbps = 500_000;
    public const int MinAudioKbps = 8;

    /// <summary>The sample rates AAC has, the highest first.</summary>
    public static readonly int[] AacSampleRates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };
    /// <summary>The sample rates MP3 has, the highest first.</summary>
    public static readonly int[] Mp3SampleRates = { 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000 };

    // The bit rates of the three generations of MP3, which go with its sample rates: 32 kHz and up, half
    // of those, and a quarter of them.
    private static readonly int[] Mpeg1Kbps = { 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
    private static readonly int[] Mpeg2Kbps = { 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
    private static readonly int[] Mpeg25Kbps = { 8, 16, 24, 32, 40, 48, 56, 64 };

    /// <summary>The bit rates the sound usually has, in kilobits per second: all of them ones AAC takes at 44.1 kHz and above.</summary>
    public static readonly int[] UsualAacKbps = { 96, 128, 160, 192, 224, 256, 320 };

    public static int[] SampleRates(bool mp3) => mp3 ? Mp3SampleRates : AacSampleRates;

    /// <summary>The only bit rates an MP3 can have at a sample rate, in kilobits per second.</summary>
    public static int[] Mp3Kbps(int sampleRate) => sampleRate >= 32000 ? Mpeg1Kbps : sampleRate >= 16000 ? Mpeg2Kbps : Mpeg25Kbps;

    /// <summary>
    /// The highest bit rate AAC in stereo can have at a sample rate, in kilobits per second: it has 6144
    /// bits for each channel in every 1024 samples and no more.
    /// </summary>
    public static int MaxAacKbps(int sampleRate) => (int)(6144L * 2 * sampleRate / 1024 / 1000);

    /// <summary>What is wrong with a height of the picture in pixels; null when it will do. The colour of a video is stored for two rows at a time, hence even.</summary>
    public static string? HeightProblem(int height) =>
        height < MinHeight || height > MaxHeight || height % 2 != 0 ? L.T("videoexport.invalid.height", MinHeight, MaxHeight) : null;

    public static string? FpsProblem(int fps) =>
        fps < MinFps || fps > MaxFps ? L.T("videoexport.invalid.fps", MinFps, MaxFps) : null;

    public static string? VideoBitrateProblem(int kbps) =>
        kbps < MinVideoKbps || kbps > MaxVideoKbps ? L.T("videoexport.invalid.bitrate", MinVideoKbps, MaxVideoKbps) : null;

    /// <param name="mp3">The sound is MP3, as in an AVI file; otherwise AAC.</param>
    public static string? SampleRateProblem(int sampleRate, bool mp3) =>
        Array.IndexOf(SampleRates(mp3), sampleRate) >= 0 ? null : L.T("videoexport.invalid.sampleRate", mp3 ? "MP3" : "AAC", List(SampleRates(mp3)));

    /// <summary>What is wrong with a bit rate of the sound at a sample rate; null when it will do, or when the sample rate itself will not.</summary>
    public static string? AudioBitrateProblem(int kbps, int sampleRate, bool mp3)
    {
        if (SampleRateProblem(sampleRate, mp3) != null) return null;
        if (mp3) return Array.IndexOf(Mp3Kbps(sampleRate), kbps) >= 0 ? null : L.T("videoexport.invalid.audioBitrateMp3", sampleRate, List(Mp3Kbps(sampleRate)));
        return kbps >= MinAudioKbps && kbps <= MaxAacKbps(sampleRate) ? null : L.T("videoexport.invalid.audioBitrateAac", sampleRate, MinAudioKbps, MaxAacKbps(sampleRate));
    }

    private static string List(int[] values) => string.Join(", ", values.OrderBy(v => v).Select(v => v.ToString(CultureInfo.InvariantCulture)));
}
