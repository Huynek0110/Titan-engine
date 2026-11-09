using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TitanEngine
{
    public static partial class EngineCore
    {

        private const string BundledBrandLogoRelativePath = @"Content\Branding\video-logo.png";
        private const string SeedBrandLogoPath = @"C:\Users\lemin\Desktop\ba546ee2-dea8-4016-b57b-12833c12710c.png";

        private static string? _ffmpegPath;
        private static string? _ffprobePath;

        /// <summary>
        /// Whether a usable ffprobe.exe was actually located at startup.
        /// <para>
        /// Exposed because <see cref="GetVideoResolutionAsync"/> answers (1920, 1080) on a missing
        /// ffprobe exactly as it does for a real 16:9 file, so a caller cannot otherwise tell "probed
        /// and it is 16:9" from "never probed". This is the one thing that IS observable, so it is
        /// what callers use to refuse a probe that cannot have happened.
        /// </para>
        /// </summary>
        internal static bool IsFfprobeAvailable
            => !string.IsNullOrEmpty(_ffprobePath) && File.Exists(_ffprobePath);

        /// <summary>Phase 2 facade (ITitanEngineService) đọc trạng thái Initialize — chỉ expose field _isInitialized có sẵn.</summary>
        public static bool IsInitialized => _isInitialized;
        private static string? _upscaylPath;
        private static string? _ngxDlvsrPath;
        private static string? _vfxUpscaleExePath;
        private static string? _vfxModelDir;
        private static string? _vfxRuntimePathPrefix;
        private static bool _isInitialized = false;

        // [NEW] Check if FFmpeg supports GPU encoders
        private static bool _hasNvencSupport = false;
        private static bool _hasHevcNvencSupport = false;
        private static bool _hasAv1NvencSupport = false;
        private static bool _hasAv1AmfSupport = false;
        private static bool _hasNvencChecked = false;

        private sealed class TemplateTimelineBuildResult
        {
            public string OutputPath { get; set; } = string.Empty;
            public double DurationSec { get; set; }
            public List<string> TempArtifacts { get; set; } = new List<string>();
        }

        // ═══════════════════════════════════════════════════════════════════
        // TEXT OVERLAY: Animation effects ported from CAPTION SYNC
        // ═══════════════════════════════════════════════════════════════════
        // Caption Sync burns timed text through an ASS subtitle track, so its
        // effects (Spotify Scroll, Pop & Bounce, Smooth Drift, Glow Pulse,
        // Karaoke Fade, Typewriter, Blur Reveal) are driven by the event
        // timeline. The Text Overlay has no timeline, so the very same effects
        // are rebuilt here purely on top of drawtext expressions that read the
        // presentation timestamp `t` (x / y / alpha / fontsize / borderw /
        // enable all accept FFmpeg expressions).
        //
        // Timing model: line `i` of the text block owns the slot
        // [i * slot, (i + 1) * slot] where slot = effectDuration / lineCount,
        // so the whole effect always completes inside `effectDuration` seconds
        // and the text then holds on screen until the end of the video.
        //
        // Per-character / per-word effects need one drawtext per revealed unit
        // (the only way to reveal text progressively in drawtext). That grows
        // the filter string linearly, so each mode has a hard filter budget to
        // keep the Windows command line under its 32K limit.
        // ═══════════════════════════════════════════════════════════════════
        // The whole FFmpeg command line is limited to 32767 characters and the
        // text overlay only owns a slice of it, so progressive-reveal modes are
        // capped. Blur Reveal is the most expensive (two layers per character),
        // then Typewriter, then word-by-word Karaoke, then plain line reveal.
        // Sized so the generated filter string stays comfortably inside budget.
        private const int TextOverlayBlurRevealCharBudget = 40;
        private const int TextOverlayTypewriterCharBudget = 60;
        private const int TextOverlayKaraokeWordBudget = 45;
        private const double TextOverlayMinEffectDuration = 1.0;
        private const double TextOverlayMaxEffectDuration = 60.0;

        // ═══════════════════════════════════════════════════════════════════
        // SOUNDBAR CONTRACT RANGES
        //
        // Single source of truth for every clamp applied to a soundbar tunable.
        // They are deliberately EQUAL to the range the renderer consumes
        // (SoundBarStyleRenderer) and to the range documented in BAS_LOCALHOST_API_GUIDE.
        //
        // The previous numbers were the XAML slider bounds, pasted into the API docs:
        //   Sensitivity 0.1-4.0 vs renderer 0.0-3.0 -> 3.0-4.0 did nothing
        //   Smoothing   0.05-5.0 vs renderer 0.0-1.0 -> everything above 1.0 collapsed
        //                                    onto the identical k = 1 - 0.85 * 1.0 = 0.15
        //   RingCount   1-12 vs renderer dual-rings 2-6 / ripple-rings 1-8
        //   LoopSeconds 1.0-20.0 vs the 180-frame ceiling at a fixed 30 fps = 6.0 s
        // An advertised range wider than the range that does anything is worse than a
        // narrow one: the caller pays for a knob that cannot move. If the renderer's clamp
        // ever changes, change the matching constant in the same commit.
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>API range for soundBarOpacity. Inside the renderer's 0.0-1.0 alpha clamp.</summary>
        public const double SoundBarMinOpacity = 0.05;
        public const double SoundBarMaxOpacity = 1.0;

        /// <summary>API range for soundBarSensitivity. Matches SoundBarStyleRenderer's 0.0-3.0.</summary>
        public const double SoundBarMinSensitivity = 0.0;
        public const double SoundBarMaxSensitivity = 3.0;

        /// <summary>
        /// API range for soundBarSmoothing. 0.0 = raw bands (no one-pole filter),
        /// 1.0 = the calmest response the renderer produces (k = 0.15). 1.0 is the ceiling
        /// because the renderer clamps there, so anything higher was always inert.
        /// </summary>
        public const double SoundBarMinSmoothing = 0.0;
        public const double SoundBarMaxSmoothing = 1.0;

        /// <summary>API range for soundBarBarCount. Matches SoundBarStyleRenderer's 8-256.</summary>
        public const int SoundBarMinBarCount = 8;
        public const int SoundBarMaxBarCount = 256;

        /// <summary>
        /// API range for soundBarRingCount. 8 is the widest per-style clamp in the renderer
        /// (ripple-rings); dual-rings clamps to 2-6 and every other style ignores the field.
        /// </summary>
        public const int SoundBarMinRingCount = 1;
        public const int SoundBarMaxRingCount = 8;

        /// <summary>
        /// API range for soundBarLoopSeconds. 6.0 s is 180 frames at the render path's fixed
        /// 30 fps, which is the hard ceiling in SoundBarVisualizerPreset.ResolveFrameCount.
        /// </summary>
        public const double SoundBarMinLoopSeconds = 1.0;
        public const double SoundBarMaxLoopSeconds = 6.0;

        /// <summary>How many preview PNGs the picker cache directory is allowed to keep.</summary>
        private const int SoundBarPreviewCacheLimit = 48;
    }
}
