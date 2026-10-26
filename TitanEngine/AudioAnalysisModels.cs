using System;
using System.Collections.Generic;

namespace TitanEngine
{
    /// <summary>
    /// The 12 audio-visualizer styles shipped in the SoundBar v1 pass.
    /// String ids are the stable contract used by the UI, the localhost API and the cache key.
    /// </summary>
    public enum SoundBarStyleId
    {
        NeonBars = 0,
        MirrorSpectrum = 1,
        PillBars = 2,
        DenseColumns = 3,
        DotMatrix = 4,
        RadialRing = 5,
        DualRings = 6,
        RippleRings = 7,
        SpokeLines = 8,
        WaveRibbon = 9,
        CenterColumn = 10,
        PeakStars = 11,
        /// <summary>
        /// The NCS signature look: one thick neon ring whose radius ripples with the waveform,
        /// with glowing particles drifting inside it. Unlike <see cref="RadialRing"/> - which draws
        /// bars radiating from a hub, the After Effects "polar spectrum" look - this is a single
        /// stroked circle, matching the ring that NCS has used on its uploads since 2013.
        /// </summary>
        AudioRing = 12
    }

    /// <summary>Which audio stream feeds the visualizer.</summary>
    public enum SoundBarAudioSource
    {
        /// <summary>External audio when present, otherwise the source video's own audio stream.</summary>
        Auto = 0,
        /// <summary>Force the external audio file (falls back to Auto if missing). When BOTH
        /// external tracks exist this is the MIXED track - use Audio1/Audio2 to target one.</summary>
        External = 1,
        /// <summary>Force the source video's own audio stream.</summary>
        Video = 2,
        /// <summary>Listen ONLY to Audio 1 (the primary track). Falls back to Auto if missing.</summary>
        Audio1 = 3,
        /// <summary>Listen ONLY to Audio 2 (the overlay track) - the mode that makes the
        /// visualiser follow a voice-over while the music plays underneath. Falls back to
        /// Auto if Audio 2 is missing.</summary>
        Audio2 = 4
    }

    public sealed class SoundBarStyleInfo
    {
        public SoundBarStyleId Id { get; set; }
        /// <summary>Stable kebab-case id, e.g. "neon-bars". Used by UI, API and cache keys.</summary>
        public string StyleId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool UsesFft { get; set; }
        public bool UsesWaveform { get; set; }
        public bool UsesOnset { get; set; }
        public bool UsesRms { get; set; }
        /// <summary>Default bar count when this style is picked.</summary>
        public int DefaultBarCount { get; set; } = 64;
    }

    /// <summary>User-tunable visualizer parameters. All resolved and clamped before rendering.</summary>
    public sealed class SoundBarSettings
    {
        public SoundBarStyleId Style { get; set; } = SoundBarStyleId.NeonBars;
        public string PaletteId { get; set; } = "ncs-cyan";

        /// <summary>Anchor point X, normalized 0..1 of the canvas. Clamped so the shape stays fully on-canvas.</summary>
        public double XPercent { get; set; } = 0.50;
        /// <summary>Anchor point Y, normalized 0..1 of the canvas.</summary>
        public double YPercent { get; set; } = 0.82;
        public double WidthPercent { get; set; } = 0.62;
        public double HeightPercent { get; set; } = 0.26;
        public double Opacity { get; set; } = 1.0;
        public double Sensitivity { get; set; } = 1.0;
        public double Smoothing { get; set; } = 1.0;
        public int BarCount { get; set; } = 64;
        public double LoopSeconds { get; set; } = 5.0;
        public double FrameRate { get; set; } = 30.0;
        /// <summary>
        /// Extra ring count for DualRings / RippleRings. Accepted as 1..12 at the settings layer,
        /// but the renderer clamps it per style before drawing: DualRings uses 2..6 and RippleRings
        /// uses 1..8, so a value outside those windows is silently brought into range rather than
        /// rejected.
        /// </summary>
        public int RingCount { get; set; } = 2;
        /// <summary>0..1. 0 = flat hard-cap bars, 1 = fully rounded pill caps.</summary>
        public double Roundness { get; set; } = 1.0;
        /// <summary>Multiplier on the additive glow passes.</summary>
        public double GlowIntensity { get; set; } = 1.0;

