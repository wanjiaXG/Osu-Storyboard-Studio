using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SbStudio.App.Audio;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>
/// Measures the average BPM and offset of a stretch of the main audio with <see cref="TimingAnalyzer"/>.
/// The stretch is typed in milliseconds or dragged on an overview of the whole song's waveform.
/// </summary>
public sealed partial class TimelinePanel
{
    private const string TimingAnalysisId = "###timingAnalysisPopup";
    private const int MinAnalysisMs = 1000;
    private bool requestTimingAnalysis;
    private Project? analysisProject;
    private int analysisStart, analysisEnd;
    private string analysisStartText = "", analysisEndText = "", analysisBpmText = "";
    private string? analysisRangeError;
    private Task<AnalysisOutcome>? analysisTask;
    private CancellationTokenSource? analysisCancel;
    private AnalysisOutcome? analysisOutcome;
    // Which part of the range the mouse is moving on the overview: 0 none, 1 start, 2 end.
    private int analysisDrag;
    private Waveform? analysisPeaksOf;
    private float analysisPeakMax = 1;
    // Variable tempo (experimental): the tolerance of a red line, and the fewest beats one covers.
    private bool analysisVariable, analysisReplace = true;
    private double analysisTolerance = 0.5;
    private string analysisToleranceText = "0.5";
    private string? analysisToleranceError;
    private int analysisMinBeats = 4, analysisAlignDivisor = 4, analysisSignature = 1, analysisCustomMeter = 5, analysisBpmDecimals = 3;
    // The user's calibration, typed freely and refused with the reason when out of range; the hint of the last run, to repeat it.
    private string analysisCalibrationText = "";
    private string? analysisCalibrationError;
    private double analysisLastHint;
    private const double CalibrationLimit = 50;
    private int analysisMeter => analysisSignature == 0 ? 3 : analysisSignature == 1 ? 4 : analysisCustomMeter;
    private string? MeterError => analysisMeter is < 1 or > 16 ? L.T("timing.invalidMeter") : null;
    private static readonly int[] MinBeatChoices = { 1, 2, 4, 8, 16 };

    private sealed record AnalysisOutcome(int Start, int End, TimingAnalysis? Result, string? Error, VariableTimingAnalysis? Variable = null)
    {
        /// <summary>The first beat of the analysed part on the song's own time line.</summary>
        public double Offset => Result == null ? 0 : Start + Result.OffsetMs;
    }

    private void OpenTimingAnalysis()
    {
        analysisProject = app.Project;
        analysisStart = 0;
        analysisEnd = Math.Max(0, (int)app.Audio.DurationMs);
        analysisStartText = analysisStart.ToString(CultureInfo.InvariantCulture);
        analysisEndText = analysisEnd.ToString(CultureInfo.InvariantCulture);
        analysisRangeError = null;
        analysisOutcome = null;
        analysisAlignDivisor = app.Divisor;
        analysisCalibrationText = app.Settings.TimingAnalysisCalibrationMs.ToString("0.##", CultureInfo.InvariantCulture);
        analysisCalibrationError = null;
        int current = app.CurrentTimingOffset?.Meter ?? 4;
        analysisSignature = current == 3 ? 0 : current == 4 ? 1 : 2;
        analysisCustomMeter = current is 3 or 4 ? 5 : current;
        requestTimingAnalysis = true;
    }

