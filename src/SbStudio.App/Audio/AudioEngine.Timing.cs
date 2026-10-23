using NAudio.Wave;

namespace SbStudio.App.Audio;

public sealed partial class AudioEngine
{
    private sealed class TimingPreparation
    {
        internal volatile bool Cancelled;
        internal volatile TimingMusic? Data;
    }

    private bool timingMode;
    private bool timingSoundFollowsTempo, playingTimingSoundFollowsTempo;
    private bool clockTimingSlowdown, playingClockTimingSlowdown;
    internal bool PlayingTimingSoundFollowsTempo => playingTimingSoundFollowsTempo;
    internal bool PlayingClockTimingSlowdown => playingClockTimingSlowdown;
    private TimingPreparation? timingMusic;
    internal bool TimingMusicReady => timingMusic?.Data != null;
    internal long TimingMusicBytes => timingMusic?.Data?.Bytes ?? 0;
    internal bool UsingTimingMusicCache { get; private set; }

    /// <summary>Advance the Timing music clock and start crossed clicks once per editor update.</summary>
    internal void UpdateTimingPlayback()
    {
        if (!IsPlaying || trialTiming == null || output == null) return;
        try
        {
            trialTiming.UpdateClock(startMs + output.TrackPositionMilliseconds, stopwatch.Elapsed.TotalMilliseconds);
            trialTiming.Gain = volume * hitSoundVolume * clickGain;
            trialTiming.UpdateSamples();
        }
        catch (Exception ex) { Error = Core.L.T("audio.playFailed", ex.Message); }
    }

    /// <summary>Spend memory on a full PCM cache as soon as Timing is enabled, before pressing Play.</summary>
    public void SetTimingMode(bool enabled, bool soundFollowsTempo = false,
        TimingSlowPlaybackStyle slowPlayback = TimingSlowPlaybackStyle.PlaybackClock)
    {
        timingSoundFollowsTempo = enabled && soundFollowsTempo;
        clockTimingSlowdown = enabled && slowPlayback == TimingSlowPlaybackStyle.PlaybackClock;
        if (timingMode == enabled) return;
        timingMode = enabled;
        if (enabled) PrepareTimingMusic();
        else ResetTimingMusic();
    }

    private void ResetTimingMusic()
    {
        if (timingMusic != null) timingMusic.Cancelled = true;
        timingMusic = null;
    }

    private void PrepareTimingMusic(bool force = false)
    {
        if ((!timingMode && !force) || musicFile == null || timingMusic != null) return;
        var preparation = new TimingPreparation();
        timingMusic = preparation;
        string file = musicFile;
        Task.Run(() =>
        {
            try
            {
                using var source = Open(file);
                var data = new TimingMusic(Normalize(source.ToSampleProvider()), () => preparation.Cancelled);
                if (!preparation.Cancelled) preparation.Data = data;
            }
            catch (Exception)
            {
                // A cancelled/unreadable/too-large cache must not prevent normal playback.
                // No decoder is shared with the live stream or the waveform worker.
            }
        });
    }
}
