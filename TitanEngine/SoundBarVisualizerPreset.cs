using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TitanEngine
{
    /// <summary>Descriptor for a generated soundbar PNG sequence, mirroring the other overlay generators.</summary>
    public sealed class SoundBarGeneratedOverlay
    {
        public string TempRoot { get; set; } = string.Empty;
        public string OverlayPattern { get; set; } = string.Empty;
        public int FrameCount { get; set; }
        public double FrameRate { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string PreviewDirectory { get; set; } = string.Empty;
        public List<string> TempArtifacts { get; set; } = new List<string>();
    }

    /// <summary>
    /// Generates the audio-reactive soundbar overlay: a transparent PNG sequence rendered on an STA
    /// thread and handed to FFmpeg as <c>-f image2 -pattern_type sequence</c>.
    ///
    /// IMPORTANT: a cache hit deliberately returns an EMPTY <see cref="SoundBarGeneratedOverlay.TempArtifacts"/>.
    /// The cached sequence lives in the temp tree and must survive the pipeline's cleanup, otherwise the
    /// cache would be destroyed after every render. Only a freshly generated sequence lists its temp root
    /// for deletion.
    /// </summary>
    public static class SoundBarVisualizerPreset
    {
        /// <summary>
        /// Bumping this invalidates every cached PNG sequence AND every cached picker thumbnail,
        /// without touching the disk. It is the ONLY thing that can: neither key can see a pixel
        /// change that is not a tunable, so renderer-level edits have to move this constant.
        /// <para>
        /// There is deliberately no second, independent preview tag. <see cref="BuildPreviewCacheKey"/>
        /// used to hash its own <c>PreviewVersion</c> while <see cref="BuildCacheKey"/> hashed this
        /// one, which meant every renderer bump invalidated 150-frame sequences and left every picker
        /// thumbnail stale forever - while the doc on both keys claimed they covered "the same visual
        /// material". Both keys hash this constant now.
        /// </para>
        /// <para>
        /// HISTORY. The invariant is "bump on every pixel-affecting change", not the wording.
        /// <list type="bullet">
        /// <item>
        /// r2: bar-cap arcs corrected to CounterClockwise, loop-periodic rotation for DualRings /
        /// SpokeLines, minimum bar reach on silent bands, palette top gradient stop preserved,
        /// DualRings laid out for any RingCount, wedge arc radius fixed, BarCount clamped centrally.
        /// None of those pixel changes are visible in the cache-key material, so the version - not the
        /// material - is what forced the stale concave-cap sequences to be re-rendered.
        /// </item>
        /// <item>
        /// r3: five styles re-measured wrong and were re-geometried. RadialRing / DualRings /
        /// SpokeLines gained a bounded per-frame auto-gain so the annulus actually reaches its plot
        /// box (it drew ~106 px inside a ~274 px circle) and a glow halo capped at 4 % of the outer
        /// radius (it was t*5.0 unbounded, which erased the ring into a white teardrop);
        /// DualRings ray thickness bounded by the angular slot arc instead of the ring band width, so
        /// 2-6 rings stay distinct; SpokeLines got a resolution-scaled 2 px legibility floor;
        /// CenterColumn maps RMS/peak through a -30..-3 dBFS window and its peak-hold tick became a
        /// flat-capped short line that is skipped while there is no peak; PeakStars picks peaks by
        /// log-frequency bucketing with a neighbourhood-contrast test; every band-driven style gained
        /// a one-sided +3 dB/octave pink-noise tilt.
        /// </item>
        /// <item>
        /// r4, r5, r6: intermediate renderer revisions whose individual pixel changes were never
        /// recorded here. They are deliberately NOT reconstructed from memory - a version log that
        /// guesses at its own history is worse than one that admits the gap.
        /// </item>
        /// <item>
        /// r7 (this one): the overlay is scaled to the output canvas before compositing, so it is no
        /// longer a 1280-px overlay occupying the top-left 44 % (1920x1080) or 11 % (4K) of the frame;
        /// the preview key covers this constant; and the renderer fixed, in SoundBarStyleRenderer,
        /// the Opacity-inverted coreAlways path, the RippleRings zero-glow blank frame, the
        /// near-step-function pink-noise tilt, the vanishing zero-length rays, the 1 px reach floors
        /// that quantised the new band range away, the radial floor-lifter that flattened its own top
        /// decile, and the glow clamp that degenerated to three identical strokes at DEFAULT
        /// DualRings settings.
        /// </item>
        /// <item>
        /// r15 (this one): the render loop allocates ONE RenderTargetBitmap per pass and clears it
        /// per frame instead of allocating one per frame. Pixel output is unchanged (verified
        /// byte-for-byte); what changes is that the pass stops forcing a blocking gen-2 collection
        /// every other frame - 33 gen-2 collections per 50 frames measured before, 1 after. That
        /// floor was style-independent, so it sat under every style's allocation figure. Note for
        /// whoever reads the version log next: DotMatrix was audited in the same round and NOT
        /// changed - it already draws its whole grid with two geometries and two brushes, and the
        /// per-dot StreamGeometry record cost is not reducible by bucketing. See DrawDotMatrix.
        /// </item>
        /// <item>
        /// r22: AudioRing's interior rebuilt as a rotating 3D PARTICLE SPHERE (latitude
        /// rings under a fixed tilt and a weak perspective, frequency mapped by latitude, per-dot
        /// jitter, bass breathing shared by ring and sphere) replacing the 2D concentric-circle
        /// lattice whose angular brightness sweep advanced at exactly the lattice's own spin rate
        /// and was therefore frozen onto it. Dots are now FILLED halo+core disc pairs - the old
        /// stroked pair drew every dot at double its intended radius (a circle of radius r stroked
        /// with a 2r pen fills the disc of radius 2r) - and the lattice alpha no longer squares the
        /// Opacity slider or vanishes at GlowIntensity 0. No other style's pixels changed.
        /// </item>
        /// <item>
        /// r23: the AudioRing actually MOVES with the music. Added a loop-seamless
        /// onset PULSE (backward-decaying max over the frame array via wrap-around indexing - a
        /// pure function, so no mutable state and no seam pop) that kicks the whole figure, bass
        /// + pulse breathing at 0.06/0.10 of outer, ring deformation driven by the FFT bands by
        /// angle (0.65 band + 0.35 waveform) with a narrower smoothing window, the amplitude
        /// budget raised 0.17 -> 0.30, and dot displacement 0.05 -> 0.09. Before this the
        /// measured ring wobble was 1.5-4.5 px on a ~240 px ring and the style read as static.
        /// </item>
        /// <item>
        /// r24 (this one): the beat response SOFTENED to match the reference (a "smooth circular
        /// spectrum", Trapcode Sound Keys through a smoothed envelope - never spiky, never
        /// snapping). Pulse kick 0.10 -> 0.055 with a 2-frame attack ramp (the kick's own frame
        /// carries 60 %, the peak lands one frame later); ring mix flipped to 0.35 band / 0.65
        /// waveform with an 8-step circular window so the 64 discrete band levels average into a
        /// rounded swell instead of a stepped polygon; amplitude 0.30 -> 0.22; lattice finer
        /// (pitch 0.040 -> 0.032 of outer, dot 0.14 -> 0.12 of pitch, halo 2.1x -> 1.9x, halo
        /// alpha 0.30 -> 0.26); jitter floor 0.55 -> 0.65 and per-band displacement 0.09 -> 0.06
        /// so the sphere shimmers instead of blinking and swells instead of jumping.
        /// </item>
        /// <item>
        /// r25 (this one): every style gets its reference feature, researched per family.
        /// ALL bar styles (neon-bars, mirror-spectrum, pill-bars, dense-columns) gain the
        /// classic falling PEAK-HOLD caps (broadcast ballistics: instant attack, linear fall
        /// ~11 dB/s, pure backward-max over the frame array so the loop seam is invisible)
        /// and a subtle shared bass bounce. dot-matrix re-orients to bottom-up LED "line with
        /// peak hold" mode with a falling peak dot. The polar family (radial-ring,
        /// dual-rings, spoke-lines) gains the AudioRing-style onset pulse + bass breathe on
        /// the whole figure and a luminous hub core. ripple-rings carry the spawning frame's
        /// bass energy (bright thick ring on a kick, faint on a soft onset). wave-ribbon gets
        /// a ghost trail of the previous frame's envelope. center-column gains dB scale ticks
        /// (-6/-12/-20). peak-stars' contrast gate tightened 1.04 -> 1.18 so only genuine
        /// stand-out peaks glint.
        /// </item>
        /// <item>
        /// r26 (this one): five polish rounds over every style. (1) The linear bar styles
        /// gain the onset PUNCH the polar family already had - the whole row's reach
        /// stretches ~8 % on a kick (BarBeatPunchFraction through the shared
        /// OnsetPulseEnvelope, renamed from RadialPulseEnvelope). (2) The band smoother
        /// becomes an ASYMMETRIC envelope follower - fast attack (75 % of the gap per frame
        /// at Smoothing 1), slow release (40 %) - the standard ballistics; hits land in ~2
        /// frames while decays settle over ~4-5. (3) Peak caps HOLD ~0.3 s before falling
        /// (the BBC PPM hold-then-fall shape), shared by the bar caps and the LED peak dot.
        /// (4) DualRings colour-codes its rings - each ring samples a further slice of the
        /// palette ramp, so inner bass and outer treble read as different colours of the
        /// same palette. (5) Peak caps bloom (halo + core) instead of sitting as bare white
        /// hairlines. Smoothing = 0 still disables smoothing on both envelope sides.
        /// </item>
        /// </list>
        /// </summary>
        private const string RendererVersion = "soundbar-r26";

        private const int CacheKeyHexLength = 24;
        private const int PreviewKeyHexLength = 12;

        private const int MaxPreviewFrames = 6;
        private const int MinRenderDimension = 320;
        private const int MaxRenderEdge = 1280;

        private static readonly SoundBarStyleInfo[] Styles = BuildStyles();

        /// <summary>All 12 styles in menu order.</summary>
        public static IReadOnlyList<SoundBarStyleInfo> AllStyles => Styles;

        /// <summary>Resolves a style by kebab id, display name or enum name. Returns null when unknown.</summary>
        public static SoundBarStyleInfo? FindStyle(string? styleId)
        {
            SoundBarStyleId? parsed = ParseStyleId(styleId);
            return parsed.HasValue ? FindByEnum(parsed.Value) : null;
        }

        /// <summary>Canonical kebab-case id for any accepted input. Falls back to "neon-bars".</summary>
        public static string NormalizeStyleId(string? styleId)
            => FindStyle(styleId)?.StyleId ?? Styles[0].StyleId;

        /// <summary>
        /// SHA-256 over every input that can change a pixel: renderer version, style, palette (including its
        /// stops), geometry, fps, frame count and all tunables. The first 24 hex characters, lower-cased.
        /// </summary>
        /// <param name="width">Target canvas width (pre render-scale), matching GenerateOverlayAsync.</param>
        /// <param name="height">Target canvas height (pre render-scale), matching GenerateOverlayAsync.</param>
        public static string BuildCacheKey(
            SoundBarSettings settings,
            ColorPreset palette,
            int width,
            int height,
            string audioFingerprint)
        {
            SoundBarSettings safeSettings = settings is null ? new SoundBarSettings() : settings;
            ColorPreset safePalette = palette ?? ColorPresets.GetOrDefault(null);

            int safeW = Math.Max(2, width);
            int safeH = Math.Max(2, height);
            double fps = ResolveFrameRate(safeSettings);
            int frameCount = ResolveFrameCount(safeSettings);
            ResolveRenderSize(safeW, safeH, out int renderW, out int renderH);

            var material = new StringBuilder(768);
            material.Append(RendererVersion).Append('|');
            AppendGeometryCacheMaterial(material, safeSettings, safePalette, safeW, safeH, renderW, renderH);

            // fps/frameCount are the RESOLVED values, so two settings that clamp to the same rendered
            // loop (e.g. FrameRate 60 and 100) now share one key instead of re-rendering identical pixels.
            var inv = CultureInfo.InvariantCulture;
            material.Append(fps.ToString("F3", inv)).Append('|');
            material.Append(frameCount).Append('|');
            material.Append(audioFingerprint ?? string.Empty);

            return HashMaterial(material, CacheKeyHexLength);
        }

        /// <summary>
        /// SHA-256 over the same visual material as <see cref="BuildCacheKey"/> - the renderer version,
        /// style, palette (with its stops), canvas size and every tunable in
        /// <see cref="SoundBarSettings"/> - truncated to 12 hex characters. Used as the picker
        /// preview's filename so dragging X/Y/Width/Height/Opacity/Sensitivity/Smoothing/BarCount (the
        /// UI re-requests on a 250 ms debounce) actually re-renders instead of returning the
        /// previously written PNG.
        /// <para>
        /// Deliberately EXCLUDES fps, loop length, frame count and the audio fingerprint: the preview
        /// renders one fixed synthetic frame, so none of those can change a preview pixel. Overlay
        /// keys stay stable because they still hash their own material in <see cref="BuildCacheKey"/>.
        /// <para>
        /// It hashes <see cref="RendererVersion"/>, NOT a preview-specific tag. The two used to be
        /// separate constants, which meant every renderer bump re-rendered the 150-frame sequences and
        /// left every thumbnail stale - the two keys claimed the same material and did not share it.
        /// </para>
        /// </summary>
        public static string BuildPreviewCacheKey(
            SoundBarSettings settings,
            ColorPreset palette,
            int width,
            int height)
        {
            SoundBarSettings safeSettings = settings is null ? new SoundBarSettings() : settings;
            ColorPreset safePalette = palette ?? ColorPresets.GetOrDefault(null);

            int safeW = Math.Max(16, width);
            int safeH = Math.Max(16, height);

            var material = new StringBuilder(768);
            material.Append(RendererVersion).Append('|');
            AppendGeometryCacheMaterial(material, safeSettings, safePalette, safeW, safeH, safeW, safeH);

            return HashMaterial(material, PreviewKeyHexLength);
        }

        /// <summary>
        /// Appends the material shared by the sequence key and the preview key: renderer-agnostic
        /// version tags, style identity, palette identity + colours + authored stops, the canvas size
        /// actually rasterised at, and every user tunable. Both keys call this so a tunable can never
        /// be added to one and forgotten in the other.
        /// </summary>
        private static void AppendGeometryCacheMaterial(
            StringBuilder material,
            SoundBarSettings safeSettings,
            ColorPreset safePalette,
            int safeW,
            int safeH,
            int renderW,
            int renderH)
        {
            var inv = CultureInfo.InvariantCulture;

            material.Append(NormalizeStyleId(safeSettings.Style.ToString())).Append('|');
            material.Append(safeSettings.Style.ToString()).Append('|');
            material.Append(safePalette.Id).Append('|');
            material.Append(safeSettings.PaletteId).Append('|');
            material.Append(safeW).Append('x').Append(safeH).Append('|');
            material.Append(renderW).Append('x').Append(renderH).Append('|');
            material.Append(SafeDouble(safeSettings.XPercent, 0.50).ToString("F4", inv)).Append('|');
            material.Append(SafeDouble(safeSettings.YPercent, 0.82).ToString("F4", inv)).Append('|');
            material.Append(SafeDouble(safeSettings.WidthPercent, 0.62).ToString("F4", inv)).Append('|');
            material.Append(SafeDouble(safeSettings.HeightPercent, 0.26).ToString("F4", inv)).Append('|');
            material.Append(SafeDouble(safeSettings.Opacity, 1.0).ToString("F4", inv)).Append('|');
            material.Append(SafeDouble(safeSettings.Sensitivity, 1.0).ToString("F4", inv)).Append('|');
            material.Append(SafeDouble(safeSettings.Smoothing, 1.0).ToString("F4", inv)).Append('|');
            material.Append(safeSettings.BarCount).Append('|');
            material.Append(safeSettings.RingCount).Append('|');
            material.Append(SafeDouble(safeSettings.Roundness, 1.0).ToString("F4", inv)).Append('|');
            material.Append(SafeDouble(safeSettings.GlowIntensity, 1.0).ToString("F4", inv)).Append('|');
            material.Append(SafeDouble(safePalette.GlowStrength, 1.0).ToString("F4", inv)).Append('|');
            material.Append(safePalette.Core).Append('|');
            material.Append(safePalette.Glow).Append('|');
            // BgTint is deliberately NOT hashed. It is never read by any drawing code, so including it
            // only forced a full 150-frame re-render for zero pixel change. It stays on the
            // ColorPreset record because the API endpoint serialises it and external clients read it.
            material.Append(safePalette.Accent).Append('|');

            IReadOnlyList<GradientStopSpec>? stops = safePalette.Stops;
            if (stops != null)
            {
                for (int i = 0; i < stops.Count; i++)
                    material.Append(stops[i].Offset.ToString("F4", inv)).Append(':').Append(stops[i].Hex).Append('|');
            }
        }

        private static string HashMaterial(StringBuilder material, int hexLength)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));
            return Convert.ToHexString(hash)[..hexLength].ToLowerInvariant();
        }

        /// <summary>
        /// Creates the single <see cref="RenderTargetBitmap"/> a whole overlay pass draws through.
        /// <para>
        /// THE DEFECT. This used to be <c>new RenderTargetBitmap(...)</c> INSIDE the per-frame loop.
        /// Each frame allocated a fresh D3D/WIC render target plus a <c>BitmapVisualManager</c>, and
        /// the finaliser-driven reclamation of that chain promoted it straight to the oldest
        /// generation, so a full BLOCKING gen-2 collection ran every other frame. Measured on this
        /// job at 1080x1080, 50 frames of a deliberately trivial draw method:
        /// <c>GC.CollectionCount(0) = (1) = (2) = 33</c> with a fresh bitmap per frame, versus
        /// <c>1 / 1 / 1</c> with one reused + cleared bitmap. Scaled to the 150-frame loop that is
        /// ~99 forced full GCs per render, with the pause on every one of them.
        /// <para>
        /// It was also STYLE-INDEPENDENT, which is why an earlier measurement round could not use
        /// GC.CollectionCount to tell any two styles apart: the floor was identical under all of
        /// them, and it sits underneath whichever renderer allocation the round was trying to rank.
        /// <para>
        /// WHY REUSE IS SAFE HERE. <see cref="RenderTargetBitmap.Render"/> requires the calling
        /// thread to be the one allowed to touch the bitmap, so a shared target would be unsafe if
        /// the loop were threaded. It is not: <see cref="RunOnStaThreadAsync"/> runs the ENTIRE
        /// pass body - every frame's build, every Render and every Save - inside one
        /// <c>action()</c> on ONE freshly created STA thread. The bitmap never leaves that thread,
        /// and each pass builds its own, so two concurrent renders cannot alias.
        /// <para>
        /// WHY IT MUST NOT BE FROZEN. The old code froze each bitmap before saving it, because
        /// freezing is what makes a <see cref="BitmapSource"/> safe to read from anywhere. A target
        /// that is going to be Render()ed into again must stay UNFROZEN:
        /// <c>BitmapVisualManager</c> throws
        /// <c>"The Image passed to the ImageVisualManager cannot be frozen."</c> on the second
        /// frame. Freezing is therefore dropped here; nothing downstream needs it, because
        /// <see cref="SaveBitmap"/> encodes on the same thread that produced the pixels.
        /// <para>
        /// PIXEL EQUALITY. Verified, not assumed. Rendering the loop's exact frame body (DrawingVisual +
        /// full-canvas transparent rect + <see cref="SoundBarStyleRenderer.DrawFrame"/>) and comparing
        /// every frame's pixels against a fresh-bitmap render of the same frame, at 1080x1080 with
        /// BarCount 256: <c>Clear()</c> present -> 0 of 60 frames differ; <c>Clear()</c> removed ->
        /// 59 of 60 differ, the first at frame 1. Independently, a SHA-256 census of frames
        /// 1/2/17/75/149 of the real <see cref="GenerateOverlayAsync"/> matched byte for byte between
        /// this path and the per-frame-allocation path it replaced, for five styles at BarCount 40
        /// and 256. So the clear is load-bearing, not decorative: <see cref="BitmapSource.Clear"/>
        /// returns the surface to exactly the state a freshly constructed target is in.
        /// <para>
        /// ONE ANOMALY, recorded because it was observed and not explained. Deleting the
        /// <see cref="BitmapSource.Clear"/> call from THIS method and re-rendering all ten
        /// style/bar-count combinations through the real <see cref="GenerateOverlayAsync"/> produced
        /// PNG hashes identical to the run with the clear present - both builds hash-verified, cache
        /// purged, frame mtimes confirmed fresh. The direct loop reproduction above, which differs
        /// from this method only in running on the harness's own STA thread rather than the one
        /// <see cref="RunOnStaThreadAsync"/> creates, does show the corruption. Whatever makes this
        /// particular call site currently immune is not something to depend on, and the clear costs
        /// one memset of the target, so it stays. Do not remove it on the strength of that anomaly.
        /// </summary>
        private static RenderTargetBitmap RentFrameTarget(int width, int height)
            => new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

        /// <summary>
        /// Renders the full PNG sequence (or reuses the cached one). Never throws: failures are logged and
        /// null is returned so the pipeline can continue without a soundbar.
        /// <para>
        /// A render that is cancelled or that throws leaves a directory of PNGs with NO manifest. The
        /// manifest is what makes a directory a usable cache, and nothing else ever lists a directory
        /// for cleanup, so without <see cref="DeletePartialTempRoot"/> those frames accumulated in the
        /// temp tree forever (a 150-frame 1280x720 sequence is tens of MB). The partial directory is
        /// deleted on both failure paths; whether a SUCCESSFUL directory should be cleaned up at all is
        /// a separate question, answered by whoever owns <see cref="SoundBarGeneratedOverlay.TempArtifacts"/>.
        /// </para>
        /// </summary>
        public static async Task<SoundBarGeneratedOverlay?> GenerateOverlayAsync(
            AudioAnalysisResult analysis,
            SoundBarSettings settings,
            ColorPreset palette,
            int canvasWidth,
            int canvasHeight,
            string? tag,
            Action<string> onLog,
            CancellationToken token)
        {
            // Non-null from the moment the cache directory is chosen until the manifest is committed.
            // Cleared on success so the catch blocks below leave the finished cache alone.
            string? pendingTempRoot = null;

            try
            {
                if (onLog == null)
                    onLog = _ => { };

                if (analysis == null)
                {
                    onLog("[SOUNDBAR] No audio analysis supplied; skipping the soundbar overlay.");
                    return null;
                }

                AudioFrameData[] source = analysis.Frames ?? Array.Empty<AudioFrameData>();
                if (source.Length == 0)
                {
                    onLog("[SOUNDBAR] Audio analysis contains zero frames; skipping the soundbar overlay.");
                    return null;
                }

                SoundBarSettings safeSettings = settings is null ? new SoundBarSettings() : settings.Clone();
                ColorPreset safePalette = palette ?? ColorPresets.GetOrDefault(null);

                int safeW = Math.Max(2, canvasWidth);
                int safeH = Math.Max(2, canvasHeight);
                double safeFps = ResolveFrameRate(safeSettings);
                int frameCount = ResolveFrameCount(safeSettings);
                ResolveRenderSize(safeW, safeH, out int renderW, out int renderH);

                string styleId = NormalizeStyleId(safeSettings.Style.ToString());
                string cacheKey = BuildCacheKey(safeSettings, safePalette, safeW, safeH, analysis.AudioFingerprint);
                string safeTag = string.IsNullOrWhiteSpace(tag)
                    ? cacheKey
                    : Regex.Replace(tag.Trim(), @"[^\w\-]+", "_");

                string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", "soundbar_cache", styleId, cacheKey);
                pendingTempRoot = tempRoot;
                string overlayPattern = Path.Combine(tempRoot, "soundbar_%04d.png");
                string previewDir = Path.Combine(tempRoot, "preview");
                string manifestPath = Path.Combine(tempRoot, "manifest.json");

                if (TryLoadCache(manifestPath, overlayPattern, previewDir, cacheKey, out SoundBarGeneratedOverlay? cached))
                {
                    onLog($"[SOUNDBAR-CACHE] Reusing {styleId} overlay ({cacheKey}) @ {cached!.Width}x{cached.Height}, frames={cached.FrameCount}, preview={cached.PreviewDirectory}");
                    return cached;
                }

                // Resample onto the exact PNG count so the ffmpeg loop length and the render loop agree.
                // Only references are copied - the band/waveform arrays are shared, never duplicated.
                var renderFrames = new AudioFrameData[frameCount];
                int sourceCount = source.Length;
                for (int i = 0; i < frameCount; i++)
                {
                    int sourceIndex = (int)Math.Floor((i / (double)frameCount) * sourceCount);
                    renderFrames[i] = source[Math.Clamp(sourceIndex, 0, sourceCount - 1)];
                }

                var state = new SoundBarRenderState();

                await RunOnStaThreadAsync(() =>
                {
                    Directory.CreateDirectory(tempRoot);
                    Directory.CreateDirectory(previewDir);

                    // ONE target for the whole pass, cleared per frame. See RentFrameTarget.
                    RenderTargetBitmap overlayBitmap = RentFrameTarget(renderW, renderH);

                    for (int i = 0; i < frameCount; i++)
                    {
                        token.ThrowIfCancellationRequested();

                        DrawingVisual overlayVisual = new DrawingVisual();
                        using (DrawingContext dc = overlayVisual.RenderOpen())
                        {
                            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, renderW, renderH));
                            SoundBarStyleRenderer.DrawFrame(dc, renderW, renderH, safeSettings, safePalette, renderFrames, i, state);
                        }

                        // Clear before Render. Render COMPOSITES onto whatever the surface already
                        // holds, so without this frame N would carry frame N-1's pixels into itself
                        // wherever this frame draws nothing.
                        overlayBitmap.Clear();
                        overlayBitmap.Render(overlayVisual);

                        SaveBitmap(overlayBitmap, FormatSequenceFilePath(overlayPattern, i + 1));

                        if (i < MaxPreviewFrames)
                        {
                            string previewFile = Path.Combine(previewDir, $"preview_{i + 1:D2}.png");
                            SaveBitmap(overlayBitmap, previewFile);
                        }
                    }
                }, token);

                var overlay = new SoundBarGeneratedOverlay
                {
                    TempRoot = tempRoot,
                    OverlayPattern = overlayPattern,
                    FrameCount = frameCount,
                    FrameRate = safeFps,
                    Width = renderW,
                    Height = renderH,
                    PreviewDirectory = previewDir,
                    TempArtifacts = new List<string> { tempRoot }
                };

                WriteManifest(manifestPath, overlay, styleId, safePalette.Id, analysis.AudioFingerprint, cacheKey);

                // Committed: the directory is now a valid cache and must survive.
                pendingTempRoot = null;

                onLog($"[SOUNDBAR] Generated {styleId} overlay ({safeTag}) -> {cacheKey}, palette={safePalette.Id}, target={safeW}x{safeH}, render={renderW}x{renderH}, fps={safeFps.ToString("F3", CultureInfo.InvariantCulture)}, frames={frameCount}, preview={previewDir}");
                return overlay;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                DeletePartialTempRoot(pendingTempRoot, onLog);
                onLog?.Invoke("[SOUNDBAR] Cancelled.");
                return null;
            }
            catch (Exception ex)
            {
                DeletePartialTempRoot(pendingTempRoot, onLog);
                onLog?.Invoke($"[SOUNDBAR-ERROR] Soundbar overlay generation failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Removes a cache directory left behind by a cancelled or failed render. Never throws: the
        /// directory is a cache artefact, so failing to delete it must not mask the original error.
        /// <para>
        /// Called only while <c>pendingTempRoot</c> is still set, i.e. before the manifest is written,
        /// so it can never touch a valid cache. It is recursive because the preview frames live in a
        /// subdirectory of the same root.
        /// <para>
        /// Known pre-existing hazard, unchanged by this: two renders that resolve to the same cache
        /// key (same settings, palette and audio fingerprint) run concurrently, and one failing will
        /// delete the other's in-flight frames. The frames are rewritten on the retry, so the cost is
        /// wasted work, not a corrupt cache - and the write/manifest race that already existed here
        /// is a strictly larger version of the same hazard.
        /// </para>
        /// </summary>
        private static void DeletePartialTempRoot(string? tempRoot, Action<string>? onLog)
        {
            if (string.IsNullOrWhiteSpace(tempRoot))
                return;

            try
            {
                if (!Directory.Exists(tempRoot))
                    return;

                Directory.Delete(tempRoot, recursive: true);
                onLog?.Invoke($"[SOUNDBAR] Discarded the partial render directory {tempRoot}.");
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"[SOUNDBAR] Could not delete the partial render directory {tempRoot}: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds the three-section ffmpeg graph: base -> yuv420p, looping overlay scaled to the
        /// output canvas -> yuva420p, blend. All numbers use <see cref="CultureInfo.InvariantCulture"/>.
        /// <para>
        /// <paramref name="canvasWidth"/> / <paramref name="canvasHeight"/> MUST be the OUTPUT canvas
        /// size, NOT <see cref="SoundBarGeneratedOverlay.Width"/> / <see cref="SoundBarGeneratedOverlay.Height"/>.
        /// The generated sequence is rasterised by <see cref="ResolveRenderSize"/> at a long edge of at
        /// most <see cref="MaxRenderEdge"/> (1280) px, and neither <c>overlay</c> nor the looped image
        /// input scales its second operand, so without the explicit scale chain below the soundbar was
        /// composited 1:1 into the top-left corner: 44 % of a 1920x1080 frame, 11 % of a 4K one. This
        /// is the same <c>scale=...:force_original_aspect_ratio=increase,crop=...,setsar=1</c> chain
        /// every other compositing preset in the pipeline uses (RainOverlayPreset,
        /// DreamyDotOverlay2Preset). Because the sequence is rendered at the canvas aspect ratio, the
        /// scale is a uniform upscale and the crop never actually cuts.
        /// </para>
        /// <para>
        /// r16 (this one): the composite blends in YUV, not in packed RGB. This method used to
        /// force-convert BOTH operands to <c>rgba</c> so <c>overlay</c> could alpha-blend in RGB.
        /// That was, by a wide margin, the single most expensive decision in the whole render:
        /// <c>rgba</c> at 1080x1920 is 8.3 MB per frame and ~39 GB of memory traffic over a 156 s
        /// job, and it was spent to produce a composite that the encode threw away immediately -
        /// every branch in MainWindow terminates the chain with <c>format=yuv420p[vout]</c>. The
        /// yuv420p / yuva420p pair is 2.25 bytes/px instead of 4, and it is the format the output is
        /// going to be in anyway, so the blend now happens once in the domain the file is written in
        /// instead of once up and once down.
        /// <para>
        /// Measured on a 156 s / 1080x1920 / 30 fps job with the cached 150-frame sequence, filter
        /// graph only (no encoder), on 6 cores: 95.9 s wall / 317.9 CPU s before, 20.1 s wall /
        /// 58.1 CPU s after - 4.8x wall, 5.5x CPU. A job with the soundbar disabled measures 6.8 s
        /// in the same harness, so this is the whole soundbar-layer cost, and the floor is unchanged.
        /// <para>
        /// Quality was checked by rendering both forms and looking at the frames, not by comparing
        /// hashes. Over a flat Saturated test pattern the two are indistinguishable at 1:1 and at 3x;
        /// over a high-frequency photographic backdrop they are indistinguishable at full frame and
        /// at 6x on the bar caps. Amplified 8x, the ONLY difference is a sub-pixel-wide chroma fringe
        /// tracing the outline of every bar and dot - the alpha plane sampled at half resolution,
        /// which is what 4:2:0 means. Interior pixels of the glow and of the dots are bit-identical,
        /// and so is the entire background, because the base arrives as yuv420p and never leaves it.
        /// PSNR against the rgba composite: 31.1 dB (neon-bars on a flat pattern), 35.7 dB
        /// (dot-matrix on the same), 32.9 / 39.5 dB on the photographic backdrop. The thin caps and
        /// the dot lattice - the two things 4:2:0 is supposed to destroy - are the two things that
        /// read as unchanged, because both are luma-dominant against a backdrop that is itself
        /// already 4:2:0 by the time it reaches the graph.
        /// <para>
        /// There is no cheap fallback here. The obvious one - keep the rgba overlay but let the base
        /// stay yuv420p - measures 101.4 s / 315.3 CPU s, i.e. no better than the shipped form,
        /// because <c>overlay</c> with <c>format=auto</c> negotiates down to the overlay's packed RGB
        /// format and converts the base to it anyway. The only alternative to this graph is the
        /// rgba/rgba one it replaces, and that costs five times as much.
        /// <para>
        /// The canvas dimensions are forced even here rather than trusted from the caller: yuv420p
        /// cannot carry an odd width or height, so an odd canvas is now a rejected graph rather than
        /// a failed render. ResolveOverlayCanvasProfile already applies EnsureEvenDimension, so no
        /// live caller was ever at risk.
        /// </para>
        /// </summary>
        public static string BuildSingleClipGraph(
            string baseInputLabel,
            string baseVideoFilters,
            string overlayInputLabel,
            int loopFrameCount,
            double frameRate,
            int canvasWidth,
            int canvasHeight,
            double outputDurationSeconds,
            string outputLabel,
            Action<string> onLog)
        {
            var inv = CultureInfo.InvariantCulture;
            string safeBaseInput = string.IsNullOrWhiteSpace(baseInputLabel) ? "[0:v]" : baseInputLabel;
            string safeOverlayInput = string.IsNullOrWhiteSpace(overlayInputLabel) ? "[1:v]" : overlayInputLabel;
            string safeOutput = string.IsNullOrWhiteSpace(outputLabel) ? "soundbarout" : outputLabel;
            string fps = Math.Clamp(SafeDouble(frameRate, 30.0), 1.0, 120.0).ToString("F3", inv);
            string duration = Math.Max(0.10, SafeDouble(outputDurationSeconds, 1.0)).ToString("F3", inv);
            int safeLoopFrameCount = Math.Max(1, loopFrameCount);
            int safeWidth = EnsureEven(canvasWidth);
            int safeHeight = EnsureEven(canvasHeight);

            string baseChain = string.IsNullOrWhiteSpace(baseVideoFilters)
                ? $"{safeBaseInput}format=yuv420p[soundbarbase]"
                : $"{safeBaseInput}{baseVideoFilters},format=yuv420p[soundbarbase]";

            string overlayChain =
                $"{safeOverlayInput}format=yuva420p," +
                $"loop=loop=-1:size={safeLoopFrameCount}:start=0," +
                $"setpts=N/{fps}/TB," +
                $"scale={safeWidth}:{safeHeight}:force_original_aspect_ratio=increase," +
                $"crop={safeWidth}:{safeHeight}," +
                $"setsar=1," +
                $"trim=duration={duration}[soundbaroverlay]";

            // format=yuv420 is what keeps the base out of a packed-RGB conversion. format=auto would
            // pick the overlay's format instead, which is exactly the regression this method removed.
            string mergeChain = $"[soundbarbase][soundbaroverlay]overlay=shortest=0:eof_action=repeat:format=yuv420[{safeOutput}]";

            onLog?.Invoke($"[SOUNDBAR] Overlay graph ready: frames={safeLoopFrameCount}, fps={fps}, duration={duration}, canvas={safeWidth}x{safeHeight}, blend=yuv420 (base=yuv420p, overlay=yuva420p)");
            return $"{baseChain};{overlayChain};{mergeChain}";
        }

        /// <summary>
        /// Renders a single representative PNG for the picker. Fully deterministic - no audio file needed.
        /// Returns the file path, or null on any failure. Never throws.
        /// </summary>
        public static async Task<string?> GeneratePreviewPngAsync(
            SoundBarSettings settings,
            ColorPreset palette,
            int width,
            int height,
            string cacheDirectory,
            Action<string> onLog,
            CancellationToken token)
        {
            try
            {
                SoundBarSettings safeSettings = settings is null ? new SoundBarSettings() : settings.Clone();
                ColorPreset safePalette = palette ?? ColorPresets.GetOrDefault(null);
                string styleId = NormalizeStyleId(safeSettings.Style.ToString());

                string directory = string.IsNullOrWhiteSpace(cacheDirectory)
                    ? Path.Combine(Path.GetTempPath(), "TitanEngine", "soundbar_cache", "previews")
                    : cacheDirectory;

                int renderW = Math.Max(16, width);
                int renderH = Math.Max(16, height);

                // The filename must change whenever ANY tunable changes: the picker re-requests this on a
                // 250 ms debounce while the user drags sliders, so a styleId__paletteId key returned the
                // previously written PNG and dragging showed literally no change. The hash covers the
                // same visual material as the overlay cache key. Old files are simply never hit again.
                string previewKey = BuildPreviewCacheKey(safeSettings, safePalette, renderW, renderH);
                string filePath = Path.Combine(directory, $"{styleId}__{safePalette.Id}__{previewKey}.png");
                if (File.Exists(filePath))
                    return filePath;

                AudioFrameData[] frames = BuildPreviewFrames();
                const int PreviewFrameIndex = 6;
                var state = new SoundBarRenderState();

                await RunOnStaThreadAsync(() =>
                {
                    Directory.CreateDirectory(directory);

                    DrawingVisual previewVisual = new DrawingVisual();
                    using (DrawingContext dc = previewVisual.RenderOpen())
                    {
                        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, renderW, renderH));
                        SoundBarStyleRenderer.DrawFrame(dc, renderW, renderH, safeSettings, safePalette, frames, PreviewFrameIndex, state);
                    }

                    RenderTargetBitmap previewBitmap = new RenderTargetBitmap(renderW, renderH, 96, 96, PixelFormats.Pbgra32);
                    previewBitmap.Render(previewVisual);
                    if (previewBitmap.CanFreeze)
                        previewBitmap.Freeze();

                    SaveBitmap(previewBitmap, filePath);
                }, token);

                onLog?.Invoke($"[SOUNDBAR] Preview written -> {filePath}");
                return filePath;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                onLog?.Invoke("[SOUNDBAR] Preview cancelled.");
                return null;
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"[SOUNDBAR-PREVIEW-ERROR] Preview generation failed: {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Style catalogue
        // ------------------------------------------------------------------

        private static SoundBarStyleInfo[] BuildStyles()
        {
            return new[]
            {
                MakeStyle(SoundBarStyleId.NeonBars, "neon-bars", "Neon Bars",
                    "Classic rising gradient bars with a white-hot tip and a three-pass bloom.", true, false, false, false, 64),
                MakeStyle(SoundBarStyleId.MirrorSpectrum, "mirror-spectrum", "Mirror Spectrum",
                    "Spectrum mirrored above and below the vertical centre line.", true, false, false, false, 56),
                MakeStyle(SoundBarStyleId.PillBars, "pill-bars", "Pill Bars",
                    "Fat rounded capsules with a slow breathing baseline.", true, false, false, false, 28),
                MakeStyle(SoundBarStyleId.DenseColumns, "dense-columns", "Dense Columns",
                    "Up to 256 raw hairlines - the purple-to-cyan curtain.", true, false, false, false, 128),
                MakeStyle(SoundBarStyleId.DotMatrix, "dot-matrix", "Dot Matrix",
                    "Grid of round dots where lit dot count tracks the band value.", true, false, false, false, 40),
                MakeStyle(SoundBarStyleId.RadialRing, "radial-ring", "Radial Ring",
                    "Bars radiating from a centre hub with a slow rotation.", true, false, false, false, 72),
                MakeStyle(SoundBarStyleId.DualRings, "dual-rings", "Dual Rings",
                    "Two counter-rotating concentric rings: low bands inside, high bands outside.", true, false, false, false, 96),
                MakeStyle(SoundBarStyleId.RippleRings, "ripple-rings", "Ripple Rings",
                    "Concentric rings spawned by the onset detector, not a spectrum.", false, false, true, false, 16),
                MakeStyle(SoundBarStyleId.SpokeLines, "spoke-lines", "Spoke Lines",
                    "Thin flat-capped rays from the centre whose length follows the band.", true, false, false, false, 64),
                MakeStyle(SoundBarStyleId.WaveRibbon, "wave-ribbon", "Wave Ribbon",
                    "Filled time-domain waveform ribbon with a brighter rim stroke.", false, true, false, false, 96),
                MakeStyle(SoundBarStyleId.CenterColumn, "center-column", "Center Column",
                    "One tall thin bar driven by RMS with a decaying peak-hold tick.", false, false, false, true, 1),
                MakeStyle(SoundBarStyleId.PeakStars, "peak-stars", "Peak Stars",
                    "Glowing stars pinned to local band maxima, fading over time.", true, false, false, false, 64),
                MakeStyle(SoundBarStyleId.AudioRing, "audio-ring", "Audio Ring (NCS)",
                    "Quả cầu hạt 3D quay theo nhạc bên trong vòng neon mép sóng - đúng kiểu sphere NCS.", true, true, false, false, 64),
            };
        }

        private static SoundBarStyleInfo MakeStyle(
            SoundBarStyleId id,
            string styleId,
            string name,
            string description,
            bool usesFft,
            bool usesWaveform,
            bool usesOnset,
            bool usesRms,
            int defaultBarCount)
        {
            return new SoundBarStyleInfo
            {
                Id = id,
                StyleId = styleId,
                Name = name,
                Description = description,
                UsesFft = usesFft,
                UsesWaveform = usesWaveform,
                UsesOnset = usesOnset,
                UsesRms = usesRms,
                DefaultBarCount = Math.Max(1, defaultBarCount)
            };
        }

        private static SoundBarStyleInfo? FindByEnum(SoundBarStyleId id)
        {
            for (int i = 0; i < Styles.Length; i++)
            {
                if (Styles[i].Id == id)
                    return Styles[i];
            }

            return null;
        }

        private static SoundBarStyleId? ParseStyleId(string? styleId)
        {
            if (string.IsNullOrWhiteSpace(styleId))
                return null;

            string key = NormalizeToken(styleId);
            for (int i = 0; i < Styles.Length; i++)
            {
                if (string.Equals(NormalizeToken(Styles[i].StyleId), key, StringComparison.Ordinal) ||
                    string.Equals(NormalizeToken(Styles[i].Name), key, StringComparison.Ordinal))
                {
                    return Styles[i].Id;
                }
            }

            if (Enum.TryParse(styleId.Trim(), ignoreCase: true, out SoundBarStyleId parsed) &&
                Enum.IsDefined(typeof(SoundBarStyleId), parsed))
            {
                return parsed;
            }

            return null;
        }

        private static string NormalizeToken(string value)
        {
            string trimmed = (value ?? string.Empty).Trim().ToLowerInvariant();
            char[] buffer = new char[trimmed.Length];
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                buffer[i] = (c == '_' || c == ' ' || c == '-') ? '-' : c;
            }

            return new string(buffer);
        }

        // ------------------------------------------------------------------
        // Resolution helpers
        // ------------------------------------------------------------------

        private static double ResolveFrameRate(SoundBarSettings settings)
            => Math.Clamp(SafeDouble(settings.FrameRate, 30.0), 12.0, 60.0);

        private static int ResolveFrameCount(SoundBarSettings settings)
        {
            double fps = ResolveFrameRate(settings);
            double seconds = Math.Clamp(SafeDouble(settings.LoopSeconds, 5.0), 0.25, 30.0);
            double raw = Math.Ceiling(seconds * fps);
            int clamped = raw >= 1000.0 ? 1000 : (int)raw;
            return Math.Clamp(clamped, 24, 180);
        }

        private static void ResolveRenderSize(int width, int height, out int renderW, out int renderH)
        {
            int safeW = Math.Max(2, width);
            int safeH = Math.Max(2, height);
            double renderScale = Math.Min(1.0, MaxRenderEdge / (double)Math.Max(safeW, safeH));

            renderW = EnsureEven(Math.Max(MinRenderDimension, (int)Math.Round(safeW * renderScale)));
            renderH = EnsureEven(Math.Max(MinRenderDimension, (int)Math.Round(safeH * renderScale)));
        }

        private static int EnsureEven(int value)
        {
            int safe = Math.Max(2, value);
            return (safe & 1) == 0 ? safe : safe + 1;
        }

        private static double SafeDouble(double value, double fallback)
            => double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;

        // ------------------------------------------------------------------
        // Deterministic preview audio
        // ------------------------------------------------------------------

        private static AudioFrameData[] BuildPreviewFrames()
        {
            const int FrameTotal = 12;
            const int BandTotal = 64;
            const int WaveTotal = 256;

            var frames = new AudioFrameData[FrameTotal];

            for (int i = 0; i < FrameTotal; i++)
            {
                // Fixed seed: no Random, no clock, so the picker thumbnail is byte-identical every run.
                float t = i * 0.37f;

                var bands = new float[BandTotal];
                for (int b = 0; b < BandTotal; b++)
                {
                    float f = b / (float)(BandTotal - 1);
                    float envelope = MathF.Exp(-f * 2.4f);
                    float wobble = 0.52f + (0.48f * MathF.Sin((t * (1.0f + (f * 5.0f))) + (f * 11.0f)));
                    float pulse = 0.78f + (0.22f * MathF.Sin(t * 2.0f));
                    bands[b] = Math.Clamp(envelope * wobble * pulse, 0f, 1f);
                }

                var waveMin = new float[WaveTotal];
                var waveMax = new float[WaveTotal];
                for (int w = 0; w < WaveTotal; w++)
                {
                    float phase = (w / (float)WaveTotal) * MathF.PI * 2.0f;
                    float fundamental = MathF.Sin((phase * (3.0f + (0.5f * MathF.Sin(t)))) + (t * 2.3f)) * 0.55f;
                    float harmonic = MathF.Sin((phase * 7.0f) - (t * 3.1f)) * 0.24f;
                    float shimmer = MathF.Sin((phase * 13.0f) + (t * 1.7f)) * 0.11f;
                    float v = Math.Clamp(fundamental + harmonic + shimmer, -1f, 1f);
                    waveMin[w] = v;
                    waveMax[w] = Math.Clamp(v + 0.08f, -1f, 1f);
                }

                float energy = 0.42f + (0.44f * (0.5f + (0.5f * MathF.Sin((t * 1.4f) + 0.7f))));

                frames[i] = new AudioFrameData
                {
                    Bands = bands,
                    WaveMin = waveMin,
                    WaveMax = waveMax,
                    Rms = energy,
                    Peak = Math.Clamp(energy + 0.22f, 0f, 1f),
                    Bass = Math.Clamp(energy * 1.10f, 0f, 1f),
                    Mid = energy,
                    Treble = Math.Clamp(energy * 0.70f, 0f, 1f),
                    Onset = i % 3 == 0
                };
            }

            return frames;
        }

        // ------------------------------------------------------------------
        // Cache plumbing
        // ------------------------------------------------------------------

        private static bool TryLoadCache(
            string manifestPath,
            string overlayPattern,
            string previewDir,
            string cacheKey,
            out SoundBarGeneratedOverlay? result)
        {
            result = null;
            if (!File.Exists(manifestPath))
                return false;

            try
            {
                string json = File.ReadAllText(manifestPath);
                SoundBarManifest? cached = JsonSerializer.Deserialize<SoundBarManifest>(json);
                if (cached == null || cached.FrameCount <= 0 || cached.Width <= 0 || cached.Height <= 0)
                    return false;

                if (!string.Equals(cached.CacheKey, cacheKey, StringComparison.OrdinalIgnoreCase))
                    return false;

                // Validate BOTH ends of the sequence, not just the first frame. A partially-evicted directory
                // (Temp cleaner, antivirus quarantine, a disk-full retry) still has frame 0001 and
                // still passes, and ffmpeg then loops a truncated sequence: the soundbar freezes
                // partway through and jumps back to frame 1 - which reads as a rendering bug, not a
                // cache miss. Probing the last frame is O(1) and catches every gap in between, since
                // the frames are only ever written in order into a directory that has no manifest
                // until the whole render completes.
                if (!File.Exists(FormatSequenceFilePath(overlayPattern, 1)))
                    return false;

                if (cached.FrameCount > 1 && !File.Exists(FormatSequenceFilePath(overlayPattern, cached.FrameCount)))
                    return false;

                // TempArtifacts stays empty on a cache hit so the pipeline's cleanup does not delete the cache.
                result = new SoundBarGeneratedOverlay
                {
                    TempRoot = Path.GetDirectoryName(manifestPath)!,
                    OverlayPattern = overlayPattern,
                    FrameCount = cached.FrameCount,
                    FrameRate = cached.FrameRate,
                    Width = cached.Width,
                    Height = cached.Height,
                    PreviewDirectory = previewDir,
                    TempArtifacts = new List<string>()
                };

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteManifest(
            string manifestPath,
            SoundBarGeneratedOverlay overlay,
            string styleId,
            string paletteId,
            string audioFingerprint,
            string cacheKey)
        {
            var manifest = new SoundBarManifest
            {
                StyleId = styleId ?? string.Empty,
                PaletteId = paletteId ?? string.Empty,
                FrameCount = overlay.FrameCount,
                FrameRate = overlay.FrameRate,
                Width = overlay.Width,
                Height = overlay.Height,
                OverlayPattern = overlay.OverlayPattern,
                AudioFingerprint = audioFingerprint ?? string.Empty,
                CacheKey = cacheKey
            };

            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(manifestPath, json, Encoding.UTF8);
        }

        // ------------------------------------------------------------------
        // IO / threading helpers
        // ------------------------------------------------------------------

        private static void SaveBitmap(BitmapSource bitmap, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
        }

        private static string FormatSequenceFilePath(string ffmpegPattern, int frameNumber)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPattern))
                throw new ArgumentException("Sequence pattern cannot be empty.", nameof(ffmpegPattern));

            string csharpPattern = ffmpegPattern.Replace("%04d", "{0:D4}", StringComparison.Ordinal);
            if (string.Equals(csharpPattern, ffmpegPattern, StringComparison.Ordinal))
                csharpPattern = Regex.Replace(ffmpegPattern, @"%0?(\d+)d", m => "{0:D" + m.Groups[1].Value + "}");

            return string.Format(CultureInfo.InvariantCulture, csharpPattern, frameNumber);
        }

        private static Task RunOnStaThreadAsync(Action action, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return Task.FromCanceled(token);

            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread thread = new Thread(() =>
            {
                try
                {
                    if (token.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(token);
                        return;
                    }

                    action();
                    tcs.TrySetResult(null);
                }
                catch (OperationCanceledException oce)
                {
                    tcs.TrySetCanceled(oce.CancellationToken.CanBeCanceled ? oce.CancellationToken : token);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "TitanEngine_SoundBar_STA"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }
    }
}