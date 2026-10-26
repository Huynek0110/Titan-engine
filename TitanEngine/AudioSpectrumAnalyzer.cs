using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TitanEngine
{
    /// <summary>
    /// Decodes a short slice of a video or audio file to raw PCM and turns it into the
    /// per-frame waveform / spectrum / onset data consumed by the SoundBar visualizer.
    /// <para>
    /// Every entry point is failure tolerant. A failed analysis returns <c>null</c> (or flat,
    /// silent data) and never throws: a broken soundbar must disable the visualizer, it must
    /// never break a render. Cancellation is reported as a <c>null</c> result as well, so callers
    /// never have to catch <see cref="OperationCanceledException"/>.
    /// </para>
    /// </summary>
    public static class AudioSpectrumAnalyzer
    {
        /// <summary>Radix-2 FFT length used for every spectrum. Must stay a power of two.</summary>
        public const int DefaultFftSize = 2048;

        /// <summary>Every decode is normalised to this PCM sample rate.</summary>
        public const int DefaultSampleRate = 22050;

        /// <summary>Number of log-spaced bands produced per frame.</summary>
        public const int DefaultBandCount = 64;

        /// <summary>Number of min/max waveform columns produced per frame.</summary>
        public const int DefaultWaveformColumns = 512;

        /// <summary>Lower frequency edge of the first band, in Hz.</summary>
        public const float DefaultFMin = 40f;

        /// <summary>Upper frequency edge of the last band, in Hz. Clamped down to Nyquist.</summary>
        public const float DefaultFMax = 16000f;

        /// <summary>
        /// Attack time constant, in seconds, of the band / RMS / peak envelope.
        /// <para>
        /// It was 0.015 s, which is very nearly zero at any real frame rate: with
        /// <c>k = 1 - exp(-dt / tau)</c> the old constant closed 89 % of the gap to the target in a
        /// single 30 fps frame (<c>k = 0.892</c>), 43 % at 120 fps and 99.9 % at 10 fps. Only the
        /// 0.130 s release was ever visible, so the bars snapped up and floated down - the
        /// opposite of the bounce the envelope exists to produce. At 0.06 s the same step closes
        /// 43 % per frame at 30 fps and 13 % at 120 fps, so the rise takes about two frames at
        /// normal rates while the release still outlasts it by 2.2x. (At 10 fps a single frame is
        /// 0.1 s - longer than the time constant itself - so the rise is still nearly per-frame
        /// there; that is the frame rate, not the constant.)
        /// </para>
        /// </summary>
        public const double DefaultAttackTau = 0.06;

        /// <summary>Release time constant, in seconds, of the band / RMS / peak envelope.</summary>
        public const double DefaultReleaseTau = 0.130;

        /// <summary>Streaming read size for raw PCM. Even, so samples never straddle chunks.</summary>
        private const int PcmReadBufferSize = 1024 * 1024;

        /// <summary>How many parent directories above the binary to probe for a bundled ffmpeg.</summary>
        private const int FfmpegParentWalkDepth = 5;

        /// <summary>SHA-256 prefix kept for the fingerprint (12 bytes = 24 hex characters).</summary>
        private const int FingerprintByteCount = 12;

        /// <summary>
        /// Time constant, in seconds, of the onset detector's adaptive-mean EMA. This replaces the
        /// frame-based rate of 0.02, whose 50-frame constant meant 1.67 s at 30 fps but 0.83 s at
        /// 60 fps and 5 s at 10 fps. Expressing it in seconds keeps the baseline tracking at the
        /// same musical speed whatever the render frame rate; 1.0 / 0.02 = 50 frames at 30 fps,
        /// which is 50 / 30 s. The EMA alpha is then 1 - exp(-dt / tau).
        /// </summary>
        private const double OnsetMeanTauSeconds = 50.0 / 30.0;

        /// <summary>Flux must exceed the adaptive mean by this factor to fire an onset.</summary>
        private const double OnsetSensitivity = 1.6;

        /// <summary>
        /// Minimum time between two onsets, in seconds. This replaces the old frame count of 4,
        /// which meant 0.400 s at 10 fps but only 0.033 s at 120 fps - a five-frame minimum gap
        /// there rejects most of what passes at 30 fps. Converted the same way as
        /// <see cref="OnsetMeanTauSeconds"/>: 4 frames at 30 fps is 4 / 30 = 0.133 s.
        /// </summary>
        private const double OnsetRefractorySeconds = 0.13;

        /// <summary>
        /// Absolute level gate for onsets, in linear RMS; 1e-3 is about -60 dBFS.
        /// <para>
        /// The flux test is purely relative (<c>flux &gt; mean * OnsetSensitivity</c>), which makes it
        /// scale invariant: it fires just as readily on a fade from -20 dBFS down to -100 dBFS as
        /// on a full-scale hit, and on audio that is only a couple of LSB of dither above digital
        /// silence, where the mean is itself noise and almost every frame is a 1.6x outlier. A
        /// purely relative threshold can never block those cases, so this absolute floor on the
        /// frame's raw, pre-envelope RMS is checked first.
        /// </para>
        /// </summary>
        private const double OnsetMinRms = 1e-3;

        /// <summary>Samples with |x| below this count as digital silence (about -80 dBFS).</summary>
        private const float SilenceSampleThreshold = 1e-4f;

        /// <summary>
        /// Per-band magnitude epsilon fed to the logarithm, so an empty band reads a finite
        /// -180 dBFS instead of -infinity and the level mapping can never produce a NaN.
        /// </summary>
        private const double BandMagnitudeEpsilon = 1e-9;

        /// <summary>
        /// Loudness dimmer and minimum ramp width, in dBFS. The frame-relative floor may never
        /// sit below it, so it bounds how much of a quiet frame's range the mapping can throw away,
        /// and a frame whose own peak is at or under it gets a ramp only as wide as the gap from
        /// its floor to its ceiling.
        /// <para>
        /// It is no longer a "nothing is audible down here" threshold. The frame gate at
        /// <see cref="BandFrameSilenceDb"/> does that job, and it is this constant that bounds the
        /// ramp: it replaced the -96 dBFS floor because a measured log-band array spans only about
        /// 43 dB, so a floor 96 dB under a -22 dBFS peak gave an ~89 dB ramp and pushed every
        /// band that actually carries programme content into its top few per cent.
        /// </para>
        /// <para>
        /// Cost, stated plainly: the backstop is a step, not a fade. A frame peaking at -72 dBFS
        /// maps entirely to zero bands, while one peaking a single dB higher already renders its
        /// loudest band at ~0.2. The <see cref="BandFrameFadeDb"/> ramp only reaches down to
        /// -80 dBFS, so it cannot smooth that edge.
        /// </para>
        /// </summary>
        private const double BandFloorAbsoluteDb = -72.0;

        /// <summary>
        /// How far below the loudest band of the current frame the reference floor is anchored, in
        /// dB. Blended with <see cref="BandFloorAbsoluteDb"/> and clamped up to it, it gives every
        /// frame roughly the same visible range whether the passage is quiet or loud.
        /// <para>
        /// 54 dB is the measured span of a real band array (about 43 dB from the strongest band to
        /// the weakest that still carries content) plus margin. It replaces 72 dB, which was sized
        /// while the ceiling was still fixed at full scale: deepening a floor only stretches the
        /// ramp, so a -22 dBFS peak was pushed further into the top of it (0.600), not lifted out
        /// of it. Deepening the floor was never what raises loud bands.
        /// </para>
        /// </summary>
        private const double BandFloorRelativeBelowPeakDb = 54.0;

        /// <summary>
        /// Weight of the relative (frame-peak) floor against the absolute one: 0 is purely
        /// absolute, 1 is purely relative. The blend is always clamped up to
        /// <see cref="BandFloorAbsoluteDb"/> afterwards.
        /// <para>
        /// 1.0 is the only value that works alongside a peak-anchored ceiling. At the old 0.75 the
        /// floor was still dragged a quarter of the way back to the absolute value, which puts a
        /// fixed term back into a reference that is supposed to be frame-relative and drags the
        /// top of the ramp down again for loud frames.
        /// </para>
        /// </summary>
        private const double BandFloorRelativeWeight = 1.0;

        /// <summary>
        /// How far above the loudest band of the current frame the top of the band ramp sits, in
        /// dB. The mapping's upper anchor.
        /// <para>
        /// This was a fixed <c>BandCeilingDb = 0.0</c> - full scale - while the floor tracked the
        /// frame peak. Real programme peaks 12-25 dB below full scale, so the top of the ramp was
        /// structurally unreachable: a -22 dBFS frame put its loudest band at 0.600 and the whole
        /// array inside 0.10-0.58, and the measured spread sat in 0.096-0.578. Four successive
        /// fixes each read that as a floor problem - a deeper floor, a relative floor, a gamma, a
        /// renderer-side tilt - and none of them could lift a ceiling that was 22 dB above the
        /// signal. Fixing the floor alone changes the distribution; only raising the ceiling puts
        /// loud bands near 1.0.
        /// </para>
        /// <para>
        /// <b>Trade-off, stated rather than hidden:</b> with the ceiling anchored to the peak, every
        /// frame peaking above roughly -18 dBFS renders its loudest band at the same ~0.944. So
        /// cross-frame loudness dynamics live below about -50 dBFS, and two loud passages of very
        /// different loudness look equally loud. That is the unavoidable cost of letting loud bands
        /// reach the top of the plot; the alternative is the defect this replaces. Per-frame
        /// absolute loudness remains available to the renderer through
        /// <see cref="AudioFrameData.Rms"/> and <see cref="AudioFrameData.Peak"/>.
        /// </para>
        /// <para>
        /// Note that no band ever reaches 1.0: <c>framePeakDb</c> is the maximum over the bands, so no
        /// band can sit above the ceiling and <c>t</c> tops out at <c>1.06 * 54 / 57 = 1.004</c>,
        /// which the linear branch renders as 0.944. That is the peak band of every frame above
        /// about -18 dBFS; once the absolute backstop takes over as the floor (peak below -18
        /// dBFS) the peak band lands lower still, 0.909 at a -40 dBFS peak. The 6 % shortfall is
        /// the soft knee's own headroom - see <see cref="BandKneeFraction"/> - and is deliberate.
        /// </para>
        /// </summary>
        private const double BandCeilingHeadroomDb = 3.0;

        /// <summary>
        /// Fraction of a frame's range, measured up from its reference floor, that is covered by
        /// the quadratic soft knee. Below it the mapping grows as t squared, so a band sitting a dB
        /// or two off the floor reads as a sliver rather than a jump - which is what keeps noise
        /// floors from producing large bars.
        /// </summary>
        private const double BandKneeFraction = 0.12;

        /// <summary>
        /// Frames whose loudest band is at or below this level are treated as silence and emit
        /// all-zero bands, so digital silence and dither can never be normalised into motion.
        /// It matches the decode-time silence threshold, which is about -80 dBFS.
        /// </summary>
        private const double BandFrameSilenceDb = -80.0;

        /// <summary>
        /// Width of the per-frame fade-in above <see cref="BandFrameSilenceDb"/>, in dB, so the bars
        /// taper to zero as a passage fades out instead of snapping off on the gated frame.
        /// </summary>
        private const double BandFrameFadeDb = 8.0;

        /// <summary>
        /// Absolute audibility gate. The band mapping normalises EVERY frame against its own
        /// loudest band, which is right for the plot's shape but wrong for liveness: a -50 dBFS
        /// passage is inaudible in the mix yet maps to the same near-full bars as a mastered
        /// chorus, so hiss and room tone dance exactly like music. This gate multiplies all
        /// mapped values by a fade anchored to the frame's absolute RMS, so near-silence stills
        /// the figure. Measured anchors on a real track (12 s window, 1%..5% of frames at
        /// -34.8..-30.4 dBFS RMS) versus a music passage attenuated to inaudibility (-52 dBFS
        /// RMS): -48 dB (off) .. -38 dB (full) leaves every audible moment at full gain while
        /// room tone and dither are frozen.
        /// </summary>
        private const double AudibleFloorDb = -48.0;
        private const double AudibleFadeDb = 10.0;

        /// <summary>Upper spectral edge, in Hz, used by the spectral-flux onset detector.</summary>
        private const double OnsetFluxMaxHz = 5000.0;

        /// <summary>
        /// Hz edges of the loudness summary fields: bass 20-150 Hz, mid 150-2000 Hz, treble
        /// 2000 Hz-Nyquist. Defined in Hz rather than as band-index fractions because log bands
        /// starting at 40 Hz are so compressed down there that an index fraction of "8 %" covers
        /// only 40-62 Hz, which misses bass guitar and most low-mid energy.
        /// </summary>
        private const float BassLowHz = 20f;
        private const float BassHighHz = 150f;
        private const float MidHighHz = 2000f;

        /// <summary>
        /// Vocal-catch band edges, in Hz, chosen by the harness sweep (three 12 s windows of a
        /// real track, scores correlated against a model-separated vocal stem). 350 Hz keeps
        /// snare body and low guitar / synth fundamentals out of the feature; 3050 Hz covers
        /// the sung formant core without picking up the bright air band the instrumental
        /// arranges. The wider 300-3400 tested, and measured worse: mean level during
        /// instrumental-only frames stayed ~0.06 higher at identical correlations - the lower
        /// floor energy was not voice and the map never recovered it.
        /// </summary>
        private const float VoiceLowHz = 350f;
        private const float VoiceHighHz = 3050f;

        /// <summary>Vocal envelope: attack reaches a syllable in ~ 60 ms; release 0.20 s was
        /// picked over the 0.40 s the handoff originally suggested because the sweep measured
        /// the slow release smearing the feature across intra-phrase gaps: instrumental-frame
        /// mean voice fell from 0.45 to 0.33 at equal correlation and equal vocal-frame mean.
        /// Unsymmetric on purpose - see the onset detector comment about why a symmetric
        /// smoother hides leading edges.</summary>
        private const double VoiceAttackTau = 0.06;
        private const double VoiceReleaseTau = 0.20;

        /// <summary>
        /// Minimum p15..p95 spread of the loop's vocal-energy statistic, in dB, below which the
        /// feature is declared "no voice in this loop" and pinned to zero. A silent loop or an
        /// instrumental-only stem still has spread from dither and reverb tails, but a real
        /// vocal passage spans tens of dB.
        /// </summary>
        private const double VoiceStatsMinSpreadDb = 0.5;

        /// <summary>
        /// Loop-relative percentile anchors of the vocal-energy statistic, chosen by the harness
        /// sweep across three windows: p15 pinned the map's floor inside the instrumental noise
        /// bed (mean voice during instrumental-only frames measured 0.62) because a pop mix is
        /// roughly 30-45 % instrumental per window. Anchoring the floor at the 40th percentile -
        /// "the quieter END of singing", not "the middle of noise" - maps instrumental frames
        /// into the knee at ~0.3 while keeping sung frames above 0.55, on all three windows,
        /// with the highest envelope correlation of the grid that satisfies both.
        /// </summary>
        private const double VoiceStatsLowPercentile = 0.40;
        private const double VoiceStatsHighPercentile = 0.95;

        /// <summary>Longest ffmpeg stderr fragment echoed into the log.</summary>
        private const int MaxFfmpegStderrLogChars = 1200;

        /// <summary>Hard ceiling on the analysed window, in seconds, so memory stays bounded.</summary>
        private const double MaxLoopSeconds = 60.0;

        /// <summary>Shortest analysed window, in seconds, so a slip still yields a loop.</summary>
        private const double MinLoopSeconds = 0.5;

        /// <summary>Frame-rate ceiling used to bound the frame array size.</summary>
        private const double MaxFrameRate = 120.0;

        /// <summary>
        /// Resolves the audio input, decodes <c>[start, start + loop)</c> to mid/side PCM with
        /// ffmpeg and analyses it frame by frame.
        /// </summary>
        /// <param name="sourceVideoPath">Source video; also used as the fallback audio source.</param>
        /// <param name="externalAudioPath">Optional user-supplied audio track. May be null or missing.</param>
        /// <param name="audioSource">Which stream should feed the visualizer.</param>
        /// <param name="startSeconds">Seek position inside the source, clamped to >= 0.</param>
        /// <param name="loopSeconds">Length of the analysed loop, clamped to 0.5..60 s.</param>
        /// <param name="frameRate">Render frame rate driving the frame count and the envelope dt.</param>
        /// <param name="tempRoot">Directory the decoded PCM is written into. Created when missing.</param>
        /// <param name="onLog">Optional log sink. Safe to pass null.</param>
        /// <param name="token">Cancellation. Cancellation returns null instead of throwing.</param>
        /// <returns>
        /// The complete analysis, or <c>null</c> when ffmpeg is missing, the source is unusable,
        /// the decode failed or the call was cancelled. Silent audio yields a valid all-zero result.
        /// </returns>
        public static async Task<AudioAnalysisResult?> AnalyzeAsync(
            string sourceVideoPath,
            string? externalAudioPath,
            SoundBarAudioSource audioSource,
            double startSeconds,
            double loopSeconds,
            int frameRate,
            string tempRoot,
            Action<string> onLog,
            CancellationToken token,
            string? audio1Path = null,
            string? audio2Path = null)
        {
            // Declared outside the try on purpose: a 5 s slice is about 2.6 MB of .raw, and every
            // path out of this method that has produced it must be able to delete it, including
            // the failure path. Declared inside, anything that threw after ffmpeg succeeded - the
            // fingerprint, the per-frame array clone, an out-of-memory - landed in the catch below
            // with the file still on disk and leaked it once per render.
            string? rawPath = null;

            try
            {
                if (token.IsCancellationRequested)
                    return null;

                string? inputPath = ResolveInputPath(sourceVideoPath, externalAudioPath, audioSource, onLog, audio1Path, audio2Path);
                if (string.IsNullOrEmpty(inputPath))
                    return null;

                string? ffmpegPath = ResolveFfmpegPath();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    onLog?.Invoke("[SOUNDBAR] ffmpeg.exe could not be located; soundbar analysis disabled.");
                    return null;
                }

                double loop = Math.Max(MinLoopSeconds, Math.Min(loopSeconds, MaxLoopSeconds));
                double fps = frameRate <= 0 ? 30.0 : Math.Clamp(frameRate, 1.0, MaxFrameRate);
                int frameCount = Math.Max(1, (int)Math.Round(loop * fps));

                string root = string.IsNullOrWhiteSpace(tempRoot)
                    ? Path.Combine(Path.GetTempPath(), "TitanEngine", "soundbar_analysis")
                    : tempRoot.Trim();
                Directory.CreateDirectory(root);

                rawPath = Path.Combine(root, $"soundbar_{Guid.NewGuid():N}.raw");

                // Frame 0 is the first thing the viewer sees each time the loop repeats, so the
                // window has to start where the music is actually playing. A start of 0 means auto.
                (float[]? decodedSamples, float[]? decodedSide, int sampleRate, double start) = await DecodeLoopWindowAsync(
                    ffmpegPath, inputPath, rawPath, startSeconds, loop, onLog, token).ConfigureAwait(false);

                if (decodedSamples == null)
                    return null;

                float[] samples = decodedSamples;

                onLog?.Invoke(string.Format(
                    CultureInfo.InvariantCulture,
                    "[SOUNDBAR] Analysing {0} frames @ {1:0.##} fps from '{2}' (start {3:0.###}s, loop {4:0.###}s).",
                    frameCount,
                    fps,
                    inputPath,
                    start,
                    loop));


                if (samples.Length == 0)
                {
                    onLog?.Invoke("[SOUNDBAR] Decoded PCM is empty; soundbar analysis disabled.");
                    TryDeleteFile(rawPath);
                    return null;
                }

                // Compare against the loop actually asked for, not samplesPerFrame * frameCount:
                // at a fractional frame rate (29.97) the frame grid needs 110400 samples while the
                // loop only has 110250, so a complete decode was reported as short. This is now a
                // diagnostic only: ReadSample always wraps, so the sample reader no longer needs it.
                long expectedSamples = (long)Math.Round(loop * sampleRate);
                bool loopIsShort = samples.Length < expectedSamples;
                if (loopIsShort)
                {
                    onLog?.Invoke(string.Format(
                        CultureInfo.InvariantCulture,
                        "[SOUNDBAR] Decoded audio ({0} samples) is shorter than the {1:0.###}s loop; the slice will repeat.",
                        samples.Length,
                        loop));
                }

                if (IsSilent(samples))
                    onLog?.Invoke("[SOUNDBAR] Decoded audio is silent; emitting flat bands.");

                AudioFrameData[]? frames = AnalyzeFrames(
                    samples,
                    decodedSide,
                    sampleRate,
                    frameCount,
                    fps,
                    DefaultFftSize,
                    DefaultBandCount,
                    DefaultWaveformColumns,
                    out int effectiveBandCount,
                    onLog,
                    token);

                if (frames == null)
                {
                    TryDeleteFile(rawPath);
                    onLog?.Invoke("[SOUNDBAR] Audio analysis cancelled.");
                    return null;
                }

                onLog?.Invoke($"[SOUNDBAR] Analysed {frames.Length} frames, pcm={rawPath}");

                return new AudioAnalysisResult
                {
                    SampleRate = sampleRate,
                    FftSize = DefaultFftSize,
                    BandCount = DefaultBandCount,
                    EffectiveBandCount = effectiveBandCount,
                    WaveformColumns = DefaultWaveformColumns,
                    FrameDurationSeconds = 1.0 / fps,
                    FrameCount = frames.Length,
                    AudioFilePath = rawPath,
                    AudioFingerprint = BuildFingerprintHex(ComputeFingerprint(samples)),
                    SourcePath = inputPath,
                    Frames = frames
                };
            }
            catch (Exception ex)
            {
                // The decode may already have written megabytes to disk before whatever threw.
                if (rawPath != null)
                    TryDeleteFile(rawPath);

                onLog?.Invoke($"[SOUNDBAR-ANALYSIS-ERROR] {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Finds the stretches of the render timeline where the chosen audio source is actually
        /// audible, for gating the looping soundbar overlay.
        ///
        /// WHY THIS EXISTS. The overlay is a short loop (5-6 s by design) that ffmpeg repeats for
        /// the whole render. On a video whose music starts at 0:18, the analyser's auto loop-start
        /// scan deliberately picks a loud window from the body of the track - so the loop is full
        /// of music and dances from 0:00, over sixteen seconds of true digital silence. Measured on
        /// a real 3:24 render, frames 2 s and 7 s were pixel-identical (PSNR 50.5 dB) while the
        /// audio was -91 dBFS. The analyser's per-frame audibility gate cannot help: it sees the
        /// loud loop window, not the silent timeline.
        ///
        /// This method therefore runs <c>silencedetect</c> over the real source and returns the
        /// complement as half-open intervals in OUTPUT seconds. The caller turns them into an
        /// <c>overlay=enable=</c> expression, so the figure is simply not composited where there is
        /// nothing to visualise. Short musical gaps are ignored (<see cref="TimelineSilenceMinSeconds"/>)
        /// to avoid blinking on every breath.
        ///
        /// Returns <c>null</c> when the scan cannot run - a missing ffmpeg, an unreadable stream,
        /// an unknown duration - so a failure degrades to the previous always-on behaviour rather
        /// than silently hiding the soundbar.
        /// </summary>
        /// <param name="audioPath">The file the analyser listened to (<see cref="AudioAnalysisResult.SourcePath"/>).</param>
        /// <param name="outputDurationSeconds">Length of the rendered timeline, in seconds.</param>
        /// <param name="sourceToOutputScale">
        /// Output seconds per source second: 1.0 at normal speed, 0.5 for a 2x slow-motion render.
        /// The detected source timestamps are multiplied by this to land on the render timeline.
        /// </param>
        public static async Task<List<AudioInterval>?> AnalyzeAudibleIntervalsAsync(
            string audioPath,
            double outputDurationSeconds,
            double sourceToOutputScale,
            Action<string>? onLog,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
                return null;

            if (token.IsCancellationRequested)
                return null;

            if (!(outputDurationSeconds > 0.01))
                return null;

            string? ffmpegPath = ResolveFfmpegPath();
            if (string.IsNullOrEmpty(ffmpegPath))
                return null;

            try
            {
                var inv = CultureInfo.InvariantCulture;
                double scale = sourceToOutputScale > 1e-6 ? sourceToOutputScale : 1.0;
                double sourceLimit = outputDurationSeconds / scale;

                // -t before -i bounds the input read; the render is never longer than the audio.
                string filter = string.Format(
                    inv,
                    "silencedetect=noise={0:0.###}dB:d={1:0.###}",
                    TimelineSilenceDb,
                    TimelineSilenceMinSeconds);
                string arguments = string.Format(
                    inv,
                    "-hide_banner -nostats -t {0:0.###} -i \"{1}\" -vn -map 0:a:0 -af \"{2}\" -f null NUL",
                    sourceLimit,
                    audioPath,
                    filter);

                (int exitCode, string stderr, bool canceled) =
                    await RunFfmpegCaptureAsync(ffmpegPath, arguments, token).ConfigureAwait(false);

                if (canceled || token.IsCancellationRequested)
                    return null;

                if (exitCode != 0)
                {
                    onLog?.Invoke($"[SOUNDBAR] Timeline silence scan failed (exit {exitCode}); the overlay stays always-on.");
                    return null;
                }

                var silences = ParseSilenceIntervals(stderr, sourceLimit);
                var audible = ComplementIntervals(silences, sourceLimit);

                var gated = new List<AudioInterval>(audible.Count);
                foreach (AudioInterval stretch in audible)
                {
                    // A rounding tail (the source length and the detected end differ by a few
                    // samples) is not audio; padding it would create a phantom 0.2 s flash at the
                    // very end of the render. Only stretches that really contain audio get padded.
                    if (stretch.End - stretch.Start < 0.05)
                        continue;

                    double start = Math.Clamp((stretch.Start * scale) - TimelineAudiblePadSeconds, 0.0, outputDurationSeconds);
                    double end = Math.Clamp((stretch.End * scale) + TimelineAudiblePadSeconds, 0.0, outputDurationSeconds);
                    if (end - start < 0.01)
                        continue;

                    if (gated.Count > 0 && start - gated[gated.Count - 1].End <= TimelineAudibleMergeSeconds)
                    {
                        gated[gated.Count - 1].End = Math.Max(gated[gated.Count - 1].End, end);
                    }
                    else
                    {
                        gated.Add(new AudioInterval { Start = start, End = end });
                    }
                }

                // One stretch covering the whole render means there is nothing to gate. Return the
                // canonical form so the caller can skip building an enable expression entirely.
                if (gated.Count == 1 && gated[0].Start <= 0.001 && gated[0].End >= outputDurationSeconds - 0.001)
                {
                    onLog?.Invoke(string.Format(
                        inv,
                        "[SOUNDBAR] Timeline audibility: continuous audio for {0:0.##}s - no overlay gate needed.",
                        outputDurationSeconds));
                    return new List<AudioInterval> { new AudioInterval { Start = 0.0, End = outputDurationSeconds } };
                }

                if (gated.Count == 0)
                {
                    onLog?.Invoke("[SOUNDBAR] Timeline audibility: no audible stretch found - the overlay will be hidden for the whole render.");
                }
                else
                {
                    onLog?.Invoke(string.Format(
                        inv,
                        "[SOUNDBAR] Timeline audibility: {0} audible stretch(es), {1:0.##}s..{2:0.##}s of {3:0.##}s; the overlay is hidden outside them.",
                        gated.Count,
                        gated[0].Start,
                        gated[gated.Count - 1].End,
                        outputDurationSeconds));
                }

                return gated;
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"[SOUNDBAR] Timeline silence scan error: {ex.Message}; the overlay stays always-on.");
                return null;
            }
        }

        /// <summary>Parses ffmpeg's <c>silencedetect</c> stderr into source-time intervals.</summary>
        private static List<AudioInterval> ParseSilenceIntervals(string stderr, double sourceLimit)
        {
            var silences = new List<AudioInterval>();
            double? openStart = null;

            if (string.IsNullOrEmpty(stderr))
                return silences;

            foreach (string rawLine in stderr.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                const string startTag = "silence_start:";
                const string endTag = "silence_end:";

                int index = line.IndexOf(startTag, StringComparison.Ordinal);
                if (index >= 0)
                {
                    if (TryParseGateTimestamp(line, index + startTag.Length, out double started))
                        openStart = started;
                    continue;
                }

                index = line.IndexOf(endTag, StringComparison.Ordinal);
                if (index >= 0 && TryParseGateTimestamp(line, index + endTag.Length, out double ended))
                {
                    double started = openStart ?? 0.0;
                    double start = Math.Clamp(started, 0.0, sourceLimit);
                    double end = Math.Clamp(ended, 0.0, sourceLimit);
                    if (end > start)
                        silences.Add(new AudioInterval { Start = start, End = end });
                    openStart = null;
                }
            }

            // A file that ends while still silent never emits silence_end.
            if (openStart.HasValue)
            {
                double start = Math.Clamp(openStart.Value, 0.0, sourceLimit);
                if (sourceLimit > start)
                    silences.Add(new AudioInterval { Start = start, End = sourceLimit });
            }

            return silences;
        }

        /// <summary>Returns <c>[0, limit]</c> minus the supplied silence intervals.</summary>
        private static List<AudioInterval> ComplementIntervals(List<AudioInterval> silences, double limit)
        {
            var audible = new List<AudioInterval>();
            double cursor = 0.0;

            foreach (AudioInterval silence in silences.OrderBy(s => s.Start))
            {
                double start = Math.Clamp(silence.Start, 0.0, limit);
                double end = Math.Clamp(silence.End, 0.0, limit);
                if (start > cursor + 1e-6)
                    audible.Add(new AudioInterval { Start = cursor, End = start });
                cursor = Math.Max(cursor, end);
            }

            if (cursor < limit - 1e-6)
                audible.Add(new AudioInterval { Start = cursor, End = limit });

            return audible;
        }

        /// <summary>Reads the first <c>number</c> after a <c>silence_start:</c> / <c>silence_end:</c> tag.</summary>
        private static bool TryParseGateTimestamp(string line, int valueStart, out double value)
        {
            value = 0.0;
            if (valueStart < 0 || valueStart >= line.Length)
                return false;

            string tail = line.Substring(valueStart).Trim();
            int bar = tail.IndexOf('|');
            if (bar >= 0)
                tail = tail.Substring(0, bar).Trim();

            return double.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Streams a raw little-endian signed 16-bit mono PCM file into normalised float samples.
        /// </summary>
        /// <param name="rawPath">Path to the <c>s16le</c> file written by ffmpeg.</param>
        /// <param name="sampleRate">
        /// Receives the sample rate the file was decoded at, which is always
        /// <see cref="DefaultSampleRate"/> because raw s16le carries no rate of its own and cannot
        /// be asked for one. That makes this assignment and the <c>-ar</c> literal in
        /// <see cref="BuildDecodeArguments"/> two independent hard-coded copies of one number, with
        /// nothing to catch them if they disagree - change both together.
        /// </param>
        /// <returns>
        /// Samples scaled to -1..1. Empty when the path is missing, unreadable or empty.
        /// A trailing odd byte is dropped.
        /// </returns>
        public static float[] ReadRawS16MonoToFloat(string rawPath, out int sampleRate)
        {
            sampleRate = DefaultSampleRate;

            try
            {
                if (string.IsNullOrWhiteSpace(rawPath) || !File.Exists(rawPath))
                    return Array.Empty<float>();

                using var stream = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read, PcmReadBufferSize, useAsync: false);
                long byteLength = stream.Length;
                int sampleCount = (int)Math.Min(int.MaxValue / 2, byteLength / 2);
                if (sampleCount <= 0)
                    return Array.Empty<float>();

                var samples = new float[sampleCount];
                var buffer = new byte[PcmReadBufferSize];
                int written = 0;
                byte pending = 0;
                bool hasPending = false;

                while (written < sampleCount)
                {
                    int wanted = Math.Min(buffer.Length, (sampleCount - written) * 2);
                    int read = 0;
                    while (read < wanted)
                    {
                        int chunk = stream.Read(buffer, read, wanted - read);
                        if (chunk <= 0) break;
                        read += chunk;
                    }

                    if (read <= 0)
                        break;

                    int offset = 0;
                    if (hasPending && read >= 1)
                    {
                        // A single leftover byte from the previous chunk is the low byte of this sample.
                        short carried = unchecked((short)(pending | (buffer[0] << 8)));
                        samples[written++] = carried * (1f / 32768f);
                        offset = 1;
                        hasPending = false;
                    }

                    int usable = read - offset;
                    usable -= usable & 1;
                    if (usable > 0)
                    {
                        ReadOnlySpan<byte> chunkSpan = buffer.AsSpan(offset, usable);
                        for (int i = 0; i < usable; i += 2)
                            samples[written++] = BinaryPrimitives.ReadInt16LittleEndian(chunkSpan.Slice(i, 2)) * (1f / 32768f);
                    }

                    if (offset + usable < read)
                    {
                        pending = buffer[offset + usable];
                        hasPending = true;
                    }
                }

                if (written < sampleCount)
                    Array.Resize(ref samples, written);

                return samples;
            }
            catch (Exception)
            {
                return Array.Empty<float>();
            }
        }

        /// <summary>
        /// Streams a raw little-endian signed 16-bit <b>stereo</b> PCM file into the mid/side
        /// pair the analyzer needs for vocal catching. The decode pipeline (
        /// <see cref="BuildDecodeArguments"/>) has already converted the channels, so this method
        /// only de-interleaves: channel 0 is mid, channel 1 is side, both scaled to -1..1.
        /// </summary>
        /// <param name="rawPath">Path to the interleaved s16le file written by ffmpeg.</param>
        /// <param name="sampleRate">
        /// Receives <see cref="DefaultSampleRate"/> - raw s16le carries no rate. Must stay in
        /// sync with the <c>-ar</c> in <see cref="BuildDecodeArguments"/>.
        /// </param>
        /// <returns>
        /// Two arrays of equal length (the PCM frame count), or two empty arrays when the path is
        /// missing, unreadable or does not hold at least one whole frame. A trailing partial frame
        /// is dropped.
        /// </returns>
        public static (float[] Mid, float[] Side) ReadRawS16StereoMidSide(string rawPath, out int sampleRate)
        {
            sampleRate = DefaultSampleRate;

            try
            {
                if (string.IsNullOrWhiteSpace(rawPath) || !File.Exists(rawPath))
                    return (Array.Empty<float>(), Array.Empty<float>());

                using var stream = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read, PcmReadBufferSize, useAsync: false);
                long byteLength = stream.Length;
                int frameCount = (int)Math.Min(int.MaxValue / 2, byteLength / 4);
                if (frameCount <= 0)
                    return (Array.Empty<float>(), Array.Empty<float>());

                var mid = new float[frameCount];
                var side = new float[frameCount];
                var buffer = new byte[PcmReadBufferSize];
                var pendingBytes = new byte[3];
                int pendingCount = 0;
                int written = 0;
                // Declared once, not inside the read loop: CA2014 (stack growth per iteration).
                Span<byte> pendingFrame = stackalloc byte[4];

                while (written < frameCount)
                {
                    int wanted = Math.Min(buffer.Length, (frameCount - written) * 4);
                    int read = 0;
                    while (read < wanted)
                    {
                        int chunk = stream.Read(buffer, read, wanted - read);
                        if (chunk <= 0) break;
                        read += chunk;
                    }

                    if (read <= 0)
                        break;

                    int offset = 0;
                    if (pendingCount > 0)
                    {
                        // Up to three leftover bytes from the previous chunk complete this frame.
                        int need = 4 - pendingCount;
                        if (read < need)
                        {
                            // Not enough yet (only reachable near EOF); stash and read again.
                            for (int i = 0; i < read; i++)
                                pendingBytes[pendingCount + i] = buffer[i];
                            pendingCount += read;
                            continue;
                        }

                        Span<byte> frame = pendingFrame;
                        for (int i = 0; i < pendingCount; i++)
                            frame[i] = pendingBytes[i];
                        for (int i = 0; i < need; i++)
                            frame[pendingCount + i] = buffer[i];

                        mid[written] = BinaryPrimitives.ReadInt16LittleEndian(frame.Slice(0, 2)) * (1f / 32768f);
                        side[written] = BinaryPrimitives.ReadInt16LittleEndian(frame.Slice(2, 2)) * (1f / 32768f);
                        written++;
                        offset = need;
                        pendingCount = 0;
                    }

                    int usable = read - offset;
                    int wholeFrames = usable / 4;
                    if (wholeFrames > 0)
                    {
                        ReadOnlySpan<byte> chunkSpan = buffer.AsSpan(offset, wholeFrames * 4);
                        for (int f = 0; f < wholeFrames; f++)
                        {
                            ReadOnlySpan<byte> quad = chunkSpan.Slice(f * 4, 4);
                            mid[written + f] = BinaryPrimitives.ReadInt16LittleEndian(quad.Slice(0, 2)) * (1f / 32768f);
                            side[written + f] = BinaryPrimitives.ReadInt16LittleEndian(quad.Slice(2, 2)) * (1f / 32768f);
                        }

                        written += wholeFrames;
                    }

                    int leftover = usable - (wholeFrames * 4);
                    if (leftover > 0)
                    {
                        for (int i = 0; i < leftover; i++)
                            pendingBytes[i] = buffer[offset + (wholeFrames * 4) + i];
                        pendingCount = leftover;
                    }
                }

                if (written < frameCount)
                {
                    Array.Resize(ref mid, written);
                    Array.Resize(ref side, written);
                }

                return (mid, side);
            }
            catch (Exception)
            {
                return (Array.Empty<float>(), Array.Empty<float>());
            }
        }

        /// <summary>
        /// Builds the <c>bandCount + 1</c> frequency edges, in Hz, of a log-spaced band layout.
        /// </summary>
        /// <param name="sampleRate">PCM sample rate; its Nyquist frequency caps the upper edge.</param>
        /// <param name="fftSize">FFT length used for the analysis.</param>
        /// <param name="bandCount">Number of bands; clamped to at least 1.</param>
        /// <param name="fMin">Lower edge in Hz; values below 1 Hz are raised to 1 Hz.</param>
        /// <param name="fMax">Upper edge in Hz; clamped to Nyquist and kept above <paramref name="fMin"/>.</param>
        /// <returns>
        /// Strictly increasing edges in Hz, length <c>bandCount + 1</c>. The caller converts
        /// them to FFT bins and must still guarantee a band spans at least one bin.
        /// </returns>
        public static float[] BuildLogBandEdges(int sampleRate, int fftSize, int bandCount, float fMin, float fMax)
        {
            int bands = Math.Max(1, bandCount);
            int rate = Math.Max(1, sampleRate);
            double nyquist = rate * 0.5;

            double lo = fMin;
            if (double.IsNaN(lo) || double.IsInfinity(lo) || lo <= 1.0)
                lo = 1.0;

            double hi = fMax;
            if (double.IsNaN(hi) || double.IsInfinity(hi) || hi <= 1.0)
                hi = nyquist;
            hi = Math.Min(hi, nyquist);
            if (hi <= lo)
                hi = Math.Min(nyquist, lo * 2.0);
            if (hi <= lo)
                hi = lo * 2.0;

            // fftSize does not constrain the edges themselves; the caller maps them onto bins
            // and is responsible for guaranteeing each band spans at least one bin.
            _ = fftSize;

            var edges = new float[bands + 1];
            double logLo = Math.Log(lo);
            double logHi = Math.Log(hi);
            for (int b = 0; b <= bands; b++)
            {
                double hz = Math.Exp(logLo + ((logHi - logLo) * b / bands));
                edges[b] = (float)(b == bands ? hi : Math.Clamp(hz, lo, hi));
            }

            edges[0] = (float)lo;
            return edges;
        }

        /// <summary>
        /// Advances a one-pole attack/release envelope by <paramref name="dtSeconds"/>.
        /// This is what makes the soundbar bars bounce; without it they look dead.
        /// </summary>
        /// <param name="previous">Envelope value of the previous frame.</param>
        /// <param name="target">Raw frame value being approached.</param>
        /// <param name="dtSeconds">Time since the previous frame.</param>
        /// <param name="attackTau">Time constant used while the value rises.</param>
        /// <param name="releaseTau">Time constant used while the value falls.</param>
        /// <returns>The smoothed value for the current frame.</returns>
        public static float EnvelopeStep(float previous, float target, double dtSeconds, double attackTau, double releaseTau)
        {
            double tau = target > previous ? attackTau : releaseTau;
            double k = 1.0 - Math.Exp(-dtSeconds / Math.Max(1e-6, tau));
            return (float)(previous + (target - previous) * k);
        }

        /// <summary>
        /// Maps one band's level onto 0..1 against its frame's reference floor, with a quadratic
        /// soft knee over the bottom <see cref="BandKneeFraction"/> of the range and a linear ramp
        /// above it.
        /// <para>
        /// The two pieces meet exactly at the knee: both give <c>KNEE / 2</c> at
        /// <c>t == KNEE</c>, and with <paramref name="span"/> scaled by <c>1 + KNEE / 2</c> the
        /// linear branch reaches exactly 1.0 at the frame's ceiling
        /// (<c>framePeakDb + BandCeilingHeadroomDb</c>). The result is non-decreasing in
        /// <paramref name="decibels"/>, which is what keeps the bars in order, and it is exactly 0
        /// for any band at or below <paramref name="referenceDb"/>, so the soft knee - not the hard
        /// clamp - is what decides how quietly the floor appears.
        /// </para>
        /// </summary>
        /// <param name="decibels">The band's level in dBFS.</param>
        /// <param name="referenceDb">Level that maps to 0 for this frame; never below the absolute floor.</param>
        /// <param name="span">dB from <paramref name="referenceDb"/> up to the frame's ceiling; greater than 0.</param>
        private static double MapBandLevel(double decibels, double referenceDb, double span)
        {
            double t = (decibels - referenceDb) / span;
            if (double.IsNaN(t) || t <= 0.0)
                return 0.0;

            if (t < BandKneeFraction)
                return (t * t) / (2.0 * BandKneeFraction);

            double value = t - (0.5 * BandKneeFraction);
            return value >= 1.0 ? 1.0 : value;
        }

        /// <summary>
        /// Hashes decoded PCM so a cached PNG sequence can never be reused for another track.
        /// </summary>
        /// <param name="samples">Decoded samples. Null or empty hashes to an empty array.</param>
        /// <returns>
        /// The first 12 bytes of the SHA-256 digest, which render as the 24 lowercase hex
        /// characters expected by <see cref="AudioAnalysisResult.AudioFingerprint"/>.
        /// </returns>
        public static byte[] ComputeFingerprint(float[] samples)
        {
            if (samples == null || samples.Length == 0)
                return Array.Empty<byte>();

            byte[] digest = SHA256.HashData(MemoryMarshal.AsBytes(samples.AsSpan()));
            if (digest.Length <= FingerprintByteCount)
                return digest;

            var fingerprint = new byte[FingerprintByteCount];
            Array.Copy(digest, fingerprint, FingerprintByteCount);
            return fingerprint;
        }

        /// <summary>Renders a fingerprint as the lowercase hex form stored on the result.</summary>
        private static string BuildFingerprintHex(byte[] fingerprint)
        {
            if (fingerprint == null || fingerprint.Length == 0)
                return string.Empty;

            return Convert.ToHexString(fingerprint).ToLowerInvariant();
        }

        /// <summary>Picks the file ffmpeg should decode, honouring the requested source preference.</summary>
        private static string? ResolveInputPath(
            string sourceVideoPath,
            string? externalAudioPath,
            SoundBarAudioSource audioSource,
            Action<string>? onLog,
            string? audio1Path = null,
            string? audio2Path = null)
        {
            string? external = string.IsNullOrWhiteSpace(externalAudioPath) ? null : externalAudioPath.Trim();
            string? video = string.IsNullOrWhiteSpace(sourceVideoPath) ? null : sourceVideoPath.Trim();
            string? audio1 = string.IsNullOrWhiteSpace(audio1Path) ? null : audio1Path.Trim();
            string? audio2 = string.IsNullOrWhiteSpace(audio2Path) ? null : audio2Path.Trim();
            bool hasExternal = external != null && File.Exists(external);
            bool hasVideo = video != null && File.Exists(video);
            bool hasAudio1 = audio1 != null && File.Exists(audio1);
            bool hasAudio2 = audio2 != null && File.Exists(audio2);

            if (audioSource == SoundBarAudioSource.Audio1)
            {
                if (hasAudio1)
                {
                    onLog?.Invoke($"[SOUNDBAR] Audio source: AUDIO 1 track '{audio1}'");
                    return audio1;
                }

                onLog?.Invoke("[SOUNDBAR] Audio 1 requested but the track is missing - falling back to the external/auto source.");
            }

            if (audioSource == SoundBarAudioSource.Audio2)
            {
                if (hasAudio2)
                {
                    onLog?.Invoke($"[SOUNDBAR] Audio source: AUDIO 2 track '{audio2}' (the visualiser now follows this track only)");
                    return audio2;
                }

                onLog?.Invoke("[SOUNDBAR] Audio 2 requested but the track is missing - falling back to the external/auto source.");
            }

            if (audioSource == SoundBarAudioSource.Video)
            {
                if (hasVideo)
                {
                    onLog?.Invoke($"[SOUNDBAR] Audio source: video stream of '{video}'");
                    return video;
                }

                onLog?.Invoke($"[SOUNDBAR] Requested the video's own audio but '{sourceVideoPath}' does not exist.");
                return null;
            }

            // Auto and External both prefer the external track and fall back to the video.
            if (hasExternal)
            {
                onLog?.Invoke($"[SOUNDBAR] Audio source: external track '{external}'");
                return external;
            }

            if (!string.IsNullOrEmpty(external))
                onLog?.Invoke($"[SOUNDBAR] External audio '{external}' is missing; falling back to the source video.");

            if (hasVideo)
            {
                onLog?.Invoke($"[SOUNDBAR] Audio source: video stream of '{video}'");
                return video;
            }

            onLog?.Invoke("[SOUNDBAR] No usable audio source; soundbar analysis disabled.");
            return null;
        }

        /// <summary>
        /// Builds the ffmpeg decode command. The codec, rate and channel count are fixed so the
        /// reader downstream can assume 16-bit mono PCM.
        /// <para>
        /// The <c>-ar 22050</c> literal below and the <see cref="DefaultSampleRate"/> that
        /// <see cref="ReadRawS16MonoToFloat"/> reports are two separate hard-coded copies of the
        /// same number, because raw s16le has no header to carry the rate. Nothing checks them
        /// against each other: parameterise one and the other silently lies, and every derived
        /// duration (frame hop, window length, loop length) is off by the ratio. Change both.
        /// </para>
        /// </summary>
        /// <summary>
        /// The energy scan never looks at more than this many seconds of the source. A fade-in lives
        /// in the first few seconds, and decoding an entire feature-length track to find it would
        /// cost more than the scan saves.
        /// </summary>
        private const double MaxAutoScanSeconds = 120.0;

        /// <summary>
        /// Absolute level at or below which a stretch of the render timeline counts as silent.
        /// Matches <c>AudibleFloorDb</c> so the two stages agree on what "nothing is playing" means.
        /// </summary>
        private const double TimelineSilenceDb = -48.0;

        /// <summary>
        /// A gap shorter than this is a musical pause (a drum fill, a breath), not silence. Gating
        /// on those would blink the figure off for half a beat every few bars.
        /// </summary>
        private const double TimelineSilenceMinSeconds = 1.0;

        /// <summary>
        /// The overlay is allowed to appear this much before the measured audio start and to stay
        /// this much after it ends, so a fade-in does not clip the first attack and a reverb tail
        /// is not cut dead.
        /// </summary>
        private const double TimelineAudiblePadSeconds = 0.2;

        /// <summary>Audible stretches separated by less than this are merged into one gate.</summary>
        private const double TimelineAudibleMergeSeconds = 0.5;

        /// <summary>
        /// How strongly a candidate window's END level is allowed to disagree with its START level,
        /// in "dB of penalty per dB of mismatch". The wrap is a hard cut from the last frame back to
        /// frame 0, so a window that ends loud and begins quiet pops every time it repeats - and a
        /// window that is merely quiet draws nothing. This trades the two off.
        /// </summary>
        private const double LoopWrapMismatchPenaltyDb = 2.0;

        /// <summary>Scan results, keyed by source + loop length. Re-probing the same track is pure waste.</summary>
        private static readonly ConcurrentDictionary<string, double> AutoLoopStartCache
            = new ConcurrentDictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Chooses where the visual loop should begin, so that frame 0 is not a near-silent frame.
        ///
        /// WHY THIS EXISTS. The analyser used to take its start offset straight from the caller,
        /// which passes 0 - "from the beginning of the track". Measured on a real 156 s render,
        /// the track fades in: mean level was -76.2 dB at t=0, -58.3 dB at t=2.5 s and -19.5 dB at
        /// t=4.5 s. So a 5 s loop taken from the top is 56.7 dB quieter at its start than at its
        /// end, and every repeat drops the figure to a flat line and re-ramps it. The two-pass
        /// envelope warm-up that was added to fix the loop seam cannot help here, and never could:
        /// the warm-up repairs RECURSIVE STATE, but the content at t=0 really is 56 dB down. It
        /// registers as a seam defect only because the synthetic signal the harness uses has no
        /// fade-in, which is exactly the kind of test that cannot see this class of bug.
        ///
        /// The objective is therefore two-sided: maximise the window's mean level, and penalise the
        /// level step between the window's first and last moments, because that step is exactly what
        /// the wrap exposes.
        ///
        /// One extra ffmpeg pass over the audio, cached per source and loop length. Returns
        /// <paramref name="requestedStartSeconds"/> unchanged when the scan cannot run, so a missing
        /// ffprobe or an unreadable stream degrades to the old behaviour rather than failing the job.
        /// </summary>
private static async Task<(float[]? Samples, float[]? SideSamples, int SampleRate, double Start)> DecodeLoopWindowAsync(
            string ffmpegPath,
            string inputPath,
            string rawPath,
            double requestedStartSeconds,
            double loopSeconds,
            Action<string>? onLog,
            CancellationToken token)
        {
            // A non-zero request is an explicit instruction; never second-guess it.
            if (requestedStartSeconds > 0.001)
            {
                (int directExit, string directErr, bool directCanceled) = await RunFfmpegCaptureAsync(
                    ffmpegPath, BuildDecodeArguments(inputPath, rawPath, requestedStartSeconds, loopSeconds), token).ConfigureAwait(false);

                if (directCanceled || token.IsCancellationRequested)
                {
                    TryDeleteFile(rawPath);
                    onLog?.Invoke("[SOUNDBAR] Audio decode cancelled.");
                    return (null, null, 0, 0.0);
                }

                if (directExit != 0)
                {
                    onLog?.Invoke($"[SOUNDBAR] ffmpeg audio decode failed (exit {directExit}): {TruncateForLog(directErr)}");
                    TryDeleteFile(rawPath);
                    return (null, null, 0, 0.0);
                }

                (float[] direct, float[] directSide) = ReadRawS16StereoMidSide(rawPath, out int directRate);
                if (direct.Length == 0)
                {
                    onLog?.Invoke("[SOUNDBAR] ffmpeg produced no audio data; the source probably has no audio stream.");
                    TryDeleteFile(rawPath);
                    return (null, null, 0, 0.0);
                }

                return (direct, directSide, directRate, requestedStartSeconds);
            }

            // AUTO. Decode a bounded prefix ONCE through the same proven decode path, measure it here
            // in C#, pick the window and slice. Asking ffmpeg for the levels instead (astats fed to
            // ametadata) needs a second process AND a drive-letter escape inside a filter argument,
            // which does not work here: the filter parser reads "file=C\:/..." as the option name "C"
            // followed by garbage, and the graph fails to configure. Measuring samples we already
            // have costs nothing and is exact rather than quantised to one-second slots.
            double scanSeconds = Math.Max(MaxAutoScanSeconds, loopSeconds + 1.0);
            (int scanExit, string scanErr, bool scanCanceled) = await RunFfmpegCaptureAsync(
                ffmpegPath, BuildDecodeArguments(inputPath, rawPath, 0.0, scanSeconds), token).ConfigureAwait(false);

            if (scanCanceled || token.IsCancellationRequested)
            {
                TryDeleteFile(rawPath);
                onLog?.Invoke("[SOUNDBAR] Audio decode cancelled.");
                return (null, null, 0, 0.0);
            }

            float[] prefix;
            float[] prefixSide;
            int rate;

            if (scanExit == 0 && File.Exists(rawPath) && new FileInfo(rawPath).Length > 0L)
            {
                (prefix, prefixSide) = ReadRawS16StereoMidSide(rawPath, out rate);
            }
            else
            {
                // The long scan decode failed where the short one might not have. Retry the original
                // request so a transient scan failure degrades to the old behaviour instead of
                // losing the whole job.
                onLog?.Invoke($"[SOUNDBAR] Loop-start scan failed (exit {scanExit}); retrying from t=0.");
                (int retryExit, string retryErr, bool retryCanceled) = await RunFfmpegCaptureAsync(
                    ffmpegPath, BuildDecodeArguments(inputPath, rawPath, 0.0, loopSeconds), token).ConfigureAwait(false);

                if (retryCanceled || token.IsCancellationRequested)
                {
                    TryDeleteFile(rawPath);
                    onLog?.Invoke("[SOUNDBAR] Audio decode cancelled.");
                    return (null, null, 0, 0.0);
                }

                if (retryExit != 0)
                {
                    onLog?.Invoke($"[SOUNDBAR] ffmpeg audio decode failed (exit {retryExit}): {TruncateForLog(retryErr)}");
                    TryDeleteFile(rawPath);
                    return (null, null, 0, 0.0);
                }

                (prefix, prefixSide) = ReadRawS16StereoMidSide(rawPath, out rate);
            }

            if (prefix.Length == 0)
            {
                onLog?.Invoke("[SOUNDBAR] ffmpeg produced no audio data; the source probably has no audio stream.");
                TryDeleteFile(rawPath);
                return (null, null, 0, 0.0);
            }

            List<double> levels = ComputePerSecondRmsDb(prefix, rate);
            double best = ChooseLoopStart(levels, loopSeconds, out double bestScore, out double scoreAtZero);

            int from = (int)Math.Round(best * rate);
            int length = (int)Math.Round(loopSeconds * rate);
            if (from >= prefix.Length) from = 0;
            if (from + length > prefix.Length) length = prefix.Length - from;
            if (length <= 0)
            {
                TryDeleteFile(rawPath);
                return (null, null, 0, 0.0);
            }

            float[] window = new float[length];
            float[] windowSide = (prefixSide.Length == prefix.Length)
                ? new float[length]
                : Array.Empty<float>();
            Array.Copy(prefix, from, window, 0, length);
            if (windowSide.Length > 0)
                Array.Copy(prefixSide, from, windowSide, 0, length);

            if (best > 0.0)
            {
                int endIdx = Math.Min(levels.Count - 1, (int)Math.Round(loopSeconds) - 1);
                onLog?.Invoke(string.Format(
                    CultureInfo.InvariantCulture,
                    "[SOUNDBAR] Loop start moved to {0:0.##}s (score {1:0.0} vs {2:0.0} at t=0). A window from t=0 would begin {3:0.0} dB {4} where it ends.",
                    best,
                    bestScore,
                    scoreAtZero,
                    Math.Abs(levels[0] - levels[endIdx]),
                    levels[0] < levels[endIdx] ? "below" : "above"));
            }

            TryDeleteFile(rawPath);
            return (window, windowSide, rate, best);
        }

        /// <summary>
        /// Per-second RMS in dB, computed over samples that have already been decoded. Shared with
        /// the cache so a second render of the same track does not re-measure anything.
        /// </summary>
        private static List<double> ComputePerSecondRmsDb(float[] samples, int sampleRate)
        {
            var levels = new List<double>();
            int perSecond = Math.Max(1, sampleRate);

            for (int s = 0; s * perSecond < samples.Length; s++)
            {
                int from = s * perSecond;
                int to = Math.Min(samples.Length, from + perSecond);
                double sumSquares = 0.0;
                for (int i = from; i < to; i++)
                {
                    double v = samples[i];
                    sumSquares += v * v;
                }

                double rms = Math.Sqrt(sumSquares / Math.Max(1, to - from));
                levels.Add(rms > 1e-9 ? (20.0 * Math.Log10(rms)) : -120.0);
            }

            return levels;
        }

        /// <summary>
        /// Picks the window start that maximises mean level minus the wrap's level step.
        /// Candidates are whole seconds, which is coarser than a frame but far cheaper than a
        /// sample-accurate search and lands within 1 s of the optimum in practice.
        /// </summary>
        private static double ChooseLoopStart(List<double> levelsDb, double loopSeconds, out double bestScore, out double scoreAtZero)
        {
            bestScore = double.NegativeInfinity;
            scoreAtZero = 0.0;
            double best = 0.0;

            int window = Math.Max(1, (int)Math.Round(loopSeconds));
            int last = levelsDb.Count - window;
            if (last < 0)
                return 0.0;

            for (int s = 0; s <= last; s++)
            {
                double sum = 0.0;
                for (int i = s; i < s + window; i++)
                    sum += levelsDb[i];
                double mean = sum / window;

                // The wrap cuts straight from the window's final second to its first, so those two
                // are what the viewer sees adjacent. Sample them over a second each rather than
                // single frames, because astats already averages over one.
                double startLevel = levelsDb[s];
                double endLevel = levelsDb[s + window - 1];
                double score = mean - (LoopWrapMismatchPenaltyDb * Math.Abs(endLevel - startLevel));

                if (s == 0)
                    scoreAtZero = score;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = s;
                }
            }

            return best;
        }

        private static string BuildDecodeArguments(string inputPath, string rawPath, double startSeconds, double loopSeconds)
        {
            string start = Math.Max(0.0, startSeconds).ToString("0.###", CultureInfo.InvariantCulture);
            string length = Math.Max(MinLoopSeconds, Math.Min(loopSeconds, MaxLoopSeconds)).ToString("0.###", CultureInfo.InvariantCulture);

            // Mid/side decode for vocal catching. The pan filter rewrites the stereo image as
            // c0 = mid = 0.5L + 0.5R (EXACTLY the coefficients of the legacy `-ac 1` downmix, so
            // bands/RMS/waveform/onsets stay bit-identical to the mono pipeline) and
            // c1 = side = 0.5L - 0.5R, which carries what modern masters spread away from the
            // centre. Vocals sit in the centre, so voice ≈ mid − side; without the side channel
            // that subtraction is impossible and every "vocal" feature is just mid-band loudness.
            // aformat FIRST is mandatory: a mono input has no c1 and pan alone fails the graph.
            // After the mono→stereo upmix both channels are identical, so side = 0 and the voice
            // feature degrades to plain band energy - the correct behaviour for a mono source
            // AND for an all-vocals stem (whole file is voice → side is silence there anyway).
            // Verified against the shipped ffmpeg on stereo, mono and 5.1 inputs.
            const string midSideFilter = "aformat=channel_layouts=stereo,pan=stereo|c0=0.5*c0+0.5*c1|c1=0.5*c0-0.5*c1";
            return string.Format(
                CultureInfo.InvariantCulture,
                "-y -ss {0} -i \"{1}\" -t {2} -vn -map 0:a:0 -af \"{3}\" -ac 2 -ar {4} -c:a pcm_s16le -f s16le \"{5}\"",
                start, inputPath, length, midSideFilter, DefaultSampleRate, rawPath);
        }

        /// <summary>
        /// Locates ffmpeg without touching <c>EngineCore</c>'s private cached path:
        /// beside the binary, then on PATH, then up to <see cref="FfmpegParentWalkDepth"/>
        /// parent directories (the layout used by a portable install).
        /// </summary>
        /// <returns>The full path to ffmpeg.exe, or null when it cannot be found.</returns>
        private static string? ResolveFfmpegPath()
        {
            string baseDirectory = AppContext.BaseDirectory;

            string besideBinary = Path.Combine(baseDirectory, "ffmpeg.exe");
            if (File.Exists(besideBinary))
                return besideBinary;

            string? onPath = ResolveFromEnvironmentPath();
            if (onPath != null)
                return onPath;

            DirectoryInfo? directory = new DirectoryInfo(baseDirectory);
            for (int depth = 0; depth <= FfmpegParentWalkDepth && directory != null; depth++)
            {
                string nested = Path.Combine(directory.FullName, "ffmpeg", "bin", "ffmpeg.exe");
                if (File.Exists(nested))
                    return nested;

                string flat = Path.Combine(directory.FullName, "ffmpeg.exe");
                if (File.Exists(flat))
                    return flat;

                directory = directory.Parent;
            }

            return null;
        }

        /// <summary>Scans PATH for ffmpeg, equivalent to running <c>where.exe ffmpeg</c>.</summary>
        private static string? ResolveFromEnvironmentPath()
        {
            string? pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVariable))
                return null;

            string[] entries = pathVariable.Split(Path.PathSeparator);
            foreach (string rawEntry in entries)
            {
                string entry = rawEntry.Trim().Trim('"');
                if (entry.Length == 0)
                    continue;

                string withExtension = Path.Combine(entry, "ffmpeg.exe");
                if (File.Exists(withExtension))
                    return withExtension;

                string withoutExtension = Path.Combine(entry, "ffmpeg");
                if (File.Exists(withoutExtension))
                    return withoutExtension;
            }

            return null;
        }

        /// <summary>Runs ffmpeg, capturing stderr and killing the process when the token fires.</summary>
        private static async Task<(int ExitCode, string Stderr, bool Canceled)> RunFfmpegCaptureAsync(string ffmpegPath, string arguments, CancellationToken token)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { /* already gone */ }

                // Kill only requests termination. Without waiting for the real exit, the handle
                // is disposed while ffmpeg still holds the .raw open, and TryDeleteFile swallows
                // the sharing violation, leaking a multi-MB file on every cancelled render.
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { /* already gone */ }

                try { _ = await stderrTask; } catch { /* pipe torn down with the process */ }
                return (-1, "cancelled", true);
            }

            string stderr = await stderrTask;
            return (process.ExitCode, stderr, false);
        }

        /// <summary>
        /// Runs the per-frame pipeline: min/max waveform buckets, Hann-windowed FFT, log bands,
        /// attack/release smoothing and spectral-flux onsets. Carries the onset state in a private
        /// working object so concurrent calls never share mutable static state.
        /// </summary>
        /// <param name="effectiveBandCount">
        /// Receives the number of bands that survive duplicate-range collapsing; see
        /// <see cref="ResolveBandRanges"/>.
        /// </param>
        /// <returns>The analysed frames, or null when the call was cancelled.</returns>
        private static AudioFrameData[]? AnalyzeFrames(
            float[] samples,
            float[]? sideSamples,
            int sampleRate,
            int frameCount,
            double frameRate,
            int fftSize,
            int bandCount,
            int waveformColumns,
            out int effectiveBandCount,
            Action<string>? onLog,
            CancellationToken token)
        {
            int binCount = fftSize / 2 + 1;
            var fft = new Radix2Fft(fftSize);
            float[] window = BuildHannWindow(fftSize);
            // FIX 4: 2.0 / fftSize assumes a rectangular window. The Hann window's coherent gain is
            // sum(w) / fftSize = 0.5 - 0.5/fftSize, so scaling that way made a full-scale sinusoid
            // read 0.900 instead of 1.0: a flat -6.02 dB calibration error wasting the top 10 % of
            // every bar's range. sum(w) is computed once here, not per frame.
            double magnitudeScale = 2.0 / WindowSum(window);
            var real = new float[fftSize];
            var imaginary = new float[fftSize];
            var magnitudes = new float[binCount];

            // FIX 3: build the nominal per-band bin ranges, then collapse duplicates and hand the
            // freed slots to the widest (highest-frequency) bands.
            float[] edges = BuildLogBandEdges(sampleRate, fftSize, bandCount, DefaultFMin, DefaultFMax);
            var bandLow = new int[bandCount];
            var bandHigh = new int[bandCount];
            for (int b = 0; b < bandCount; b++)
            {
                int lo = FrequencyToBin(edges[b], sampleRate, fftSize, binCount);
                int hi = FrequencyToBin(edges[b + 1], sampleRate, fftSize, binCount);
                lo = Math.Clamp(lo, 0, binCount - 1);
                hi = Math.Clamp(hi, lo + 1, binCount);
                bandLow[b] = lo;
                bandHigh[b] = hi;
            }

            int bandTotal = ResolveBandRanges(bandLow, bandHigh, bandCount);
            effectiveBandCount = bandTotal;

            if (bandTotal < bandCount)
            {
                onLog?.Invoke(string.Format(
                    CultureInfo.InvariantCulture,
                    "[SOUNDBAR] FFT cannot resolve the low end at {0} bins; {1} of {2} bands carry distinct measurements.",
                    fftSize,
                    bandTotal,
                    bandCount));
            }

            // FIX 8: the loudness ranges are defined in Hz and mapped through FrequencyToBin, so
            // they no longer depend on bandCount. Bass 20-150, mid 150-2000, treble 2000-Nyquist.
            int bassLo = FrequencyToBin(BassLowHz, sampleRate, fftSize, binCount);
            int bassHi = FrequencyToBin(BassHighHz, sampleRate, fftSize, binCount);
            int midHi = FrequencyToBin(MidHighHz, sampleRate, fftSize, binCount);

            int fluxBinLimit = Math.Clamp((int)Math.Round(OnsetFluxMaxHz * fftSize / sampleRate) + 1, 1, binCount);
            double dt = 1.0 / frameRate;

            // FIX 1: the detector's EMA is seconds-based, so dt has to reach its constructor.
            var onsetDetector = new OnsetDetector(binCount, dt);

            // VOCAL CATCH, statistics pre-pass. The vocal feature has to be normalised against the
            // STATISTICS OF THE WHOLE LOOP (quartiles of its own dB level), not against the frame
            // peak the way the bands are: "the singer is louder than the loop's other content" is
            // the discrimination the visualiser needs, and a per-frame anchor throws that away.
            // That anchor needs the full loop's levels before any frame can be mapped, so the
            // levels are collected here in one bounded pre-pass (one FFT pair per frame - about
            // +25 ms for a 5 s loop - all scratch, nothing retained), and the main two-pass
            // envelope loop below just reads the precomputed per-frame dB. The envelope still
            // runs on BOTH warm-up passes, exactly like the bands' envelope and the onset
            // detector, because it is recursive state and frame 0 must continue frame N-1.
            //
            // The side array is exactly as long as the mid array (same decode), and wrap-around
            // reads assume that; a mismatched length is treated as "no side channel", which
            // degrades the feature to plain 300-3400 Hz band energy - the right answer for a
            // mono source, where side really is silence anyway.
            bool hasSide = sideSamples != null && sideSamples.Length == samples.Length;
            if (!hasSide && sideSamples is { Length: > 0 })
                onLog?.Invoke("[SOUNDBAR] Side channel length mismatch; vocal catch falls back to band energy.");

            double[] voiceDecibelsRaw = CollectVoiceDecibels(
                samples, hasSide ? sideSamples : Array.Empty<float>(),
                sampleRate, frameCount, frameRate, fftSize,
                VoiceLowHz, VoiceHighHz, token);
            if (token.IsCancellationRequested)
                return null;

            // Percentiles over the exact dB values the envelope loop will read back, so the
            // mapping seen during warm-up pass 2 equals the mapping computed here.
            double[] sortedVoiceDb = new double[frameCount];
            Array.Copy(voiceDecibelsRaw, sortedVoiceDb, frameCount);
            Array.Sort(sortedVoiceDb);
            int lowIndex = Math.Clamp((int)((double)VoiceStatsLowPercentile * (frameCount - 1)), 0, frameCount - 1);
            int highIndex = Math.Clamp((int)Math.Ceiling((double)VoiceStatsHighPercentile * (frameCount - 1)), 0, frameCount - 1);
            double voicePercentileLow = frameCount > 0 ? sortedVoiceDb[lowIndex] : 0.0;
            double voicePercentileHigh = frameCount > 0 ? sortedVoiceDb[highIndex] : 0.0;
            double voiceSpread = voicePercentileHigh - voicePercentileLow;
            bool voiceTrackable = voiceSpread >= VoiceStatsMinSpreadDb;
            onLog?.Invoke(string.Format(
                CultureInfo.InvariantCulture,
                "[SOUNDBAR] Vocal catch floor(p40)={0:0.##} dB, ceiling(p95)={1:0.##} dB (spread {2:0.##} dB, {3}); {4}.",
                voicePercentileLow,
                voicePercentileHigh,
                voiceSpread,
                voiceTrackable ? "trackable" : "pinned off",
                hasSide ? "mid/side channels present" : "mono source, band-energy fallback"));

            var frames = new AudioFrameData[frameCount];
            var previousBands = new float[bandCount];
            var bands = new float[bandCount];

            // The band level mapping is relative to the loudest band of the current frame, so every
            // band has to be measured in dB before any of them can be converted. This is the
            // per-frame scratch that pass 1 fills and pass 2 reads.
            var bandDecibels = new double[bandCount];
            float previousRms = 0f;
            float previousPeak = 0f;
            float previousVoice = 0f;

            // The attack/release envelope and the onset detector are recursive, so their state at
            // frame 0 depends on what came BEFORE it. Starting from silence makes frame 0 read as a
            // near-empty spectrum, and because the PNG sequence is handed to FFmpeg with
            // `loop=loop=-1`, frame N-1 is followed by frame 0 - so the figure visibly collapses to
            // an empty line and re-ramps every single loop, roughly once every LoopSeconds.
            //
            // The audio window wraps around, so the content that genuinely precedes frame 0 is the
            // TAIL of this same loop. Running the analysis twice and keeping only the second pass's
            // output therefore seeds the envelope with the real predecessor state: frame 0 continues
            // frame N-1 instead of restarting from zero. The renderer already relies on this - its
            // FrameAt(-1) wraps to the last frame - so before this the two halves disagreed.
            //
            // Cost is one extra pass over the frames (~20 ms for a 5 s loop at 30 fps); correctness
            // at the loop seam is worth it.
            const int EnvelopeWarmupPasses = 2;

            for (int pass = 0; pass < EnvelopeWarmupPasses; pass++)
            {
                bool keepResults = pass == EnvelopeWarmupPasses - 1;

            for (int frame = 0; frame < frameCount; frame++)
            {
                if (token.IsCancellationRequested)
                    return null;

                // FIX 2: one window length for every signal. The frame hop is frameRate-driven
                // (735 samples at 22050/30) but the analysis window is the full FFT length (2048),
                // so Bands, WaveMin/WaveMax, Rms and Peak all describe the same time window instead
                // of 93 ms and 33 ms at once, and the spectrum no longer spans three frames of
                // pre-echo. Samples are read with wrap-around, so the tail of a loop continues into
                // its head instead of collapsing into zero padding.
                long frameStart = (long)Math.Round(frame * (double)sampleRate / frameRate);
                long windowEnd = frameStart + fftSize;
                long span = fftSize;

                // Time domain: min/max envelope, never an average, so transients survive.
                var waveMin = new float[waveformColumns];
                var waveMax = new float[waveformColumns];
                for (int column = 0; column < waveformColumns; column++)
                {
                    long bucketStart = frameStart + (long)column * span / waveformColumns;
                    long bucketEnd = frameStart + (long)(column + 1) * span / waveformColumns;
                    if (bucketEnd <= bucketStart)
                        bucketEnd = bucketStart + 1;

                    float min = float.MaxValue;
                    float max = float.MinValue;
                    for (long s = bucketStart; s < bucketEnd; s++)
                    {
                        float value = ReadSample(samples, s);
                        if (value < min) min = value;
                        if (value > max) max = value;
                    }

                    waveMin[column] = min == float.MaxValue ? 0f : min;
                    waveMax[column] = max == float.MinValue ? 0f : max;
                }

                // Frequency domain: Hann window over the same window the bands and loudness use.
                for (int k = 0; k < fftSize; k++)
                {
                    real[k] = ReadSample(samples, frameStart + k) * window[k];
                    imaginary[k] = 0f;
                }

                fft.Forward(real, imaginary);

                for (int k = 0; k < binCount; k++)
                {
                    double re = real[k];
                    double im = imaginary[k];
                    magnitudes[k] = (float)(Math.Sqrt((re * re) + (im * im)) * magnitudeScale);
                }

                // Log bands, pass 1: the mean (not sum) per band, converted to dBFS. Nothing is
                // mapped yet, because the reference level is the loudest band of THIS frame.
                double framePeakDb = double.NegativeInfinity;
                for (int b = 0; b < bandTotal; b++)
                {
                    int lo = bandLow[b];
                    int hi = bandHigh[b];
                    double sum = 0.0;
                    for (int k = lo; k < hi; k++)
                        sum += magnitudes[k];

                    double mean = sum / Math.Max(1, hi - lo);
                    double decibels = 20.0 * Math.Log10(mean + BandMagnitudeEpsilon);
                    bandDecibels[b] = decibels;
                    if (decibels > framePeakDb)
                        framePeakDb = decibels;
                }

                // FIX 9: the old mapping was the fixed linear ramp (dBFS + 60) / 60, which pinned
                // every band at or below -60 dBFS to exactly 0.0 - and a 0.0 stays 0.0 through any
                // downstream gain, so the renderer's pink-noise tilt could not bring them back.
                // Log bands give the high-frequency end very wide buckets and on real tonal music
                // most of those bands measure well below -60 dBFS, which is why the right third of
                // every bar style rendered as a dead flat line.
                //
                // FIX 10: the floor is frame-relative but the ceiling was still fixed at full scale,
                // which is a ceiling problem wearing a floor problem's clothes. Real programme peaks
                // 12-25 dB below full scale, so no floor setting could ever bring the loudest band
                // near the top of the plot. The mapping, per frame, is:
                //
                //   framePeak = max over bands of dBFS                    (this frame's loudness)
                //   relative  = framePeak - BandFloorRelativeBelowPeakDb   (54 dB below it)
                //   blended   = absolute + W * (relative - absolute)       (W = 1, purely relative)
                //   reference = max(absolute, blended)                     (floor never below absolute)
                //   ceiling   = framePeak + BandCeilingHeadroomDb          (3 dB above the loudest band)
                //   span      = (ceiling - reference) / (1 + KNEE / 2)      (knee headroom)
                //   t         = (dBFS - reference) / span                  (0..1 over the frame)
                //   value     = t * t / (2 * KNEE)       for t <  KNEE      (quadratic knee)
                //             = t - KNEE / 2             for t >= KNEE      (linear to 1)
                //   frameGate = clamp((framePeak + 80 dB) / 8 dB, 0, 1)     (silence fade)
                //   band      = clamp(frameGate * value, 0, 1)              (then attack/release)
                //
                // Worked for a frame peaking at -22 dBFS, reference = max(-72, -22 - 54) = -72,
                // ceiling = -19 and span = 53 / 1.06 = 50.0, so the array now runs the whole ramp:
                //
                //   band dBFS   -22   -35   -45   -55   -62   -65   -70   -72
                //   value       0.94  0.68  0.48  0.28  0.14  0.08  0.007  0.00
                //
                // where the old constants gave 0.600 / 0.408 / 0.282 / 0.178 / 0.118 / 0.095 /
                // 0.063 / 0.051 for the same bands - every one of them in the middle of the plot,
                // nothing near either end. The measured array span goes from 0.096-0.578 to
                // 0.000-0.940, which is the bar-saturation defect.
                //
                // Two properties this buys:
                //   * Every frame spans the full ramp whatever the absolute level of the programme:
                //     the loudest band lands near 0.94 and the weakest real band near 0. Loud frames
                //     used to be stuck at 0.60 with nothing below 0.10 - the plateau this fixes.
                //   * value is a non-decreasing function of band level and frameGate is a single
                //     non-negative scalar per frame, so a louder band can never render smaller than
                //     a quieter one in the same frame.
                //
                // The price is in BandCeilingHeadroomDb: above about -18 dBFS the loudest band is a
                // constant ~0.944 regardless of how loud the frame actually is. That is inherent to
                // anchoring the ceiling to the peak and is documented there.
                if (double.IsNaN(framePeakDb))
                    framePeakDb = BandFrameSilenceDb;

                double frameGate = Math.Clamp((framePeakDb - BandFrameSilenceDb) / BandFrameFadeDb, 0.0, 1.0);

                // ABSOLUTE AUDIBILITY. Computed from the raw time-domain level (not from the band
                // statistic) because RMS in dBFS has a known calibration: 20*log10 of the same
                // linear value the renderer already treats as loudness. The band mapping below is
                // frame-relative and would otherwise map a -50 dBFS room tone to near-full bars.
                double sumSquaresForGate = 0.0;
                float peakForGate = 0f;
                for (long s = frameStart; s < windowEnd; s++)
                {
                    float value = ReadSample(samples, s);
                    sumSquaresForGate += value * value;
                    float absolute = Math.Abs(value);
                    if (absolute > peakForGate) peakForGate = absolute;
                }

                float rawRms = Math.Clamp((float)Math.Sqrt(sumSquaresForGate / span), 0f, 1f);
                float rawPeak = Math.Clamp(peakForGate, 0f, 1f);
                double rmsDb = 20.0 * Math.Log10(Math.Max(1e-9, rawRms));
                double audibility = Math.Clamp((rmsDb - AudibleFloorDb) / AudibleFadeDb, 0.0, 1.0);

                if (frameGate <= 0.0)
                {
                    // Genuine silence: emit exact zeros and drop the envelope state with them, so a
                    // silent frame is flat to the bit and the next one attacks from rest.
                    for (int b = 0; b < bandTotal; b++)
                    {
                        bands[b] = 0f;
                        previousBands[b] = 0f;
                    }
                }
                else
                {
                    double referenceDb = BandFloorAbsoluteDb
                        + (BandFloorRelativeWeight
                           * ((framePeakDb - BandFloorRelativeBelowPeakDb) - BandFloorAbsoluteDb));

                    // The blend may aim below the absolute floor for a very quiet frame; clamping it
                    // up is what guarantees the mapping can never amplify the noise floor.
                    if (referenceDb < BandFloorAbsoluteDb)
                        referenceDb = BandFloorAbsoluteDb;

                    // The top of the ramp is anchored to the frame's own loudest band, not to full
                    // scale: a fixed 0 dBFS ceiling sat 12-25 dB above real programme peaks and was
                    // the reason loud bands could never reach the plot. See BandCeilingHeadroomDb.
                    double ceilingDb = framePeakDb + BandCeilingHeadroomDb;

                    // Dividing by (1 + KNEE / 2) pays back the offset the soft knee costs above the
                    // knee, so a band sitting exactly on the ceiling still reaches 1.0. The guard
                    // covers the degenerate case of a peak within a dB of the absolute floor, where
                    // the ramp would otherwise collapse to nothing.
                    double mappingSpan = (ceilingDb - referenceDb) / (1.0 + (0.5 * BandKneeFraction));
                    if (mappingSpan < 1.0)
                        mappingSpan = 1.0;

                    for (int b = 0; b < bandTotal; b++)
                    {
                        float mapped = (float)MapBandLevel(bandDecibels[b], referenceDb, mappingSpan);

                        // No gamma. It existed to pull the mid and low bands down out of a plateau
                        // that was really caused by a ceiling the frame could never reach, so all it
                        // did was crush the quiet end a second time: at 1.8 a band that mapped to
                        // 0.50 read 0.29, one that mapped to 0.30 read 0.11, and anything inside
                        // the knee was raised to the 1.8 power after already being squared.
                        float rawBand = (float)Math.Clamp(frameGate * audibility * mapped, 0.0, 1.0);

                        bands[b] = EnvelopeStep(previousBands[b], rawBand, dt, DefaultAttackTau, DefaultReleaseTau);
                        previousBands[b] = bands[b];
                    }
                }

                // Bands past the effective count carry no measurement; keep them flat at zero so a
                // renderer indexing to BandCount never reads stale values from a previous frame.
                for (int b = bandTotal; b < bandCount; b++)
                {
                    bands[b] = 0f;
                    previousBands[b] = 0f;
                }

                // Loudness: RMS and peak over the same window as the bands, smoothed identically.
                // rawRms/rawPeak were computed above for the audibility gate; here they are scaled
                // by that same gate so the meter column and peak-hold freeze with the bars.
                float rms = EnvelopeStep(previousRms, rawRms * (float)audibility, dt, DefaultAttackTau, DefaultReleaseTau);
                float smoothedPeak = EnvelopeStep(previousPeak, rawPeak * (float)audibility, dt, DefaultAttackTau, DefaultReleaseTau);
                previousRms = rms;
                previousPeak = smoothedPeak;

                // VOCAL CATCH: read the pre-pass dB for this frame, map it against the loop's own
                // p15/p95 and push it through the vocal envelope. The read/decide happens on both
                // warm-up passes (recursive state - see the long comment above the pass loop);
                // only the STORE below is gated on keepResults, exactly like every other envelope.
                // A silent frame drops the target to zero AND resets the envelope state to match
                // how the band envelope resets in its silence branch, so the next frame attacks
                // from rest instead of blending with a stale level.
                float rawVoice;
                if (frameGate <= 0.0 || audibility <= 0.0 || !voiceTrackable)
                {
                    rawVoice = 0f;
                    previousVoice = 0f;
                }
                else
                {
                    rawVoice = (float)Math.Clamp(
                        ((voiceDecibelsRaw[frame] - voicePercentileLow) / Math.Max(1e-9, voiceSpread)) * audibility,
                        0.0, 1.0);
                }

                float voice = EnvelopeStep(previousVoice, rawVoice, dt, VoiceAttackTau, VoiceReleaseTau);
                previousVoice = voice;

                // The onset detector must run on BOTH passes, even though only the second pass's
                // results are kept. Its state (_previousMagnitudes, the running flux mean and the
                // refractory timer) is recursive, exactly like the RMS envelope above, so
                // skipping it on the warm-up pass left frame 0 facing a detector that had never
                // seen a single bin - which silently defeated the loop-seam fix for every
                // onset-driven style (ripple-rings, peak-stars, dot-matrix). Gate the STORE, not
                // the processing.
                bool fired = onsetDetector.Process(magnitudes, fluxBinLimit, rawRms) && audibility >= 0.5;

                if (!keepResults)
                    continue;

                frames[frame] = new AudioFrameData
                {
                    // bands is reused every frame, so each result owns its own copy.
                    Bands = (float[])bands.Clone(),
                    WaveMin = waveMin,
                    WaveMax = waveMax,
                    Rms = rms,
                    Peak = smoothedPeak,
                    // FIX 8: means, so all three share one statistic and are directly comparable.
                    Bass = MeanRange(bands, bandLow, bandHigh, bandTotal, bassLo, bassHi),
                    Mid = MeanRange(bands, bandLow, bandHigh, bandTotal, bassHi, midHi),
                    Treble = MeanRange(bands, bandLow, bandHigh, bandTotal, midHi, binCount),
                    Voice = voice,
                    // FIX 1: the frame's RMS gates the fire, so silence cannot produce onsets. It is
                    // rawRms, not the smoothed envelope: an envelope follower lags its target by
                    // design, so gating on it would judge the leading edge of a transient by the
                    // quiet that preceded it and swallow exactly the hits that matter.
                    Onset = fired
                };
            }
            }

            return frames;
        }

        /// <summary>
        /// Collects the raw per-frame vocal-energy level, in dB, for the whole loop, in one
        /// bounded pre-pass. For every frame the SAME window the band path uses (frame_start round
        /// to <c>frame * sampleRate / frameRate</c>, length <paramref name="fftSize"/>, wrap-around
        /// reads via <see cref="ReadSample"/>) is FFT'd on the mid channel and, when present, on
        /// the side channel; the bin range <c>[voiceLowHz, voiceHighHz)</c> is reduced with
        /// <c>mean(max(0, mid − side))</c>, the classic centre-extraction estimate, and converted
        /// to dBFS exactly like <see cref="BandMagnitudeEpsilon"/>-anchored band levels.
        ///
        /// Return values feed the p15/p95 map in <see cref="AnalyzeFrames"/>; the array itself is
        /// read back by the envelope loop, so the values the percentiles were computed over are
        /// the values that get mapped.
        /// </summary>
        private static double[] CollectVoiceDecibels(
            float[] samples,
            float[]? sideSamples,
            int sampleRate,
            int frameCount,
            double frameRate,
            int fftSize,
            double voiceLowHz,
            double voiceHighHz,
            CancellationToken token)
        {
            var decibels = new double[frameCount];
            if (frameCount <= 0)
                return decibels;

            int binCount = fftSize / 2 + 1;
            var fft = new Radix2Fft(fftSize);
            float[] window = BuildHannWindow(fftSize);
            double magnitudeScale = 2.0 / WindowSum(window);
            var midReal = new float[fftSize];
            var midImaginary = new float[fftSize];
            var sideReal = new float[fftSize];
            var sideImaginary = new float[fftSize];

            int voiceBinLo = FrequencyToBin((float)voiceLowHz, sampleRate, fftSize, binCount);
            int voiceBinHi = FrequencyToBin((float)voiceHighHz, sampleRate, fftSize, binCount);
            if (voiceBinHi <= voiceBinLo)
                voiceBinHi = Math.Min(binCount, voiceBinLo + 1);

            bool hasSide = sideSamples is { Length: > 0 } && sideSamples.Length == samples.Length;
            float[] sideOrNull = hasSide ? sideSamples! : Array.Empty<float>();

            for (int frame = 0; frame < frameCount; frame++)
            {
                if (token.IsCancellationRequested)
                    return decibels;

                long frameStart = (long)Math.Round(frame * (double)sampleRate / frameRate);

                for (int k = 0; k < fftSize; k++)
                {
                    midReal[k] = ReadSample(samples, frameStart + k) * window[k];
                    midImaginary[k] = 0f;
                }

                fft.Forward(midReal, midImaginary);

                if (hasSide)
                {
                    for (int k = 0; k < fftSize; k++)
                    {
                        sideReal[k] = ReadSample(sideOrNull, frameStart + k) * window[k];
                        sideImaginary[k] = 0f;
                    }

                    fft.Forward(sideReal, sideImaginary);
                }
                else
                {
                    Array.Clear(sideReal, 0, sideReal.Length);
                    Array.Clear(sideImaginary, 0, sideImaginary.Length);
                }

                double sumVoice = 0.0;
                for (int k = voiceBinLo; k < voiceBinHi; k++)
                {
                    double midMag = Math.Sqrt((midReal[k] * midReal[k]) + (midImaginary[k] * midImaginary[k]));
                    double sideMag = hasSide
                        ? Math.Sqrt((sideReal[k] * sideReal[k]) + (sideImaginary[k] * sideImaginary[k]))
                        : 0.0;

                    // The subtraction can go negative when decorrelation noise misplaces energy
                    // into side; the centre-voice estimate is the NON-NEGATIVE remainder only.
                    double voiceMagnitude = midMag - sideMag;
                    if (voiceMagnitude > 0.0)
                        sumVoice += voiceMagnitude;
                }

                double voiceMean = sumVoice / Math.Max(1, voiceBinHi - voiceBinLo);
                decibels[frame] = 20.0 * Math.Log10(voiceMean + BandMagnitudeEpsilon);
            }

            return decibels;
        }

        /// <summary>
        /// Reads one sample of the decoded buffer, <b>always wrapping</b> past the end.
        /// <para>
        /// The analysis window is the full FFT length (2048 samples) while the frame hop is much
        /// shorter (735 at 22050/30 fps), so the last frames of a loop would otherwise be a few
        /// hundred real samples followed by a run of zeros. The Hann window tapers those trailing
        /// samples, the spectrum reads ~12 dB low, and the attack/release envelope turns that into a
        /// visible notch that snaps back at frame 0: a permanent artifact at the loop point of every
        /// render. Wrapping reads the loop's head instead, which is also the semantically correct
        /// behaviour for a slice shorter than the loop, because the buffer is a ring.
        /// </para>
        /// </summary>
        private static float ReadSample(float[] samples, long absoluteIndex)
        {
            int length = samples.Length;
            if (length == 0)
                return 0f;

            long index = absoluteIndex % length;
            if (index < 0)
                index += length;

            return samples[(int)index];
        }

        /// <summary>
        /// Converts a frequency to the FFT bin that contains it, clamped into the usable range.
        /// Uses <see cref="Math.Floor"/> - the conventional frequency-to-bin mapping, where a bin
        /// covers <c>[k * sampleRate / fftSize, (k + 1) * sampleRate / fftSize)</c> - rather than
        /// <see cref="Math.Round"/>, whose banker's rounding sends an exact .5 to even and would make
        /// a band edge land on a different side of the boundary depending on the bin index.
        /// </summary>
        private static int FrequencyToBin(float frequencyHz, int sampleRate, int fftSize, int binCount)
        {
            if (sampleRate <= 0 || fftSize <= 0 || binCount <= 0)
                return 0;

            double bin = Math.Floor((double)frequencyHz * fftSize / sampleRate);
            return (int)Math.Clamp(bin, 0.0, binCount - 1.0);
        }

        /// <summary>
        /// Collapses bands whose bin range duplicates the previous band's and redistributes the
        /// freed slots by subdividing the widest remaining bands, so every surviving band carries a
        /// measurement distinct from its neighbour.
        /// <para>
        /// With <c>fMin = 40 Hz</c>, <c>fMax</c> clamped to Nyquist and a 10.767 Hz bin width, log
        /// edges map bands 0-9 onto just six distinct bin ranges: 10 of 64 bars carried only 6 values
        /// and the left sixth of the display was a dead flat plateau. Collapsing alone would leave
        /// fewer bars than requested, so the freed slots are handed to the high-frequency bands,
        /// which have dozens of bins each and can genuinely be subdivided.
        /// </para>
        /// </summary>
        /// <param name="bandLow">Low bin per band, rewritten in place. Length must be bandCount.</param>
        /// <param name="bandHigh">Exclusive high bin per band, rewritten in place.</param>
        /// <param name="bandCount">Requested band count; the arrays are exactly this long.</param>
        /// <returns>The number of bands in use, i.e. the distinct measurement count.</returns>
        private static int ResolveBandRanges(int[] bandLow, int[] bandHigh, int bandCount)
        {
            int count = Math.Min(bandCount, Math.Min(bandLow.Length, bandHigh.Length));
            if (count <= 0)
                return 0;

            // Pass 1: drop runs whose range is identical to the previous band's.
            int collapsed = 0;
            for (int b = 0; b < count; b++)
            {
                if (collapsed > 0 && bandLow[collapsed - 1] == bandLow[b] && bandHigh[collapsed - 1] == bandHigh[b])
                    continue;

                bandLow[collapsed] = bandLow[b];
                bandHigh[collapsed] = bandHigh[b];
                collapsed++;
            }

            // Pass 2: hand the freed slots back by halving the widest band.
            for (int b = collapsed; b < count; b++)
            {
                int widest = -1;
                int widestSpan = 1;
                for (int i = 0; i < collapsed; i++)
                {
                    int span = bandHigh[i] - bandLow[i];
                    if (span <= widestSpan)
                        continue;

                    // Only split a band sitting in a clean neighbourhood. The nominal ranges are
                    // not always monotonic - the "at least one bin" clamp can push a band's high
                    // past the next band's low at the compressed low end - and halving such a band
                    // would drop a copy of its neighbour's range right beside it.
                    if (i > 0 && bandHigh[i - 1] > bandLow[i])
                        continue;
                    if (i + 1 < collapsed && bandHigh[i] > bandLow[i + 1])
                        continue;

                    widestSpan = span;
                    widest = i;
                }

                // Nothing left can be split: every remaining band is a single bin, so there are
                // genuinely fewer distinct measurements than bands requested.
                if (widest < 0)
                    break;

                // mid stays strictly inside (lo, hi) because the span is at least 2, so both halves
                // are non-empty and the split cannot duplicate the band either side of it.
                int mid = bandLow[widest] + (widestSpan / 2);

                // Make room at widest + 1. The highs shift from widest onward because the split band
                // keeps the old high as the new band's high; the lows shift from widest + 1 onward
                // because the split band keeps its own low and every band above it keeps its own.
                Array.Copy(bandHigh, widest, bandHigh, widest + 1, collapsed - widest);
                Array.Copy(bandLow, widest + 1, bandLow, widest + 2, collapsed - widest - 1);
                bandHigh[widest] = mid;
                bandLow[widest + 1] = mid;
                collapsed++;
            }

            return collapsed;
        }

        /// <summary>
        /// Mean over the bands whose bin range overlaps the half-open bin range
        /// <c>[binLo, binHi)</c>. Used for Bass / Mid / Treble so all three are the same statistic
        /// and can be compared directly. Bands beyond <paramref name="bandCount"/> are ignored and
        /// an empty range returns 0 rather than throwing.
        /// </summary>
        private static float MeanRange(
            float[] values,
            int[] rangeLow,
            int[] rangeHigh,
            int bandCount,
            int binLo,
            int binHi)
        {
            if (binHi <= binLo)
                return 0f;

            double sum = 0.0;
            int counted = 0;
            int usable = Math.Min(Math.Min(bandCount, values.Length), rangeLow.Length);
            for (int b = 0; b < usable; b++)
            {
                int lo = rangeLow[b];
                int hi = rangeHigh[b];
                if (hi <= lo || hi <= binLo || lo >= binHi)
                    continue;

                sum += values[b];
                counted++;
            }

            return counted > 0 ? (float)(sum / counted) : 0f;
        }

        /// <summary>Sum of a window's coefficients, i.e. its unnormalised coherent gain.</summary>
        private static double WindowSum(float[] window)
        {
            double sum = 0.0;
            for (int i = 0; i < window.Length; i++)
                sum += window[i];

            return sum > 0.0 ? sum : 1.0;
        }

        /// <summary>Builds the periodic-free Hann window shared by every frame.</summary>
        private static float[] BuildHannWindow(int size)
        {
            var window = new float[size];
            if (size <= 0)
                return window;

            if (size == 1)
            {
                window[0] = 1f;
                return window;
            }

            double denominator = size - 1;
            for (int i = 0; i < size; i++)
                window[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / denominator)));

            return window;
        }

        /// <summary>True when every sample sits at or below the digital silence floor.</summary>
        private static bool IsSilent(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                if (Math.Abs(samples[i]) > SilenceSampleThreshold)
                    return false;
            }

            return true;
        }

        /// <summary>Keeps the tail of ffmpeg stderr, where the actual error lives.</summary>
        private static string TruncateForLog(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "(no stderr)";

            string trimmed = text.Trim();
            if (trimmed.Length <= MaxFfmpegStderrLogChars)
                return trimmed;

            return "..." + trimmed.Substring(trimmed.Length - MaxFfmpegStderrLogChars);
        }

        /// <summary>Best-effort temp cleanup; never throws.</summary>
        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // A locked temp file is harmless; the OS temp sweeper will get it.
            }
        }

        /// <summary>
        /// Spectral-flux onset detector. One instance lives for the duration of a single
        /// <see cref="AnalyzeAsync"/> call, so the previous-magnitude history is never shared.
        /// </summary>
        private sealed class OnsetDetector
        {
            private readonly float[] _previousMagnitudes;
            private readonly double _meanAlpha;
            private readonly double _dtSeconds;
            private double _mean;
            private double _secondsSinceOnset;

            /// <param name="magnitudeCount">Length of the per-frame magnitude spectrum.</param>
            /// <param name="dtSeconds">
            /// Time since the previous frame. Both of the detector's time constants are in
            /// seconds: the adaptive mean is an EMA (<c>alpha = 1 - exp(-dt / OnsetMeanTauSeconds)</c>)
            /// so the baseline tracks at the same speed at 10, 30 or 60 fps, instead of a fixed rate
            /// that meant 5 s, 1.67 s and 0.83 s respectively for the same constant, and the
            /// refractory period is <see cref="OnsetRefractorySeconds"/> of wall clock rather than a
            /// frame count that meant 0.400 s at 10 fps and 0.033 s at 120 fps.
            /// </param>
            public OnsetDetector(int magnitudeCount, double dtSeconds)
            {
                _previousMagnitudes = new float[Math.Max(1, magnitudeCount)];
                _dtSeconds = dtSeconds > 0.0 ? dtSeconds : 1.0 / 30.0;
                _meanAlpha = 1.0 - Math.Exp(-_dtSeconds / OnsetMeanTauSeconds);
                _mean = 0.0;

                // Starts already refractory, so the first frames of a track cannot fire a burst of
                // onsets against the still-zeroed magnitude history.
                _secondsSinceOnset = OnsetRefractorySeconds;
            }

            /// <summary>Feeds one frame's magnitudes and reports whether an onset fired.</summary>
            /// <param name="magnitudes">This frame's spectrum.</param>
            /// <param name="binLimit">Highest bin the flux sums over.</param>
            /// <param name="rms">This frame's raw, un-smoothed RMS; below <see cref="OnsetMinRms"/>
            /// the frame is too quiet to be an onset at all, checked before the flux comparison.</param>
            public bool Process(ReadOnlySpan<float> magnitudes, int binLimit, double rms)
            {
                int limit = Math.Clamp(binLimit, 1, Math.Min(magnitudes.Length, _previousMagnitudes.Length));

                double flux = 0.0;
                for (int k = 0; k < limit; k++)
                {
                    float current = magnitudes[k];
                    float delta = current - _previousMagnitudes[k];
                    if (delta > 0f)
                        flux += delta;
                    _previousMagnitudes[k] = current;
                }

                // The baseline tracks every frame, loud or quiet, so a track that fades in is already
                // adapted by the time it is loud enough to matter.
                _mean += (flux - _mean) * _meanAlpha;
                _secondsSinceOnset += _dtSeconds;

                // The absolute level gate comes first: flux > mean * 1.6 is purely relative and so
                // is scale invariant - it fires on dither-level noise and at the same rate on a
                // fade to -100 dBFS as on a full-scale hit. OnsetMinRms is what stops that.
                bool fired = rms >= OnsetMinRms
                    && flux > _mean * OnsetSensitivity
                    && _secondsSinceOnset >= OnsetRefractorySeconds;

                if (fired)
                    _secondsSinceOnset = 0.0;

                return fired;
            }
        }

        /// <summary>
        /// <summary>
        /// In-place iterative radix-2 Cooley-Tukey decimation-in-time FFT. The bit-reversal
        /// permutation and the twiddle tables are computed once in the constructor, so the
        /// butterfly loop never calls into <see cref="Math"/> trig.
        /// <para>
        /// The length is validated, not rounded. It used to be rounded up to the next power of two
        /// while <see cref="Forward"/> silently returned when the buffers were shorter than that,
        /// so a non-power-of-two length produced no transform and no error - the caller then read
        /// the previous frame's spectrum back as if it were current. A wrong FFT length is a
        /// programming error, not something to paper over.
        /// </para>
        /// </summary>
        private sealed class Radix2Fft
        {
            private readonly int _size;
            private readonly int[] _reversed;
            private readonly float[] _cosine;
            private readonly float[] _sine;

            /// <exception cref="ArgumentException">
            /// <paramref name="size"/> is not a power of two of at least 2.
            /// </exception>
            public Radix2Fft(int size)
            {
                if (size < 2 || (size & (size - 1)) != 0)
                {
                    throw new ArgumentException(
                        $"FFT length must be a power of two of at least 2; got {size}.",
                        nameof(size));
                }

                int n = size;

                _size = n;
                _reversed = new int[n];
                _cosine = new float[n / 2];
                _sine = new float[n / 2];

                int bits = 0;
                while ((1 << bits) < n)
                    bits++;

                for (int i = 0; i < n; i++)
                {
                    _reversed[i] = ReverseBits(i, bits);
                    if (i < n / 2)
                    {
                        // Negative angle: the forward transform uses e^(-2*pi*i*k/n).
                        double angle = -2.0 * Math.PI * i / n;
                        _cosine[i] = (float)Math.Cos(angle);
                        _sine[i] = (float)Math.Sin(angle);
                    }
                }
            }

            /// <summary>Transform length. <see cref="Forward"/> requires buffers of at least this length.</summary>
            public int Size => _size;

            /// <summary>Runs the forward transform over the first <see cref="Size"/> elements.</summary>
            /// <exception cref="ArgumentException">
            /// A buffer is shorter than <see cref="Size"/>. Returning quietly would leave the buffers
            /// untransformed and hand the caller a stale spectrum.
            /// </exception>
            public void Forward(float[] real, float[] imaginary)
            {
                int n = _size;
                if (real == null)
                    throw new ArgumentNullException(nameof(real));

                if (imaginary == null)
                    throw new ArgumentNullException(nameof(imaginary));

                if (real.Length < n)
                    throw new ArgumentException($"FFT input must be at least {n} samples; got {real.Length}.", nameof(real));

                if (imaginary.Length < n)
                    throw new ArgumentException($"FFT input must be at least {n} samples; got {imaginary.Length}.", nameof(imaginary));

                for (int i = 0; i < n; i++)
                {
                    int j = _reversed[i];
                    if (j > i)
                    {
                        float swapRe = real[i];
                        real[i] = real[j];
                        real[j] = swapRe;

                        float swapIm = imaginary[i];
                        imaginary[i] = imaginary[j];
                        imaginary[j] = swapIm;
                    }
                }

                for (int length = 2; length <= n; length <<= 1)
                {
                    int half = length >> 1;
                    int step = n / length;

                    for (int start = 0; start < n; start += length)
                    {
                        for (int offset = 0; offset < half; offset++)
                        {
                            int twiddle = offset * step;
                            float cos = _cosine[twiddle];
                            float sin = _sine[twiddle];

                            int a = start + offset;
                            int b = a + half;
                            float scaledRe = (real[b] * cos) - (imaginary[b] * sin);
                            float scaledIm = (real[b] * sin) + (imaginary[b] * cos);

                            real[b] = real[a] - scaledRe;
                            imaginary[b] = imaginary[a] - scaledIm;
                            real[a] += scaledRe;
                            imaginary[a] += scaledIm;
                        }
                    }
                }
            }

            private static int ReverseBits(int value, int bits)
            {
                int reversed = 0;
                for (int i = 0; i < bits; i++)
                    reversed = (reversed << 1) | ((value >> i) & 1);

                return reversed;
            }
        }
    }
}