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
        /// Bumping this invalidates every cached PNG sequence without touching the disk.
        /// r2: bar-cap arcs corrected to CounterClockwise, loop-periodic rotation for DualRings /
        /// SpokeLines, minimum bar reach on silent bands, palette top gradient stop preserved,
        /// DualRings laid out for any RingCount, wedge arc radius fixed, BarCount clamped centrally.
        /// None of those pixel changes are visible in the cache-key material, so the version - not the
        /// material - is what forces the stale concave-cap sequences to be re-rendered.
        /// <para>
        /// r3: five styles re-measured wrong and were re-geometried. RadialRing / DualRings /
        /// SpokeLines gained a bounded per-frame auto-gain so the annulus actually reaches its plot
        /// box (it drew ~106 px inside a ~274 px circle) and a glow halo capped at 4 % of the outer
        /// radius (it was t*5.0 unbounded, which erased the ring into a white teardrop);
        /// DualRings ray thickness is now bounded by the angular slot arc instead of the ring band
        /// width, so 2-6 rings stay distinct; SpokeLines got a resolution-scaled 2 px legibility
        /// floor (it was 1.1 px, i.e. invisible); CenterColumn maps RMS/peak through a
        /// -30..-3 dBFS window (Rms 0.13 was a 20 px stub in a 280 px plot) and its peak-hold tick
        /// is a flat-capped short line that is skipped while there is no peak, instead of a
        /// round-capped zero-length line that painted a glowing white disc on the baseline;
        /// PeakStars picks peaks by log-frequency bucketing with a neighbourhood-contrast test
        /// instead of a strict local-maximum test, so the stars span the width instead of only the
        /// lowest bands. Finally, every band-driven style applies a one-sided +3 dB/octave
        /// pink-noise tilt (anchored at 0 dB on the lowest band, capped at +9 dB, silent bands
        /// untouched) so a tonal track no longer goes flat across the right third of the plot.
        /// As with r2, none of this is visible in the cache-key material, so the version bump is
        /// what forces the broken sequences to be re-rendered rather than replayed from cache.
        /// </para>
        /// </summary>
        private const string RendererVersion = "soundbar-r3";

        /// <summary>Version tag for the single-frame picker preview key. Independent of the sequence key.</summary>
        private const string PreviewVersion = "soundbar-preview-r1";

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
        /// SHA-256 over the same visual material as <see cref="BuildCacheKey"/> - style, palette (with its
        /// stops), canvas size and every tunable in <see cref="SoundBarSettings"/> - truncated to 12 hex
        /// characters. Used as the picker preview's filename so dragging X/Y/Width/Height/Opacity/
        /// Sensitivity/Smoothing/BarCount (the UI re-requests on a 250 ms debounce) actually re-renders
        /// instead of returning the previously written PNG.
        /// <para>
        /// Deliberately EXCLUDES fps, loop length, frame count and the audio fingerprint: the preview
        /// renders one fixed synthetic frame, so none of those can change a preview pixel. Overlay keys
        /// stay stable because they still hash their own material in <see cref="BuildCacheKey"/>.
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
            material.Append(PreviewVersion).Append('|');
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
        /// Renders the full PNG sequence (or reuses the cached one). Never throws: failures are logged and
        /// null is returned so the pipeline can continue without a soundbar.
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

                    for (int i = 0; i < frameCount; i++)
                    {
                        token.ThrowIfCancellationRequested();

                        DrawingVisual overlayVisual = new DrawingVisual();
                        using (DrawingContext dc = overlayVisual.RenderOpen())
                        {
                            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, renderW, renderH));
                            SoundBarStyleRenderer.DrawFrame(dc, renderW, renderH, safeSettings, safePalette, renderFrames, i, state);
                        }

                        RenderTargetBitmap overlayBitmap = new RenderTargetBitmap(renderW, renderH, 96, 96, PixelFormats.Pbgra32);
                        overlayBitmap.Render(overlayVisual);
                        if (overlayBitmap.CanFreeze)
                            overlayBitmap.Freeze();

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
                onLog($"[SOUNDBAR] Generated {styleId} overlay ({safeTag}) -> {cacheKey}, palette={safePalette.Id}, target={safeW}x{safeH}, render={renderW}x{renderH}, fps={safeFps.ToString("F3", CultureInfo.InvariantCulture)}, frames={frameCount}, preview={previewDir}");
                return overlay;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                onLog?.Invoke("[SOUNDBAR] Cancelled.");
                return null;
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"[SOUNDBAR-ERROR] Soundbar overlay generation failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Builds the three-section ffmpeg graph: base -> rgba, looping overlay -> rgba, merge.
        /// All numbers use <see cref="CultureInfo.InvariantCulture"/>.
        /// </summary>
        public static string BuildSingleClipGraph(
            string baseInputLabel,
            string baseVideoFilters,
            string overlayInputLabel,
            int loopFrameCount,
            double frameRate,
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

            string baseChain = string.IsNullOrWhiteSpace(baseVideoFilters)
                ? $"{safeBaseInput}format=rgba[soundbarbase]"
                : $"{safeBaseInput}{baseVideoFilters},format=rgba[soundbarbase]";

            string overlayChain =
                $"{safeOverlayInput}format=rgba," +
                $"loop=loop=-1:size={safeLoopFrameCount}:start=0," +
                $"setpts=N/{fps}/TB," +
                $"trim=duration={duration}[soundbaroverlay]";

            string mergeChain = $"[soundbarbase][soundbaroverlay]overlay=shortest=0:eof_action=repeat:format=auto[{safeOutput}]";

            onLog?.Invoke($"[SOUNDBAR] Overlay graph ready: frames={safeLoopFrameCount}, fps={fps}, duration={duration}");
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

                if (!File.Exists(FormatSequenceFilePath(overlayPattern, 1)))
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