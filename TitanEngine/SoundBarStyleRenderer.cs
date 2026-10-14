using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace TitanEngine
{
    /// <summary>
    /// Mutable scratch state carried across the frames of ONE <c>GenerateOverlayAsync</c> call.
    /// A fresh instance is created per call by the preset so nothing leaks between loops or renders.
    /// Styles that do not animate (everything except RippleRings and PeakStars) ignore it.
    /// </summary>
    public sealed class SoundBarRenderState
    {
        internal readonly struct Ripple
        {
            public Ripple(int spawnFrame, float energy)
            {
                SpawnFrame = spawnFrame;
                Energy = energy;
            }

            public int SpawnFrame { get; }
            public float Energy { get; }
        }

        internal readonly struct Star
        {
            public Star(int bandIndex, int spawnFrame, float peak, float x, float y)
            {
                BandIndex = bandIndex;
                SpawnFrame = spawnFrame;
                Peak = peak;
                X = x;
                Y = y;
            }

            public int BandIndex { get; }
            public int SpawnFrame { get; }
            public float Peak { get; }
            public float X { get; }
            public float Y { get; }
        }

        private readonly List<Ripple> _ripples = new List<Ripple>(8);
        private readonly Dictionary<int, Star> _stars = new Dictionary<int, Star>(64);
        private int _lastOnsetFrame = -1;

        internal IReadOnlyList<Ripple> Ripples => _ripples;

        internal void NoteOnset(int frameIndex, int maxLiveRings)
        {
            if (frameIndex == _lastOnsetFrame)
                return;

            _lastOnsetFrame = frameIndex;

            // Fixed-size pool: the oldest ripple is recycled once the pool is full.
            if (_ripples.Count >= Math.Max(1, maxLiveRings))
                _ripples.RemoveAt(0);

            _ripples.Add(new Ripple(frameIndex, 1f));
        }

        internal void ExpireRipples(int frameIndex, int lifetimeFrames)
        {
            for (int i = _ripples.Count - 1; i >= 0; i--)
            {
                if (frameIndex - _ripples[i].SpawnFrame > lifetimeFrames)
                    _ripples.RemoveAt(i);
            }
        }

        internal void TrackStar(int bandIndex, int frameIndex, float peak, float x, float y)
        {
            // A peak that is still present on a later frame keeps its original spawn time so it fades
            // continuously instead of restarting its life every frame.
            if (_stars.TryGetValue(bandIndex, out Star existing) && existing.SpawnFrame <= frameIndex)
                return;

            _stars[bandIndex] = new Star(bandIndex, frameIndex, peak, x, y);
        }

        internal void ExpireStars(int frameIndex, int lifetimeFrames)
        {
            if (_stars.Count == 0)
                return;

            List<int>? expired = null;
            foreach (KeyValuePair<int, Star> entry in _stars)
            {
                if (frameIndex - entry.Value.SpawnFrame > lifetimeFrames)
                    (expired ??= new List<int>()).Add(entry.Key);
            }

            if (expired == null)
                return;

            for (int i = 0; i < expired.Count; i++)
                _stars.Remove(expired[i]);
        }

        internal IEnumerable<Star> EnumerateStars()
            => _stars.Values;
    }

    /// <summary>
    /// Draws one audio-visualizer frame onto a <see cref="DrawingContext"/>.
    ///
    /// Hard rules baked into this renderer (a <see cref="System.Windows.Media.Imaging.RenderTargetBitmap"/>
    /// rasterises on the CPU and silently drops shader effects):
    /// <list type="bullet">
    /// <item>Never uses <c>Effect</c>/<c>BlurEffect</c> - glow is faked with three stroke passes.</item>
    /// <item>Glow = wide (t*5.0, a~0.08), mid (t*2.4, a~0.22), core (t*1.0, a~0.92) of the SAME geometry.
    /// When the halo is clamped the wide/mid pair compresses but NEVER collapses onto the core - see
    /// <see cref="DrawGlowPasses"/>.</item>
    /// <item><see cref="FrameContext.AlphaFactor"/> (Opacity) is applied on EVERY draw path, including
    /// the glow-off one, so the slider is never inverted for a single style.</item>
    /// <item>One <see cref="StreamGeometry"/> with many figures, drawn three times - never a call per bar.</item>
    /// <item>Every brush / pen / geometry is frozen; they are shared across STA threads.</item>
    /// <item>Everything is clamped inside [0,width] x [0,height].</item>
    /// </list>
    /// </summary>
    public static class SoundBarStyleRenderer
    {
        /// <summary>
        /// Global bar-count range. Settings.BarCount is clamped ONCE, centrally, into FrameContext so
        /// every style derives from the same already-clamped value and the UI's advertised 8..256 is
        /// what actually gets drawn. A style may still demand a higher FLOOR of its own where the
        /// geometry needs it (see WaveRibbon's column floor), never a lower ceiling.
        /// </summary>
        private const int MinBars = 8;
        private const int MaxBars = 256;
        private const int AngularSectorCount = 180;
        private const double AngularSectorDegrees = 2.0;
        private const int MaxRipples = 8;

        /// <summary>
        /// Top slice of a bar's gradient reserved for the white-hot tip ramp. Authored palette stops
        /// above this offset are clamped down to it; a stop authored exactly at it keeps its colour.
        /// </summary>
        private const double TipStart = 0.94;

        // ------------------------------------------------------------------
        // Tuning constants. Each block documents the measured defect it answers; see the helper
        // named in the summary for the full derivation.
        // ------------------------------------------------------------------

        /// <summary>Lower edge of the analyser's log band layout (AudioSpectrumAnalyzer.DefaultFMin).</summary>
        private const double PinkBandLowHz = 40.0;

        /// <summary>
        /// Upper edge of the analyser's log band layout. AudioSpectrumAnalyzer clamps fMax to Nyquist
        /// and always decodes at DefaultSampleRate (22050), so this is always 11025 Hz.
        /// </summary>
        private const double PinkBandHighHz = 11025.0;

        /// <summary>
        /// Slope of the pink-noise tilt, in dB per octave of band frequency. DELIBERATELY ZERO.
        /// <para>
        /// The ramp was introduced to rescue "HF bands read as a dead flat line", on the premise that
        /// the analyser's decibel floor was keeping the noise floor flat. Measured exactly, the old
        /// +3 dB/octave ramp was not a tilt: <c>gainDb(i) = clamp(3.0 * octaves(i), 0, 9)</c> is
        /// exactly 1.0 at band 0 only, reaches x2.818 at band 24, and pins 40 of 64 bands (62.5 %) at
        /// the +9 dB ceiling. It cost three separate defects:
        /// <list type="number">
        /// <item>A 2.9x discontinuity at <see cref="PinkSilenceFloor"/>: a value just above the floor
        /// passes through at x1.0 and the next representable float jumps to x2.818. Bands
        /// oscillating across that boundary - which the DSP change makes the norm for quiet HF, not
        /// the exception - shimmer frame to frame.</item>
        /// <item>It manufactured motion out of the noise floor: the effective gate drops from 0.0039
        /// to 0.0039 / 2.818 = 0.0014, so sub-pixel dither becomes visible.</item>
        /// <item>It decoupled Sensitivity, because it is applied AFTER it: Sensitivity 3 gives band 0
        /// a x3 ceiling and band 60 a x8.45 ceiling - one slider, 2.8:1 of asymmetry.</item>
        /// </list>
        /// The premise has since been fixed upstream twice over, so the tilt is now neutral: the gain
        /// table is all 1.0, <see cref="ApplyPinkTilt"/> is the identity, and
        /// <see cref="Sensitivity"/> is the only level knob left. Restore a non-zero value here (and
        /// widen <see cref="PinkTiltMaxDb"/>) only with a curve that is genuinely linear in log
        /// frequency across the whole band array, never a clamp that saturates half of it.
        /// </para>
        /// </summary>
        private const double PinkTiltDbPerOctave = 0.0;

        /// <summary>Ceiling of the pink-noise tilt, in dB. x2.82. Unreachable while the slope is 0.</summary>
        private const double PinkTiltMaxDb = 9.0;

        /// <summary>
        /// One 8-bit code. Bands at or below this are digital silence or dither and are never lifted.
        /// </summary>
        private const float PinkSilenceFloor = 0.0039f;

        /// <summary>
        /// Hard ceiling on the widest glow pass, as a fraction of the radial element's OUTER radius.
        /// See <see cref="RadialHaloExtent"/>.
        /// </summary>
        private const double RadialHaloFraction = 0.04;

        /// <summary>Most a single frame's auto-gain may lift the radial shapes. Bounds noise-only frames.</summary>
        private const double RadialAutoGainLimit = 4.0;

        /// <summary>Reference level at or below which no auto-gain is applied, so silence stays silent.</summary>
        private const double RadialAutoGainFloor = 0.02;

        /// <summary>
        /// Reference percentile for radial normalisation. Normalising against the single loudest band
        /// let one transient own the whole ring: every other bar collapsed onto the hub radius, which
        /// is why RadialRing read as a solid blob with one spike and SpokeLines read as a single
        /// sliver. Note the flat caps were not what hid those rays - a peak-referenced gain still
        /// left short but NON-ZERO lengths, and <c>PenLineCap.Flat</c> paints those fine; flat caps
        /// only draw nothing at exactly zero length. The 90th percentile tracks the body of the
        /// spectrum instead of its outlier.
        /// </summary>
        private const double RadialPercentile = 0.90;

        /// <summary>
        /// Level the reference percentile is lifted to BEFORE the gamma curve is applied.
        /// <para>
        /// This was 0.62, which is not a target the gamma can deliver on: 0.62 ^ 0.45 = 0.806, so
        /// the p90 band drew 80.6 % of the annulus and the peak drew 100 % - the entire top decile
        /// was compressed into [0.806, 1.000] and a loud passage could not be told from a quiet one.
        /// 0.92 keeps the headroom that makes the normalisation a normalisation: after the gamma the
        /// p90 band lands at ~0.963, so the top decile spans [0.963, 1.000] and the auto-gain still
        /// has 8 % of the annulus to work with.
        /// </para>
        /// </summary>
        private const double RadialPercentileTarget = 0.92;

        /// <summary>
        /// Threshold applied to the GAINED level BEFORE the gamma. At or below it the band draws at
        /// the hub radius, i.e. not at all.
        /// <para>
        /// Without it the gamma is itself a floor-lifter: 0.0039 ^ 0.45 = 0.083, so a band sitting on
        /// ONE 8-bit code already drew 8-16 % of the annulus and the radial response never reached
        /// zero except at exact zero. At 0.0078 (two codes) the two quietest representable codes are
        /// hub-length and the gamma only ever acts on genuinely audible bands.
        /// </para>
        /// </summary>
        private const double RadialNoiseFloor = 0.0078;

        /// <summary>
        /// Perceptual compression applied to radial levels. Music spectra are heavily skewed - in the
        /// distribution this curve was fitted against (the pre-DSP-change one) the bass bands sat near
        /// 0.9 while most of the array was under 0.1 - so a linear radial mapping spends almost the
        /// entire annulus on the first handful of bars. A gamma below 1 lifts the quiet majority while
        /// barely touching the peak, so the ring and the starburst both read as shapes instead of a
        /// dot with a spike. It is now applied to a floor-subtracted, rescaled level
        /// (<see cref="RadialNoiseFloor"/>), so it compresses the audible range rather than the noise
        /// floor.
        /// </summary>
        private const double RadialGamma = 0.45;

        /// <summary>
        /// Smallest radial bar length, in px of radius, so a flat-capped style can always paint a ray.
        /// Matches the wedge floor in <see cref="DrawRadialWedges"/>; shared through
        /// <see cref="FillRadialBarSet"/> so the rays and the wedges can never disagree.
        /// </summary>
        private const double MinRadialReach = 0.5;

        // AudioRing (the NCS look) proportions, all relative to the inscribed outer radius.
        private const double AudioRingBaseFraction = 0.74;
        private const double AudioRingAmplitudeFraction = 0.26;
        private const double AudioRingThicknessFraction = 0.092;
        /// <summary>Spacing between the concentric dot rings, as a fraction of the outer radius.</summary>
        private const double AudioRingLatticeStep = 0.042;
        /// <summary>Angular offset added per lattice ring, in degrees - this is what makes the spiral.</summary>
        private const double AudioRingLatticeSpiralDegrees = 17.0;
        /// <summary>How far the ring's bloom may spread, as a fraction of the outer radius.</summary>
        private const double AudioRingHaloFraction = 0.105;

        /// <summary>
        /// How many alpha buckets the AudioRing dot lattice is quantised into.
        /// <para>
        /// The lattice is ~297 dots at the default job, and drawing them one at a time cost an
        /// EllipseGeometry plus two brushes plus two pens PER DOT: 1 485 WPF objects and 594
        /// DrawGeometry calls per frame, 474 KiB of recording, 3.1 ms - more than every other style
        /// in this file combined. A dot's radius and its alpha are both monotonic in its band
        /// energy, so energy is quantised into this many buckets and every dot in a bucket
        /// accumulates as one more figure in that bucket's single StreamGeometry. The frame then
        /// draws at most <c>2 x AudioRingDotBuckets</c> times, with the pens cached across frames.
        /// </para>
        /// <para>
        /// The count is a quantisation-vs-object-count trade, and the measurement settles it. Whole
        /// loop, worst frame, ncs-cyan at 1080x1080: 16 buckets -> 33 DrawGeometry, 24 -> 47,
        /// 32 -> 53, 48 -> 69. 24 is the largest count that still honours the "&lt;= 24 geometries,
        /// &lt;= 48 draw calls" budget in the WORST frame, not just the median one. Against the
        /// unquantised reference the mean per-channel error is 3.14 (16), 2.86 (24), 2.81 (32) and
        /// 2.79 (48) out of 255 - i.e. 24 is within 3 % of a 48-step staircase while costing
        /// 47 draw calls instead of 69. Allocation barely moves across the whole range
        /// (236 KB at 16, 249 KB at 24, 262 KB at 48) because the 297 ellipse figures dominate
        /// and the count does not change how many of them there are.
        /// </para>
        /// </summary>
        private const int AudioRingDotBuckets = 24;

        /// <summary>RippleRings: inner hub radius and stroke weight, both relative to the outer radius.</summary>
        private const double RippleInnerRadiusFraction = 0.14;
        private const double RippleThicknessFraction = 0.042;
        /// <summary>Degrees the whole dot lattice drifts over one loop. A whole turn keeps the seam invisible.</summary>
        private const double AudioRingParticleSpinDegrees = 360.0;

        /// <summary>
        /// Shortest column the four linear bar styles will paint, as a fraction of the length a
        /// full-scale band would reach.
        /// <para>
        /// This used to be a hard 1.0 px. Against the new DSP range that floor binds below
        /// value = 1/187 = 0.00534 on a 187 px plot, so every band more than ~25 dB under the frame
        /// ceiling collapsed to an identical 1 px rail - roughly half the high-frequency bands -
        /// and the whole new dynamic range was spent above that rail. A proportional floor only
        /// guards the exactly-zero case, which is all it was ever for. Capped at 1.0 px so a large
        /// canvas cannot turn it into a visible stub of its own.
        /// </para>
        /// </summary>
        private const double MinimumReachFraction = 0.004;

        /// <summary>Thickness of the widest glow pass, as a multiple of the element's own stroke.</summary>
        private const double GlowWideRatio = 5.0;

        /// <summary>Thickness of the middle glow pass, as a multiple of the element's own stroke.</summary>
        private const double GlowMidRatio = 2.4;

        private const double GlowWideAlpha = 0.08;
        private const double GlowMidAlpha = 0.22;
        private const double GlowCoreAlpha = 0.92;

        /// <summary>
        /// Narrowest halo-to-core and mid-to-core ratios the halo clamp may leave behind.
        /// <para>
        /// The clamp scales the halo and mid passes down by the same factor and floors both at the
        /// core thickness, so once <c>maxHaloThickness &lt; t</c> all three passes collapse onto
        /// <c>t</c> and the glow stops being a glow. That is not a corner case: at the DEFAULT job for
        /// DualRings <c>t = 8.25</c> and <c>maxHalo = 3.67</c>, so the unmodified recipe drew three
        /// identical 8.25 px strokes. These floors guarantee the stack still reads as 1 : 1.35 : 1.9
        /// when the clamp binds, and never bind at all when it does not.
        /// </para>
        /// </summary>
        private const double GlowMinMidRatio = 1.35;
        private const double GlowMinWideRatio = 1.9;

        /// <summary>Bottom of the CenterColumn decibel window, dBFS. x0.0316 linear.</summary>
        private const double CenterColumnFloorDb = -30.0;

        /// <summary>Top of the CenterColumn decibel window, dBFS. x0.708 linear.</summary>
        private const double CenterColumnCeilDb = -3.0;

        /// <summary>Height a sub-floor (but non-silent) CenterColumn sample is mapped to.</summary>
        private const double CenterColumnMinFraction = 0.10;

        /// <summary>Absolute level a PeakStars slice must clear before it draws.</summary>
        private const float PeakStarFloor = 0.05f;

        /// <summary>How far a PeakStars slice maximum must beat its neighbourhood mean.</summary>
        private const double PeakStarContrastRatio = 1.04;

        private static readonly ConcurrentDictionary<string, ResolvedPalette> PaletteCache =
            new ConcurrentDictionary<string, ResolvedPalette>(StringComparer.Ordinal);

        /// <summary>Pink-tilt gain tables, one per band-array length. Pure functions of the length.</summary>
        private static readonly ConcurrentDictionary<int, float[]> PinkTiltCache =
            new ConcurrentDictionary<int, float[]>();

        private static readonly Dictionary<GradientKey, LinearGradientBrush> GradientBrushCache =
            new Dictionary<GradientKey, LinearGradientBrush>(256);

        private static readonly object GradientCacheLock = new object();

        /// <summary>
        /// Renders <paramref name="currentFrameIndex"/> of <paramref name="frames"/> for the requested style.
        /// </summary>
        /// <param name="frames">
        /// The whole analysed sequence. Time-varying styles (RadialRing, DualRings, RippleRings, PeakStars)
        /// read earlier entries for rotation, smoothing and fading.
        /// </param>
        /// <param name="state">Optional per-render scratch state. Pass one instance for the whole loop.</param>
        public static void DrawFrame(
            DrawingContext dc,
            int width,
            int height,
            SoundBarSettings settings,
            ColorPreset palette,
            AudioFrameData[] frames,
            int currentFrameIndex,
            SoundBarRenderState? state)
        {
            if (dc == null)
                throw new ArgumentNullException(nameof(dc));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (width <= 0 || height <= 0)
                return;

            ColorPreset safePalette = palette ?? ColorPresets.GetOrDefault(null);
            AudioFrameData[] safeFrames = frames ?? Array.Empty<AudioFrameData>();

            var ctx = new FrameContext(settings, safePalette, safeFrames, currentFrameIndex, state, width, height);

            var clipRect = new Rect(0, 0, width, height);
            RectangleGeometry clip = new RectangleGeometry(clipRect);
            if (clip.CanFreeze)
                clip.Freeze();

            dc.PushClip(clip);
            try
            {
                switch (settings.Style)
                {
                    case SoundBarStyleId.MirrorSpectrum:
                        DrawMirrorSpectrum(dc, ctx);
                        break;
                    case SoundBarStyleId.PillBars:
                        DrawPillBars(dc, ctx);
                        break;
                    case SoundBarStyleId.DenseColumns:
                        DrawDenseColumns(dc, ctx);
                        break;
                    case SoundBarStyleId.DotMatrix:
                        DrawDotMatrix(dc, ctx);
                        break;
                    case SoundBarStyleId.RadialRing:
                        DrawRadialRing(dc, ctx);
                        break;
                    case SoundBarStyleId.DualRings:
                        DrawDualRings(dc, ctx);
                        break;
                    case SoundBarStyleId.RippleRings:
                        DrawRippleRings(dc, ctx);
                        break;
                    case SoundBarStyleId.SpokeLines:
                        DrawSpokeLines(dc, ctx);
                        break;
                    case SoundBarStyleId.WaveRibbon:
                        DrawWaveRibbon(dc, ctx);
                        break;
                    case SoundBarStyleId.CenterColumn:
                        DrawCenterColumn(dc, ctx);
                        break;
                    case SoundBarStyleId.PeakStars:
                        DrawPeakStars(dc, ctx);
                        break;
                    case SoundBarStyleId.AudioRing:
                        DrawAudioRing(dc, ctx);
                        break;
                    case SoundBarStyleId.NeonBars:
                    default:
                        DrawNeonBars(dc, ctx);
                        break;
                }
            }
            finally
            {
                dc.Pop();
            }
        }

        // ------------------------------------------------------------------
        // 1. NeonBars
        // ------------------------------------------------------------------

        private static void DrawNeonBars(DrawingContext dc, FrameContext ctx)
        {
            Rect plot = ctx.Plot;
            if (plot.Width < 2.0 || plot.Height < 2.0)
                return;

            int barCount = ctx.BarCount;
            double slot = plot.Width / barCount;
            double barWidth = ClampSafe(slot * 0.64, 1.0, slot * 0.96);
            var columns = new List<ColumnSpec>(barCount);

            for (int i = 0; i < barCount; i++)
            {
                float value = ctx.SampleBand(i, barCount, true);
                // Minimum reach, matching MirrorSpectrum / PillBars / DenseColumns. Without it a
                // silent band is a zero-length line, and a zero-length line with PenLineCap.Round
                // paints a filled dot of diameter `t` in the near-white Core colour - a row of hot
                // white beads floating on the baseline with no bar attached. A short rounded stub
                // reads correctly. It is a FRACTION of the plot height, not a fixed 1.0 px: a hard
                // pixel floor quantises every genuinely quiet band onto the same rail.
                double reach = Math.Max(MinimumReach(plot.Height), value * plot.Height);
                double tipY = plot.Bottom - reach;
                columns.Add(new ColumnSpec(plot.X + (slot * (i + 0.5)), plot.Bottom, tipY, barWidth));
            }

            DrawColumns(dc, ctx, columns, Math.Max(0.6, barWidth * 0.34), PenLineCap.Round);
        }

        // ------------------------------------------------------------------
        // 2. MirrorSpectrum
        // ------------------------------------------------------------------

        private static void DrawMirrorSpectrum(DrawingContext dc, FrameContext ctx)
        {
            Rect plot = ctx.Plot;
            if (plot.Width < 2.0 || plot.Height < 2.0)
                return;

            double midY = plot.Y + (plot.Height * 0.5);
            double halfHeight = plot.Height * 0.5;
            int barCount = ctx.BarCount;
            double slot = plot.Width / barCount;
            double barWidth = ClampSafe(slot * 0.62, 1.0, slot * 0.96);
            var columns = new List<ColumnSpec>(barCount * 2);

            for (int i = 0; i < barCount; i++)
            {
                float value = ctx.SampleBand(i, barCount, true);
                double reach = Math.Max(MinimumReach(halfHeight), value * halfHeight);
                double x = plot.X + (slot * (i + 0.5));
                columns.Add(new ColumnSpec(x, midY, midY - reach, barWidth));
                columns.Add(new ColumnSpec(x, midY, midY + reach, barWidth));
            }

            DrawColumns(dc, ctx, columns, Math.Max(0.6, barWidth * 0.34), PenLineCap.Round);
        }

        // ------------------------------------------------------------------
        // 3. PillBars
        // ------------------------------------------------------------------

        private static void DrawPillBars(DrawingContext dc, FrameContext ctx)
        {
            Rect plot = ctx.Plot;
            if (plot.Width < 2.0 || plot.Height < 2.0)
                return;

            // ctx.BarCount is already clamped to the global [8, 256] range centrally, so asking for
            // 128 genuinely renders 128 capsules (the old local clamp to 64 silently halved it).
            int barCount = ctx.BarCount;
            double slot = plot.Width / barCount;
            double barWidth = ClampSafe(slot * 0.94, 1.5, slot * 0.98);

            // Slow breathing so the block never looks frozen between beats.
            double breathe = 0.93 + (0.07 * Math.Sin(ctx.Time * Math.PI * 2.0));
            double baseY = plot.Bottom - (plot.Height * 0.04 * (1.0 - breathe));

            var columns = new List<ColumnSpec>(barCount);
            for (int i = 0; i < barCount; i++)
            {
                float value = ctx.SampleBand(i, barCount, true);
                double span = plot.Height * breathe;
                double reach = Math.Max(MinimumReach(span), value * span);
                columns.Add(new ColumnSpec(plot.X + (slot * (i + 0.5)), baseY, baseY - reach, barWidth));
            }

            DrawColumns(dc, ctx, columns, Math.Max(0.8, barWidth * 0.42), PenLineCap.Round);
        }

        // ------------------------------------------------------------------
        // 4. DenseColumns - the "purple -> cyan curtain"
        // ------------------------------------------------------------------

        private static void DrawDenseColumns(DrawingContext dc, FrameContext ctx)
        {
            Rect plot = ctx.Plot;
            if (plot.Width < 2.0 || plot.Height < 2.0)
                return;

            // Centrally clamped to [8, 256] - no style-local ceiling, so the curtain always has the
            // bar count the user asked for. thickness is capped at 3 px so 256 columns stay hairlines.
            int barCount = ctx.BarCount;
            double slot = plot.Width / barCount;
            double thickness = Math.Clamp(slot * 0.62, 1.0, 3.0);
            var columns = new List<ColumnSpec>(barCount);

            for (int i = 0; i < barCount; i++)
            {
                // Deliberately unsmoothed: the curtain is meant to look raw and slightly ragged.
                float value = ctx.SampleBand(i, barCount, false);
                double reach = Math.Max(MinimumReach(plot.Height), value * plot.Height);
                columns.Add(new ColumnSpec(plot.X + (slot * (i + 0.5)), plot.Bottom, plot.Bottom - reach, thickness));
            }

            DrawColumns(dc, ctx, columns, Math.Max(0.5, thickness * 0.55), PenLineCap.Round);
        }

        // ------------------------------------------------------------------
        // 5. DotMatrix
        // ------------------------------------------------------------------

        /// <summary>
        /// The dot grid.
        /// <para>
        /// AUDITED AND DELIBERATELY LEFT ALONE - read this before trying to "optimise" it.
        /// <para>
        /// Measured on this job (1080x1080 canvas, 62 % x 62 % plot), this style records
        /// 4 474 075 B/frame and 3.93 ms at BarCount 256, and 652 311 B/frame and 0.75 ms at
        /// BarCount 40. The 256 figure is the largest in this file, so it looks like the
        /// AudioRing defect all over again. It is not, and the AudioRing fix does not transfer.
        /// <para>
        /// WHY THE AUDIT FOUND NOTHING TO BUCKET. At BarCount 256 the grid is 256 columns x 24 rows
        /// (1080x1080, 62 % plot -> stepX 2.62 px, stepY 27.5 px, rows clamped to its 24 ceiling) =
        /// 6 144 dots/frame. Each dot is written as BeginFigure + 4 ArcTo, so the frame records
        /// 30 720 MIL path records into two StreamGeometry streams - one lit, one dim - and that is
        /// ALREADY the bucketed shape: 2 geometries, 2 brushes, 2 DrawGeometry calls, no per-dot
        /// object, brush or pen anywhere in the loop. There is nothing left to merge, because the
        /// two buckets are keyed on the only two things that vary (lit vs unlit, and the 0.72
        /// radius ratio), and every dot in a bucket already shares that bucket's brush exactly.
        /// <para>
        /// The cost is the PATH RECORD STREAM, not the object graph. Measured directly, 6 144 dots
        /// built five different ways, bytes allocated per frame:
        /// <list type="bullet">
        /// <item>2 geometries (what this code does), frozen: 3 911 336 B = 637 B/dot.</item>
        /// <item>2 geometries, not frozen: 3 911 336 B - identical, so the freeze is not the cost.</item>
        /// <item>2 geometries + 2 DrawGeometry: 3 911 992 B - the draws themselves are free.</item>
        /// <item>256 geometries, one per column: 5 093 400 B = 829 B/dot. 30 % WORSE, not better.</item>
        /// <item>6 144 EllipseGeometry objects + 6 144 DrawEllipse: 790 792 B = 129 B/dot.</item>
        /// </list>
        /// The reason is <see cref="MemoryStream"/> doubling: ~199 B of records per dot in a stream
        /// that grows by repeated doubling, so the transients sum to roughly 2x the final buffer.
        /// Splitting the same dots across MORE streams adds per-stream overhead to every one of
        /// them, which is why "one geometry per column" - the fallback shape the AudioRing note
        /// allows - measurably loses. And the cheap-looking option is disqualified on pixels: a WPF
        /// <see cref="EllipseGeometry"/> rasterises to max 60 / mean 1.85 per channel off the
        /// 4-ArcTo circle over 6.58 % of pixels, because it flattens with the 4-bezier kappa
        /// approximation while the arc records convert adaptively. This grid is drawn at full size,
        /// so that is not rounding, it is a different picture.
        /// <para>
        /// A per-column geometry CACHE across frames was the last idea worth a number, and it is
        /// dead on arrival too: measured over the 150 analysed frames a column's lit count changes
        /// on 50.1 % (BarCount 40) / 49.2 % (BarCount 256) of frame transitions, so half the grid
        /// would be rebuilt every frame anyway - on top of trading 2 draw calls for 512 and holding
        /// 512 geometries live.
        /// <para>
        /// Net: at BarCount 256 this style is already doing the minimum a 6 144-dot grid can do,
        /// and any further reduction has to come from drawing fewer or flatter dots. That is a
        /// visual decision, not an allocation one.
        /// </summary>
        private static void DrawDotMatrix(DrawingContext dc, FrameContext ctx)
        {
            Rect plot = ctx.Plot;
            if (plot.Width < 4.0 || plot.Height < 4.0)
                return;

            // Centrally clamped to [8, 256]: the old local cap of 96 made a requested 128 silently
            // render 96 columns with no feedback.
            int columns = ctx.BarCount;
            int rows = Math.Clamp((int)Math.Floor(plot.Height / Math.Max(3.0, plot.Width / columns)), 3, 24);

            double stepX = plot.Width / columns;
            double stepY = plot.Height / rows;
            double radius = Math.Clamp(Math.Min(stepX, stepY) * 0.38, 0.7, Math.Max(1.2, Math.Min(stepX, stepY) * 0.46));

            var lit = new StreamGeometry();
            var dim = new StreamGeometry();

            using (StreamGeometryContext litCtx = lit.Open())
            using (StreamGeometryContext dimCtx = dim.Open())
            {
                for (int c = 0; c < columns; c++)
                {
                    float value = ctx.SampleBand(c, columns, true);
                    int litCount = (int)Math.Round(value * rows);
                    litCount = Math.Clamp(litCount, 1, rows);

                    double x = plot.X + (stepX * (c + 0.5));
                    for (int r = 0; r < rows; r++)
                    {
                        double y = plot.Y + (stepY * (r + 0.5));
                        var point = new Point(x, y);
                        if (r < litCount)
                            AddEllipseFigure(litCtx, point, radius, radius);
                        else
                            AddEllipseFigure(dimCtx, point, radius * 0.72, radius * 0.72);
                    }
                }
            }

            if (lit.CanFreeze) lit.Freeze();
            if (dim.CanFreeze) dim.Freeze();

            Brush dimBrush = CreateBrush(WithAlpha(ctx.Resolved.Glow, 0.10 * ctx.GlowIntensity));
            dc.DrawGeometry(dimBrush, null, dim);

            // One absolute gradient over the whole grid: rows brighten towards the top of the plot.
            Brush litBrush = ctx.GetPlotGradientBrush(plot.Bottom, plot.Top);
            dc.DrawGeometry(litBrush, null, lit);
        }

        // ------------------------------------------------------------------
        // 6. RadialRing
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
// 13. AudioRing - the NCS look
// ------------------------------------------------------------------

        /// <summary>
        /// Draws the ring NCS actually uses: one thick neon circle whose radius ripples with the
        /// waveform, plus glowing particles drifting inside it.
        /// <para>
        /// This is deliberately NOT <see cref="RadialRing"/>. That style draws bars radiating from
        /// a hub - the After Effects "polar spectrum" look - whereas the NCS visualiser is a single
        /// stroked ring, so the two read nothing alike. The reference is a thick yellow neon circle
        /// on a dark field with a scatter of small glowing dots inside it.
        /// <para>
        /// The ring radius is driven by the time-domain waveform (not the FFT), sampled around the
        /// circumference: that is what makes the outline wobble instead of showing discrete spokes.
        /// The stroke then gets the standard three additive passes so it blooms. The inner particles
        /// are positioned from a stable per-index pseudo-random angle and radius, modulated by band
        /// energy, so they shimmer without ever popping between frames.
        /// </para>
        /// </summary>
        private static void DrawAudioRing(DrawingContext dc, FrameContext ctx)
        {
            Rect plot = ctx.Plot;
            double outer = MaxRadius(plot);
            if (outer < 6.0)
                return;

            Point center = plotCenter(plot);
            double baseRadius = outer * AudioRingBaseFraction;
            double amplitude = (outer - baseRadius) * AudioRingAmplitudeFraction;

            AudioFrameData? frame = ctx.Current;
            // Both waveform arrays are read at the same index in the loop below, so the bucket
            // count has to be bounded by the SHORTER of the two. Reading WaveMin.Length while
            // indexing WaveMax threw IndexOutOfRangeException on any frame where the two
            // disagreed - and the preset generator catches that exception, which turned a
            // one-frame defect into a lost render.
            int columns = Math.Min(frame?.WaveMin?.Length ?? 0, frame?.WaveMax?.Length ?? 0);

            // --- the ring itself -------------------------------------------------
            var ring = new StreamGeometry();
            using (StreamGeometryContext g = ring.Open())
            {
                const int RingSteps = 240;
                g.BeginFigure(PointOnCircle(center, baseRadius, 0), true, true);
                for (int s = 1; s <= RingSteps; s++)
                {
                    double angle = 360.0 * s / RingSteps;
                    double value = 0.0;

                    if (columns > 0)
                    {
                        // Walk the waveform around the circumference; average the min/max in each
                        // bucket so a quiet passage still gives a closed, non-degenerate ring.
                        double start = (s - 1) / (double)RingSteps * columns;
                        double end = s / (double)RingSteps * columns;
                        int i0 = (int)Math.Floor(start);
                        int i1 = Math.Max(i0 + 1, (int)Math.Ceiling(end));
                        double lo = 0.0, hi = 0.0;
                        int n = 0;
                        for (int i = i0; i < i1 && i < columns; i++)
                        {
                            lo += frame!.WaveMin[i];
                            hi += frame!.WaveMax[i];
                            n++;
                        }
                        if (n > 0)
                            value = Math.Abs(((hi - lo) * 0.5f)) * ctx.Sensitivity;
                    }

                    double radius = Math.Clamp(
                        baseRadius + (Math.Clamp(value, 0.0, 1.0) * amplitude),
                        1.0, outer);
                    g.LineTo(PointOnCircle(center, radius, angle), true, false);
                }
            }
            if (ring.CanFreeze) ring.Freeze();

            double ringThickness = Math.Max(1.5, outer * AudioRingThicknessFraction);
            DrawGlowPasses(
                dc, ring, ringThickness, ctx.Resolved, ctx.GlowIntensity,
                ctx.AlphaFactor, PenLineCap.Round,
                Math.Max(RadialHaloExtent(outer), outer * AudioRingHaloFraction));

            // --- the dot lattice inside the ring --------------------------------
            // The NCS reference is not a scatter of particles: it is a LATTICE of dots on
            // concentric circles, each circle rotated a little further than the last. The overlap
            // between neighbouring circles is what reads as the moire / vortex that gives the
            // reference its spherical depth. So the lattice is built as arcs here, not as a
            // golden-angle point cloud - a point cloud cannot produce that interference pattern.
            //
            // The dots are drawn in BATCHES, not one at a time: see AudioRingDotBuckets. A dot's
            // radius and its alpha are both monotonic in its band energy, so the energy is snapped
            // to a bucket and every dot in that bucket is appended to that bucket's one geometry.
            float[]? bands = frame?.Bands;
            if (bands == null || bands.Length == 0)
                return;

            double latticeInner = baseRadius * 0.10;
            double latticeSpan = (baseRadius * 0.80) - latticeInner;
            int ringCount = Math.Clamp((int)(latticeSpan / Math.Max(1.0, outer * AudioRingLatticeStep)), 3, 12);

            // Target angular spacing between neighbouring dots, so outer circles get more of them.
            double dotSpacing = Math.Max(3.0, ringThickness * 0.92);
            double spin = ctx.Time * AudioRingParticleSpinDegrees;

            // Perpendicular spacing between adjacent circles sets how dense the interference is.
            // It is a property of the WHOLE lattice, not of one circle: it used to be recomputed
            // once per circle, and `densityBoost` - the only thing that reads it - once per DOT.
            double circleGap = latticeSpan / Math.Max(1, ringCount - 1);
            double densityBoost = Math.Clamp(circleGap / Math.Max(1.0, dotSpacing), 0.25, 1.0);

            // Everything the dot loop still needs that does NOT vary per dot, hoisted: what is left
            // of the old expressions is `dotUnit * (0.55 + 0.45 * energy)` and
            // `alphaBase * (0.58 + 0.42 * energy)`, both now evaluated once per BUCKET.
            double dotUnit = dotSpacing * 0.30 * (0.7 + (0.3 * densityBoost));
            double alphaBase = ctx.AlphaFactor * ctx.GlowIntensity;
            double sensitivity = ctx.Sensitivity;

            AudioRingBatches batches = RentBatches();
            batches.Prepare(dotUnit, alphaBase);

            try
            {
                for (int latticeRing = 0; latticeRing < ringCount; latticeRing++)
                {
                    double t = ringCount == 1 ? 0.0 : latticeRing / (double)(ringCount - 1);
                    double radius = latticeInner + (latticeSpan * t);

                    int dotsOnRing = Math.Max(8, (int)Math.Round(2.0 * Math.PI * radius / dotSpacing));

                    // Each circle is rotated a little further than the previous one: the spiral.
                    double ringOffset = latticeRing * AudioRingLatticeSpiralDegrees + spin;

                    for (int d = 0; d < dotsOnRing; d++)
                    {
                        double a = ringOffset + (360.0 * d / dotsOnRing);

                        // Energy for this dot comes from the waveform angle it sits at, so the lattice
                        // brightens and dims in step with the ring's own wobble.
                        int bandIndex = Math.Clamp(
                            (int)(((a % 360.0) / 360.0) * bands.Length), 0, bands.Length - 1);
                        double energy = Math.Clamp(bands[bandIndex] * sensitivity, 0.0, 1.0);

                        // Dots tighten toward the hub where the circles crowd together, matching the
                        // reference's denser inner spiral. Both the radius and the alpha the dot
                        // paints with are now read off the bucket, so the whole bucket shares one
                        // geometry, one brush pair and one pen pair.
                        int bucket = (int)Math.Round(energy * (AudioRingDotBuckets - 1));
                        if (bucket < 0)
                            bucket = 0;
                        else if (bucket > AudioRingDotBuckets - 1)
                            bucket = AudioRingDotBuckets - 1;

                        // The reference keeps the whole lattice readable and lets brightness ride on top,
                        // rather than letting quiet bands black the dots out and turning the moire
                        // into chains. Checked on the BUCKET alpha so that every dot in a bucket is
                        // treated identically - that is the whole point of bucketing - and so that
                        // Opacity = 0 rejects all of them before any geometry is opened.
                        if (batches.Alphas[bucket] <= 0.004)
                            continue;

                        double dotRadius = batches.Radii[bucket];
                        AddEllipseFigure(
                            batches.Open(bucket),
                            PointOnCircle(center, radius, a),
                            dotRadius,
                            dotRadius);
                    }
                }

                // One halo pass then one core pass per bucket, in the same order every single dot
                // used to get: the translucent halo outline first, the bright core over it.
                for (int b = 0; b < AudioRingDotBuckets; b++)
                {
                    if (!batches.Used[b])
                        continue;

                    Geometry geometry = batches.Seal(b);
                    if (geometry.CanFreeze)
                        geometry.Freeze();

                    double dotRadius = batches.Radii[b];
                    double alpha = batches.Alphas[b];

                    Pen[] pens = ctx.Resolved.GetDotPens(
                        dotRadius,
                        WithAlpha(ctx.Resolved.Glow, 0.20 * alpha),
                        WithAlpha(ctx.Resolved.Core, 0.95 * alpha));

                    dc.DrawGeometry(null, pens[0], geometry);
                    dc.DrawGeometry(null, pens[1], geometry);
                }
            }
            finally
            {
                batches.Release();
            }
        }

        [ThreadStatic]
        private static AudioRingBatches? _audioRingBatches;

        /// <summary>
        /// Per-frame scratch for the bucketed AudioRing lattice: one lazily-opened
        /// <see cref="StreamGeometry"/> per alpha bucket, reused across frames on the same thread.
        /// <para>
        /// Thread-static rather than static because two overlay renders can be in flight at once;
        /// not a lock because a bucket's geometry is only ever touched by the thread that built it.
        /// The arrays are allocated once per thread, never per frame.
        /// </para>
        /// </summary>
        private sealed class AudioRingBatches
        {
            private StreamGeometry?[] _geometries = Array.Empty<StreamGeometry?>();
            private StreamGeometryContext?[] _contexts = Array.Empty<StreamGeometryContext?>();
            private bool[] _used = Array.Empty<bool>();
            private int _count;

            /// <summary>Dot radius per bucket. Dots tighten toward the hub where the circles crowd.</summary>
            public double[] Radii { get; } = new double[AudioRingDotBuckets];

            /// <summary>Alpha per bucket. Carries the Opacity slider, so 0 rejects the whole lattice.</summary>
            public double[] Alphas { get; } = new double[AudioRingDotBuckets];

            /// <summary>Which buckets got at least one dot this frame.</summary>
            public bool[] Used => _used;

            /// <summary>Recomputes the per-bucket radius and alpha tables for this frame's canvas.</summary>
            public void Prepare(double dotUnit, double alphaBase)
            {
                if (_count != AudioRingDotBuckets)
                {
                    Release();
                    _geometries = new StreamGeometry?[AudioRingDotBuckets];
                    _contexts = new StreamGeometryContext?[AudioRingDotBuckets];
                    _used = new bool[AudioRingDotBuckets];
                    _count = AudioRingDotBuckets;
                }

                for (int b = 0; b < AudioRingDotBuckets; b++)
                {
                    // Bucket centres span the closed energy range [0, 1] so the extremes - a dead
                    // quiet band and a full-scale one - are still drawn at their exact old values.
                    double energy = b / (double)(AudioRingDotBuckets - 1);
                    Radii[b] = Math.Max(0.7, dotUnit * (0.55 + (0.45 * energy)));
                    Alphas[b] = alphaBase * (0.58 + (0.42 * energy));
                    _used[b] = false;
                }
            }

            /// <summary>Returns the bucket's open context, creating its geometry on first touch.</summary>
            public StreamGeometryContext Open(int bucket)
            {
                if (_contexts[bucket] == null)
                {
                    var geometry = new StreamGeometry();
                    _geometries[bucket] = geometry;
                    _contexts[bucket] = geometry.Open();
                    _used[bucket] = true;
                }

                return _contexts[bucket]!;
            }

            /// <summary>Closes the bucket's context so the geometry can be frozen and drawn.</summary>
            public Geometry Seal(int bucket)
            {
                _contexts[bucket]?.Close();
                _contexts[bucket] = null;
                return _geometries[bucket]!;
            }

            /// <summary>Closes anything left open and drops every geometry so the next frame starts clean.</summary>
            public void Release()
            {
                for (int b = 0; b < _count; b++)
                {
                    _contexts[b]?.Close();
                    _contexts[b] = null;
                    _geometries[b] = null;
                    _used[b] = false;
                }
            }
        }

        private static AudioRingBatches RentBatches()
            => _audioRingBatches ??= new AudioRingBatches();

        private static void DrawRadialRing(DrawingContext dc, FrameContext ctx)
        {
            Rect plot = ctx.Plot;
            // The ring is INSCRIBED in the plot box: outer = min(w, h) / 2 less a small inset, so a
            // 660 x 280 plot draws a ~274 px ring. The measured defect was not this radius - it was
            // that the drawn extent collapsed to ~106 px because the BAR LENGTHS were raw band
            // values, and real music only carries 0.1-0.4 of the 0..1 band scale. The per-frame
            // auto-gain below is what actually fills the box.
            double outer = MaxRadius(plot);
            if (outer < 4.0)
                return;

            Point center = plotCenter(plot);
            double inner = outer * 0.30;

            int barCount = ctx.BarCount;
            double rotation = ctx.Time * 360.0;
            double span = 360.0 / barCount;

            var rawLevels = new double[barCount];
            for (int i = 0; i < barCount; i++)
                rawLevels[i] = ctx.SampleBand(i, barCount, true);

            double[] levels = ResolveRadialLevels(rawLevels);

            var tipRadii = new double[barCount];
            var angles = new double[barCount];
            var rays = new List<RaySpec>(barCount);

            FillRadialBarSet(levels, center, inner, outer, rotation, span, tipRadii, angles, rays);

            DrawRadialWedges(dc, ctx, center, inner, tipRadii, angles, span);
            DrawRadialRays(dc, ctx, rays, Math.Max(0.6, (outer - inner) * 0.020), PenLineCap.Round, RadialHaloExtent(outer));
        }

        // ------------------------------------------------------------------
        // 7. DualRings
        // ------------------------------------------------------------------

        private static void DrawDualRings(DrawingContext dc, FrameContext ctx)
        {
            Rect plot = ctx.Plot;
            double outer = MaxRadius(plot);
            if (outer < 6.0)
                return;

            Point center = plotCenter(plot);
            int ringCount = Math.Clamp(ctx.Settings.RingCount, 2, 6);

            // Fit the ring layout to the AVAILABLE annular width divided by the ACTUAL ring count.
            // The old fixed 0.24 factor made ringOuter negative by ring 4, so the `continue` fired and
            // 2-3 requested rings silently never rendered. Here each ring gets an equal share of the
            // annulus [hub, outer], so every requested ring is drawn and ringOuter > ringInner is
            // guaranteed by construction (stride >= thickness).
            // `gapRatio` was widened from 0.16 to 0.26: once the rings actually fill the annulus (see
            // the auto-gain below) a 16 % gap was swallowed by each ring's own glow and 6 rings read
            // as one solid band. 26 % keeps a clear dark channel at every ring count, because the
            // glow stack is now capped at RadialHaloFraction of the outer radius (< the gap width).
            const double hubFraction = 0.10;
            const double gapRatio = 0.26;
            double hub = outer * hubFraction;
            double annulus = Math.Max(1.0, outer - hub);
            double stride = annulus / ringCount;
            double ringThickness = Math.Max(1.0, stride * (1.0 - gapRatio));
            double maxHalo = RadialHaloExtent(outer);

            int barCount = Math.Max(24, ctx.BarCount / ringCount);
            double span = 360.0 / barCount;
            float[]? bands = ctx.Current?.Bands;
            int bandLength = bands?.Length ?? 0;
            int slice = Math.Max(1, bandLength / ringCount);

            var rawRingLevels = new double[barCount];
            var rays = new List<RaySpec>(barCount);

            for (int ring = 0; ring < ringCount; ring++)
            {
                double ringOuter = outer - (ring * stride);
                double ringInner = ringOuter - ringThickness;
                if (ringInner < hub)
                    ringInner = hub;
                // Guard: the stride-derived layout already guarantees ringOuter > ringInner, but
                // clamp defensively so a degenerate plot can never emit an inverted ring.
                if (ringOuter <= ringInner)
                {
                    ringOuter = outer;
                    ringInner = Math.Max(hub, outer - Math.Max(1.0, ringThickness));
                    if (ringOuter <= ringInner)
                        continue;
                }

                // Counter-rotation: even rings turn forward, odd rings turn backward.
                // LOOP-PERIODICITY: ctx.Time sweeps 0 -> 1 across the loop and ffmpeg feeds the PNG
                // sequence with loop=loop=-1, so frame N-1 is followed by frame 0. A rotation is
                // therefore seamless only when the total sweep is an integer multiple of 360 deg,
                // which makes frame N-1 congruent to frame 0 by construction. The original rates
                // (0.35 -> 126 deg, -0.26 -> -93.6 deg) are not integer turns and visibly snapped at
                // the wrap. They are snapped to the nearest integer turn at or above the intended
                // speed: +1 turn forward for even rings, -1 turn backward for odd rings. Equal
                // magnitudes in opposite directions keep the counter-rotation visually distinct.
                // -0.5 turn was rejected for the odd rings because it leaves the ring 180 deg rotated
                // at the wrap, which is still a snap.
                double direction = (ring % 2 == 0) ? 1.0 : -1.0;
                double rotation = ctx.Time * 360.0 * direction;

                var tipRadii = new double[barCount];
                var angles = new double[barCount];

                // Each ring normalises against its OWN frequency slice, so the quiet high-frequency
                // ring still reaches its outer radius instead of collapsing into the hub.
                for (int i = 0; i < barCount; i++)
                    rawRingLevels[i] = ctx.SampleBandRange(i, barCount, ring * slice, slice, true);

                double[] levels = ResolveRadialLevels(rawRingLevels);
                rays.Clear();

                FillRadialBarSet(levels, center, ringInner, ringOuter, rotation, span, tipRadii, angles, rays);

                DrawRadialWedges(dc, ctx, center, ringInner, tipRadii, angles, span);

                // Ray thickness is bounded by the ANGULAR SLOT ARC, not by the ring's radial
                // thickness. The old `ringThickness * 0.14` fed the ring's BAND width (45.7 px at
                // RingCount=2 / a 274 px ring) straight into the pen: a 6.4 px stroke whose 3-pass
                // halo was 32 px wide, drawn additively over a 45 px band of solid wedges - which is
                // the reported white teardrop. Slot arc is now the cap, so a ring never paints wider
                // than the angle it is given and the halo cap below keeps it from bridging the gap.
                double slotArc = (2.0 * Math.PI * Math.Max(1.0, (ringInner + ringOuter) * 0.5)) / Math.Max(1, barCount);
                double rayThickness = Math.Clamp(ringThickness * 0.28, 0.8, Math.Max(0.8, slotArc * 0.55));
                DrawRadialRays(dc, ctx, rays, rayThickness, PenLineCap.Round, maxHalo);
            }
        }

        // ------------------------------------------------------------------
        // 8. RippleRings
        // ------------------------------------------------------------------

        private static void DrawRippleRings(DrawingContext dc, FrameContext ctx)
        {
            Rect plot = ctx.Plot;
            double outer = MaxRadius(plot);
            if (outer < 4.0)
                return;

            Point center = plotCenter(plot);
            double inner = outer * RippleInnerRadiusFraction;
            // The default 0.022 stroke was ~1.4 px at 1080 - far too thin to read as a neon ring
            // once the glow is spread over it. RippleRings draws up to MaxRipples concentric rings,
            // so they need real weight to stay legible against the video behind them.
            double thickness = Math.Max(1.5, outer * RippleThicknessFraction);
            int lifetime = Math.Max(6, ctx.FrameCount / 2);
            int maxLive = Math.Clamp(ctx.Settings.RingCount, 1, MaxRipples);

            if (ctx.Current?.Onset == true)
                ctx.State?.NoteOnset(ctx.Index, maxLive);

            ctx.State?.ExpireRipples(ctx.Index, lifetime);

            var ringRadii = new List<double>(maxLive);
            var ringAlpha = new List<double>(maxLive);

            if (ctx.State != null)
            {
                foreach (SoundBarRenderState.Ripple ripple in ctx.State.Ripples)
                {
                    int age = ctx.Index - ripple.SpawnFrame;
                    if (age < 0)
                        continue;

                    double t = Math.Clamp(age / (double)lifetime, 0.0, 1.0);
                    double eased = 1.0 - Math.Pow(1.0 - t, 3.0);
                    double radius = inner + ((outer - inner) * eased);
                    double fade = Math.Pow(1.0 - t, 1.6) * (0.35 + (0.65 * ripple.Energy));

                    // Drop fully-faded ripples HERE rather than letting them survive the count check
                    // below and then be skipped by the alpha cutoff in the draw loop. A ripple that is
                    // still alive but has decayed to nothing used to leave ringRadii non-empty, which
                    // suppressed the idle fallback and produced a 100 % transparent frame - the figure
                    // silently vanished for ~0.1 s on healthy audio (measured on frames 134-136).
                    // The drawn core alpha is 0.92 * fade, so gating the COUNT on bare `fade` kept
                    // ripples alive (suppressing the idle fallback) long after they had stopped being
                    // visible - measured as 6 consecutive frames at <= 5.5 % alpha.
                    //
                    // The gate is on `fade` ALONE. It used to be `fade * GlowIntensity`, which meant
                    // the whole style went 100 % transparent at GlowIntensity = 0: the gate dropped
                    // every ripple, and every ripple also had to clear the alpha cutoff inside the
                    // draw loop. The glow knob is applied where it belongs - in the alpha of the
                    // passes (see DrawGlowPasses) - so turning glow off now means "flat rings, no
                    // bloom", which is what it means in every other style.
                    if (fade <= 0.05)
                        continue;

                    ringRadii.Add(radius);
                    ringAlpha.Add(Math.Clamp(fade, 0.0, 1.0));
                }
            }

            // Whether the lists below are real ripples or the idle stand-in. Only real ripples have a
            // "youngest" ring to bloom, and the idle set has no such member.
            bool hasLiveRipple = ringRadii.Count > 0;

            if (!hasLiveRipple)
            {
                // No live ripple (first frame, or a cached preview): show a calm idle ring set.
                // `t` spans [0, 1) and NOT [1/N, 1]. With the old i+1 over maxLive the LAST entry was
                // t = 1 and its alpha was 0.30 * (1 - 1) = 0, so at RingCount = 1 - or with glow
                // off - the outermost ring of the idle set was a guaranteed no-op.
                for (int i = 0; i < maxLive; i++)
                {
                    double t = i / (double)maxLive;
                    ringRadii.Add(inner + ((outer - inner) * t));
                    ringAlpha.Add(0.30 * (1.0 - t));
                }
            }

            // The shared three-pass glow recipe, halo first and core last, with the ripple's own
            // fade folded in as the per-shape alpha scale. `coreAlways` is what keeps the style
            // alive at GlowIntensity = 0: the core still paints at `Accent` x fade, flat, with no
            // bloom. The halo is now bounded by RadialHaloExtent(outer) like the other three radial
            // styles, instead of an unbounded thickness * 5.0 that could halo wider than the ring.
            for (int i = 0; i < ringRadii.Count; i++)
            {
                double radius = Math.Clamp(ringRadii[i], 0.5, outer);

                var ellipse = new EllipseGeometry(center, radius, radius);
                if (ellipse.CanFreeze)
                    ellipse.Freeze();

                DrawGlowPasses(
                    dc,
                    ellipse,
                    thickness,
                    ctx.Resolved,
                    ctx.GlowIntensity,
                    ctx.AlphaFactor,
                    PenLineCap.Round,
                    RadialHaloExtent(outer),
                    coreAlways: true,
                    alphaScale: ringAlpha[i],
                    coreColor: ctx.Resolved.Accent);
            }

            // Soft bloom tracking the youngest ripple.
            double leadRadius = ringRadii[ringRadii.Count - 1];
            double leadAlpha = ringAlpha[ringAlpha.Count - 1];
            if (hasLiveRipple && leadAlpha > 0.02)
            {
                RadialGradientBrush bloom = CreateRadialGlowBrush(
                    ctx.Resolved.Glow,
                    0.55 * leadAlpha * ctx.AlphaFactor,
                    center,
                    Math.Max(2.0, leadRadius * 1.6));

                if (bloom.CanFreeze)
                    bloom.Freeze();

                dc.DrawEllipse(bloom, null, center, Math.Max(2.0, leadRadius * 1.6), Math.Max(2.0, leadRadius * 1.6));
            }
        }

        // ------------------------------------------------------------------
        // 9. SpokeLines
        // ------------------------------------------------------------------

        private static void DrawSpokeLines(DrawingContext dc, FrameContext ctx)
        {
            Rect plot = ctx.Plot;
            double outer = MaxRadius(plot);
            if (outer < 4.0)
                return;

            Point center = plotCenter(plot);
            double inner = outer * 0.14;
            int barCount = ctx.BarCount;
            double span = 360.0 / barCount;
            // Loop-periodic by construction: the sweep over one loop is exactly -1 full turn, so
            // frame N-1 is congruent to frame 0 and the ffmpeg loop=loop=-1 wrap is seamless.
            // See DrawDualRings for the full rationale.
            double rotation = ctx.Time * -360.0;

            var rawLevels = new double[barCount];
            for (int i = 0; i < barCount; i++)
                rawLevels[i] = ctx.SampleBand(i, barCount, true);

            double[] levels = ResolveRadialLevels(rawLevels);

            var rays = new List<RaySpec>(barCount);

            // SpokeLines draws no wedges, so it passes null for the two wedge-only outputs rather than
            // allocating a pair of barCount-sized arrays per frame for FillRadialBarSet to fill.
            FillRadialBarSet(levels, center, inner, outer, rotation, span, null, null, rays);

            // Legibility floor. The proportional term (outer - inner) * 0.014 is ~1.1 px at
            // 1280x720 and shrinks again as the canvas or the plot grows, so the whole style
            // rendered as "the halo of an invisible figure". 2.0 px is the floor at the 1080 HD
            // reference and scales with the canvas from there (never below 2.0, never above 8.0 so a
            // 4K canvas cannot turn thin rays into slabs), which keeps the rays visible at
            // 1280x720 AND on a thumbnail-sized plot, where the proportional term is ~0.5 px.
            double floorPx = OverlayLayoutHelper.ScaleFromHdReference(ctx.Layout, 2.0, 2.0, 8.0);
            double thickness = Math.Clamp((outer - inner) * 0.014, floorPx, floorPx * 3.0);

            DrawRadialRays(dc, ctx, rays, thickness, PenLineCap.Flat, RadialHaloExtent(outer), coreAlways: true);
        }

        // ------------------------------------------------------------------
        // 10. WaveRibbon
        // ------------------------------------------------------------------

        private static void DrawWaveRibbon(DrawingContext dc, FrameContext ctx)
        {
            Rect plot = ctx.Plot;
            if (plot.Width < 4.0 || plot.Height < 4.0)
                return;

            double midY = plot.Y + (plot.Height * 0.5);
            double halfHeight = plot.Height * 0.48;

            AudioFrameData? frame = ctx.Current;
            float[]? waveMin = frame?.WaveMin;
            float[]? waveMax = frame?.WaveMax;
            int available = Math.Min(waveMin?.Length ?? 0, waveMax?.Length ?? 0);
            // Clamp BEFORE multiplying: ctx.BarCount is already in [8, 256] so the product cannot
            // overflow (the old `ctx.Settings.BarCount * 2` overflowed to a negative int at
            // int.MaxValue and silently clamped up to the 24-column floor). The ribbon needs twice
            // the bar count for a smooth waveform, with a geometry floor of 24 columns.
            int columns = Math.Clamp(ctx.BarCount * 2, 24, 512);

            var top = new Point[columns];
            var bottom = new Point[columns];
            double slot = plot.Width / columns;

            for (int i = 0; i < columns; i++)
            {
                double x = plot.X + (slot * (i + 0.5));
                float lo = 0f;
                float hi = 0f;

                if (available > 0)
                {
                    int si = Math.Clamp((int)Math.Floor((i / (double)columns) * available), 0, available - 1);
                    lo = Math.Clamp(waveMin![si], -1f, 1f);
                    hi = Math.Clamp(waveMax![si], -1f, 1f);
                }

                if (lo > hi)
                {
                    (lo, hi) = (hi, lo);
                }

                top[i] = new Point(x, midY - (hi * halfHeight));
                bottom[i] = new Point(x, midY - (lo * halfHeight));
            }

            var ribbon = new StreamGeometry();
            using (StreamGeometryContext gctx = ribbon.Open())
            {
                gctx.BeginFigure(top[0], true, true);
                for (int i = 1; i < columns; i++)
                    gctx.LineTo(top[i], true, true);
                for (int i = columns - 1; i >= 0; i--)
                    gctx.LineTo(bottom[i], true, true);
            }

            if (ribbon.CanFreeze)
                ribbon.Freeze();

            dc.DrawGeometry(ctx.GetPlotGradientBrush(plot.Bottom, plot.Top), null, ribbon);

            var rim = new StreamGeometry();
            using (StreamGeometryContext gctx = rim.Open())
            {
                gctx.BeginFigure(top[0], false, false);
                for (int i = 1; i < columns; i++)
                    gctx.LineTo(top[i], true, false);

                gctx.BeginFigure(bottom[0], false, false);
                for (int i = 1; i < columns; i++)
                    gctx.LineTo(bottom[i], true, false);
            }

            if (rim.CanFreeze)
                rim.Freeze();

            DrawGlowPasses(dc, rim, Math.Max(0.6, plot.Height * 0.012), ctx.Resolved, ctx.GlowIntensity, ctx.AlphaFactor, PenLineCap.Round);
        }

        // ------------------------------------------------------------------
        // 11. CenterColumn
        // ------------------------------------------------------------------

        private static void DrawCenterColumn(DrawingContext dc, FrameContext ctx)
        {
            Rect plot = ctx.Plot;
            if (plot.Width < 2.0 || plot.Height < 2.0)
                return;

            double centerX = plot.X + (plot.Width * 0.5);
            double thickness = Math.Clamp(plot.Height * 0.10, 2.0, Math.Max(2.0, plot.Width * 0.30));
            double rms = CenterColumnLevel(ctx.Current?.Rms ?? 0f);

            // Peak hold: exponential decay over the previous 24 analysed frames.
            float rawHold = Math.Clamp(ctx.Current?.Peak ?? 0f, 0f, 1f);
            AudioFrameData[] frames = ctx.Frames;
            for (int back = 1; back <= 24 && ctx.Index - back >= 0; back++)
            {
                AudioFrameData? back_frame = FrameAt(frames, ctx.Index - back);
                if (back_frame == null)
                    break;

                float decayed = back_frame.Peak * (float)Math.Pow(0.985, back);
                if (decayed > rawHold)
                    rawHold = decayed;
            }

            // The tick is tracked in the SAME 0..1 height space as the column, so it can never
            // drift off the top of the plot the way a raw 0..1 peak did.
            double hold = CenterColumnLevel(rawHold);

            // Faint full-height track so the column always reads as a meter.
            var track = new StreamGeometry();
            using (StreamGeometryContext gctx = track.Open())
            {
                gctx.BeginFigure(new Point(centerX, plot.Bottom), false, false);
                gctx.LineTo(new Point(centerX, plot.Y), true, false);
            }

            if (track.CanFreeze) track.Freeze();
            dc.DrawGeometry(null, CreatePen(CreateBrush(WithAlpha(ctx.Resolved.Glow, 0.14 * ctx.AlphaFactor)), thickness * 0.45), track);

            // The four linear bar styles now floor at MinimumReach(span), a FRACTION of their span. This
            // one deliberately keeps a flat 1.0 px floor: Rms and Peak are LINEAR and do not get the
            // dynamic range the band array just gained, so there is nothing here to quantise away,
            // and a meter stub should stay legible at any column height.
            // At rms == 0 nothing is drawn at all: a 1 px line with a 28 px stroke and a
            // PenLineCap.Round is still a 28 x 8.4 px glowing capsule parked on the baseline,
            // which is the reported oval. The faint track above already reads as "meter at rest",
            // so true digital silence draws the track and nothing else.
            double columnReach = rms > 0.0 ? Math.Max(1.0, rms * plot.Height) : 0.0;
            if (columnReach > 0.0)
            {
                var columns = new List<ColumnSpec>(1)
                {
                    new ColumnSpec(centerX, plot.Bottom, plot.Bottom - columnReach, thickness)
                };

                DrawColumns(dc, ctx, columns, Math.Max(0.8, thickness * 0.30), PenLineCap.Round);
            }

            // Peak-hold tick: a SHORT HORIZONTAL LINE, flat-capped and kept clear of the column top.
            // Three things stop it turning back into the reported glowing oval on the baseline:
            //   1. it is SKIPPED entirely while hold is zero - a zero-length horizontal line with
            //      PenLineCap.Round is a filled near-white DISC of diameter t, which is literally a
            //      white bead parked on the baseline with no column attached;
            //   2. PenLineCap.Flat, so even a short tick stays a line instead of closing into a disc;
            //   3. the glow stack is capped at 3x the tick's own stroke, so the halo can never be
            //      wider than the tick it is blooming.
            double tickStroke = Math.Max(0.8, thickness * 0.10);
            double tickY = plot.Bottom - (hold * plot.Height);
            // Always at least a tick's worth of clear air above the column tip, so the two read as
            // separate marks rather than one fat cap.
            double tickCeiling = plot.Bottom - columnReach - (tickStroke * 3.0) - 2.0;
            tickY = Math.Min(tickY, tickCeiling);

            if (hold > 0.001 && tickY > plot.Y + 1.0)
            {
                double tickHalf = ClampSafe(thickness * 0.62, 3.0, Math.Max(3.0, plot.Width * 0.22));
                var tick = new StreamGeometry();
                using (StreamGeometryContext gctx = tick.Open())
                {
                    gctx.BeginFigure(new Point(centerX - tickHalf, tickY), false, false);
                    gctx.LineTo(new Point(centerX + tickHalf, tickY), true, false);
                }

                if (tick.CanFreeze) tick.Freeze();
                DrawGlowPasses(dc, tick, tickStroke, ctx.Resolved, ctx.GlowIntensity, ctx.AlphaFactor, PenLineCap.Flat, tickStroke * 3.0);

                Brush dotBrush = CreateBrush(WithAlpha(ctx.Resolved.Accent, 0.95 * ctx.AlphaFactor));
                dc.DrawEllipse(dotBrush, null, new Point(centerX + tickHalf, tickY), tickStroke, tickStroke);
            }
        }

        // ------------------------------------------------------------------
        // 12. PeakStars
        // ------------------------------------------------------------------

        private static void DrawPeakStars(DrawingContext dc, FrameContext ctx)
        {
            Rect plot = ctx.Plot;
            if (plot.Width < 4.0 || plot.Height < 4.0)
                return;

            float[]? bands = ctx.Current?.Bands;
            if (bands == null || bands.Length < 3)
                return;

            SoundBarRenderState? state = ctx.State;
            int lifetime = Math.Max(6, ctx.FrameCount / 2);

            // Peak positions go through the same Sensitivity + pink-tilt the bar styles use, so a
            // star always sits where the spectrum the user actually sees is.
            var tilted = new float[bands.Length];
            for (int i = 0; i < bands.Length; i++)
                tilted[i] = ctx.TiltBand(bands[i], i, bands.Length);

            var candidates = new List<(int Index, float Peak)>(32);
            CollectPeakBands(tilted, ctx.BarCount, candidates);

            if (state != null)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    (int index, float peak) = candidates[i];
                    double x = plot.X + (plot.Width * (index + 0.5) / tilted.Length);
                    double y = plot.Bottom - (Math.Clamp(peak, 0f, 1f) * plot.Height);
                    state.TrackStar(index, ctx.Index, peak, (float)x, (float)y);
                }

                state.ExpireStars(ctx.Index, lifetime);
            }

            var live = new List<SoundBarRenderState.Star>(64);
            if (state != null)
            {
                foreach (SoundBarRenderState.Star star in state.EnumerateStars())
                {
                    double age = (ctx.Index - star.SpawnFrame) / (double)lifetime;
                    if (age < 0.0 || age > 1.0)
                        continue;

                    live.Add(star);
                }
            }
            else
            {
                // Stateless fallback: draw the current frame's maxima without any fade.
                for (int i = 0; i < candidates.Count; i++)
                {
                    (int index, float peak) = candidates[i];
                    double x = plot.X + (plot.Width * (index + 0.5) / tilted.Length);
                    double y = plot.Bottom - (Math.Clamp(peak, 0f, 1f) * plot.Height);
                    live.Add(new SoundBarRenderState.Star(index, ctx.Index, peak, (float)x, (float)y));
                }
            }

            double maxStarRadius = Math.Max(2.0, plot.Height * 0.07);
            Brush starBrush = CreateBrush(WithAlpha(ctx.Resolved.Core, ctx.AlphaFactor));

            for (int i = 0; i < live.Count; i++)
            {
                SoundBarRenderState.Star star = live[i];
                double age = state == null ? 0.0 : Math.Clamp((ctx.Index - star.SpawnFrame) / (double)lifetime, 0.0, 1.0);
                double fade = Math.Pow(1.0 - age, 1.6);
                if (fade <= 0.01)
                    continue;

                double x = Math.Clamp(star.X, plot.X, plot.Right);
                double y = Math.Clamp(star.Y, plot.Y, plot.Bottom);
                double radius = maxStarRadius * (0.45 + (0.55 * star.Peak)) * (0.65 + (0.35 * fade));
                var center = new Point(x, y);

                RadialGradientBrush glow = CreateRadialGlowBrush(
                    star.Peak > 0.66f ? ctx.Resolved.Accent : ctx.Resolved.Glow,
                    0.85 * fade * ctx.AlphaFactor,
                    center,
                    Math.Max(2.0, radius * 2.6));

                dc.DrawEllipse(glow, null, center, Math.Max(2.0, radius * 2.6), Math.Max(2.0, radius * 2.6));

                Geometry sparkle = BuildSparkleGeometry(center, radius);
                dc.DrawGeometry(starBrush, null, sparkle);
            }
        }

        // ------------------------------------------------------------------
        // Shared drawing helpers
        // ------------------------------------------------------------------

        private static void DrawColumns(DrawingContext dc, FrameContext ctx, List<ColumnSpec> columns, double glowThickness, PenLineCap cap)
        {
            if (columns.Count == 0)
                return;

            // Gradient body: columns bucketed by quantized height so one frozen brush serves many columns.
            // Every caller already floors the reach above zero (MinimumReach for the four linear bar
            // styles, CenterColumn's own 1.0 px floor, and it draws nothing at all when the
            // sample is exactly zero), so a paintability test here can never bind. That is why
            // there is none: the body and the glow pass below are built from the SAME list and
            // can never disagree about what got painted.
            var buckets = new Dictionary<(int Height, bool Downward), List<ColumnSpec>>(columns.Count);
            for (int i = 0; i < columns.Count; i++)
            {
                ColumnSpec column = columns[i];
                var key = ((int)Math.Round(Math.Min(column.Height, 4096.0)), column.TipY >= column.BaseY);
                if (!buckets.TryGetValue(key, out List<ColumnSpec>? bucket))
                {
                    bucket = new List<ColumnSpec>();
                    buckets.Add(key, bucket);
                }

                bucket.Add(column);
            }

            double roundness = Math.Clamp(ctx.Settings.Roundness, 0.0, 1.0);
            foreach (KeyValuePair<(int Height, bool Downward), List<ColumnSpec>> entry in buckets)
            {
                List<ColumnSpec> bucket = entry.Value;
                if (bucket.Count == 0)
                    continue;

                ColumnSpec first = bucket[0];
                double height = entry.Key.Height;
                double tipY = entry.Key.Item2 ? first.BaseY + height : first.BaseY - height;

                Geometry body = BuildColumnGeometry(bucket, roundness);
                dc.DrawGeometry(ctx.GetColumnBrush(first.BaseY, tipY), null, body);
            }

            // Three-pass additive glow over the SAME line geometry: halo wide -> core last.
            Geometry lines = BuildLineGeometry(columns);
            DrawGlowPasses(dc, lines, glowThickness, ctx.Resolved, ctx.GlowIntensity, ctx.AlphaFactor, cap);
        }

        /// <summary>
        /// Shortest column any of the four linear bar styles will paint, for a bar whose full-scale
        /// reach would be <paramref name="span"/> px. Proportional so it guards ONLY the exactly-zero
        /// case; see <see cref="MinimumReachFraction"/> for why a hard 1.0 px floor is wrong now.
        /// </summary>
        private static double MinimumReach(double span)
            => ClampSafe(span * MinimumReachFraction, 0.0, 1.0);

        /// <summary>
        /// The mandatory neon trick: one geometry, three passes, halo first and core last.
        /// Alphas are multiplied by <paramref name="intensity"/> (always
        /// <see cref="FrameContext.GlowIntensity"/>, which already folds in
        /// <c>settings.GlowIntensity</c>, <c>palette.GlowStrength</c> and <c>settings.Opacity</c>) and by
        /// <paramref name="alphaScale"/>, the per-shape fade of a style that draws several shapes at
        /// different strengths.
        /// <para>
        /// <paramref name="maxHaloThickness"/> bounds the widest pass. The classic recipe asks for
        /// <c>t * 5.0</c>, which is unbounded with respect to the shape it is blooming: on an element
        /// much smaller than its own stroke budget the additive halo stops being a highlight and
        /// becomes the thing the eye sees - the reported white teardrop, where the ring measured
        /// 106 x 86 px with the glow off and 106 x 230 px with it on. So the halo and mid passes are
        /// scaled down together, while the core pass never is - it IS the shape.
        /// <para>
        /// The clamp does NOT preserve the recipe's proportions, and previously it did not even
        /// preserve a GLOW: both scaled passes were floored at <c>t</c>, so whenever
        /// <c>maxHaloThickness &lt; t</c> all three collapsed onto <c>t</c> and the shape was stroked
        /// three times with the same pen. That is not a corner case - traced at the DEFAULT job for
        /// DualRings, <c>t = 8.25</c> against <c>maxHalo = 3.67</c>, the default configuration drew no
        /// glow at all. <see cref="GlowMinWideRatio"/> / <see cref="GlowMinMidRatio"/> are the floors
        /// that keep the stack strictly ordered (core : mid : wide = 1 : 1.35 : 1.9 at worst).
        /// <para>
        /// The default is <see cref="double.PositiveInfinity"/>, so the clamp never binds and
        /// <c>t * 5.0</c> / <c>t * 2.4</c> / <c>t * 1.0</c> come out bit-identical for every
        /// pre-existing caller. Bar styles pass nothing and are unaffected by construction.
        /// </para>
        /// <para>
        /// <paramref name="alphaFactor"/> is <see cref="FrameContext.AlphaFactor"/> (the Opacity
        /// slider) and is honoured on EVERY path, including the flat-core path below. It cannot be
        /// recovered from <paramref name="intensity"/> there, because the early-out is reached exactly
        /// when <paramref name="intensity"/> is 0: without it a style whose only output is these
        /// passes painted at alpha 255 at <c>Opacity = 0</c>, which inverted the slider for that one
        /// style (SpokeLines) while every other style honoured it.
        /// </para>
        /// </summary>
        private static void DrawGlowPasses(
            DrawingContext dc,
            Geometry geometry,
            double thickness,
            ResolvedPalette resolved,
            double intensity,
            double alphaFactor,
            PenLineCap cap,
            double maxHaloThickness = double.PositiveInfinity,
            bool coreAlways = false,
            double alphaScale = 1.0,
            Color? coreColor = null)
        {
            // Opacity is the master fold. Nothing below - not the halo, not the mid, not the flat
            // core - is allowed to draw at all when the user has turned it to zero.
            if (alphaFactor <= 0.002)
                return;

            Color core = coreColor ?? resolved.Core;
            double fade = Math.Clamp(alphaScale, 0.0, 1.0);

            // A style whose ONLY output is these three passes has nothing left when glow is off:
            // SpokeLines measured 150/150 fully transparent frames at GlowIntensity 0 because the
            // early-out below skipped the halo, the mid AND the core. Those styles pass
            // coreAlways = true so the core still paints - flat colour, no bloom, which is the
            // correct meaning of "glow off". `alphaFactor` is applied here explicitly; before it
            // was passed this branch used `resolved.Core` verbatim, and since TryParseHex always
            // returns an OPAQUE colour, Opacity = 0 drew at alpha 255.
            if (intensity <= 0.002)
            {
                if (coreAlways)
                    dc.DrawGeometry(null, CreatePen(CreateBrush(WithAlpha(core, alphaFactor * fade)), Math.Max(0.25, thickness), cap), geometry);
                return;
            }

            double t = Math.Max(0.25, thickness);
            double halo = t * GlowWideRatio;
            double scale = halo > maxHaloThickness ? (maxHaloThickness / halo) : 1.0;

            // Both scaled passes are clamped into [their own minimum ratio, ceiling], and mid is
            // additionally capped at wide, so the three strokes are always strictly ordered. Without
            // the ratio floors the two Math.Max(t, ...) terms below both returned t and the "glow"
            // was three identical strokes.
            double ceiling = Math.Max(t * GlowMinWideRatio, maxHaloThickness);
            double wideThickness = Math.Max(t * GlowMinWideRatio, Math.Min(halo * scale, ceiling));
            double midThickness = Math.Min(wideThickness, Math.Max(t * GlowMinMidRatio, Math.Min((t * GlowMidRatio) * scale, ceiling)));

            Pen haloPen = CreatePen(CreateBrush(WithAlpha(resolved.Glow, GlowWideAlpha * intensity * fade)), wideThickness, cap);
            Pen midPen = CreatePen(CreateBrush(WithAlpha(resolved.Glow, GlowMidAlpha * intensity * fade)), midThickness, cap);
            Pen corePen = CreatePen(CreateBrush(WithAlpha(core, GlowCoreAlpha * intensity * fade)), t, cap);

            dc.DrawGeometry(null, haloPen, geometry);
            dc.DrawGeometry(null, midPen, geometry);
            dc.DrawGeometry(null, corePen, geometry);
        }

        private static void DrawRadialRays(
            DrawingContext dc,
            FrameContext ctx,
            List<RaySpec> rays,
            double thickness,
            PenLineCap cap,
            double maxHaloThickness,
            bool coreAlways = false)
        {
            if (rays.Count == 0)
                return;

            Geometry geometry = BuildRayGeometry(rays);
            DrawGlowPasses(dc, geometry, thickness, ctx.Resolved, ctx.GlowIntensity, ctx.AlphaFactor, cap, maxHaloThickness, coreAlways);
        }

        /// <summary>
        /// Widest allowed glow pass for the radial styles, expressed as a fraction of the element's
        /// own OUTER radius - the same bounding the geometry itself is measured against.
        /// <para>
        /// The floor of 2.0 px matters: <c>outer</c> can be a few pixels on a thumbnail-sized plot,
        /// where <c>outer * 0.04</c> would round the halo away to nothing and the style would lose
        /// its glow entirely. 0.04 of a 137 px ring is 5.5 px, which is a highlight on a 274 px ring
        /// rather than a smear across it.
        /// </para>
        /// </summary>
        private static double RadialHaloExtent(double outer) => Math.Max(2.0, outer * RadialHaloFraction);

        /// <summary>
        /// Turns raw band levels into radial bar lengths for RadialRing, DualRings and SpokeLines.
        /// <para>
        /// Three stages, and all three are load-bearing:
        /// <list type="number">
        /// <item><b>Percentile gain.</b> The reference is the <see cref="RadialPercentile"/> band, not
        /// the peak. Peak-referenced gain hands the whole radius to whichever single band is loudest
        /// right now; every other bar is pushed onto the hub radius and the wedges merge into one
        /// filled disc - which is exactly the measured "solid blob with one spike" and "single
        /// vertical sliver". (The flat caps were not the reason those rays vanished: a peak-referenced
        /// gain still left short, non-zero lengths, which <c>PenLineCap.Flat</c> paints fine. They
        /// draw nothing at exactly zero length, which is what <see cref="MinRadialReach"/> now
        /// guards.)</item>
        /// <item><b>Noise floor.</b> At or below <see cref="RadialNoiseFloor"/> the band is drawn at
        /// the hub, i.e. not at all. This runs BEFORE the gamma because a gamma below 1 is itself a
        /// floor-lifter: <c>0.0039 ^ 0.45 = 0.083</c>, so a band sitting on one 8-bit code already
        /// drew 8-16 % of the annulus and the response never reached zero except at exact zero.</item>
        /// <item><b>Gamma compression.</b> The audible remainder is rescaled to 0..1 and raised to
        /// <see cref="RadialGamma"/>, so a large dynamic range still resolves as a ring rather than
        /// as a dot with a spike.</item>
        /// </list>
        /// <para>
        /// The gain stays bounded by <see cref="RadialAutoGainLimit"/> and is skipped below
        /// <see cref="RadialAutoGainFloor"/> so silence is never amplified into a solid disc. It is
        /// also one-sided (&gt;= 1.0): a frame that already reaches
        /// <see cref="RadialPercentileTarget"/> is left exactly as it was, so loud passages are not
        /// pushed further and cannot clip.
        /// <para>
        /// <see cref="RadialPercentileTarget"/> is a PRE-gamma level, so it must be read before the
        /// gamma, not after: the old 0.62 came out of the curve as 0.806 and flattened the whole top
        /// decile of the spectrum into the top 19 % of the annulus. At 0.92 the p90 band lands at
        /// ~0.963 post-gamma and the top decile spans [0.963, 1.000].
        /// <para>
        /// Returns a fresh array; the input is left untouched.
        /// </para>
        /// </summary>
        private static double[] ResolveRadialLevels(double[] rawLevels)
        {
            int count = rawLevels.Length;
            var levels = new double[count];
            if (count <= 0)
                return levels;

            double[] sorted = (double[])rawLevels.Clone();
            Array.Sort(sorted);
            int referenceIndex = Math.Clamp((int)(count * RadialPercentile), 0, count - 1);
            double reference = sorted[referenceIndex];

            double gain = reference <= RadialAutoGainFloor
                ? 1.0
                : Math.Clamp(RadialPercentileTarget / reference, 1.0, RadialAutoGainLimit);

            double span = 1.0 - RadialNoiseFloor;
            for (int i = 0; i < count; i++)
            {
                double gained = Math.Clamp(rawLevels[i] * gain, 0.0, 1.0);
                if (gained <= RadialNoiseFloor)
                {
                    levels[i] = 0.0;
                    continue;
                }

                levels[i] = Math.Pow((gained - RadialNoiseFloor) / span, RadialGamma);
            }

            return levels;
        }

        /// <summary>
        /// Maps a linear RMS / peak sample onto a 0..1 CenterColumn height.
        /// <para>
        /// The band array is decibel-mapped upstream by the analyser's adaptive curve (absolute
        /// floor blended with a relative-to-frame-peak floor, plus a soft knee), but
        /// <see cref="AudioFrameData.Rms"/> and <see cref="AudioFrameData.Peak"/> are LINEAR, and
        /// real music sits at Rms ~= 0.10-0.20. Drawn linearly that is a ~20 px stub in a 280 px
        /// column whose three-pass glow halo (42 px, from a stroke of <c>thickness * 0.30</c> on a
        /// 28 px column) is wider than the column itself - which is exactly the reported
        /// "nearly invisible + a glowing oval on the baseline".
        /// <para>
        /// So the same decibel anchoring the analyser uses is re-applied for the levels a VU-style meter
        /// actually lives at:
        /// <code>
        ///   dB     = 20 * log10(sample)                                   (0 -> -inf)
        ///   height = CenterColumnMinFraction
        ///          + (1 - CenterColumnMinFraction) * (dB - FloorDb) / (CeilDb - FloorDb)
        /// </code>
        /// with FloorDb = -30 dBFS (x0.0316 linear) and CeilDb = -3 dBFS (x0.708 linear).
        /// Worked through: a quiet passage at Rms 0.03 (-30.5 dBFS) lands on 10 %, a loud one at
        /// Rms 0.5 (-6.0 dBFS) lands on 90 %, and typical music at Rms 0.13 (-17.7 dBFS) lands on
        /// ~51 %. Digital silence maps to 0 - only the faint track is drawn - while anything above
        /// zero but below the window floor keeps the 10 % minimum so the meter never vanishes.
        /// </summary>
        private static double CenterColumnLevel(double linear)
        {
            if (linear <= 0.0)
                return 0.0;

            double floorLinear = Math.Pow(10.0, CenterColumnFloorDb / 20.0);
            if (linear <= floorLinear)
                return CenterColumnMinFraction;

            double db = 20.0 * Math.Log10(linear);
            double t = (db - CenterColumnFloorDb) / (CenterColumnCeilDb - CenterColumnFloorDb);
            return CenterColumnMinFraction + ((1.0 - CenterColumnMinFraction) * Math.Clamp(t, 0.0, 1.0));
        }

        /// <summary>
        /// Picks the spectral peaks PeakStars draws, as (band index, level) pairs.
        /// <para>
        /// The original test was <c>bands[i] &gt; bands[i-1] &amp;&amp; bands[i] &gt; bands[i+1]</c>
        /// plus a 0.34 absolute floor. Real spectra are monotonically decreasing across most of
        /// their length, so the only true local maxima sat in the lowest bands and ~8 stars clustered
        /// on the left third. Bucketing instead makes distribution structural rather than emergent:
        /// the array is cut into <c>BarCount / 8</c> equal slices (the band layout is log-spaced, so
        /// a slice IS a frequency range) and each slice contributes at most one star, taken at that
        /// slice's own maximum. A falling spectrum therefore yields one star per slice, down-slope,
        /// and the width of the plot is covered.
        /// <para>
        /// A slice only draws when its maximum (a) clears <see cref="PeakStarFloor"/>, and (b) beats
        /// the mean of itself and its two neighbouring slices by <see cref="PeakStarContrastRatio"/>.
        /// (a) keeps silence dead; (b) keeps a dead-flat noise floor from sprouting stars across the
        /// whole width. Ties resolve to the LOWEST bin in the slice, which is what makes a plateau
        /// (a flat top - a legitimate peak) land on a stable bin instead of jittering between equal
        /// neighbours frame to frame.
        /// </para>
        /// </summary>
        private static void CollectPeakBands(float[] bands, int barCount, List<(int Index, float Peak)> results)
        {
            results.Clear();

            int length = bands.Length;
            if (length < 3 || barCount <= 0)
                return;

            // Roughly BarCount / 8 live stars, bounded so a 256-bar request does not carpet the plot.
            int slices = Math.Clamp(Math.Min(barCount / 8, length), 4, 24);
            if (slices <= 0)
                return;

            var slicePeak = new float[slices];
            var sliceMean = new double[slices];

            for (int s = 0; s < slices; s++)
            {
                int lo = SliceStart(length, slices, s);
                int hi = SliceEnd(length, slices, s);
                if (hi <= lo)
                    continue;

                float peak = 0f;
                double sum = 0.0;
                for (int i = lo; i < hi; i++)
                {
                    float v = bands[i];
                    sum += v;
                    if (v > peak)
                        peak = v;
                }

                slicePeak[s] = peak;
                sliceMean[s] = sum / (hi - lo);
            }

            for (int s = 0; s < slices; s++)
            {
                float peak = slicePeak[s];
                if (peak < PeakStarFloor)
                    continue;

                int from = Math.Max(0, s - 1);
                int to = Math.Min(slices - 1, s + 1);
                double local = 0.0;
                for (int k = from; k <= to; k++)
                    local += sliceMean[k];
                local /= (to - from + 1);

                if (local > 0.0 && peak < local * PeakStarContrastRatio)
                    continue;

                int lo = SliceStart(length, slices, s);
                int hi = SliceEnd(length, slices, s);
                if (hi <= lo)
                    continue;

                int best = lo;
                for (int i = lo; i < hi; i++)
                {
                    if (bands[i] <= bands[best])
                        continue;

                    best = i;
                }

                results.Add((best, bands[best]));
            }
        }

        private static int SliceStart(int length, int slices, int index)
            => Math.Clamp((int)((long)index * length / slices), 0, Math.Max(0, length - 1));

        private static int SliceEnd(int length, int slices, int index)
            => Math.Clamp((int)((long)(index + 1) * length / slices), 1, length);

        /// <summary>
        /// Per-band pink-noise (1/f) compensation. See <see cref="ApplyPinkTilt"/>.
        /// </summary>
        private static float[] GetPinkTilt(int bandCount)
            => PinkTiltCache.GetOrAdd(Math.Max(1, bandCount), static count => BuildPinkTilt(count));

        private static float[] BuildPinkTilt(int bandCount)
        {
            int count = Math.Max(1, bandCount);
            var tilt = new float[count];
            double spanOctaves = Math.Log2(PinkBandHighHz / PinkBandLowHz);

            for (int i = 0; i < count; i++)
            {
                double octaves = count <= 1 ? 0.0 : (spanOctaves * i / (count - 1));
                double db = Math.Clamp(PinkTiltDbPerOctave * octaves, 0.0, PinkTiltMaxDb);
                tilt[i] = (float)Math.Pow(10.0, db / 20.0);
            }

            return tilt;
        }

        /// <summary>
        /// Applies the per-band pink-noise (1/f) compensation to one already-sensitivity-scaled value.
        /// <para>
        /// CURRENTLY THE IDENTITY. <see cref="PinkTiltDbPerOctave"/> is 0.0, so <c>gainDb(i)</c> is 0
        /// for every band, the gain table is all 1.0, and the only thing this still does is refuse to
        /// touch values at or below <see cref="PinkSilenceFloor"/>.
        /// <para>
        /// It was not always so. The rationale for the ramp, for the record and so it is not
        /// mistaken for a live premise again: <c>AudioSpectrumAnalyzer</c> lays its bands out
        /// log-spaced from 40 Hz to Nyquist (11.025 kHz at its fixed 22.05 kHz rate) and decibel-maps
        /// each against an adaptive floor; musical spectra fall at roughly 3 dB per octave, so the top
        /// octaves - which are also the WIDEST log buckets, and so the widest slice of the plot - were
        /// the ones that needed lifting, and while the analyser "kept the noise floor flat" they read
        /// as a dead flat minimum-height line across the right third of NeonBars / DenseColumns /
        /// MirrorSpectrum / PillBars. Neither Sensitivity nor Smoothing could rescue that, because
        /// both act on level, not slope.
        /// <para>
        /// The premise has since been fixed in the analyser, twice, so the ramp is neutral. What it
        /// actually computed is worth recording, because it was not a tilt:
        /// <code>
        ///   octaves(i) = log2(PinkBandHighHz / PinkBandLowHz) * i / (N - 1)   // 0 .. 8.11 octaves
        ///   gainDb(i)  = clamp(PinkTiltDbPerOctave * octaves(i), 0, PinkTiltMaxDb)
        ///   gain(i)    = 10 ^ (gainDb(i) / 20)
        ///   out(i)     = in(i) * gain(i)                                    // clamped to [0, 1]
        /// </code>
        /// At 3 dB/octave the gain is exactly 1.0 at band 0 ONLY - not "over the low 40 % of the
        /// spectrum", which is what the old note here claimed - it reaches x2.818 at band 24 and pins
        /// 40 of 64 bands (62.5 %) at the +9 dB ceiling (which first binds at band 24, not 23). A
        /// near-step function that bought three defects: a x2.9 discontinuity at
        /// <see cref="PinkSilenceFloor"/> that shimmered as HF bands crossed it frame to frame; a
        /// lowered effective noise gate of 0.0039 / 2.818 = 0.0014 that made sub-pixel dither
        /// visible; and, because it is applied AFTER <c>Sensitivity</c>, a 2.8:1 asymmetry in what
        /// that one slider means (Sensitivity 3 gave band 0 a x3 ceiling and band 60 a x8.45 one).
        /// </summary>
        private static float ApplyPinkTilt(float value, int index, int bandCount)
        {
            // Identity while PinkTiltDbPerOctave is 0.0: the table is all 1.0 and the clamp below is a
            // no-op on an input that is already in [0, 1]. Kept (rather than deleted) so the ramp is
            // one constant away if the analyser ever regresses to a flat HF floor.
            if (value <= PinkSilenceFloor)
                return value;

            float[] tilt = GetPinkTilt(bandCount);
            float gained = value * tilt[Math.Clamp(index, 0, tilt.Length - 1)];
            return gained >= 1f ? 1f : gained;
        }

        /// <summary>
        /// Turns resolved radial levels into the per-bar angle / tip radius / ray triplet that
        /// RadialRing, DualRings and SpokeLines all draw. Extracted so the three cannot drift apart:
        /// they differed only in hub, outer radius, rotation and whether wedges are drawn on top.
        /// <para>
        /// The ray minimum reach is <see cref="MinRadialReach"/>, the same floor
        /// <see cref="DrawRadialWedges"/> applies to each wedge. Without it a band at exactly zero -
        /// which the new DSP makes common rather than exceptional - yields a zero-length ray, and a
        /// zero-length ray with <c>PenLineCap.Flat</c> (SpokeLines) paints nothing at all, so rays
        /// dropped out of the middle of the ring instead of reading as stubs at the hub.
        /// <para>
        /// <paramref name="rays"/> is appended to, never cleared: DualRings reuses one scratch list
        /// across its rings and clears it itself. <paramref name="tipRadii"/> and
        /// <paramref name="angles"/> are optional - a style that draws no wedges (SpokeLines) passes
        /// null for both and gets the rays only.
        /// </summary>
        private static void FillRadialBarSet(
            double[] levels,
            Point center,
            double inner,
            double outer,
            double rotation,
            double span,
            double[]? tipRadii,
            double[]? angles,
            List<RaySpec> rays)
        {
            int count = levels.Length;
            if (tipRadii != null && angles != null)
                count = Math.Min(count, Math.Min(tipRadii.Length, angles.Length));

            for (int i = 0; i < count; i++)
            {
                double angle = rotation + (span * i);
                double tip = Math.Max(inner + MinRadialReach, inner + (levels[i] * (outer - inner)));

                if (tipRadii != null)
                    tipRadii[i] = tip;

                if (angles != null)
                    angles[i] = angle;

                Point from = PointOnCircle(center, inner, angle);
                Point to = PointOnCircle(center, tip, angle);
                rays.Add(new RaySpec(from.X, from.Y, to.X, to.Y));
            }
        }

        /// <summary>
        /// Angular colour. WPF has no SweepGradientBrush, so a ring is rasterised as a set of small wedge
        /// sectors (2 degrees each), every wedge filled with its own frozen solid brush sampled from the
        /// palette ramp. The result is baked into a frozen <see cref="DrawingGroup"/> and issued as one call.
        /// </summary>
        private static void DrawRadialWedges(
            DrawingContext dc,
            FrameContext ctx,
            Point center,
            double inner,
            double[] tipRadii,
            double[] angles,
            double spanDegrees)
        {
            var group = new DrawingGroup();
            int count = Math.Min(tipRadii.Length, angles.Length);
            if (count <= 0)
                return;

            // Angular gap between adjacent wedges, as a fraction of the slot. Tightened from
            // span*0.20 / 2.4 deg to span*0.14 / 1.8 deg (60 % -> 72 % coverage) so a ring reads as
            // a ring rather than as a dashed arc. Only RadialRing and DualRings use wedges.
            double gap = Math.Min(spanDegrees * 0.14, 1.8);
            double halfSpan = (spanDegrees * 0.5) - gap;

            for (int i = 0; i < count; i++)
            {
                double tip = Math.Max(inner + MinRadialReach, tipRadii[i]);
                double a0 = angles[i] - halfSpan;
                double a1 = angles[i] + halfSpan;
                if (a1 <= a0)
                    continue;

                int sectors = Math.Max(1, (int)Math.Ceiling((a1 - a0) / AngularSectorDegrees));
                for (int s = 0; s < sectors; s++)
                {
                    double b0 = a0 + ((a1 - a0) * s / sectors);
                    double b1 = a0 + ((a1 - a0) * (s + 1) / sectors);
                    Geometry wedge = BuildWedge(center, inner, tip, b0, b1);
                    group.Children.Add(new GeometryDrawing(ctx.Resolved.GetAngularBrush(b0, ctx.Alpha), null, wedge));
                }
            }

            if (group.CanFreeze)
                group.Freeze();

            dc.DrawDrawing(group);
        }

        private static Geometry BuildColumnGeometry(List<ColumnSpec> columns, double roundness)
        {
            var geometry = new StreamGeometry();
            double corner = Math.Clamp(roundness, 0.0, 1.0);

            using (StreamGeometryContext ctx = geometry.Open())
            {
                for (int i = 0; i < columns.Count; i++)
                {
                    ColumnSpec column = columns[i];
                    double halfWidth = Math.Max(0.30, column.Width * 0.5);
                    double top = Math.Min(column.BaseY, column.TipY);
                    double bottom = Math.Max(column.BaseY, column.TipY);
                    double radius = Math.Min(halfWidth * corner, (bottom - top) * 0.5);

                    if (radius <= 0.6)
                    {
                        ctx.BeginFigure(new Point(column.CenterX - halfWidth, top), true, true);
                        ctx.LineTo(new Point(column.CenterX + halfWidth, top), true, true);
                        ctx.LineTo(new Point(column.CenterX + halfWidth, bottom), true, true);
                        ctx.LineTo(new Point(column.CenterX - halfWidth, bottom), true, true);
                        continue;
                    }

                    Size arcSize = new Size(radius * 2.0, radius * 2.0);
                    ctx.BeginFigure(new Point(column.CenterX - radius, top), true, true);
                    ctx.LineTo(new Point(column.CenterX - radius, bottom - radius), true, true);
                    // Both caps are traversed CounterClockwise - the OPPOSITE sweep direction from
                    // AddEllipseFigure - because the outline is walked in the opposite rotational
                    // direction: top-left -> down the left edge -> left-to-right across the BOTTOM cap
                    // -> up the right edge -> right-to-left across the TOP cap.
                    // In y-down screen space, CounterClockwise takes the bottom cap left->down->right
                    // (bulging DOWN/outward) and the top cap right->up->left (bulging UP/outward), so
                    // the outline is convex and the painted extent is exactly top..bottom, since the
                    // cap arcs at top+radius and bottom-radius reach out to top and bottom again.
                    // Callers therefore pass the TOTAL height INCLUDING the caps as top/bottom; no
                    // caller needs a radius added back. With Clockwise both arcs bulge INTO the body,
                    // producing concave scoops - and for a bar shorter than 2*radius the two inward
                    // arcs cross, degenerating the pill into a lens/bowtie.
                    ctx.ArcTo(new Point(column.CenterX + radius, bottom - radius), arcSize, 0.0, false, SweepDirection.Counterclockwise, true, true);
                    ctx.LineTo(new Point(column.CenterX + radius, top + radius), true, true);
                    ctx.ArcTo(new Point(column.CenterX - radius, top + radius), arcSize, 0.0, false, SweepDirection.Counterclockwise, true, true);
                }
            }

            if (geometry.CanFreeze)
                geometry.Freeze();

            return geometry;
        }

        private static Geometry BuildLineGeometry(List<ColumnSpec> segments)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                for (int i = 0; i < segments.Count; i++)
                {
                    ColumnSpec segment = segments[i];
                    ctx.BeginFigure(new Point(segment.CenterX, segment.BaseY), false, false);
                    ctx.LineTo(new Point(segment.CenterX, segment.TipY), true, false);
                }
            }

            if (geometry.CanFreeze)
                geometry.Freeze();

            return geometry;
        }

        /// <summary>
        /// Builds true line segments between arbitrary endpoints - the geometry the radial styles need.
        /// One <see cref="StreamGeometry"/> for the whole ray set, so the three glow passes still cost
        /// three draw calls regardless of how many rays there are.
        /// </summary>
        private static Geometry BuildRayGeometry(List<RaySpec> segments)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                for (int i = 0; i < segments.Count; i++)
                {
                    RaySpec segment = segments[i];
                    if (!IsFinite(segment.X0) || !IsFinite(segment.Y0) ||
                        !IsFinite(segment.X1) || !IsFinite(segment.Y1))
                        continue;

                    ctx.BeginFigure(new Point(segment.X0, segment.Y0), false, false);
                    ctx.LineTo(new Point(segment.X1, segment.Y1), true, false);
                }
            }

            if (geometry.CanFreeze)
                geometry.Freeze();

            return geometry;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static Geometry BuildWedge(Point center, double r0, double r1, double angleStart, double angleEnd)
        {
            var geometry = new StreamGeometry();
            double outerR = Math.Max(r1, r0 + 0.5);

            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(PointOnCircle(center, outerR, angleStart), true, true);
                ctx.LineTo(PointOnCircle(center, r0, angleStart), true, true);
                // Both arc endpoints lie on the circle of radius r0 centred at `center`, so the arc's
                // ellipse radii must be r0 (Size = diameter). Passing the band thickness instead
                // displaces each arc's centre from `center` by |r0 - band| and flips the scalloping
                // depending on bar height - sub-0.05px at the default 2-degree sectors, but unbounded
                // as the sector widens.
                ctx.ArcTo(PointOnCircle(center, r0, angleEnd), new Size(r0 * 2.0, r0 * 2.0), 0.0, false, SweepDirection.Clockwise, true, true);
                ctx.LineTo(PointOnCircle(center, outerR, angleEnd), true, true);
            }

            if (geometry.CanFreeze)
                geometry.Freeze();

            return geometry;
        }

        private static Geometry BuildSparkleGeometry(Point center, double radius)
        {
            var geometry = new StreamGeometry();
            double r = Math.Max(0.8, radius);
            double inner = r * 0.22;

            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(center.X, center.Y - r), true, true);
                ctx.LineTo(new Point(center.X + inner, center.Y - inner), true, true);
                ctx.LineTo(new Point(center.X + r, center.Y), true, true);
                ctx.LineTo(new Point(center.X + inner, center.Y + inner), true, true);
                ctx.LineTo(new Point(center.X, center.Y + r), true, true);
                ctx.LineTo(new Point(center.X - inner, center.Y + inner), true, true);
                ctx.LineTo(new Point(center.X - r, center.Y), true, true);
                ctx.LineTo(new Point(center.X - inner, center.Y - inner), true, true);
            }

            if (geometry.CanFreeze)
                geometry.Freeze();

            return geometry;
        }

        private static void AddEllipseFigure(StreamGeometryContext ctx, Point center, double radiusX, double radiusY)
        {
            double rx = Math.Max(0.25, radiusX);
            double ry = Math.Max(0.25, radiusY);
            Size size = new Size(rx * 2.0, ry * 2.0);

            ctx.BeginFigure(new Point(center.X, center.Y - ry), true, true);
            ctx.ArcTo(new Point(center.X + rx, center.Y), size, 0.0, false, SweepDirection.Clockwise, true, true);
            ctx.ArcTo(new Point(center.X, center.Y + ry), size, 0.0, false, SweepDirection.Clockwise, true, true);
            ctx.ArcTo(new Point(center.X - rx, center.Y), size, 0.0, false, SweepDirection.Clockwise, true, true);
            ctx.ArcTo(new Point(center.X, center.Y - ry), size, 0.0, false, SweepDirection.Clockwise, true, true);
        }

        private static SolidColorBrush CreateBrush(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            if (brush.CanFreeze)
                brush.Freeze();
            return brush;
        }

        private static Pen CreatePen(Brush brush, double thickness, PenLineCap cap = PenLineCap.Round)
        {
            Pen pen = new Pen(brush, Math.Max(0.25, thickness))
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = cap,
                EndLineCap = cap
            };

            if (pen.CanFreeze)
                pen.Freeze();

            return pen;
        }

        /// <summary>
        /// Rule 7 glow brush: absolute mapping, Center == GradientOrigin and RadiusX == RadiusY, with
        /// FIVE discrete alpha stops rather than a continuous ramp.
        /// <para>
        /// The discrete stops are a dithering measure, NOT an anti-banding measure - do not read this
        /// as "fewer stops prevent banding". A continuous ramp across a large radius quantises into
        /// visible concentric rings on 8-bit alpha; each stop's plateau gives the gradientiser a flat
        /// run to land on. Fewer, wider-separated stops make the remaining transitions MORE visible,
        /// not less, which is why the alpha differences between neighbours are deliberately
        /// proportional (0.85 / 0.42 / 0.18 / 0.06) rather than a small uniform step.
        /// </para>
        /// </summary>
        private static RadialGradientBrush CreateRadialGlowBrush(Color color, double alpha, Point center, double radius)
        {
            double safeAlpha = Math.Clamp(alpha, 0.0, 1.0);
            double safeRadius = Math.Max(0.5, radius);

            var brush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = center,
                GradientOrigin = center,
                RadiusX = safeRadius,
                RadiusY = safeRadius
            };

            brush.GradientStops.Add(new GradientStop(WithAlpha(color, safeAlpha * 0.85), 0.00));
            brush.GradientStops.Add(new GradientStop(WithAlpha(color, safeAlpha * 0.42), 0.28));
            brush.GradientStops.Add(new GradientStop(WithAlpha(color, safeAlpha * 0.18), 0.55));
            brush.GradientStops.Add(new GradientStop(WithAlpha(color, safeAlpha * 0.06), 0.78));
            brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0.00), 1.00));

            if (brush.CanFreeze)
                brush.Freeze();

            return brush;
        }

        private static Color WithAlpha(Color color, double alphaFactor)
        {
            byte alpha = (byte)Math.Clamp(Math.Round(alphaFactor * 255.0), 0.0, 255.0);
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        /// <summary>Math.Clamp that tolerates an inverted range instead of throwing on degenerate plots.</summary>
        private static double ClampSafe(double value, double min, double max)
        {
            if (max < min)
            {
                double swap = min;
                min = max;
                max = swap;
            }

            return Math.Clamp(value, min, max);
        }

        private static Point PointOnCircle(Point center, double radius, double angleDegrees)
        {
            double radians = angleDegrees * Math.PI / 180.0;
            return new Point(
                center.X + (Math.Cos(radians) * radius),
                center.Y + (Math.Sin(radians) * radius));
        }

        private static double NormalizeAngle(double degrees)
        {
            double a = degrees % 360.0;
            if (a < 0.0)
                a += 360.0;
            return a;
        }

        private static Point plotCenter(Rect plot) => new Point(plot.X + (plot.Width * 0.5), plot.Y + (plot.Height * 0.5));

        /// <summary>
        /// Radius of the circle the radial styles inscribe in the plot box: <c>min(w, h) / 2</c> less a
        /// 2 % inset so the stroke and its glow never touch the plot edge. Shared by RadialRing,
        /// DualRings, RippleRings and SpokeLines, so it is deliberately left alone - a ring that
        /// still looks small is a bar-LENGTH problem (<see cref="ResolveRadialLevels"/>), not a radius
        /// problem.
        /// </summary>
        private static double MaxRadius(Rect plot) => Math.Max(0.0, Math.Min(plot.Width, plot.Height) * 0.5 * 0.98);

        /// <summary>
        /// Places the plot rectangle using the settings' normalized anchors, then clamps it fully
        /// inside the canvas so nothing can be cropped by the PNG sequence.
        /// </summary>
        private static Rect ResolvePlotRect(SoundBarSettings settings, int width, int height)
        {
            double w = Math.Max(2.0, width);
            double h = Math.Max(2.0, height);

            double widthPercent = Math.Clamp(settings.WidthPercent, 0.02, 1.0);
            double heightPercent = Math.Clamp(settings.HeightPercent, 0.02, 1.0);

            double plotW = widthPercent * w;
            double plotH = heightPercent * h;

            // Styles whose figure is a circle (or a single centred column) draw inside
            // min(plot.Width, plot.Height), so the plot is squared first - but IN PIXELS, not in
            // percentages.
            //
            // The percentage version was measurably wrong in two directions. Equalising the two
            // PERCENTAGES leaves the rect at 499 x 281 on a 16:9 canvas, so min(w,h) - and hence
            // the radius - was bit-for-bit identical to not squaring at all: the fix bought
            // nothing while adding a new failure. And on a portrait canvas, where WIDTH is the
            // smaller edge, it actively shrank the figure: 1080 x 1920 lost 44 % of its radius
            // (244.5 -> 137.6 px), on the orientation this product is mostly used in.
            //
            // Squaring in pixels has neither defect. min(plotW, plotH) is the same quantity the
            // circle styles already used, so the radius is unchanged; what it buys is that the
            // square is genuinely square, so the anchor clamp can place the figure's CENTRE at
            // the requested X instead of shoving it inward by half the plot's width.
            if (UsesSquarePlot(settings.Style))
            {
                double side = Math.Min(plotW, plotH);
                plotW = side;
                plotH = side;
            }

            double anchorX = Math.Clamp(settings.XPercent, 0.0, 1.0) * w;
            double anchorY = Math.Clamp(settings.YPercent, 0.0, 1.0) * h;

            double left = Math.Clamp(anchorX - (plotW * 0.5), 0.0, Math.Max(0.0, w - plotW));
            double top = Math.Clamp(anchorY - plotH, 0.0, Math.Max(0.0, h - plotH));

            return new Rect(left, top, plotW, plotH);
        }

        /// <summary>
        /// Styles that draw a centred, aspect-symmetric figure and therefore ignore the plot's
        /// aspect ratio: every one of them sizes itself from min(plot.Width, plot.Height). The
        /// plot rect is squared for these so the anchor clamp can actually reach the edges.
        ///
        /// NOTE for whoever adds a style here: membership is a claim about the GEOMETRY, and it
        /// is enforced by measurement. PeakStars was wrongly listed - its star field really does
        /// span plot.Width (measured ink width / plot width = 1.04 at four scales), so squaring
        /// collapsed it from 1190 px to 499 px on the default job. If a new style draws anything
        /// that spans the width, it does not belong on this list.
        ///
        /// Consequence to document rather than fight: for these styles the WIDTH control cannot
        /// resize the figure - the circle is limited by the smaller edge, which on a landscape
        /// canvas is always the height. That is why the "KICH THUOC" slider scales BOTH.
        /// </summary>
        internal static bool UsesSquarePlot(SoundBarStyleId style)
            => style is SoundBarStyleId.RadialRing
                or SoundBarStyleId.DualRings
                or SoundBarStyleId.RippleRings
                or SoundBarStyleId.SpokeLines
                or SoundBarStyleId.CenterColumn
                or SoundBarStyleId.AudioRing;

        private static AudioFrameData? FrameAt(AudioFrameData[] frames, int index)
        {
            if (frames == null || frames.Length == 0)
                return null;

            int i = index % frames.Length;
            if (i < 0)
                i += frames.Length;

            return frames[i];
        }

        private static ResolvedPalette ResolvePalette(ColorPreset preset)
            => PaletteCache.GetOrAdd(preset?.Id ?? string.Empty, _ => ResolvedPalette.Build(preset));

        private static ResolvedPalette BuildResolved(ColorPreset? preset)
        {
            ColorPreset safe = preset ?? ColorPresets.Default;

            var offsets = new List<double>();
            var colors = new List<Color>();
            IReadOnlyList<GradientStopSpec>? source = safe.Stops;
            if (source != null)
            {
                for (int i = 0; i < source.Count; i++)
                {
                    GradientStopSpec spec = source[i];
                    offsets.Add(Math.Clamp(spec.Offset, 0.0, 1.0));
                    colors.Add(ColorPreset.TryParseHex(spec.Hex, out Color parsed) ? parsed : Colors.White);
                }
            }

            if (offsets.Count == 0)
            {
                offsets.Add(0.0);
                colors.Add(Colors.White);
                offsets.Add(1.0);
                colors.Add(Colors.White);
            }

            var angular = new SolidColorBrush[AngularSectorCount];
            var ramp = new double[offsets.Count];
            var rampColors = new Color[colors.Count];
            for (int i = 0; i < offsets.Count; i++)
            {
                ramp[i] = offsets[i];
                rampColors[i] = colors[i];
            }

            for (int i = 0; i < AngularSectorCount; i++)
            {
                // Skip the very dark base stops: they would render as invisible sectors around the ring.
                double t = 0.25 + (0.75 * (i / (double)(AngularSectorCount - 1)));
                angular[i] = CreateBrush(SampleRamp(ramp, rampColors, t));
            }

            return new ResolvedPalette
            {
                Id = safe.Id ?? string.Empty,
                Core = safe.CoreColor,
                Glow = safe.GlowColor,
                Accent = safe.AccentColor,
                RampOffsets = ramp,
                RampColors = rampColors,
                AngularBrushes = angular
            };
        }

        private static Color SampleRamp(double[] offsets, Color[] colors, double t)
        {
            double clamped = Math.Clamp(t, 0.0, 1.0);
            if (offsets.Length == 0)
                return Colors.White;

            if (clamped <= offsets[0])
                return colors[0];

            for (int i = 1; i < offsets.Length; i++)
            {
                if (clamped > offsets[i])
                    continue;

                double span = offsets[i] - offsets[i - 1];
                double local = span <= 0.0001 ? 0.0 : (clamped - offsets[i - 1]) / span;
                byte r = (byte)Math.Clamp(Math.Round((colors[i - 1].R * (1.0 - local)) + (colors[i].R * local)), 0.0, 255.0);
                byte g = (byte)Math.Clamp(Math.Round((colors[i - 1].G * (1.0 - local)) + (colors[i].G * local)), 0.0, 255.0);
                byte b = (byte)Math.Clamp(Math.Round((colors[i - 1].B * (1.0 - local)) + (colors[i].B * local)), 0.0, 255.0);
                return Color.FromRgb(r, g, b);
            }

            return colors[colors.Length - 1];
        }

        private static LinearGradientBrush BuildColumnBrush(ResolvedPalette resolved, double baseY, double tipY, int alpha)
        {
            var brush = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, baseY),
                EndPoint = new Point(0, tipY)
            };

            double alphaFactor = alpha / 255.0;
            double[] offsets = resolved.RampOffsets;
            Color[] colors = resolved.RampColors;

            // White-hot tip technique, done by INSERTION rather than clamp-and-overwrite.
            // The top TipStart (6%) of every bar is reserved for the tip ramp, so authored stops are
            // kept at their own offsets and only stops authored ABOVE TipStart are clamped down to it.
            // The previous code clamped every authored stop to 0.94 and then unconditionally overwrote
            // 0.94 with `Core`, which silently discarded the palette's authored final stop - every one
            // of the 30 shipped palettes authors its last stop at 1.0, so `holographic` lost its mint
            // #5CFFC8 and `glitch-rgb` lost its white entirely.
            // -1.0, not 0.0: every palette authors its first stop at offset 0 and a `<=` guard would
            // silently drop that base colour.
            double lastOffset = -1.0;
            bool hasStopAtTipStart = false;

            for (int i = 0; i < offsets.Length; i++)
            {
                double offset = Math.Clamp(offsets[i], 0.0, TipStart);
                // GradientStopCollection requires ascending offsets; skip a stop that would collide
                // with the previous one rather than risking an insertion-order exception.
                if (offset <= lastOffset)
                    continue;

                brush.GradientStops.Add(new GradientStop(WithAlpha(colors[i], alphaFactor), offset));
                lastOffset = offset;
                hasStopAtTipStart = offset >= TipStart;
            }

            if (!hasStopAtTipStart)
            {
                // No authored colour of its own at the tip: ramp from the palette core into pure white.
                // Only add when strictly greater than the last stop, preserving ascending order.
                if (TipStart > lastOffset)
                {
                    brush.GradientStops.Add(new GradientStop(WithAlpha(resolved.Core, alphaFactor), TipStart));
                    lastOffset = TipStart;
                }
            }

            if (lastOffset < 1.0)
                brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Clamp(alpha, 0, 255), 255, 255, 255), 1.0));

            if (brush.CanFreeze)
                brush.Freeze();

            return brush;
        }

        private readonly record struct GradientKey(string PaletteId, int BaseY, int Height, bool Downward, int Alpha);

        private static LinearGradientBrush GetColumnBrush(ResolvedPalette resolved, double baseY, double tipY, int alpha)
        {
            double height = Math.Abs(tipY - baseY);
            bool downward = tipY >= baseY;

            var key = new GradientKey(
                resolved.Id,
                (int)Math.Round(Math.Clamp(baseY, -4096.0, 4096.0)),
                (int)Math.Round(Math.Clamp(height, 1.0, 4096.0)),
                downward,
                Math.Clamp(alpha, 0, 255));

            lock (GradientCacheLock)
            {
                if (GradientBrushCache.TryGetValue(key, out LinearGradientBrush? cached))
                    return cached;
            }

            LinearGradientBrush brush = BuildColumnBrush(resolved, baseY, tipY, key.Alpha);

            lock (GradientCacheLock)
            {
                if (GradientBrushCache.Count > 4096)
                    GradientBrushCache.Clear();

                GradientBrushCache[key] = brush;
            }

            return brush;
        }

        private readonly struct ColumnSpec
        {
            public ColumnSpec(double centerX, double baseY, double tipY, double width)
            {
                CenterX = centerX;
                BaseY = baseY;
                TipY = tipY;
                Width = width;
            }

            public double CenterX { get; }
            public double BaseY { get; }
            public double TipY { get; }
            public double Width { get; }
            public double Height => Math.Abs(TipY - BaseY);
        }

        /// <summary>
        /// A genuine polar ray: an arbitrary line segment between two absolute points.
        /// <para>
        /// This exists because the radial styles used to be built by packing polar coordinates into
        /// <see cref="ColumnSpec"/>, which is <c>(centerX, baseY, tipY, width)</c> and whose geometry
        /// builder emits only VERTICAL segments. Passing <c>(from.X, from.Y, to.X, to.Y)</c> therefore
        /// produced a vertical line at x = from.X running from from.Y down to to.X, with to.Y consumed
        /// as the stroke width. Every ray became a downward smear clipped by the canvas edge, and a
        /// flat-capped style whose rays were mostly horizontal drew nothing at all.
        /// </para>
        /// </summary>
        private readonly struct RaySpec
        {
            public RaySpec(double x0, double y0, double x1, double y1)
            {
                X0 = x0;
                Y0 = y0;
                X1 = x1;
                Y1 = y1;
            }

            public double X0 { get; }
            public double Y0 { get; }
            public double X1 { get; }
            public double Y1 { get; }
        }

        private sealed class ResolvedPalette
        {
            public string Id { get; init; } = string.Empty;
            public Color Core { get; init; }
            public Color Glow { get; init; }
            public Color Accent { get; init; }
            public double[] RampOffsets { get; init; } = Array.Empty<double>();
            public Color[] RampColors { get; init; } = Array.Empty<Color>();
            public SolidColorBrush[] AngularBrushes { get; init; } = Array.Empty<SolidColorBrush>();

            public static ResolvedPalette Build(ColorPreset? preset) => BuildResolved(preset);

            public SolidColorBrush GetAngularBrush(double angleDegrees, int alpha)
            {
                int index = (int)Math.Round(NormalizeAngle(angleDegrees) / AngularSectorDegrees) % AngularSectorCount;
                if (index < 0)
                    index += AngularSectorCount;

                if (alpha >= 255)
                    return AngularBrushes[index];

                lock (AlphaAngularCache)
                {
                    if (AlphaAngularCache.TryGetValue((index, alpha), out SolidColorBrush? tinted))
                        return tinted;

                    // Pre-tinted variant, cached per (sector, alpha) inside the resolved palette.
                    Color source = AngularBrushes[index].Color;
                    SolidColorBrush built = CreateBrush(WithAlpha(source, alpha / 255.0));

                    if (AlphaAngularCache.Count > 512)
                        AlphaAngularCache.Clear();

                    AlphaAngularCache[(index, alpha)] = built;
                    return built;
                }
            }

            /// <summary>
            /// AudioRing's two outline pens per (dot radius, halo alpha byte, core alpha byte).
            /// <para>
            /// Same hoisting play as <see cref="GetAngularBrush"/>, one level up: the lattice used to
            /// build a fresh <see cref="SolidColorBrush"/> pair and a fresh <see cref="Pen"/> pair for
            /// every dot of every frame. The dot radius and both alpha bytes are part of the key
            /// because all three move with the canvas, the Opacity and Glow sliders and the
            /// sensitivity - keying on the bucket index alone would hand back a pen of the wrong
            /// width or the wrong alpha the moment any of them changed.
            /// </para>
            /// </summary>
            public Pen[] GetDotPens(double dotRadius, Color haloColor, Color coreColor)
            {
                byte halo = haloColor.A;
                byte core = coreColor.A;

                lock (DotPenCache)
                {
                    if (DotPenCache.TryGetValue((dotRadius, halo, core), out Pen[]? cached))
                        return cached;

                    Pen[] built =
                    {
                        CreatePen(CreateBrush(haloColor), dotRadius * 2.8, PenLineCap.Round),
                        CreatePen(CreateBrush(coreColor), dotRadius * 2.0, PenLineCap.Round)
                    };

                    // A palette is rendered at a handful of canvas sizes; the cap only exists so a
                    // pathological resize sweep cannot grow this without bound.
                    if (DotPenCache.Count > 512)
                        DotPenCache.Clear();

                    DotPenCache[(dotRadius, halo, core)] = built;
                    return built;
                }
            }

            private readonly Dictionary<(int Index, int Alpha), SolidColorBrush> AlphaAngularCache =
                new Dictionary<(int Index, int Alpha), SolidColorBrush>(16);

            private readonly Dictionary<(double Radius, byte Halo, byte Core), Pen[]> DotPenCache =
                new Dictionary<(double Radius, byte Halo, byte Core), Pen[]>(64);
        }

        private sealed class FrameContext
        {
            public FrameContext(
                SoundBarSettings settings,
                ColorPreset palette,
                AudioFrameData[] frames,
                int currentFrameIndex,
                SoundBarRenderState? state,
                int width,
                int height)
            {
                Settings = settings;
                Resolved = ResolvePalette(palette);
                Frames = frames;
                State = state;
                Width = Math.Max(1, width);
                Height = Math.Max(1, height);
                // The soundbar draws on the whole overlay canvas, so the canvas IS the content rect.
                // Used only by the resolution-scaled legibility floors (see DrawSpokeLines).
                Layout = OverlayLayoutHelper.Create(Width, Height, Width, Height);
                FrameCount = Math.Max(1, frames.Length);
                Index = Math.Clamp(currentFrameIndex, 0, FrameCount - 1);
                Time = FrameCount <= 1 ? 0.0 : Index / (double)(FrameCount - 1);
                Plot = ResolvePlotRect(settings, Width, Height);

                Alpha = (byte)Math.Clamp(Math.Round(Math.Clamp(settings.Opacity, 0.0, 1.0) * 255.0), 0.0, 255.0);
                AlphaFactor = Alpha / 255.0;
                Sensitivity = (float)Math.Clamp(settings.Sensitivity, 0.0, 3.0);
                Smoothing = (float)Math.Clamp(settings.Smoothing, 0.0, 1.0);
                GlowIntensity =
                    Math.Clamp(settings.GlowIntensity, 0.0, 3.0) *
                    Math.Clamp(palette.GlowStrength, 0.0, 2.0) *
                    AlphaFactor;
                BarCount = Math.Clamp(settings.BarCount, MinBars, MaxBars);
            }

            public SoundBarSettings Settings { get; }
            public ResolvedPalette Resolved { get; }
            public AudioFrameData[] Frames { get; }
            public SoundBarRenderState? State { get; }
            public int Width { get; }
            public int Height { get; }
            public Rect Plot { get; }

            /// <summary>
            /// Canvas-sized layout, for <see cref="OverlayLayoutHelper.ScaleFromHdReference"/>. The
            /// soundbar overlay is drawn across the whole target canvas, so the content rect is the
            /// canvas rect and the HD scale is derived from the canvas' own short edge.
            /// </summary>
            public OverlayContentLayout Layout { get; }
            public int FrameCount { get; }
            public int Index { get; }
            /// <summary>Seamless loop progress in the range 0..1. All rotation is derived from this.</summary>
            public double Time { get; }
            public byte Alpha { get; }
            /// <summary>The Opacity slider as 0..1. Passed to DrawGlowPasses SEPARATELY from
            /// <see cref="GlowIntensity"/> because the glow-off early-out cannot recover it from a
            /// zeroed intensity.</summary>
            public double AlphaFactor { get; }
            public float Sensitivity { get; }
            public float Smoothing { get; }
            /// <summary>Glow slider x palette strength x Opacity. Already includes the Opacity fold,
            /// so callers must NOT apply <see cref="AlphaFactor"/> to it a second time.</summary>
            public double GlowIntensity { get; }
            public int BarCount { get; }

            public AudioFrameData? Current => FrameAt(Frames, Index);

            public AudioFrameData? Previous => FrameAt(Frames, Index - 1);

            public float SampleBand(int barIndex, int barCount, bool smoothed)
                => SampleBandRange(barIndex, barCount, 0, int.MaxValue, smoothed);

            /// <summary>
            /// Applies <c>Sensitivity</c> and the pink-noise tilt (currently a no-op - see
            /// <see cref="PinkTiltDbPerOctave"/>) to one RAW band value, for the styles
            /// that read <see cref="AudioFrameData.Bands"/> directly instead of through
            /// <see cref="SampleBand"/> (PeakStars). Keeping both paths on the same maths is what stops
            /// the stars drifting away from the bars they annotate.
            /// </summary>
            public float TiltBand(float raw, int index, int bandCount)
                => SoundBarStyleRenderer.ApplyPinkTilt(Math.Clamp(raw * Sensitivity, 0f, 1f), index, bandCount);

            /// <summary>
            /// Reads one band, optionally restricted to a slice of the spectrum (used by DualRings to give
            /// each concentric ring its own frequency window) and optionally temporally smoothed against
            /// the previous analysed frame.
            /// <para>
            /// The pink-noise tilt is applied to BOTH the current and the prior sample, after
            /// <c>Sensitivity</c> and before smoothing, so the two sides of the one-pole filter see the
            /// same spectrum and the temporal smoothing is not fighting the tilt.
            /// </para>
            /// </summary>
            public float SampleBandRange(int barIndex, int barCount, int rangeStart, int rangeCount, bool smoothed)
            {
                AudioFrameData? current = Current;
                float[]? currentBands = current?.Bands;
                if (currentBands == null || barCount <= 0)
                    return 0f;

                int length = currentBands.Length;
                if (length <= 0)
                    return 0f;

                int start = Math.Clamp(rangeStart, 0, length - 1);
                int count = rangeCount <= 0 ? 0 : Math.Min(rangeCount, length - start);
                if (count <= 0)
                    return 0f;

                int index = start + (int)Math.Floor((barIndex / (double)barCount) * count);
                index = Math.Clamp(index, start, start + count - 1);

                float value = ApplyPinkTilt(Math.Clamp(currentBands[index] * Sensitivity, 0f, 1f), index, length);
                if (!smoothed)
                    return value;

                AudioFrameData? previous = Previous;
                float[]? previousBands = previous?.Bands;
                float prior = previousBands != null && index < previousBands.Length
                    ? ApplyPinkTilt(Math.Clamp(previousBands[index] * Sensitivity, 0f, 1f), index, length)
                    : 0f;

                float k = 1f - (0.85f * Smoothing);
                return Math.Clamp(prior + ((value - prior) * k), 0f, 1f);
            }

            public LinearGradientBrush GetColumnBrush(double baseY, double tipY)
                => SoundBarStyleRenderer.GetColumnBrush(Resolved, baseY, tipY, Alpha);

            public LinearGradientBrush GetPlotGradientBrush(double baseY, double tipY)
                => GetColumnBrush(baseY, tipY);
        }
    }
}