        public SoundBarSettings Clone() => (SoundBarSettings)MemberwiseClone();
    }

    /// <summary>
    /// One analysed video frame worth of audio data. Values are pre-normalized to 0..1
    /// so the renderer never has to know anything about FFT or dBFS.
    /// </summary>
    public sealed class AudioFrameData
    {
        /// <summary>
        /// Log-spaced band magnitudes, length = bandCount, each 0..1. Normalised per frame rather
        /// than against a fixed dBFS window: the reference floor tracks the frame's own loudest
        /// band, bounded below by an absolute backstop that stops a quiet frame's ramp being
        /// stretched arbitrarily wide, the top of the ramp sits a few dB above that same peak, and
        /// the whole frame fades out below the silence level. So a quiet track still drives visible
        /// motion instead of pinning to zero, and 0 usually means "below this frame's floor" -
        /// though a band far under a very loud frame's peak does also reach 0 on a frame that is
        /// otherwise plainly not silent.
        /// </summary>
        public float[] Bands { get; set; } = Array.Empty<float>();
        /// <summary>Per-column waveform minimum, length = waveformColumns, range -1..1.</summary>
        public float[] WaveMin { get; set; } = Array.Empty<float>();
        /// <summary>Per-column waveform maximum, length = waveformColumns, range -1..1.</summary>
        public float[] WaveMax { get; set; } = Array.Empty<float>();

        public float Rms { get; set; }
        public float Peak { get; set; }
        public float Bass { get; set; }
        public float Mid { get; set; }
        public float Treble { get; set; }
        /// <summary>
        /// Vocal-catch feature, 0..1. Center-extracted energy in 300-3400 Hz (mid magnitude minus
        /// side magnitude, so instruments panned away from the centre are rejected), mapped against
        /// the loop's own p15/p95 vocal statistics and smoothed with a fast attack / slow release
        /// envelope. Already normalised to the loop; the renderer must NOT multiply it by
        /// Sensitivity (that would double-scale it - see VoiceSwellFraction in the renderer).
        /// </summary>
        public float Voice { get; set; }
        /// <summary>True on this frame when the onset detector fired.</summary>
        public bool Onset { get; set; }
    }

    /// <summary>The complete pre-computed analysis for one soundbar loop.</summary>
    public sealed class AudioAnalysisResult
    {
        public int SampleRate { get; set; }
        public int FftSize { get; set; }
        public int BandCount { get; set; }
        /// <summary>
        /// Number of bands carrying distinct measurements. May be lower than
        /// <see cref="BandCount"/> when the FFT cannot resolve the low end: duplicate bin ranges
        /// are collapsed and the freed slots are only reused by bands wide enough to subdivide.
        /// <see cref="AudioFrameData.Bands"/> still has length <see cref="BandCount"/>; entries past
        /// this index are zero.
        /// </summary>
        public int EffectiveBandCount { get; set; }
        public int WaveformColumns { get; set; }
        public double FrameDurationSeconds { get; set; }
        public int FrameCount { get; set; }

        public string AudioFilePath { get; set; } = string.Empty;
        /// <summary>SHA-256 of the decoded PCM, so the PNG cache never serves another track's bars.</summary>
        public string AudioFingerprint { get; set; } = string.Empty;

        public AudioFrameData[] Frames { get; set; } = Array.Empty<AudioFrameData>();
    }

    /// <summary>Cache manifest written next to a generated PNG sequence.</summary>
    public sealed class SoundBarManifest
    {
        public string StyleId { get; set; } = string.Empty;
        public string PaletteId { get; set; } = string.Empty;
        public int FrameCount { get; set; }
        public double FrameRate { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string OverlayPattern { get; set; } = string.Empty;
        public string AudioFingerprint { get; set; } = string.Empty;
        public string CacheKey { get; set; } = string.Empty;
        public List<string> TempArtifacts { get; set; } = new List<string>();
    }
}