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

        /// <summary>Number of ripples currently alive in the pool.</summary>
        public int RippleCount => _ripples.Count;

        /// <summary>Number of tracked peak stars.</summary>
        public int PeakStarCount => _stars.Count;

        /// <summary>Clears every pooled ripple and star. Call this when restarting a loop from frame zero.</summary>
        public void Reset()
        {
            _ripples.Clear();
            _stars.Clear();
            _lastOnsetFrame = -1;
        }

        internal IReadOnlyList<Ripple> Ripples => _ripples;

        internal int LastOnsetFrame => _lastOnsetFrame;

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
    /// <item>Glow = wide (t*5.0, a~0.08), mid (t*2.4, a~0.22), core (t*1.0, a~0.92) of the SAME geometry.</item>
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

        /// <summary>Slope of the pink-noise tilt, in dB per octave of band frequency.</summary>
        private const double PinkTiltDbPerOctave = 3.0;

        /// <summary>Ceiling of the pink-noise tilt, in dB. x2.82 - deliberately modest.</summary>
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

        /// <summary>Level the loudest bar of a radial frame is normalised to, in 0..1 of the annulus.</summary>
        private const double RadialAutoGainTarget = 0.94;

        /// <summary>Most a single frame's auto-gain may lift the radial shapes. Bounds noise-only frames.</summary>
        private const double RadialAutoGainLimit = 4.0;

        /// <summary>Frame peak at or below which no auto-gain is applied, so silence stays silent.</summary>
        private const double RadialAutoGainFloor = 0.02;

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
                // Minimum reach, matching MirrorSpectrum / PillBars / DenseColumns. Without it a silent
                // band is a zero-length line, and a zero-length line with PenLineCap.Round paints a
                // filled dot of diameter `t` in the near-white Core colour - a row of hot white beads
                // floating on the baseline with no bar attached. A short rounded stub reads correctly.
                double reach = Math.Max(1.0, value * plot.Height);
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
                double reach = Math.Max(1.0, value * halfHeight);
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
                double reach = Math.Max(1.0, value * plot.Height * breathe);
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
                double reach = Math.Max(1.0, value * plot.Height);
                columns.Add(new ColumnSpec(plot.X + (slot * (i + 0.5)), plot.Bottom, plot.Bottom - reach, thickness));
            }

            DrawColumns(dc, ctx, columns, Math.Max(0.5, thickness * 0.55), PenLineCap.Round);
        }

        // ------------------------------------------------------------------
        // 5. DotMatrix
        // ------------------------------------------------------------------

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

            var levels = new double[barCount];
            for (int i = 0; i < barCount; i++)
                levels[i] = ctx.SampleBand(i, barCount, true);

            double gain = ResolveRadialGain(levels);

            var tipRadii = new double[barCount];
            var angles = new double[barCount];
            var rays = new List<ColumnSpec>(barCount);

            for (int i = 0; i < barCount; i++)
            {
                double angle = rotation + (span * i);
                double tip = inner + (Math.Clamp(levels[i] * gain, 0.0, 1.0) * (outer - inner));

                angles[i] = angle;
                tipRadii[i] = tip;

                Point from = PointOnCircle(center, inner, angle);
                Point to = PointOnCircle(center, tip, angle);
                rays.Add(new ColumnSpec(from.X, from.Y, to.X, to.Y));
            }

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

            var levels = new double[barCount];
            var rays = new List<ColumnSpec>(barCount);

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
                    levels[i] = ctx.SampleBandRange(i, barCount, ring * slice, slice, true);

                double gain = ResolveRadialGain(levels);
                rays.Clear();

                for (int i = 0; i < barCount; i++)
                {
                    double angle = rotation + (span * i);
                    double tip = ringInner + (Math.Clamp(levels[i] * gain, 0.0, 1.0) * (ringOuter - ringInner));

                    angles[i] = angle;
                    tipRadii[i] = tip;

                    Point from = PointOnCircle(center, ringInner, angle);
                    Point to = PointOnCircle(center, tip, angle);
                    rays.Add(new ColumnSpec(from.X, from.Y, to.X, to.Y));
                }

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
            double inner = outer * 0.08;
            double thickness = Math.Max(1.0, outer * 0.022);
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

                    ringRadii.Add(radius);
                    ringAlpha.Add(Math.Clamp(fade, 0.0, 1.0));
                }
            }

            if (ringRadii.Count == 0)
            {
                // No live ripple (first frame, or a cached preview): show a calm idle ring set.
                for (int i = 0; i < maxLive; i++)
                {
                    double t = (i + 1) / (double)maxLive;
                    ringRadii.Add(inner + ((outer - inner) * t));
                    ringAlpha.Add(0.30 * (1.0 - t));
                }
            }

            // Three additive passes per ring, halo first and core last. At most eight rings are alive
            // so the per-ring fade is worth the extra draws.
            double intensity = ctx.GlowIntensity;
            for (int i = 0; i < ringRadii.Count; i++)
            {
                double radius = Math.Clamp(ringRadii[i], 0.5, outer);
                double alpha = ringAlpha[i] * intensity;
                if (alpha <= 0.004)
                    continue;

                var ellipse = new EllipseGeometry(center, radius, radius);
                if (ellipse.CanFreeze)
                    ellipse.Freeze();

                dc.DrawGeometry(null, CreatePen(CreateBrush(WithAlpha(ctx.Resolved.Glow, 0.08 * alpha)), thickness * 5.0), ellipse);
                dc.DrawGeometry(null, CreatePen(CreateBrush(WithAlpha(ctx.Resolved.Glow, 0.22 * alpha)), thickness * 2.4), ellipse);
                dc.DrawGeometry(null, CreatePen(CreateBrush(WithAlpha(ctx.Resolved.Accent, 0.92 * alpha)), thickness), ellipse);
            }

            // Soft bloom tracking the youngest ripple.
            double leadRadius = ringRadii[ringRadii.Count - 1];
            double leadAlpha = ringAlpha[ringAlpha.Count - 1];
            if (leadAlpha > 0.02)
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

            var levels = new double[barCount];
            for (int i = 0; i < barCount; i++)
                levels[i] = ctx.SampleBand(i, barCount, true);

            double gain = ResolveRadialGain(levels);

            var rays = new List<ColumnSpec>(barCount);
            for (int i = 0; i < barCount; i++)
            {
                double angle = rotation + (span * i);
                double tip = inner + (Math.Clamp(levels[i] * gain, 0.0, 1.0) * (outer - inner));

                Point from = PointOnCircle(center, inner, angle);
                Point to = PointOnCircle(center, tip, angle);
                rays.Add(new ColumnSpec(from.X, from.Y, to.X, to.Y));
            }

            // Legibility floor. The proportional term (outer - inner) * 0.014 is ~1.1 px at
            // 1280x720 and shrinks again as the canvas or the plot grows, so the whole style
            // rendered as "the halo of an invisible figure". 2.0 px is the floor at the 1080 HD
            // reference and scales with the canvas from there (never below 2.0, never above 8.0 so a
            // 4K canvas cannot turn thin rays into slabs), which keeps the rays visible at
            // 1280x720 AND on a thumbnail-sized plot, where the proportional term is ~0.5 px.
            double floorPx = OverlayLayoutHelper.ScaleFromHdReference(ctx.Layout, 2.0, 2.0, 8.0);
            double thickness = Math.Clamp((outer - inner) * 0.014, floorPx, floorPx * 3.0);

            DrawRadialRays(dc, ctx, rays, thickness, PenLineCap.Flat, RadialHaloExtent(outer));
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

            DrawGlowPasses(dc, rim, Math.Max(0.6, plot.Height * 0.012), ctx.Resolved, ctx.GlowIntensity, PenLineCap.Round);
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

            double columnReach = rms > 0.0 ? Math.Max(1.0, rms * plot.Height) : 0.0;
            if (columnReach >= MinColumnHeight)
            {
                var columns = new List<ColumnSpec>(1)
                {
                    // Same minimum reach as NeonBars / MirrorSpectrum / PillBars / DenseColumns so a
                    // near-silent RMS paints a short stub rather than a bare round-capped dot.
                    // At rms == 0 nothing is drawn at all: a 1 px line with a 28 px stroke and a
                    // PenLineCap.Round is still a 28 x 8.4 px glowing capsule parked on the baseline,
                    // which is the reported oval. The faint track above already reads as "meter at
                    // rest", so true digital silence draws the track and nothing else.
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
                DrawGlowPasses(dc, tick, tickStroke, ctx.Resolved, ctx.GlowIntensity, PenLineCap.Flat, tickStroke * 3.0);

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
            var buckets = new Dictionary<(int Height, bool Downward), List<ColumnSpec>>(columns.Count);
            var painted = new List<ColumnSpec>(columns.Count);
            for (int i = 0; i < columns.Count; i++)
            {
                ColumnSpec column = columns[i];
                if (!IsPaintableColumn(column))
                    continue;

                painted.Add(column);

                var key = ((int)Math.Round(Math.Min(column.Height, 4096.0)), column.TipY >= column.BaseY);
                if (!buckets.TryGetValue(key, out List<ColumnSpec>? bucket))
                {
                    bucket = new List<ColumnSpec>();
                    buckets.Add(key, bucket);
                }

                bucket.Add(column);
            }

            if (painted.Count == 0)
                return;

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
            // Gated on the same paintable set as the body so a skipped stub can never leave a halo
            // (and therefore a bright dot) hanging where there is no bar underneath it.
            Geometry lines = BuildLineGeometry(painted);
            DrawGlowPasses(dc, lines, glowThickness, ctx.Resolved, ctx.GlowIntensity, cap);
        }

        /// <summary>
        /// Shortest column that gets painted at all. Below this the gradient body is not worth drawing
        /// and the round-capped glow line degenerates into a dot.
        /// </summary>
        private const double MinColumnHeight = 0.30;

        private static bool IsPaintableColumn(ColumnSpec column) => column.Height >= MinColumnHeight;

        /// <summary>
        /// The mandatory neon trick: one geometry, three passes, halo first and core last.
        /// Alphas are multiplied by <see cref="FrameContext.GlowIntensity"/> which already folds in
        /// <c>settings.GlowIntensity</c>, <c>palette.GlowStrength</c> and <c>settings.Opacity</c>.
        /// <para>
        /// <paramref name="maxHaloThickness"/> bounds the widest pass. The classic recipe asks for
        /// <c>t * 5.0</c>, which is unbounded with respect to the shape it is blooming: on an element
        /// much smaller than its own stroke budget the additive halo stops being a highlight and
        /// becomes the thing the eye sees - the reported white teardrop, where the ring measured
        /// 106 x 86 px with the glow off and 106 x 230 px with it on. When the clamp binds, the
        /// halo and mid passes are scaled down together (the core pass never is - it IS the shape),
        /// so the recipe's proportions survive.
        /// <para>
        /// The default is <see cref="double.PositiveInfinity"/>, so the clamp never binds and
        /// <c>t * 5.0</c> / <c>t * 2.4</c> / <c>t * 1.0</c> come out bit-identical for every
        /// pre-existing caller. Bar styles pass nothing and are unaffected by construction.
        /// </para>
        /// </summary>
        private static void DrawGlowPasses(
            DrawingContext dc,
            Geometry geometry,
            double thickness,
            ResolvedPalette resolved,
            double intensity,
            PenLineCap cap,
            double maxHaloThickness = double.PositiveInfinity)
        {
            if (intensity <= 0.002)
                return;

            double t = Math.Max(0.25, thickness);
            double halo = t * 5.0;
            double scale = halo > maxHaloThickness ? (maxHaloThickness / halo) : 1.0;
            double wideThickness = Math.Max(t, halo * scale);
            double midThickness = Math.Max(t, (t * 2.4) * scale);

            Pen wide = CreatePen(CreateBrush(WithAlpha(resolved.Glow, 0.08 * intensity)), wideThickness, cap);
            Pen mid = CreatePen(CreateBrush(WithAlpha(resolved.Glow, 0.22 * intensity)), midThickness, cap);
            Pen core = CreatePen(CreateBrush(WithAlpha(resolved.Core, 0.92 * intensity)), t, cap);

            dc.DrawGeometry(null, wide, geometry);
            dc.DrawGeometry(null, mid, geometry);
            dc.DrawGeometry(null, core, geometry);
        }

        private static void DrawRadialRays(
            DrawingContext dc,
            FrameContext ctx,
            List<ColumnSpec> rays,
            double thickness,
            PenLineCap cap,
            double maxHaloThickness)
        {
            if (rays.Count == 0)
                return;

            Geometry geometry = BuildLineGeometry(rays);
            DrawGlowPasses(dc, geometry, thickness, ctx.Resolved, ctx.GlowIntensity, cap, maxHaloThickness);
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
        /// Per-frame auto-gain for the RADIAL styles only (RadialRing, DualRings, SpokeLines).
        /// <para>
        /// Those three mapped the raw 0..1 band value straight onto the annulus length, so the drawn
        /// extent was whatever the music happened to measure: on the 1280x720 test a real track
        /// peaked near 0.12 and the ring came out 106 x 86 px inside a 660 x 280 plot box whose
        /// inscribed circle is 274 px across. The fix is not to move the hub - it is to make the
        /// LENGTHS use the box, so the loudest bar of the frame is normalised to
        /// <see cref="RadialAutoGainTarget"/> of the annulus.
        /// <para>
        /// The gain is bounded by <see cref="RadialAutoGainLimit"/> and is skipped entirely below
        /// <see cref="RadialAutoGainFloor"/>, so a near-silent frame is lifted at most 4x rather
        /// than to full scale, and digital silence is not amplified into a solid disc. It is also
        /// one-sided (>= 1.0): a frame that already reaches 0.94 is left exactly as it was, so loud
        /// passages are not pushed further and cannot clip.
        /// </para>
        /// </summary>
        private static double ResolveRadialGain(double[] levels)
        {
            double peak = 0.0;
            for (int i = 0; i < levels.Length; i++)
            {
                if (levels[i] > peak)
                    peak = levels[i];
            }

            if (peak <= RadialAutoGainFloor)
                return 1.0;

            return Math.Clamp(RadialAutoGainTarget / peak, 1.0, RadialAutoGainLimit);
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
        /// <c>AudioSpectrumAnalyzer</c> lays its bands out log-spaced from 40 Hz to Nyquist (11.025 kHz
        /// at its fixed 22.05 kHz rate) and decibel-maps each against an adaptive floor. Musical spectra
        /// fall at roughly 3 dB per octave, so the top two or three octaves - which are also the
        /// WIDEST log buckets and therefore the widest slice of the plot - are the ones that need
        /// lifting, and before the analyser's floor was lowered they read as a dead flat
        /// minimum-height line across the
        /// right third of NeonBars / DenseColumns / MirrorSpectrum / PillBars. The default
        /// Sensitivity and Smoothing cannot rescue that, because both act on level, not slope.
        /// <para>
        /// The compensation is a linear ramp in log frequency, anchored at 0 dB on the lowest band:
        /// <code>
        ///   octaves(i) = log2(PinkBandHighHz / PinkBandLowHz) * i / (N - 1)   // 0 .. 8.11 octaves
        ///   gainDb(i)  = clamp(PinkTiltDbPerOctave * octaves(i), 0, PinkTiltMaxDb)
        ///   gain(i)    = 10 ^ (gainDb(i) / 20)                               // 1.00 .. 2.82
        ///   out(i)     = in(i) * gain(i)                                    // clamped to [0, 1]
        /// </code>
        /// Three properties matter:
        /// <list type="number">
        /// <item>
        /// It is a TILT, not a boost. The gain is exactly 1.0 over the low ~40 % of the spectrum, so
        /// the bands that carry the perceived energy - and the left half of every bar style, which is
        /// what already reads as correct - are bit-for-bit unchanged, and the overall perceived
        /// loudness does not move. The symmetric (mean-1) form of the same ramp was rejected: it
        /// also attenuates the bass by ~12 dB, which shrinks every bar style that is currently right.
        /// </item>
        /// <item>
        /// Broadband noise cannot run away: the +9 dB ceiling (reached at band 23 of 64) bounds the
        /// gain at x2.82, the ramp is one-sided so nothing is amplified at the low end, and the
        /// output is clamped to [0, 1]. A -3 dB/octave signal becomes flat, which is the point.
        /// </item>
        /// <item>
        /// <see cref="PinkSilenceFloor"/> short-circuits anything at or below one 8-bit code, so
        /// digital silence and dither are never lifted into visible motion. True zero stays zero.
        /// </item>
        /// </list>
        /// </summary>
        private static float ApplyPinkTilt(float value, int index, int bandCount)
        {
            if (value <= PinkSilenceFloor)
                return value;

            float[] tilt = GetPinkTilt(bandCount);
            float gained = value * tilt[Math.Clamp(index, 0, tilt.Length - 1)];
            return gained >= 1f ? 1f : gained;
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
                double tip = Math.Max(inner + 0.5, tipRadii[i]);
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
        /// Rule 7 glow brush: absolute mapping, Center == GradientOrigin, RadiusX == RadiusY and
        /// DISCRETE alpha steps. A shallow alpha ramp bands into visible rings on 8-bit alpha.
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
        /// still looks small is a bar-LENGTH problem (<see cref="ResolveRadialGain"/>), not a radius
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

            double plotW = Math.Clamp(settings.WidthPercent, 0.02, 1.0) * w;
            double plotH = Math.Clamp(settings.HeightPercent, 0.02, 1.0) * h;

            double anchorX = Math.Clamp(settings.XPercent, 0.0, 1.0) * w;
            double anchorY = Math.Clamp(settings.YPercent, 0.0, 1.0) * h;

            double left = Math.Clamp(anchorX - (plotW * 0.5), 0.0, Math.Max(0.0, w - plotW));
            double top = Math.Clamp(anchorY - plotH, 0.0, Math.Max(0.0, h - plotH));

            return new Rect(left, top, plotW, plotH);
        }

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

            private readonly Dictionary<(int Index, int Alpha), SolidColorBrush> AlphaAngularCache =
                new Dictionary<(int Index, int Alpha), SolidColorBrush>(16);
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
                Palette = palette;
                Resolved = ResolvePalette(palette);
                Frames = frames;
                State = state;
                Width = Math.Max(1, width);
                Height = Math.Max(1, height);
                Canvas = new Rect(0, 0, Width, Height);
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
            public ColorPreset Palette { get; }
            public ResolvedPalette Resolved { get; }
            public AudioFrameData[] Frames { get; }
            public SoundBarRenderState? State { get; }
            public int Width { get; }
            public int Height { get; }
            public Rect Canvas { get; }
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
            public double AlphaFactor { get; }
            public float Sensitivity { get; }
            public float Smoothing { get; }
            public double GlowIntensity { get; }
            public int BarCount { get; }

            public AudioFrameData? Current => FrameAt(Frames, Index);

            public AudioFrameData? Previous => FrameAt(Frames, Index - 1);

            public float SampleBand(int barIndex, int barCount, bool smoothed)
                => SampleBandRange(barIndex, barCount, 0, int.MaxValue, smoothed);

            /// <summary>
            /// Applies <c>Sensitivity</c> and the pink-noise tilt to one RAW band value, for the styles
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