    private void DrawTimingAnalysisPopup()
    {
        string title = L.T("timing.analysis.title") + TimingAnalysisId;
        if (requestTimingAnalysis)
        {
            requestTimingAnalysis = false;
            ImGui.OpenPopup(title);
        }
        var viewport = ImGui.GetMainViewport();
        if (!MainUi.BeginModal(title, viewport.Pos + viewport.Size / 2, 760))
        {
            if (analysisTask != null) analysisCancel?.Cancel();
            return;
        }
        if (app.Project != analysisProject)
        {
            analysisCancel?.Cancel();
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        if (analysisTask is { IsCompleted: true } finished)
        {
            analysisOutcome = finished.IsCompletedSuccessfully ? finished.Result : null;
            analysisTask = null;
        }

        int duration = (int)app.Audio.DurationMs;
        Theme.TextWrapped(L.T("timing.analysis.note"));
        Theme.TextWrapped(L.T("timing.analysis.notAi"));
        // Reference only: nothing here sets the timing, which stays the mapper's work.
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextWarning);
        Theme.TextWrapped(L.T("timing.analysis.reference"));
        ImGui.PopStyleColor();
        bool busy = analysisTask != null;
        ImGui.BeginDisabled(busy);
        if (ImGui.RadioButton(L.T("timing.analysis.modeFixed"), !analysisVariable) && analysisVariable)
        {
            analysisVariable = false;
            analysisOutcome = null;
        }
        ImGui.SameLine(0, Theme.Px(16));
        if (ImGui.RadioButton(L.T("timing.analysis.modeVariable"), analysisVariable) && !analysisVariable)
        {
            analysisVariable = true;
            analysisOutcome = null;
        }
        ImGui.EndDisabled();
        if (analysisVariable)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextWarning);
            Theme.TextWrapped(L.T("timing.analysis.experimental"));
            ImGui.PopStyleColor();
        }
        if (duration <= 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(L.T("timing.analysis.noAudio"));
            ImGui.PopStyleColor();
        }
        else DrawAnalysisOverview(duration);

        bool running = analysisTask != null;
        ImGui.BeginDisabled(running || duration <= 0);
        DrawAnalysisTime("##analysisStart", L.T("timing.analysis.start"), ref analysisStartText, true, duration);
        ImGui.SameLine(0, Theme.Px(24));
        DrawAnalysisTime("##analysisEnd", L.T("timing.analysis.end"), ref analysisEndText, false, duration);
        ImGui.SameLine(0, Theme.Px(24));
        if (ImGui.Button(L.T("timing.analysis.whole"))) SetAnalysisRange(0, duration);
        ImGui.EndDisabled();
        if (analysisRangeError != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(analysisRangeError);
            ImGui.PopStyleColor();
        }

        // Fixed tempo: a known BPM keeps it exactly. Variable tempo: a BPM only says where to look for the beat.
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T(analysisVariable ? "timing.analysis.tempoHint" : "timing.analysis.knownBpm"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.Px(100));
        ImGui.BeginDisabled(running);
        ImGui.InputText("##analysisBpm", ref analysisBpmText, 16, ImGuiInputTextFlags.CharsDecimal);
        ImGui.EndDisabled();
        Theme.Tooltip(L.T(analysisVariable ? "timing.analysis.tempoHint.tip" : "timing.analysis.knownBpm.tip"));
        // Which beats are downbeats: the red lines go on the first beat of a bar, and are set to this metre.
        ImGui.BeginDisabled(running);
        ImGui.SameLine(0, Theme.Px(24));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("timing.analysis.meter"));
        Theme.Tooltip(L.T("timing.analysis.meter.tip"));
        // The same choices as adding a red line: 3/4, 4/4, or any other number of beats per bar.
        ImGui.SameLine();
        if (ImGui.RadioButton("3 / 4##analysisMeter3", analysisSignature == 0)) analysisSignature = 0;
        ImGui.SameLine();
        if (ImGui.RadioButton("4 / 4##analysisMeter4", analysisSignature == 1)) analysisSignature = 1;
        ImGui.SameLine();
        if (ImGui.RadioButton(L.T("timing.otherSignature") + "##analysisMeterOther", analysisSignature == 2)) analysisSignature = 2;
        ImGui.SameLine();
        ImGui.BeginDisabled(analysisSignature != 2);
        Theme.Stepper("analysisCustomMeter", ref analysisCustomMeter, 1, 16, Theme.Px(50));
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextUnformatted("/ 4");
        ImGui.EndDisabled();
        DrawAnalysisCalibration(running);
        if (analysisVariable) DrawVariableSettings(running);
        string? bpmError = analysisBpmText.Trim().Length == 0 ? null : TimingPointEditing.ValidateBpmInput(analysisBpmText, out _);
        if (bpmError != null)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            ImGui.TextUnformatted(bpmError == "timing.bpmPrecision" ? L.T("timing.bpmPrecision") : L.T("timing.invalidBpm"));
            ImGui.PopStyleColor();
        }
        if (analysisCalibrationError != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(analysisCalibrationError);
            ImGui.PopStyleColor();
        }
        if (MeterError is { } meterError)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(meterError);
            ImGui.PopStyleColor();
        }
        if (analysisVariable && analysisToleranceError != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(analysisToleranceError);
            ImGui.PopStyleColor();
        }

        ImGui.Separator();
        if (analysisVariable) DrawVariableResult(running);
        else DrawAnalysisResult(running);
        DrawAnalysisReferences();
        ImGui.Separator();

        if (running)
        {
            ImGui.BeginDisabled();
            ImGui.Button(L.T("timing.analysis.running"));
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button(L.T("common.cancel") + "##analysisStop")) analysisCancel?.Cancel();
        }
        else
        {
            ImGui.BeginDisabled(duration <= 0 || bpmError != null || MeterError != null);
            if (ImGui.Button(L.T("timing.analysis.run"))) StartAnalysis(0);
            ImGui.EndDisabled();
            // The beat may be counted at half or double the tempo found; these look again near that tempo.
            double foundBpm = analysisOutcome?.Result is { Confidence: not TimingConfidence.Failed } fixedResult ? fixedResult.Bpm
                : analysisOutcome?.Variable is { Confidence: not TimingConfidence.Failed } variableResult ? variableResult.CentreBpm : 0;
            bool found = foundBpm > 0 && bpmError == null && (analysisVariable || analysisBpmText.Trim().Length == 0);
            ImGui.BeginDisabled(!found);
            ImGui.SameLine();
            if (ImGui.Button(L.T("timing.analysis.half"))) StartAnalysis(foundBpm / 2);
            Theme.Tooltip(L.T("timing.analysis.octave.tip"));
            ImGui.SameLine();
            if (ImGui.Button(L.T("timing.analysis.double"))) StartAnalysis(foundBpm * 2);
            Theme.Tooltip(L.T("timing.analysis.octave.tip"));
            ImGui.EndDisabled();
            // One click to red lines as a starting point; reference values that go into the history like any edit.
            ImGui.SameLine();
            if (analysisVariable)
            {
                bool canAddAll = analysisOutcome?.Variable is { Confidence: not TimingConfidence.Failed, Sections.Length: > 0 };
                ImGui.BeginDisabled(!canAddAll || app.PreviewingEdit || MeterError != null);
                if (ImGui.Button(L.T("timing.analysis.addAll")) && analysisOutcome != null) AddAnalysedSections(analysisOutcome);
                ImGui.EndDisabled();
                Theme.Tooltip(L.T("timing.analysis.addAll.tip"));
                ImGui.SameLine();
                ImGui.BeginDisabled(!canAddAll || app.PreviewingEdit || MeterError != null);
                if (ImGui.Button(L.T("timing.analysis.clearAdd")) && analysisOutcome != null) AskClearAndAdd(analysisOutcome);
                ImGui.EndDisabled();
                Theme.Tooltip(L.T("timing.analysis.clearAdd.tip"));
                ImGui.SameLine();
                ImGui.Checkbox(L.T("timing.analysis.replace"), ref analysisReplace);
                Theme.Tooltip(L.T("timing.analysis.replace.tip"));
            }
            else
            {
                bool canAdd = analysisOutcome?.Result is { Confidence: not TimingConfidence.Failed };
                ImGui.BeginDisabled(!canAdd || app.PreviewingEdit);
                if (ImGui.Button(L.T("timing.analysis.addLine")) && analysisOutcome != null) AddAnalysedLine(analysisOutcome);
                ImGui.EndDisabled();
                Theme.Tooltip(L.T("timing.analysis.addLine.tip"));
                ImGui.SameLine();
                ImGui.BeginDisabled(!canAdd || app.PreviewingEdit);
                if (ImGui.Button(L.T("timing.analysis.clearAdd")) && analysisOutcome != null) AskClearAndAdd(analysisOutcome);
                ImGui.EndDisabled();
                Theme.Tooltip(L.T("timing.analysis.clearAdd.tip"));
            }
        }
        bool confirming = DrawClearAndAddConfirm();
        ImGui.SameLine();
        if (Theme.Button(L.T("common.close"), 100) || (!confirming && ImGui.IsKeyPressed(ImGuiKey.Escape)))
        {
            analysisCancel?.Cancel();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    /// <summary>The whole song's waveform; the stretch to analyse is dragged on it, and results are drawn over it.</summary>
    private void DrawAnalysisOverview(int duration)
    {
        var io = ImGui.GetIO();
        var dl = ImGui.GetWindowDrawList();
        float width = ImGui.GetContentRegionAvail().X, height = Theme.Px(96);
        Vector2 pos = ImGui.GetCursorScreenPos(), max = pos + new Vector2(width, height);
        float X(double ms) => pos.X + (float)(ms / duration) * width;
        double Ms(float x) => Math.Clamp((x - pos.X) / width, 0, 1) * duration;

        dl.AddRectFilled(pos, max, Theme.Rgba(0, 0, 0));
        var waveform = app.Audio.Waveform;
        if (waveform == null)
            dl.AddText(pos + new Vector2(8, 8), Theme.TextDim, L.T("timeline.analysing"));
        else
        {
            if (waveform != analysisPeaksOf)
            {
                analysisPeaksOf = waveform;
                analysisPeakMax = Math.Max(1e-4f, waveform.Peaks.DefaultIfEmpty(0).Max());
            }
            float mid = (pos.Y + max.Y) / 2, amplitude = height / 2 - Theme.Px(4);
            uint colour = Theme.Rgba(82, 145, 226);
            for (float x = pos.X; x < max.X; x++)
            {
                float peak = waveform.PeakForDisplay(Ms(x), Ms(x + 1)) / analysisPeakMax * amplitude;
                if (peak > 0.5f) dl.AddLine(new(x, mid - peak), new(x, mid + peak), colour);
            }
        }

        // Outside the stretch is darkened; its two edges can be dragged.
        float left = X(analysisStart), right = X(analysisEnd);
        dl.AddRectFilled(pos, new(left, max.Y), Theme.Rgba(0, 0, 0, 150));
        dl.AddRectFilled(new(right, pos.Y), max, Theme.Rgba(0, 0, 0, 150));
        uint edge = Theme.Rgba(255, 214, 92);
        dl.AddLine(new(left, pos.Y), new(left, max.Y), edge, 2);
        dl.AddLine(new(right, pos.Y), new(right, max.Y), edge, 2);

        // The result: its beats where they are far enough apart to see, and the flags of each 10 seconds.
        if (analysisOutcome is { Result: { Confidence: not TimingConfidence.Failed } result } outcome)
        {
            double period = 60000.0 / result.Bpm;
            if (period / duration * width >= 4)
                for (double t = outcome.Offset; t <= outcome.End; t += period)
                    dl.AddLine(new(X(t), pos.Y), new(X(t), pos.Y + Theme.Px(10)), Theme.Rgba(255, 255, 255, 110));
            for (int c = 0; c < result.Columns.Length; c++)
            {
                var flag = result.Columns[c];
                if (flag == TimingColumnFlag.None) continue;
                double from = outcome.Start + c * (double)TimingAnalyzer.ColumnMs, to = Math.Min(outcome.End, from + TimingAnalyzer.ColumnMs);
                uint flagColour = AnalysisFlagColour(flag);
                dl.AddRectFilled(new(X(from), max.Y - Theme.Px(6)), new(MathF.Max(X(from) + 2, X(to) - 1), max.Y), flagColour);
                string mark = TimingAnalysis.Symbol(flag).ToString();
                float markX = (X(from) + X(to) - ImGui.CalcTextSize(mark).X) / 2;
                dl.AddText(new(markX, max.Y - Theme.Px(6) - ImGui.GetFontSize()), flagColour, mark);
            }
        }
        if (analysisOutcome is { Variable: { Confidence: not TimingConfidence.Failed } variable } tracked)
        {
            foreach (double beat in variable.Beats)
                dl.AddLine(new(X(tracked.Start + beat), pos.Y), new(X(tracked.Start + beat), pos.Y + Theme.Px(8)), Theme.Rgba(255, 255, 255, 110));
            foreach (var section in variable.Sections)
                dl.AddLine(new(X(tracked.Start + section.OffsetMs), pos.Y), new(X(tracked.Start + section.OffsetMs), max.Y),
                    section.Correction ? Theme.Rgba(255, 170, 60, 200) : Theme.Rgba(255, 70, 70, 200));
        }
        dl.AddLine(new(X(app.Time), pos.Y), new(X(app.Time), max.Y), Theme.Playhead, 1);

        ImGui.InvisibleButton("##analysisOverview", new(width, height));
        bool hovered = ImGui.IsItemHovered();
        float grab = Theme.Px(6);
        if (ImGui.IsItemActivated())
        {
            float mx = io.MousePos.X;
            if (Math.Abs(mx - left) <= grab) analysisDrag = 1;
            else if (Math.Abs(mx - right) <= grab) analysisDrag = 2;
            else
            {
                // A new stretch from where the mouse went down; its far end follows the mouse.
                int at = (int)Ms(mx);
                SetAnalysisRange(at, at, check: false);
                analysisDrag = 2;
            }
        }
        if (ImGui.IsItemActive() && analysisDrag != 0)
        {
            int at = (int)Math.Round(Ms(io.MousePos.X));
            int start = analysisDrag == 1 ? at : analysisStart, end = analysisDrag == 2 ? at : analysisEnd;
            // Dragged past the other edge, the two swap roles.
            if (start > end)
            {
                (start, end) = (end, start);
                analysisDrag = analysisDrag == 1 ? 2 : 1;
            }
            SetAnalysisRange(start, end, check: false);
        }
        if (ImGui.IsItemDeactivated() && analysisDrag != 0)
        {
            analysisDrag = 0;
            SetAnalysisRange(analysisStart, analysisEnd);
        }
        if (hovered || analysisDrag != 0)
        {
            float mx = io.MousePos.X;
            if (analysisDrag != 0 || Math.Abs(mx - left) <= grab || Math.Abs(mx - right) <= grab) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
            Theme.SetTooltip($"{Theme.FormatTime(Ms(mx))}\n{L.T("timing.analysis.overview.tip")}");
        }
        Theme.TextDimmed($"{Theme.FormatTime(analysisStart)} – {Theme.FormatTime(analysisEnd)}  ({analysisEnd - analysisStart} ms)");
    }

    private static uint AnalysisFlagColour(TimingColumnFlag flag) => flag switch
    {
        TimingColumnFlag.NoBeat => Theme.Rgba(255, 96, 96),
        TimingColumnFlag.Earlier => Theme.Rgba(255, 170, 70),
        TimingColumnFlag.Later => Theme.Rgba(90, 170, 255),
        _ => Theme.Rgba(190, 190, 190),
    };

    /// <summary>A start or end field in milliseconds. A value that cannot be is refused with the reason, keeping the old one.</summary>
    private void DrawAnalysisTime(string id, string label, ref string text, bool start, int duration)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.Px(110));
        ImGui.InputText(id, ref text, 16, ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.AutoSelectAll);
        if (ImGui.IsItemDeactivated()) TakeAnalysisTime(text, start, duration);
        ImGui.SameLine();
        if (ImGui.Button(L.T("timing.analysis.useCurrent") + id))
        {
            int now = (int)Math.Clamp(Math.Round(app.Time), 0, duration);
            if (start) SetAnalysisRange(now, analysisEnd);
            else SetAnalysisRange(analysisStart, now);
        }
        Theme.Tooltip(L.T("timing.analysis.useCurrent.tip"));
    }

    private void TakeAnalysisTime(string text, bool start, int duration)
    {
        string trimmed = text.Trim();
        string? why = null;
        if (trimmed.Length == 0) why = L.T("input.empty");
        else if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
            why = L.T("input.notNumber", trimmed);
        else if (number != Math.Round(number)) why = L.T("input.notInteger", trimmed);
        else if (number < 0 || number > duration) why = L.T("timing.analysis.outOfSong", duration);
        if (why != null)
        {
            RefuseAnalysisRange(why);
            return;
        }
        int value = (int)double.Parse(trimmed, CultureInfo.InvariantCulture);
        if (start) SetAnalysisRange(value, analysisEnd);
        else SetAnalysisRange(analysisStart, value);
    }

    /// <summary>Sets the stretch; when checked, one that is too short or reversed is refused and the old one kept.</summary>
    private void SetAnalysisRange(int start, int end, bool check = true)
    {
        if (check)
        {
            string? why = end <= start ? L.T("timing.analysis.order")
                : end - start < MinAnalysisMs ? L.T("timing.analysis.tooShort", MinAnalysisMs) : null;
            if (why != null)
            {
                RefuseAnalysisRange(why);
                return;
            }
            analysisRangeError = null;
        }
        analysisStart = start;
        analysisEnd = end;
        analysisStartText = start.ToString(CultureInfo.InvariantCulture);
        analysisEndText = end.ToString(CultureInfo.InvariantCulture);
    }

    private void RefuseAnalysisRange(string why)
    {
        analysisRangeError = why;
        analysisStartText = analysisStart.ToString(CultureInfo.InvariantCulture);
        analysisEndText = analysisEnd.ToString(CultureInfo.InvariantCulture);
    }

    private void StartAnalysis(double tempoHint)
    {
        analysisLastHint = tempoHint;
        double calibration = app.Settings.TimingAnalysisCalibrationMs;
        if (analysisEnd - analysisStart < MinAnalysisMs)
        {
            RefuseAnalysisRange(L.T("timing.analysis.tooShort", MinAnalysisMs));
            return;
        }
        double knownBpm = TimingPointEditing.ValidateBpmInput(analysisBpmText, out double typed) == null ? typed : 0;
        int start = analysisStart, end = analysisEnd;
        bool variable = analysisVariable;
        double tolerance = analysisTolerance;
        int minBeats = analysisMinBeats, alignDivisor = analysisAlignDivisor, meter = analysisMeter, bpmDecimals = analysisBpmDecimals;
        // The red line in force where the range starts (and not replaced): the first red line found starts on its grid.
        var timing = app.Project!.Settings.Timing;
        var before = timing.Where(p => double.IsFinite(p.Offset) && p.Offset < start).OrderBy(p => p.Offset).ToList();
        var previous = before.Count > 0 ? new TimingPoint { Offset = before[^1].Offset, BeatLength = before[^1].BeatLength, Meter = before[^1].Meter } : null;
        bool previousIsFirst = before.Count == 1 && timing.Where(p => double.IsFinite(p.Offset)).Min(p => p.Offset) == before[0].Offset;
        var audio = app.Audio;
        analysisCancel = new CancellationTokenSource();
        var cancel = analysisCancel.Token;
        analysisTask = Task.Run(() =>
        {
            float[]? stereo = audio.DecodeMusic();
            if (stereo == null) return new AnalysisOutcome(start, end, null, L.T("timing.analysis.decodeFailed"));
            cancel.ThrowIfCancellationRequested();
            // One channel: left and right summed (the analysis does not depend on the level).
            long from = Math.Min(stereo.Length / 2, (long)start * AudioEngine.SampleRate / 1000);
            long to = Math.Min(stereo.Length / 2, (long)end * AudioEngine.SampleRate / 1000);
            var mono = new float[Math.Max(0, to - from)];
            for (long i = 0; i < mono.Length; i++) mono[i] = stereo[(from + i) * 2] + stereo[(from + i) * 2 + 1];
            if (variable)
            {
                var tracked = TimingAnalyzer.AnalyzeVariable(mono, AudioEngine.SampleRate, new VariableTimingOptions(tolerance, minBeats,
                    tempoHint > 0 ? tempoHint : knownBpm, start, alignDivisor, previous, previousIsFirst, meter, calibration, bpmDecimals), cancel);
                return new AnalysisOutcome(start, end, null, tracked.Confidence == TimingConfidence.Failed ? L.T("timing.analysis.failed") : null, tracked);
            }
            var result = TimingAnalyzer.Analyze(mono, AudioEngine.SampleRate, knownBpm, tempoHint, meter, calibration, cancel);
            return new AnalysisOutcome(start, end, result, result.Confidence == TimingConfidence.Failed ? L.T("timing.analysis.failed") : null);
        }, cancel);
    }

    private void DrawAnalysisResult(bool running)
    {
        if (running)
        {
            Theme.TextDimmed(L.T("timing.analysis.running"));
            return;
        }
        if (analysisOutcome == null)
        {
            Theme.TextDimmed(L.T("timing.analysis.none"));
            return;
        }
        if (analysisOutcome.Error != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(analysisOutcome.Error);
            ImGui.PopStyleColor();
            return;
        }
        var result = analysisOutcome.Result!;
        // Measured values with their standard errors: the BPM to three decimals, the offset in whole milliseconds.
        ImGui.TextUnformatted(L.T("timing.analysis.resultBpm", $"{result.Bpm:0.000}", $"{result.BpmError:0.000}"));
        // Most songs are made at a whole BPM; within the measuring error of one, say so.
        double whole = Math.Round(result.Bpm);
        if (Math.Abs(result.Bpm - whole) > 0.0005 && Math.Abs(result.Bpm - whole) <= Math.Max(3 * result.BpmError, 0.02))
        {
            ImGui.SameLine();
            Theme.TextDimmed(L.T("timing.analysis.integerHint", whole));
        }
        // Whole milliseconds, as osu! keeps offsets and exactly as the red line would be added.
        ImGui.TextUnformatted(L.T("timing.analysis.resultOffset", $"{Math.Round(analysisOutcome.Offset, MidpointRounding.AwayFromZero):0}", $"{result.OffsetError:0.0}"));
        Theme.TextDimmed(L.T("timing.analysis.detail", Theme.FormatTime(analysisOutcome.Start), Theme.FormatTime(analysisOutcome.End),
            $"{result.BeatsHeard * 100:0}", $"{result.SpreadMs:0.0}"));
        switch (result.Confidence)
        {
            case TimingConfidence.High: ImGui.TextUnformatted(L.T("timing.analysis.confidenceHigh")); break;
            case TimingConfidence.Normal: ImGui.TextUnformatted(L.T("timing.analysis.confidenceNormal")); break;
            default:
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextWarning);
                Theme.TextWrapped(L.T("timing.analysis.approximate"));
                ImGui.PopStyleColor();
                break;
        }
        void Flag(TimingColumnFlag flag, string text)
        {
            if (!result.Columns.Contains(flag)) return;
            ImGui.PushStyleColor(ImGuiCol.Text, AnalysisFlagColour(flag));
            Theme.TextWrapped(text);
            ImGui.PopStyleColor();
        }
        Flag(TimingColumnFlag.NoBeat, L.T("timing.analysis.flagNoBeat"));
        Flag(TimingColumnFlag.Earlier, L.T("timing.analysis.flagEarlier"));
        Flag(TimingColumnFlag.Later, L.T("timing.analysis.flagLater"));
        Flag(TimingColumnFlag.Unsteady, L.T("timing.analysis.flagUnsteady"));
    }

    /// <summary>
    /// The user's offset calibration, saved with the settings. A change re-runs a shown analysis, so the figures on screen
    /// always include it.
    /// </summary>
    private void DrawAnalysisCalibration(bool running)
    {
        ImGui.BeginDisabled(running);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("timing.analysis.calibration"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.Px(70));
        ImGui.InputText("##analysisCalibration", ref analysisCalibrationText, 12, ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.AutoSelectAll);
        if (ImGui.IsItemDeactivated()) TakeAnalysisCalibration(analysisCalibrationText);
        Theme.Tooltip(L.T("timing.analysis.calibration.tip"));
        ImGui.EndDisabled();
    }

    private void TakeAnalysisCalibration(string text)
    {
        double old = app.Settings.TimingAnalysisCalibrationMs, value = 0;
        string trimmed = text.Trim();
        string? why = trimmed.Length == 0 ? L.T("input.empty")
            : !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value) ? L.T("input.notNumber", trimmed)
            : Math.Abs(value) > CalibrationLimit ? L.T("timing.analysis.calibrationRange", -CalibrationLimit, CalibrationLimit) : null;
        analysisCalibrationError = why;
        if (why == null && value != old)
        {
            app.Settings.TimingAnalysisCalibrationMs = value;
            app.Settings.Save();
            if (analysisOutcome != null && analysisTask == null) StartAnalysis(analysisLastHint);
        }
        analysisCalibrationText = app.Settings.TimingAnalysisCalibrationMs.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>Tolerance and the fewest beats per red line, for the variable-tempo analysis.</summary>
    private void DrawVariableSettings(bool running)
    {
        ImGui.BeginDisabled(running);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("timing.analysis.tolerance"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.Px(60));
        ImGui.InputText("##analysisTolerance", ref analysisToleranceText, 12, ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.AutoSelectAll);
        if (ImGui.IsItemDeactivated()) TakeAnalysisTolerance(analysisToleranceText);
        Theme.Tooltip(L.T("timing.analysis.tolerance.tip"));
        ImGui.SameLine(0, Theme.Px(24));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("timing.analysis.minBeats"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.Px(90));
        if (ImGui.BeginCombo("##analysisMinBeats", L.T("timing.analysis.minBeatsItem", analysisMinBeats)))
        {
            foreach (int choice in MinBeatChoices)
                if (ImGui.Selectable(L.T("timing.analysis.minBeatsItem", choice), choice == analysisMinBeats)) analysisMinBeats = choice;
            ImGui.EndCombo();
        }
        Theme.Tooltip(L.T("timing.analysis.minBeats.tip"));
        ImGui.SameLine(0, Theme.Px(24));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("timing.analysis.align"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.Px(70));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.SnapColor(analysisAlignDivisor));
        bool alignOpen = ImGui.BeginCombo("##analysisAlign", $"1/{analysisAlignDivisor}", ImGuiComboFlags.HeightLarge);
        ImGui.PopStyleColor();
        if (alignOpen)
        {
            foreach (int d in TimingUtil.Divisors)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.SnapColor(d));
                if (ImGui.Selectable($"1/{d}", d == analysisAlignDivisor)) analysisAlignDivisor = d;
                ImGui.PopStyleColor();
            }
            ImGui.EndCombo();
        }
        Theme.Tooltip(L.T("timing.analysis.align.tip"));
        // How precise each red line's BPM may be: whole numbers, or 1 to 3 decimals.
        ImGui.SameLine(0, Theme.Px(24));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("timing.analysis.bpmStep"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.Px(110));
        if (ImGui.BeginCombo("##analysisBpmDecimals", BpmDecimalsLabel(analysisBpmDecimals)))
        {
            for (int decimals = 0; decimals <= 3; decimals++)
                if (ImGui.Selectable(BpmDecimalsLabel(decimals), decimals == analysisBpmDecimals)) analysisBpmDecimals = decimals;
            ImGui.EndCombo();
        }
        Theme.Tooltip(L.T("timing.analysis.bpmStep.tip"));
        ImGui.EndDisabled();
    }

    private static string BpmDecimalsLabel(int decimals) =>
        decimals == 0 ? L.T("timing.analysis.bpmWhole") : L.T("timing.analysis.bpmDecimals", decimals);

    private void TakeAnalysisTolerance(string text)
    {
        const double low = 0.5, high = 50;
        string trimmed = text.Trim();
        string? why = trimmed.Length == 0 ? L.T("input.empty")
            : !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) ? L.T("input.notNumber", trimmed)
            : value < low || value > high ? L.T("timing.analysis.toleranceRange", low, high) : null;
        if (why == null)
        {
            analysisTolerance = double.Parse(trimmed, CultureInfo.InvariantCulture);
            analysisToleranceError = null;
        }
        else analysisToleranceError = why;
        analysisToleranceText = analysisTolerance.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void DrawVariableResult(bool running)
    {
        if (running)
        {
            Theme.TextDimmed(L.T("timing.analysis.running"));
            return;
        }
        if (analysisOutcome == null)
        {
            Theme.TextDimmed(L.T("timing.analysis.none"));
            return;
        }
        if (analysisOutcome.Error != null || analysisOutcome.Variable == null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(analysisOutcome.Error ?? L.T("timing.analysis.failed"));
            ImGui.PopStyleColor();
            return;
        }
        var result = analysisOutcome.Variable;
        ImGui.TextUnformatted(L.T("timing.analysis.variableSummary", result.Sections.Length, result.Beats.Length,
            $"{result.MinBpm:0.#}", $"{result.MaxBpm:0.#}"));
        int corrections = result.Sections.Count(section => section.Correction);
        if (corrections > 0)
        {
            ImGui.SameLine();
            Theme.TextDimmed(L.T("timing.analysis.corrections", corrections));
        }
        switch (result.Confidence)
        {
            case TimingConfidence.High: ImGui.TextUnformatted(L.T("timing.analysis.confidenceHigh")); break;
            case TimingConfidence.Normal: ImGui.TextUnformatted(L.T("timing.analysis.confidenceNormal")); break;
            default:
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextWarning);
                Theme.TextWrapped(L.T("timing.analysis.approximate"));
                ImGui.PopStyleColor();
                break;
        }
        // Every red line found, on the song's own time line, as it would be added.
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("##analysisSections", 6, flags, new(0, Theme.Px(150))))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, Theme.Px(36));
            ImGui.TableSetupColumn(L.T("timing.analysis.colOffset"));
            ImGui.TableSetupColumn("BPM");
            ImGui.TableSetupColumn(L.T("timing.analysis.colBeats"));
            ImGui.TableSetupColumn(L.T("timing.analysis.colError"));
            ImGui.TableSetupColumn(L.T("timing.analysis.colKind"));
            ImGui.TableHeadersRow();
            for (int i = 0; i < result.Sections.Length; i++)
            {
                var section = result.Sections[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted((i + 1).ToString(CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{Math.Round(analysisOutcome.Start + section.OffsetMs, MidpointRounding.AwayFromZero):0}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(section.Bpm.ToString("0.###", CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(section.Beats.ToString(CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{section.MaxErrorMs:0.0}");
                ImGui.TableNextColumn();
                if (section.Correction)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, Theme.Rgba(255, 170, 60));
                    ImGui.TextUnformatted(L.T("timing.analysis.kindCorrection"));
                    ImGui.PopStyleColor();
                    Theme.Tooltip(L.T("timing.analysis.kindCorrection.tip"));
                }
                else ImGui.TextUnformatted(L.T(i == 0 ? "timing.analysis.kindFirst" : "timing.analysis.kindTempo"));
            }
            ImGui.EndTable();
        }
    }

    private const string ClearAddConfirmId = "###timingAnalysisClearAdd";
    private AnalysisOutcome? clearAddOutcome;

    /// <summary>Asks before the result replaces every red line of the project.</summary>
    private void AskClearAndAdd(AnalysisOutcome outcome)
    {
        clearAddOutcome = outcome;
        ImGui.OpenPopup(L.T("timing.analysis.clearAdd.title") + ClearAddConfirmId);
    }

    /// <summary>
    /// The question before all timing is cleared and the analysed red lines take its place, in one step of the history.
    /// Returns whether it is open, so that Esc closes it and not the analysis window under it.
    /// </summary>
    private bool DrawClearAndAddConfirm()
    {
        var viewport = ImGui.GetMainViewport();
        if (!MainUi.BeginModal(L.T("timing.analysis.clearAdd.title") + ClearAddConfirmId, viewport.Pos + viewport.Size / 2, 420)) return false;
        Theme.TextWrapped(L.T("timing.analysis.clearAdd.text"));
        ImGui.Spacing();
        if (Theme.Button(L.T("common.confirm"), 110) && clearAddOutcome is { } outcome)
        {
            ClearAndAdd(outcome);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (Theme.Button(L.T("common.cancel"), 110) || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
        return true;
    }

    /// <summary>Clears every red line of the project and adds the result in their place: fixed tempo as one red line, variable as all of its sections.</summary>
    private void ClearAndAdd(AnalysisOutcome outcome)
    {
        List<(double Offset, double Bpm)> sections;
        int meter;
        if (outcome.Variable is { } variable)
        {
            sections = variable.Sections.Select(s => (outcome.Start + s.OffsetMs, s.Bpm)).ToList();
            meter = analysisMeter;
        }
        else
        {
            sections = new() { (outcome.Offset, outcome.Result!.Bpm) };
            meter = outcome.Result.Meter;
        }
        int added = app.AddAnalysedTiming(sections, double.NegativeInfinity, double.PositiveInfinity, true, meter);
        rowsStale = true;
        app.SetStatus(L.T("timing.analysis.clearAdded", added));
    }

    /// <summary>Adds every red line of the variable-tempo result in one step of the history.</summary>
    private void AddAnalysedSections(AnalysisOutcome outcome)
    {
        var sections = outcome.Variable!.Sections.Select(s => (outcome.Start + s.OffsetMs, s.Bpm)).ToList();
        int added = app.AddAnalysedTiming(sections, outcome.Start, outcome.End, analysisReplace, analysisMeter);
        rowsStale = true;
        app.SetStatus(L.T("timing.analysis.addedAll", added));
    }

    /// <summary>
    /// Adds the result as a red line in the chosen metre: the offset in whole milliseconds and the BPM to three decimals, as the editor
    /// keeps them. Refused with the reason when a red line is already there.
    /// </summary>
    private void AddAnalysedLine(AnalysisOutcome outcome)
    {
        int offset = (int)Math.Round(outcome.Offset, MidpointRounding.AwayFromZero);
        double bpm = Math.Round(outcome.Result!.Bpm, 3);
        int meter = outcome.Result.Meter;
        string? error = TimingPointEditing.Validate(app.Project!.Settings.Timing, offset, bpm, meter);
        if (error == null && app.AddTimingOffset(offset, bpm, meter))
        {
            rowsStale = true;
            app.SetStatus(L.T("timing.analysis.added", offset, bpm));
            return;
        }
        app.ShowMessage(L.T("input.invalid.title"), error switch
        {
            "timing.duplicateOffset" => L.T("timing.duplicateOffset"),
            "timing.bpmPrecision" => L.T("timing.bpmPrecision"),
            "timing.invalidBpm" => L.T("timing.invalidBpm"),
            _ => L.T("timing.invalidOffset"),
        });
    }

    /// <summary>The published methods the analysis is built on, and the paper that pointed to the approach.</summary>
    private static readonly (string Citation, string? Url)[] AnalysisReferences =
    {
        ("J. P. Bello et al., \"A Tutorial on Onset Detection in Music Signals\", IEEE TSAP 13(5), 2005", "https://doi.org/10.1109/TSA.2005.851998"),
        ("S. Böck, G. Widmer, \"Maximum Filter Vibrato Suppression for Onset Detection\", DAFx-13, 2013", "https://www.dafx.de/paper-archive/2013/papers/09.dafx2013_submission_12.pdf"),
        ("A. Klapuri, \"Sound Onset Detection by Applying Psychoacoustic Knowledge\", ICASSP 1999", "https://doi.org/10.1109/ICASSP.1999.757494"),
        ("D. P. W. Ellis, \"Beat Tracking by Dynamic Programming\", J. New Music Research 36(1), 2007", "https://doi.org/10.1080/09298210701653344"),
        ("P. Grosche, M. Müller, \"Extracting Predominant Local Pulse Information From Music Recordings\", IEEE TASLP 19(6), 2011", "https://doi.org/10.1109/TASL.2010.2096216"),
        ("R. Bristow-Johnson, \"Audio EQ Cookbook\" (W3C Note)", "https://www.w3.org/TR/audio-eq-cookbook/"),
        ("M. O'Malley, \"Dance Dance ConvLSTM\", arXiv:2507.01644, 2025 (BPM by B. van Wetering's non-causal beat tracking for rhythm games)", "https://arxiv.org/abs/2507.01644"),
    };

    private void DrawAnalysisReferences()
    {
        if (!ImGui.CollapsingHeader(L.T("timing.analysis.papers"))) return;
        foreach (var (citation, url) in AnalysisReferences)
        {
            if (url == null)
            {
                Theme.TextWrapped("• " + citation);
                continue;
            }
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
            Theme.TextWrapped("• " + citation);
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                Theme.SetTooltip(L.T("timing.analysis.paper.tip") + Environment.NewLine + url);
            }
            if (ImGui.IsItemClicked())
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { app.SetStatus(url); }
            }
        }
    }
}
