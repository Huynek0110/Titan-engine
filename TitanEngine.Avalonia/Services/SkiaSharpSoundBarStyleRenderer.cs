using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SkiaSharp;
using TitanEngine;
using TitanEngine.Core;

namespace TitanEngine.Avalonia.Services;

/// <summary>
/// SoundBar visualizer ported from WPF <see cref="TitanEngine.SoundBarStyleRenderer"/> to SkiaSharp,
/// running on Avalonia's engine thread (no WPF/STA, no RenderTargetBitmap).
/// <para>
/// Hard rules carried over from the WPF original:
/// <list type="bullet">
/// <item>Never use blur/shader effects - glow is faked with three stroke passes (wide / mid / core).</item>
/// <item>Glow = wide (t*5.0, a~0.08), mid (t*2.4, a~0.22), core (t*1.0, a~0.92) of the SAME geometry.</item>
/// <item>Everything clamped inside [0,width] x [0,height].</item>
/// <item>One geometry per stream, drawn three times - never a call per bar.</item>
/// <item>Deterministic: pure functions of the analysed frames; loop seam invisible by construction.</item>
/// </list>
/// </summary>
public static class SkiaSharpSoundBarStyleRenderer
{
    private const int MinBars = 8;
    private const int MaxBars = 256;
    private const int AngularSectorCount = 180;
    private const double AngularSectorDegrees = 2.0;
    private const int MaxRipples = 8;

    private const double TipStart = 0.94;

    private const double PinkBandLowHz = 40.0;
    private const double PinkBandHighHz = 11025.0;
    private const double PinkTiltDbPerOctave = 0.0;
    private const double PinkTiltMaxDb = 9.0;
    private const float PinkSilenceFloor = 0.0039f;

    private const double RadialHaloFraction = 0.04;
    private const double RadialAutoGainLimit = 4.0;
    private const double RadialAutoGainFloor = 0.02;
    private const double RadialPercentile = 0.90;
    private const double RadialPercentileTarget = 0.92;
    private const double RadialNoiseFloor = 0.0078;
    private const double RadialGamma = 0.45;
    private const double MinRadialReach = 0.5;

    private const int DotMatrixLitGroupColumns = 4;
    private const double DotMatrixMinPitch = 7.0;
    private const int DotMatrixMaxRows = 24;

    private const double AudioRingBaseFraction = 0.74;
    private const double AudioRingAmplitudeFraction = 0.22;
    private const double AudioRingThicknessFraction = 0.127;
    private const double AudioRingHaloFraction = 0.105;

    private const double AudioRingSphereRadiusFraction = 0.60;
    private const double AudioRingSphereTiltDegrees = 18.0;
    private const double AudioRingSpherePerspective = 0.22;
    private const double AudioRingLatitudeMaxDegrees = 84.0;
    private const double AudioRingRingPitchFraction = 0.032;
    private const double AudioRingDotRadiusPitchRatio = 0.12;
    private const double AudioRingHaloToCoreRatio = 1.9;
    private const double AudioRingHaloAlphaRatio = 0.26;
    private const double AudioRingDisplaceFraction = 0.06;
    private const double AudioRingBassBreatheFraction = 0.05;
    private const double AudioRingPulseKickFraction = 0.055;
    private const double AudioRingPulseAttackLevel = 0.6;
    private const double AudioRingPulseDecayPerFrame = 0.80;
    private const int AudioRingPulseTailFrames = 9;
    private const double AudioRingBackDimFloor = 0.35;

    private const double PeakCapFallPerFrame = 0.012;
    private const int PeakCapHoldFrames = 9;
    private const int PeakCapMaxLookback = 24;

    private const double BarBassBounceFraction = 0.04;
    private const double MidSwellFraction = 0.03;
    private const double VoiceSwellFraction = 0.10;

    private const double VoiceRippleReachFraction = 0.08;
    private const double VoiceCenterColumnFraction = 0.10;
    private const double VoiceStarFlashFraction = 0.30;

    private const double BarBeatPunchFraction = 0.08;
    private const double RadialPulseKickFraction = 0.045;
    private const double RadialBassBreatheFraction = 0.03;

    private const double AudioRingJitterFloor = 0.65;
    private const int AudioRingDotBuckets = 24;

    private const double RippleInnerRadiusFraction = 0.14;
    private const double RippleThicknessFraction = 0.042;
    private const double AudioRingParticleSpinDegrees = 360.0;

    private const double MinimumReachFraction = 0.004;

    private const double GlowWideRatio = 5.0;
    private const double GlowMidRatio = 2.4;
    private const double GlowWideAlpha = 0.08;
    private const double GlowMidAlpha = 0.22;
    private const double GlowCoreAlpha = 0.92;
    private const double GlowColumnCoreWidthRatio = 0.34;
    private const double GlowMinMidRatio = 1.35;
    private const double GlowMinWideRatio = 1.9;

    private const double CenterColumnFloorDb = -30.0;
    private const double CenterColumnCeilDb = -3.0;
    private const double CenterColumnMinFraction = 0.10;

    private const float PeakStarFloor = 0.12f;
    private const double PeakStarContrastRatio = 1.18;

    // Mutable scratch state carried across the frames of ONE GenerateOverlayAsync call.
    public sealed class SoundBarRenderState
    {
        internal readonly struct Ripple
        {
            public int SpawnFrame { get; }
            public float Energy { get; }
            public Ripple(int spawnFrame, float energy)
            {
                SpawnFrame = spawnFrame;
                Energy = energy;
            }
        }

        internal readonly struct Star
        {
            public int BandIndex { get; }
            public int SpawnFrame { get; }
            public float Peak { get; }
            public float X { get; }
            public float Y { get; }
            public Star(int bandIndex, int spawnFrame, float peak, float x, float y)
            {
                BandIndex = bandIndex;
                SpawnFrame = spawnFrame;
                Peak = peak;
                X = x;
                Y = y;
            }
        }

        private readonly List<Ripple> _ripples = new(8);
        private readonly Dictionary<int, Star> _stars = new(64);
        private int _lastOnsetFrame = -1;

        internal IReadOnlyList<Ripple> Ripples => _ripples;

        internal void NoteOnset(int frameIndex, int maxLiveRings, float energy)
        {
            if (frameIndex == _lastOnsetFrame)
                return;
            _lastOnsetFrame = frameIndex;
            if (_ripples.Count >= Math.Max(1, maxLiveRings))
                _ripples.RemoveAt(0);
            _ripples.Add(new Ripple(frameIndex, Math.Clamp(energy, 0.35f, 1f)));
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
            if (_stars.TryGetValue(bandIndex, out Star existing) && existing.SpawnFrame <= frameIndex)
                return;
            _stars[bandIndex] = new Star(bandIndex, frameIndex, peak, x, y);
        }

        internal void ExpireStars(int frameIndex, int lifetimeFrames)
        {
            if (_stars.Count == 0)
                return;
            List<int>? expired = null;
            foreach (var entry in _stars)
            {
                if (frameIndex - entry.Value.SpawnFrame > lifetimeFrames)
                    (expired ??= new List<int>()).Add(entry.Key);
            }
            if (expired == null)
                return;
            for (int i = 0; i < expired.Count; i++)
                _stars.Remove(expired[i]);
        }

        internal IEnumerable<Star> EnumerateStars() => _stars.Values;
    }

    private readonly record struct ColumnSpec(double CenterX, double BaseY, double TipY, double Width)
    {
        public double Height => Math.Abs(TipY - BaseY);
    }

    private readonly record struct RaySpec(double X0, double Y0, double X1, double Y1);

    // Resolved palette: Core / Glow / Accent + ramp stops + angular brushes (wedges).
    private sealed class ResolvedPalette
    {
        public string Id { get; init; } = string.Empty;
        public SKColor Core { get; init; }
        public SKColor Glow { get; init; }
        public SKColor Accent { get; init; }
        public double[] RampOffsets { get; init; } = Array.Empty<double>();
        public SKColor[] RampColors { get; init; } = Array.Empty<SKColor>();
        public SKColor[] AngularBrushes { get; init; } = Array.Empty<SKColor>();
    }

    private sealed class FrameContext
    {
        public SoundBarSettings Settings { get; }
        public ResolvedPalette Resolved { get; }
        public AudioFrameData[] Frames { get; }
        public SoundBarRenderState? State { get; }
        public int Width { get; }
        public int Height { get; }
        public SKRect Plot { get; }
        public byte Alpha { get; }
        public double AlphaFactor { get; }
        public float Sensitivity { get; }
        public float Smoothing { get; }
        public double GlowIntensity { get; }
        public int BarCount { get; }
        public int FrameCount { get; }
        public int Index { get; }
        public double Time { get; }

        public AudioFrameData? Current => FrameAt(Frames, Index);
        public AudioFrameData? Previous => FrameAt(Frames, Index - 1);

        public FrameContext(SoundBarSettings settings, ColorPreset palette, AudioFrameData[] frames,
            int currentFrameIndex, SoundBarRenderState? state, int width, int height)
        {
            Settings = settings;
            Resolved = ResolvePalette(palette);
            Frames = frames;
            State = state;
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            FrameCount = Math.Max(1, frames.Length);
            Index = Math.Clamp(currentFrameIndex, 0, FrameCount - 1);
            Time = FrameCount <= 1 ? 0.0 : Index / (double)(FrameCount - 1);
            Plot = ResolvePlotRect(settings, Width, Height);

            Alpha = (byte)Math.Clamp(Math.Round(Math.Clamp(settings.Opacity, 0.0, 1.0) * 255.0), 0, 255);
            AlphaFactor = Alpha / 255.0;
            Sensitivity = (float)Math.Clamp(settings.Sensitivity, 0.0, 3.0);
            Smoothing = (float)Math.Clamp(settings.Smoothing, 0.0, 1.0);
            GlowIntensity =
                Math.Clamp(settings.GlowIntensity, 0.0, 3.0) *
                Math.Clamp(palette.GlowStrength, 0.0, 2.0) *
                AlphaFactor;
            BarCount = Math.Clamp(settings.BarCount, MinBars, MaxBars);

            // Các giá trị chỉ phụ thuộc vào frame hiện tại → tính 1 lần, dùng lại hết khung
            // (giống y hệt WPF, chỉ tránh tính lại thừa).
            _midLevel = MidLevelStatic(this);
            _voiceLevel = VoiceLevelStatic(this);
            _onsetPulse = OnsetPulseEnvelopeStatic(this);
            _peakLevels = null; // lazy, tính khi cần
        }

        // Cache per-frame: MidLevel (gọi 10+ lần/frame nếu để static).
        private double _midLevel;
        public double MidLevel => _midLevel;

        // Cache per-frame: VoiceLevel.
        private double _voiceLevel;
        public double VoiceLevel => _voiceLevel;

        // Cache per-frame: OnsetPulseEnvelope (loop tối đa 9 frame history).
        private double _onsetPulse;
        public double OnsetPulse => _onsetPulse;

        // Cache per-frame: SampleBandPeak cho mọi bar (256 bar × 25 lookback = 6400 iter/frame).
        // Dùng scratch double[] pool thay new mỗi khung.
        private double[]? _peakLevels;
        public double[] PeakLevels
        {
            get
            {
                if (_peakLevels == null)
                {
                    _peakLevels = GetScratchDoubleArray(BarCount);
                    for (int i = 0; i < BarCount; i++)
                        _peakLevels[i] = SampleBandPeakStatic(this, i, BarCount);
                }
                return _peakLevels;
            }
        }

        // Scratch pools CHIA SẢ cross-frame (static ở lớp ngoài). FrameContext chỉ giữ
        // checkout index (int) — rẻ, không cấp phát mỗi khung.
        private int _scratchPathCheckout;
        private int _scratchArrayCheckout;
        private int _scratchPointArrayCheckout;
        private int _scratchFloatArrayCheckout;
        private int _scratchBoolArrayCheckout;
        private int _scratchPathArrayCheckout;
        private int _scratchColumnListCheckout;
        private int _scratchCapListCheckout;
        private int _scratchRaySpecListCheckout;
        private int _scratchDoubleListCheckout;
        private int _scratchStarListCheckout;
        private int _scratchPeakBandListCheckout;
        private int _scratchBucketListCheckout;

        public SKPath GetScratchPath()
        {
            if (_scratchPathCheckout < _staticScratchPaths.Count)
            {
                var p = _staticScratchPaths[_scratchPathCheckout];
                p.Reset();
                _scratchPathCheckout++;
                return p;
            }
            var p2 = new SKPath();
            _staticScratchPaths.Add(p2);
            _scratchPathCheckout++;
            return p2;
        }

        public void CheckinScratchPaths() => _scratchPathCheckout = 0;

        public double[] GetScratchDoubleArray(int size)
        {
            if (_scratchArrayCheckout < _staticScratchArrays.Count)
            {
                var a = _staticScratchArrays[_scratchArrayCheckout];
                if (a.Length >= size)
                {
                    _scratchArrayCheckout++;
                    return a;
                }
            }
            var a2 = new double[Math.Max(size, 16)];
            _staticScratchArrays.Add(a2);
            _scratchArrayCheckout++;
            return a2;
        }

        public void CheckinScratchArrays() => _scratchArrayCheckout = 0;

        public SKPoint[] GetScratchSKPointArray(int size)
        {
            if (_scratchPointArrayCheckout < _staticScratchPointArrays.Count)
            {
                var a = _staticScratchPointArrays[_scratchPointArrayCheckout];
                if (a.Length >= size)
                {
                    _scratchPointArrayCheckout++;
                    return a;
                }
            }
            var a2 = new SKPoint[Math.Max(size, 16)];
            _staticScratchPointArrays.Add(a2);
            _scratchPointArrayCheckout++;
            return a2;
        }

        public void CheckinScratchPointArrays() => _scratchPointArrayCheckout = 0;

        public float[] GetScratchFloatArray(int size)
        {
            if (_scratchFloatArrayCheckout < _staticScratchFloatArrays.Count)
            {
                var a = _staticScratchFloatArrays[_scratchFloatArrayCheckout];
                if (a.Length >= size)
                {
                    _scratchFloatArrayCheckout++;
                    return a;
                }
            }
            var a2 = new float[Math.Max(size, 16)];
            _staticScratchFloatArrays.Add(a2);
            _scratchFloatArrayCheckout++;
            return a2;
        }

        public void CheckinScratchFloatArrays() => _scratchFloatArrayCheckout = 0;

        public bool[] GetScratchBoolArray(int size)
        {
            if (_scratchBoolArrayCheckout < _staticScratchBoolArrays.Count)
            {
                var a = _staticScratchBoolArrays[_scratchBoolArrayCheckout];
                if (a.Length >= size)
                {
                    _scratchBoolArrayCheckout++;
                    return a;
                }
            }
            var a2 = new bool[Math.Max(size, 16)];
            _staticScratchBoolArrays.Add(a2);
            _scratchBoolArrayCheckout++;
            return a2;
        }

        public void CheckinScratchBoolArrays() => _scratchBoolArrayCheckout = 0;

        public SKPath[] GetScratchSKPathArray(int size)
        {
            if (_scratchPathArrayCheckout < _staticScratchPathArrays.Count)
            {
                var a = _staticScratchPathArrays[_scratchPathArrayCheckout];
                if (a.Length >= size)
                {
                    _scratchPathArrayCheckout++;
                    return a;
                }
            }
            var a2 = new SKPath[Math.Max(size, 16)];
            _staticScratchPathArrays.Add(a2);
            _scratchPathArrayCheckout++;
            return a2;
        }

        public void CheckinScratchPathArrays() => _scratchPathArrayCheckout = 0;

        public List<ColumnSpec> GetScratchColumnList()
        {
            if (_scratchColumnListCheckout < _staticScratchColumnLists.Count)
            {
                var list = _staticScratchColumnLists[_scratchColumnListCheckout];
                list.Clear();
                _scratchColumnListCheckout++;
                return list;
            }
            var list2 = new List<ColumnSpec>();
            _staticScratchColumnLists.Add(list2);
            _scratchColumnListCheckout++;
            return list2;
        }

        public List<(double X, double Y, double Width)> GetScratchCapList()
        {
            if (_scratchCapListCheckout < _staticScratchCapLists.Count)
            {
                var list = _staticScratchCapLists[_scratchCapListCheckout];
                list.Clear();
                _scratchCapListCheckout++;
                return list;
            }
            var list2 = new List<(double X, double Y, double Width)>();
            _staticScratchCapLists.Add(list2);
            _scratchCapListCheckout++;
            return list2;
        }

        public void CheckinScratchColumnLists() => _scratchColumnListCheckout = 0;
        public void CheckinScratchCapLists() => _scratchCapListCheckout = 0;

        public List<RaySpec> GetScratchRaySpecList()
        {
            if (_scratchRaySpecListCheckout < _staticScratchRaySpecLists.Count)
            {
                var list = _staticScratchRaySpecLists[_scratchRaySpecListCheckout];
                list.Clear();
                _scratchRaySpecListCheckout++;
                return list;
            }
            var list2 = new List<RaySpec>();
            _staticScratchRaySpecLists.Add(list2);
            _scratchRaySpecListCheckout++;
            return list2;
        }

        public List<double> GetScratchDoubleList()
        {
            if (_scratchDoubleListCheckout < _staticScratchDoubleLists.Count)
            {
                var list = _staticScratchDoubleLists[_scratchDoubleListCheckout];
                list.Clear();
                _scratchDoubleListCheckout++;
                return list;
            }
            var list2 = new List<double>();
            _staticScratchDoubleLists.Add(list2);
            _scratchDoubleListCheckout++;
            return list2;
        }

        public List<SoundBarRenderState.Star> GetScratchStarList()
        {
            if (_scratchStarListCheckout < _staticScratchStarLists.Count)
            {
                var list = _staticScratchStarLists[_scratchStarListCheckout];
                list.Clear();
                _scratchStarListCheckout++;
                return list;
            }
            var list2 = new List<SoundBarRenderState.Star>();
            _staticScratchStarLists.Add(list2);
            _scratchStarListCheckout++;
            return list2;
        }

        public List<(int Index, float Peak)> GetScratchPeakBandList()
        {
            if (_scratchPeakBandListCheckout < _staticScratchPeakBandLists.Count)
            {
                var list = _staticScratchPeakBandLists[_scratchPeakBandListCheckout];
                list.Clear();
                _scratchPeakBandListCheckout++;
                return list;
            }
            var list2 = new List<(int Index, float Peak)>();
            _staticScratchPeakBandLists.Add(list2);
            _scratchPeakBandListCheckout++;
            return list2;
        }

        public List<ColumnSpec> GetScratchBucketList()
        {
            if (_scratchBucketListCheckout < _staticScratchBucketLists.Count)
            {
                var list = _staticScratchBucketLists[_scratchBucketListCheckout];
                list.Clear();
                _scratchBucketListCheckout++;
                return list;
            }
            var list2 = new List<ColumnSpec>();
            _staticScratchBucketLists.Add(list2);
            _scratchBucketListCheckout++;
            return list2;
        }

        // Scratch Dictionary<(int Height, bool Downward), List<ColumnSpec>> dùng trong DrawColumns.
        // Static vì vẽ tuần tự (không song tiến) → reuse Dictionary qua các khung, tránh cấp
        // Dictionary mới mỗi frame. Khi clear, toàn bộ bucket list trong dictionary được trả
        // về pool (checkout về 0, list sẽ được Clear khi tái lấy qua GetScratchBucketList).
        private static readonly Dictionary<(int Height, bool Downward), List<ColumnSpec>> _scratchColumnBuckets =
            new(16);

        public Dictionary<(int Height, bool Downward), List<ColumnSpec>> GetScratchColumnBuckets()
        {
            _scratchBucketListCheckout = 0; // trả toàn bộ bucket list về pool
            _scratchColumnBuckets.Clear();
            return _scratchColumnBuckets;
        }

        public void CheckinScratchRaySpecLists() => _scratchRaySpecListCheckout = 0;
        public void CheckinScratchDoubleLists() => _scratchDoubleListCheckout = 0;
        public void CheckinScratchStarLists() => _scratchStarListCheckout = 0;
        public void CheckinScratchPeakBandLists() => _scratchPeakBandListCheckout = 0;
        public void CheckinScratchBucketLists() => _scratchBucketListCheckout = 0;

        public float SampleBand(int barIndex, int barCount, bool smoothed)
            => SampleBandRange(barIndex, barCount, 0, int.MaxValue, smoothed);

        public float TiltBand(float raw, int index, int bandCount)
            => ApplyPinkTilt(Math.Clamp(raw * Sensitivity, 0f, 1f), index, bandCount);

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
            float k = value >= prior
                ? (float)(1.0 - (0.25 * Smoothing))
                : (float)(1.0 - (0.60 * Smoothing));
            return Math.Clamp(prior + ((value - prior) * k), 0f, 1f);
        }
    }

    /// <summary>
    /// Renders one frame of the requested style onto <paramref name="canvas"/>.
    /// </summary>
    // PaintCache có hiệu lực chỉ trong một lần DrawFrame (render tuần tự, không song tiến).
    // Mọi SKPaint/SKShader được tạo lại trong khung được cache theo khóa, dùng lại cho toàn bộ
    // khung, rồi giải phóng ở EndFrame → giảm đáng kể cấp phát native mỗi frame.
    private static readonly PaintCache _paintCache = new();

    // Cross-frame SKShader cache (không xóa mỗi khung, chỉ clear khi >64 entry).
    // Shader được tạo lại khi tham số thay đổi; cache hit khi tham số trùng (ví dụ đoạn
    // im lặng → alpha không đổi giữa các khung). Key gồm byte alpha chính xác → pixel-perfect.
    private static readonly ShaderCache _shaderCache = new(64);

    // Cross-frame SKShader→SKPaint wrapper cache. Shader đã chia sẻ cross-frame qua
    // _shaderCache; paint wrapper cũng được tái dùng thay vì new mỗi khung. Clear khi >64 entry.
    private static readonly Dictionary<uint, SKPaint> _shaderPaintCache = new(64);

    // ========================================================================
    // Scratch pools CHIA SẢ cross-frame (không phải per-FrameContext).
    // FrameContext được new mỗi khung, nên nếu pool nằm trong FrameContext thì
    //pool cũng bị tirar lại mỗi khung. Đặt pool ở đây (static) để tái dùng qua các khung.
    // FrameContext chỉ giữ checkout index (int) — rẻ, không cấp phát.
    // ========================================================================
    private static readonly List<SKPath> _staticScratchPaths = new(8);
    private static readonly List<double[]> _staticScratchArrays = new(8);
    private static readonly List<SKPoint[]> _staticScratchPointArrays = new(8);
    private static readonly List<float[]> _staticScratchFloatArrays = new(8);
    private static readonly List<bool[]> _staticScratchBoolArrays = new(8);
    private static readonly List<SKPath[]> _staticScratchPathArrays = new(8);
    private static readonly List<List<ColumnSpec>> _staticScratchColumnLists = new(4);
    private static readonly List<List<(double X, double Y, double Width)>> _staticScratchCapLists = new(4);
    private static readonly List<List<RaySpec>> _staticScratchRaySpecLists = new(4);
    private static readonly List<List<double>> _staticScratchDoubleLists = new(8);
    private static readonly List<List<SoundBarRenderState.Star>> _staticScratchStarLists = new(4);
    private static readonly List<List<(int Index, float Peak)>> _staticScratchPeakBandLists = new(4);
    private static readonly List<List<ColumnSpec>> _staticScratchBucketLists = new(8);

    private static SKPaint GetShaderPaint(uint key, SKShader shader)
    {
        if (_shaderPaintCache.TryGetValue(key, out var paint))
            return paint;
        paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Shader = shader
        };
        _shaderPaintCache[key] = paint;
        if (_shaderPaintCache.Count > 64)
        {
            foreach (var p in _shaderPaintCache.Values) p.Dispose();
            _shaderPaintCache.Clear();
        }
        return paint;
    }

    // Hash tất cả tham số shader thành uint key (không cấp mảng). Mảng colors/offsets chỉ
    // được tạo khi cache miss (trong lambda create).
    private static uint LinearShaderKey(SKPoint start, SKPoint end,
        SKColor c0, float o0, SKColor c1, float o1, SKColor c2, float o2,
        SKColor c3, float o3, SKShaderTileMode tileMode, int colorCount)
    {
        unchecked
        {
            uint h = 2166136261u;
            h = (h * 16777619u) ^ (uint)start.GetHashCode();
            h = (h * 16777619u) ^ (uint)end.GetHashCode();
            h = (h * 16777619u) ^ (uint)tileMode.GetHashCode();
            h = (h * 16777619u) ^ (uint)colorCount;
            h = (h * 16777619u) ^ (uint)c0.GetHashCode() ^ (uint)o0.GetHashCode();
            h = (h * 16777619u) ^ (uint)c1.GetHashCode() ^ (uint)o1.GetHashCode();
            h = (h * 16777619u) ^ (uint)c2.GetHashCode() ^ (uint)o2.GetHashCode();
            h = (h * 16777619u) ^ (uint)c3.GetHashCode() ^ (uint)o3.GetHashCode();
            return h;
        }
    }

    // Các overload nhận từng màu/offset riêng lẻ: mảng SKColor[]/float[] chỉ được cấp
    // bên trong lambda create → chỉ tạo khi cache miss. Cache hit không cấp phát gì.
    private static SKShader GetLinearShader2(uint key, SKPoint start, SKPoint end,
        SKColor c0, float o0, SKColor c1, float o1, SKShaderTileMode tileMode)
        => _shaderCache.GetOrCreate(key, () =>
        {
            var colors = new SKColor[] { c0, c1 };
            var offsets = new float[] { o0, o1 };
            return SKShader.CreateLinearGradient(start, end, colors, offsets, tileMode);
        });

    private static SKShader GetLinearShader3(uint key, SKPoint start, SKPoint end,
        SKColor c0, float o0, SKColor c1, float o1, SKColor c2, float o2,
        SKShaderTileMode tileMode)
        => _shaderCache.GetOrCreate(key, () =>
        {
            var colors = new SKColor[] { c0, c1, c2 };
            var offsets = new float[] { o0, o1, o2 };
            return SKShader.CreateLinearGradient(start, end, colors, offsets, tileMode);
        });

    private static SKShader GetLinearShader4(uint key, SKPoint start, SKPoint end,
        SKColor c0, float o0, SKColor c1, float o1, SKColor c2, float o2,
        SKColor c3, float o3, SKShaderTileMode tileMode)
        => _shaderCache.GetOrCreate(key, () =>
        {
            var colors = new SKColor[] { c0, c1, c2, c3 };
            var offsets = new float[] { o0, o1, o2, o3 };
            return SKShader.CreateLinearGradient(start, end, colors, offsets, tileMode);
        });

    public static void DrawFrame(SKCanvas canvas, int width, int height, SoundBarSettings settings,
        ColorPreset palette, AudioFrameData[] frames, int currentFrameIndex, SoundBarRenderState? state)
    {
        if (canvas == null)
            throw new ArgumentNullException(nameof(canvas));
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (width <= 0 || height <= 0)
            return;

        var ctx = new FrameContext(settings, palette, frames ?? Array.Empty<AudioFrameData>(),
            currentFrameIndex, state, width, height);

        _paintCache.BeginFrame();
        try
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, width, height));
            switch (settings.Style)
            {
                case SoundBarStyleId.MirrorSpectrum: DrawMirrorSpectrum(canvas, ctx); break;
                case SoundBarStyleId.PillBars: DrawPillBars(canvas, ctx); break;
                case SoundBarStyleId.DenseColumns: DrawDenseColumns(canvas, ctx); break;
                case SoundBarStyleId.DotMatrix: DrawDotMatrix(canvas, ctx); break;
                case SoundBarStyleId.RadialRing: DrawRadialRing(canvas, ctx); break;
                case SoundBarStyleId.DualRings: DrawDualRings(canvas, ctx); break;
                case SoundBarStyleId.RippleRings: DrawRippleRings(canvas, ctx); break;
                case SoundBarStyleId.SpokeLines: DrawSpokeLines(canvas, ctx); break;
                case SoundBarStyleId.WaveRibbon: DrawWaveRibbon(canvas, ctx); break;
                case SoundBarStyleId.CenterColumn: DrawCenterColumn(canvas, ctx); break;
                case SoundBarStyleId.PeakStars: DrawPeakStars(canvas, ctx); break;
                case SoundBarStyleId.AudioRing: DrawAudioRing(canvas, ctx); break;
                case SoundBarStyleId.NeonBars:
                default: DrawNeonBars(canvas, ctx); break;
            }
        }
        finally
        {
            canvas.Restore();
            _paintCache.EndFrame();
            ctx.CheckinScratchPaths();
            ctx.CheckinScratchArrays();
            ctx.CheckinScratchPointArrays();
            ctx.CheckinScratchFloatArrays();
            ctx.CheckinScratchBoolArrays();
            ctx.CheckinScratchPathArrays();
            ctx.CheckinScratchColumnLists();
            ctx.CheckinScratchCapLists();
            ctx.CheckinScratchRaySpecLists();
            ctx.CheckinScratchDoubleLists();
            ctx.CheckinScratchStarLists();
            ctx.CheckinScratchPeakBandLists();
            ctx.CheckinScratchBucketLists();
        }
    }

    // ========================================================================
    // 1. NeonBars
    // ========================================================================
    private static void DrawNeonBars(SKCanvas canvas, FrameContext ctx)
    {
        SKRect plot = ctx.Plot;
        if (plot.Width < 2.0f || plot.Height < 2.0f)
            return;

        int barCount = ctx.BarCount;
        double slot = plot.Width / barCount;
        double barWidth = ClampSafe(slot * 0.64, 1.0, slot * 0.96);
        var columns = ctx.GetScratchColumnList();
        var caps = ctx.GetScratchCapList();

        double bass = Math.Clamp((ctx.Current?.Bass ?? 0f) * ctx.Sensitivity, 0.0, 1.0);
        double bounce = 1.0
            + (BarBassBounceFraction * bass)
            + (MidSwellFraction * ctx.MidLevel)
            + (VoiceSwellFraction * ctx.VoiceLevel)
            + (BarBeatPunchFraction * ctx.OnsetPulse);

        for (int i = 0; i < barCount; i++)
        {
            float value = ctx.SampleBand(i, barCount, true);
            double reach = Math.Max(MinimumReach(plot.Height), value * plot.Height * bounce);
            double tipY = plot.Bottom - reach;
            columns.Add(new ColumnSpec(plot.Left + (slot * (i + 0.5)), plot.Bottom, tipY, barWidth));

            double peakReach = ctx.PeakLevels[i] * plot.Height * bounce;
            if (peakReach > reach + 2.0)
                caps.Add((plot.Left + (slot * (i + 0.5)), plot.Bottom - peakReach - (barWidth * 0.30), barWidth));
        }

        DrawColumns(canvas, ctx, columns, Math.Max(0.6, barWidth * 0.34), SKStrokeCap.Round);
        DrawPeakCaps(canvas, ctx, caps, Math.Max(1.4, barWidth * 0.22));
        DrawGlossyReflection(canvas, ctx, columns, plot.Bottom);
        DrawBaselineLine(canvas, ctx, plot.Bottom, plot);
        DrawFloorSpill(canvas, ctx, plot.Bottom, plot.Width, plot.Left);
    }

    // ========================================================================
    // 2. MirrorSpectrum
    // ========================================================================
    private static void DrawMirrorSpectrum(SKCanvas canvas, FrameContext ctx)
    {
        SKRect plot = ctx.Plot;
        if (plot.Width < 2.0f || plot.Height < 2.0f)
            return;

        double midY = plot.Top + (plot.Height * 0.5);
        double halfHeight = plot.Height * 0.5;
        int barCount = ctx.BarCount;
        double slot = plot.Width / barCount;
        double barWidth = ClampSafe(slot * 0.62, 1.0, slot * 0.96);
        var columns = ctx.GetScratchColumnList();
        var caps = ctx.GetScratchCapList();

        double bass = Math.Clamp((ctx.Current?.Bass ?? 0f) * ctx.Sensitivity, 0.0, 1.0);
        double bounce = 1.0
            + (BarBassBounceFraction * bass)
            + (MidSwellFraction * ctx.MidLevel)
            + (VoiceSwellFraction * ctx.VoiceLevel)
            + (BarBeatPunchFraction * ctx.OnsetPulse);

        // Mirror axis: thin bright line anchoring the centre so the two halves read as one figure.
        var axisPath = ctx.GetScratchPath();
        axisPath.MoveTo((float)plot.Left, (float)midY);
        axisPath.LineTo((float)plot.Right, (float)midY);
        canvas.DrawPath(axisPath, StrokePaint(WithAlpha(ctx.Resolved.Core, 0.30 * ctx.AlphaFactor),
            Math.Max(1.2, plot.Height * 0.004), SKStrokeCap.Round));

        for (int i = 0; i < barCount; i++)
        {
            float value = ctx.SampleBand(i, barCount, true);
            double reach = Math.Max(MinimumReach(halfHeight), value * halfHeight * bounce);
            double x = plot.Left + (slot * (i + 0.5));
            columns.Add(new ColumnSpec(x, midY, midY - reach, barWidth));
            columns.Add(new ColumnSpec(x, midY, midY + reach, barWidth));

            double peakReach = ctx.PeakLevels[i] * halfHeight * bounce;
            if (peakReach > reach + 2.0)
            {
                caps.Add((x, midY - peakReach - (barWidth * 0.30), barWidth));
                caps.Add((x, midY + peakReach + (barWidth * 0.30), barWidth));
            }
        }

        // Spectral envelope curve: 5-tap smoothed line threading both halves, drawn under the caps.
        var envelopeTop = ctx.GetScratchDoubleArray(barCount);
        var envelopeBottom = ctx.GetScratchDoubleArray(barCount);
        for (int i = 0; i < barCount; i++)
        {
            double sum = 0.0;
            int n = 0;
            for (int k = -2; k <= 2; k++)
            {
                int idx = i + k;
                if (idx < 0 || idx >= barCount)
                    continue;
                sum += ctx.SampleBand(idx, barCount, true);
                n++;
            }
            envelopeTop[i] = midY - ((sum / Math.Max(1, n)) * halfHeight * bounce);
            envelopeBottom[i] = midY + ((sum / Math.Max(1, n)) * halfHeight * bounce);
        }

        var envelopePath = ctx.GetScratchPath();
        envelopePath.MoveTo((float)(plot.Left + (slot * 0.5)), (float)envelopeTop[0]);
        for (int i = 1; i < barCount; i++)
            envelopePath.LineTo((float)(plot.Left + (slot * (i + 0.5))), (float)envelopeTop[i]);
        envelopePath.MoveTo((float)(plot.Left + (slot * 0.5)), (float)envelopeBottom[0]);
        for (int i = 1; i < barCount; i++)
            envelopePath.LineTo((float)(plot.Left + (slot * (i + 0.5))), (float)envelopeBottom[i]);

        DrawGlowPasses(canvas, envelopePath, Math.Max(1.0, barWidth * 0.16), ctx.Resolved,
            ctx.GlowIntensity * 0.8, ctx.AlphaFactor * 0.80, SKStrokeCap.Round,
            Math.Max(2.5, barWidth * 0.9));

        DrawColumns(canvas, ctx, columns, Math.Max(0.6, barWidth * 0.34), SKStrokeCap.Round);
        DrawPeakCaps(canvas, ctx, caps, Math.Max(1.4, barWidth * 0.22));
    }

    // ========================================================================
    // 3. PillBars
    // ========================================================================
    private static void DrawPillBars(SKCanvas canvas, FrameContext ctx)
    {
        SKRect plot = ctx.Plot;
        if (plot.Width < 2.0f || plot.Height < 2.0f)
            return;

        int barCount = ctx.BarCount;
        double slot = plot.Width / barCount;
        double barWidth = ClampSafe(slot * 0.72, 1.5, slot * 0.98);

        double audioPresence = Math.Clamp((ctx.Current?.Rms ?? 0f) / 0.004, 0.0, 1.0);
        double breathe = 0.93 + (0.07 * (1.0 - audioPresence))
            + (0.07 * audioPresence * Math.Sin(ctx.Time * Math.PI * 2.0));
        double pillPunch = ctx.OnsetPulse;
        double baseY = plot.Bottom - (plot.Height * 0.04 * (1.0 - breathe));

        var columns = ctx.GetScratchColumnList();
        var caps = ctx.GetScratchCapList();
        for (int i = 0; i < barCount; i++)
        {
            float value = ctx.SampleBand(i, barCount, true);
            double span = plot.Height * breathe
                * (1.0 + (BarBeatPunchFraction * pillPunch) + (MidSwellFraction * ctx.MidLevel)
                + (VoiceSwellFraction * ctx.VoiceLevel));
            double reach = Math.Max(MinimumReach(span), value * span);
            columns.Add(new ColumnSpec(plot.Left + (slot * (i + 0.5)), baseY, baseY - reach, barWidth));

            double peakReach = ctx.PeakLevels[i] * span;
            if (peakReach > reach + 2.0)
                caps.Add((plot.Left + (slot * (i + 0.5)), baseY - peakReach - (barWidth * 0.45), barWidth));
        }

        DrawColumns(canvas, ctx, columns, Math.Max(0.8, barWidth * 0.42), SKStrokeCap.Round);
        DrawPeakCaps(canvas, ctx, caps, Math.Max(2.0, barWidth * 0.42));
        DrawGlossyReflection(canvas, ctx, columns, baseY);
        DrawBaselineLine(canvas, ctx, baseY, plot);

        // Inner filament: thin hot line up the centre of every capsule (neon TUBE read).
        var filamentPath = ctx.GetScratchPath();
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnSpec column = columns[i];
            if (column.Height < barWidth * 0.8)
                continue;
            filamentPath.MoveTo((float)column.CenterX, (float)(column.BaseY - (barWidth * 0.35)));
            filamentPath.LineTo((float)column.CenterX, (float)(column.TipY + (barWidth * 0.35)));
        }
        canvas.DrawPath(filamentPath, StrokePaint(WithAlpha(ctx.Resolved.Core, 0.55 * ctx.AlphaFactor),
            Math.Max(1.0, barWidth * 0.16), SKStrokeCap.Round));

        DrawFloorSpill(canvas, ctx, baseY, plot.Width, plot.Left);
    }

    // ========================================================================
    // 4. DenseColumns - the "purple -> cyan curtain"
    // ========================================================================
    private static void DrawDenseColumns(SKCanvas canvas, FrameContext ctx)
    {
        SKRect plot = ctx.Plot;
        if (plot.Width < 2.0f || plot.Height < 2.0f)
            return;

        int barCount = ctx.BarCount;
        double slot = plot.Width / barCount;
        double thickness = Math.Clamp(slot * 0.62, 1.0, 5.0);
        var columns = ctx.GetScratchColumnList();

        double columnsBass = Math.Clamp((ctx.Current?.Bass ?? 0f) * ctx.Sensitivity, 0.0, 1.0);
        double columnsBounce = 1.0
            + (BarBassBounceFraction * columnsBass)
            + (MidSwellFraction * ctx.MidLevel)
            + (VoiceSwellFraction * ctx.VoiceLevel)
            + (BarBeatPunchFraction * ctx.OnsetPulse);
        for (int i = 0; i < barCount; i++)
        {
            // Deliberately unsmoothed: the curtain looks raw and slightly ragged.
            float value = ctx.SampleBand(i, barCount, false);
            double reach = Math.Max(MinimumReach(plot.Height), value * plot.Height * columnsBounce);
            columns.Add(new ColumnSpec(plot.Left + (slot * (i + 0.5)), plot.Bottom, plot.Bottom - reach, thickness));
        }

        const int CurtainHueBuckets = 24;
        var hueGeometries = ctx.GetScratchSKPathArray(CurtainHueBuckets);
        for (int b = 0; b < CurtainHueBuckets; b++)
            hueGeometries[b] = ctx.GetScratchPath();

        var tipsPath = ctx.GetScratchPath();
        double tipHeight = Math.Max(2.0, thickness * 1.8);

        for (int i = 0; i < columns.Count; i++)
        {
            ColumnSpec column = columns[i];
            double left = column.CenterX - (column.Width * 0.5);
            double top = Math.Min(column.BaseY, column.TipY);
            double bottom = Math.Max(column.BaseY, column.TipY);

            int bucket = (int)Math.Clamp(
                Math.Round(((column.CenterX - plot.Left) / Math.Max(1.0, plot.Width)) * (CurtainHueBuckets - 1)),
                0, CurtainHueBuckets - 1);

            var bucketPath = hueGeometries[bucket];
            bucketPath.MoveTo((float)left, (float)top);
            bucketPath.LineTo((float)(left + column.Width), (float)top);
            bucketPath.LineTo((float)(left + column.Width), (float)bottom);
            bucketPath.LineTo((float)left, (float)bottom);
            bucketPath.Close();

            tipsPath.MoveTo((float)left, (float)top);
            tipsPath.LineTo((float)(left + column.Width), (float)top);
            tipsPath.LineTo((float)(left + column.Width), (float)(top + tipHeight));
            tipsPath.LineTo((float)left, (float)(top + tipHeight));
            tipsPath.Close();
        }

        for (int b = 0; b < CurtainHueBuckets; b++)
        {
            double hueT = 0.15 + (0.85 * (b / (double)(CurtainHueBuckets - 1)));
            SKColor hue = SampleRamp(ctx.Resolved.RampOffsets, ctx.Resolved.RampColors, hueT);
            canvas.DrawPath(hueGeometries[b], FillPaint(WithAlpha(hue, 0.88 * ctx.AlphaFactor)));
        }

        canvas.DrawPath(tipsPath, FillPaint(WithAlpha(ctx.Resolved.Core, 0.85 * ctx.AlphaFactor)));

        SKPath lines = ctx.GetScratchPath();
        BuildLineGeometry(lines, columns);
        DrawGlowPasses(canvas, lines, Math.Max(0.5, thickness * 0.55), ctx.Resolved,
            ctx.GlowIntensity, ctx.AlphaFactor, SKStrokeCap.Round,
            coreWidthRatio: GlowColumnCoreWidthRatio);

        DrawBaselineLine(canvas, ctx, plot.Bottom, plot);
        DrawFloorSpill(canvas, ctx, plot.Bottom, plot.Width, plot.Left);
    }

    // ========================================================================
    // 5. DotMatrix
    // ========================================================================
    private static void DrawDotMatrix(SKCanvas canvas, FrameContext ctx)
    {
        SKRect plot = ctx.Plot;
        if (plot.Width < 4.0f || plot.Height < 4.0f)
            return;

        int columns = Math.Clamp(ctx.BarCount, 1, Math.Max(1, (int)Math.Floor(plot.Width / DotMatrixMinPitch)));
        double pitch = plot.Width / columns;
        int rows = Math.Clamp((int)Math.Floor(plot.Height / pitch), 1, DotMatrixMaxRows);

        double stepX = pitch;
        double stepY = plot.Height / rows;
        double radius = Math.Clamp(Math.Min(stepX, stepY) * 0.38, 0.7, Math.Max(1.2, Math.Min(stepX, stepY) * 0.46));

        int litGroupCount = Math.Clamp(columns / DotMatrixLitGroupColumns, 1, DotMatrixLitGroupColumns);
        int columnsPerGroup = (columns + litGroupCount - 1) / litGroupCount;

        double dotBeat = 1.0
            + (BarBassBounceFraction * Math.Clamp((ctx.Current?.Bass ?? 0f) * ctx.Sensitivity, 0.0, 1.0))
            + (MidSwellFraction * ctx.MidLevel)
            + (VoiceSwellFraction * ctx.VoiceLevel)
            + (BarBeatPunchFraction * ctx.OnsetPulse);

        var lit = ctx.GetScratchSKPathArray(litGroupCount);
        for (int g = 0; g < litGroupCount; g++)
            lit[g] = ctx.GetScratchPath();
        // Cache sở hữu lifetime → không dùng using.
        var dimPath = ctx.GetScratchPath();
        var peakPath = ctx.GetScratchPath();
        var hotTopsPath = ctx.GetScratchPath();
        bool hasPeakDot = false;
        bool hasHotTop = false;

        for (int c = 0; c < columns; c++)
        {
            float value = ctx.SampleBand(c, columns, true);
            int litCount = (int)Math.Round(value * dotBeat * rows);
            litCount = Math.Clamp(litCount, 1, rows);

            double peakValue = ctx.PeakLevels[c];
            int peakRow = Math.Clamp((int)Math.Round(peakValue * rows), 1, rows) + 1;
            bool showPeakDot = peakRow > litCount && peakRow <= rows;

            double x = plot.Left + (stepX * (c + 0.5));
            SKPath litCtx = lit[Math.Min(c / columnsPerGroup, litGroupCount - 1)];
            for (int r = 0; r < rows; r++)
            {
                double y = plot.Top + (stepY * (r + 0.5));
                SKPoint point = new((float)x, (float)y);
                if (r == rows - litCount)
                {
                    AddEllipseFigure(hotTopsPath, point, radius, radius);
                    hasHotTop = true;
                    AddEllipseFigure(litCtx, point, radius, radius);
                }
                else if (r > rows - litCount)
                {
                    AddEllipseFigure(litCtx, point, radius, radius);
                }
                else if (showPeakDot && r == rows - peakRow)
                {
                    AddEllipseFigure(peakPath, point, radius * 1.15, radius * 1.15);
                    hasPeakDot = true;
                }
                else
                {
                    AddEllipseFigure(dimPath, point, radius * 0.72, radius * 0.72);
                }
            }
        }

        canvas.DrawPath(dimPath, FillPaint(WithAlpha(ctx.Resolved.Glow, 0.10 * ctx.GlowIntensity)));

        SKPaint litBrush = GetPlotGradientPaint(ctx, plot.Bottom, plot.Top);
        for (int g = 0; g < litGroupCount; g++)
            canvas.DrawPath(lit[g], litBrush);

        if (hasHotTop)
            canvas.DrawPath(hotTopsPath, FillPaint(WithAlpha(ctx.Resolved.Core, 0.80 * ctx.AlphaFactor)));

        if (hasPeakDot)
            canvas.DrawPath(peakPath, FillPaint(WithAlpha(ctx.Resolved.Core, 0.92 * ctx.AlphaFactor)));

        DrawBaselineLine(canvas, ctx, plot.Bottom, plot);
    }

    // ========================================================================
    // 6. RadialRing
    // ========================================================================
    private static void DrawRadialRing(SKCanvas canvas, FrameContext ctx)
    {
        SKRect plot = ctx.Plot;
        double outer = MaxRadius(plot);
        if (outer < 4.0)
            return;

        SKPoint center = PlotCenter(plot);
        double inner = outer * 0.30;

        int barCount = ctx.BarCount;
        double rotation = ctx.Time * 360.0;
        double span = 360.0 / barCount;

        var rawLevels = ctx.GetScratchDoubleArray(barCount);
        for (int i = 0; i < barCount; i++)
            rawLevels[i] = ctx.SampleBand(i, barCount, true);

        double[] levels = ResolveRadialLevels(ctx, rawLevels);

        double beat = RadialBeatScale(ctx);
        for (int i = 0; i < levels.Length; i++)
            levels[i] = Math.Min(1.0, levels[i] * beat);

        var tipRadii = ctx.GetScratchDoubleArray(barCount);
        var angles = ctx.GetScratchDoubleArray(barCount);
        var rays = ctx.GetScratchRaySpecList();

        FillRadialBarSet(levels, center, inner, outer, rotation, span, tipRadii, angles, rays);

        DrawAmbientBloom(canvas, ctx, center, outer);

        // Outer boundary circle.
        var boundaryPath = ctx.GetScratchPath();
        boundaryPath.AddCircle((float)center.X, (float)center.Y, (float)outer);
        DrawGlowPasses(canvas, boundaryPath, Math.Max(0.8, outer * 0.006), ctx.Resolved,
            ctx.GlowIntensity * 0.35, ctx.AlphaFactor * 0.6, SKStrokeCap.Round,
            Math.Max(2.0, outer * 0.03));

        // Baseline circle.
        var baselinePath = ctx.GetScratchPath();
        baselinePath.AddCircle((float)center.X, (float)center.Y, (float)inner);
        DrawGlowPasses(canvas, baselinePath, Math.Max(0.8, (outer - inner) * 0.012), ctx.Resolved,
            ctx.GlowIntensity * 0.7, ctx.AlphaFactor, SKStrokeCap.Round,
            Math.Max(2.0, (outer - inner) * 0.05));

        // Bright hub core.
        double hubR = Math.Max(2.0, inner * 1.15 * beat);
        // Cache sở hữu lifetime → không dùng using.
        var hubPaint = CreateRadialGlowPaint(ctx.Resolved.Glow,
            Math.Min(1.0, 0.50 * ctx.GlowIntensity) * ctx.AlphaFactor, center, hubR);
        canvas.DrawCircle((float)center.X, (float)center.Y, (float)hubR, hubPaint);

        DrawRadialWedges(canvas, ctx, center, inner, tipRadii, angles, span);
        DrawRadialRays(canvas, ctx, rays, Math.Max(0.6, (outer - inner) * 0.030), SKStrokeCap.Round, RadialHaloExtent(outer));
    }

    // ========================================================================
    // AudioRing - the NCS look (ported before DualRings so it exists)
    // ========================================================================
    private static void DrawAudioRing(SKCanvas canvas, FrameContext ctx)
    {
        SKRect plot = ctx.Plot;
        double outer = MaxRadius(plot);
        if (outer < 6.0)
            return;

        SKPoint center = PlotCenter(plot);

        AudioFrameData? frame = ctx.Current;
        float[]? bands = frame?.Bands;

        // The pulse - beat swell, backward-looking wrapped max over the frames array.
        double pulse = 0.0;
        for (int k = 0; k <= AudioRingPulseTailFrames; k++)
        {
            AudioFrameData? back = FrameAt(ctx.Frames, ctx.Index - k);
            if (back?.Onset != true)
                continue;

            double level = k == 0
                ? AudioRingPulseAttackLevel
                : Math.Pow(AudioRingPulseDecayPerFrame, k - 1);
            pulse = Math.Max(pulse, level);
        }

        // Whole-figure breathing: ring and sphere scaled by the same factor.
        double bass = Math.Clamp((ctx.Current?.Bass ?? 0f) * ctx.Sensitivity, 0.0, 1.0);
        double breathe = 1.0
            + (AudioRingBassBreatheFraction * bass)
            + (MidSwellFraction * ctx.MidLevel)
            + (VoiceSwellFraction * ctx.VoiceLevel)
            + (AudioRingPulseKickFraction * pulse);

        double baseRadius = outer * AudioRingBaseFraction * breathe;
        double amplitude = (outer - baseRadius) * AudioRingAmplitudeFraction;

        int columns = Math.Min(frame?.WaveMin?.Length ?? 0, frame?.WaveMax?.Length ?? 0);

        // --- the ring itself ---
        var ringPath = ctx.GetScratchPath();
        const int RingSteps = 240;
        var ringValues = ctx.GetScratchDoubleArray(RingSteps);
        for (int s = 0; s < RingSteps; s++)
        {
            double v = 0.0;
            if (columns > 0)
            {
                double start = s / (double)RingSteps * columns;
                double end = (s + 1) / (double)RingSteps * columns;
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
                    v = Math.Abs((hi - lo) * 0.5) * ctx.Sensitivity;
            }

            if (bands is { Length: > 0 })
            {
                int bandIndex = (int)Math.Clamp(
                    Math.Floor((s / (double)RingSteps) * bands.Length), 0, bands.Length - 1);
                double bandLevel = Math.Clamp(bands[bandIndex] * ctx.Sensitivity, 0.0, 1.0);
                v = (0.35 * bandLevel) + (0.65 * v);
            }

            ringValues[s] = v;
        }

        // Circular moving average.
        const int RingSmoothHalfWindow = 8;
        for (int s = 0; s < RingSteps; s++)
        {
            double sum = 0.0;
            for (int k = -RingSmoothHalfWindow; k <= RingSmoothHalfWindow; k++)
            {
                int idx = s + k;
                idx %= RingSteps;
                if (idx < 0)
                    idx += RingSteps;
                sum += ringValues[idx];
            }
            ringValues[s] = sum / ((2 * RingSmoothHalfWindow) + 1);
        }

        SKPoint first = PointOnCircle(center, baseRadius, 0);
        ringPath.MoveTo(first);
        for (int s = 1; s <= RingSteps; s++)
        {
            double angle = 360.0 * s / RingSteps;
            double radius = Math.Clamp(
                baseRadius + (Math.Clamp(ringValues[s % RingSteps], 0.0, 1.0) * amplitude),
                1.0, outer);
            ringPath.LineTo(PointOnCircle(center, radius, angle));
        }
        ringPath.Close();

        double ringThickness = Math.Max(1.5, outer * AudioRingThicknessFraction);
        DrawGlowPasses(canvas, ringPath, ringThickness, ctx.Resolved, ctx.GlowIntensity,
            ctx.AlphaFactor, SKStrokeCap.Round,
            Math.Max(RadialHaloExtent(outer), outer * AudioRingHaloFraction * (1.0 + (0.25 * pulse))),
            alphaScale: 1.0 + (0.35 * pulse));

        // --- the particle sphere inside the ring ---
        if (bands == null || bands.Length == 0)
            return;

        double sphereR = outer * AudioRingSphereRadiusFraction * breathe;
        if (sphereR < 8.0)
            return;

        double ringPitch = Math.Max(6.0, outer * AudioRingRingPitchFraction);
        double maxCoreRadius = Math.Max(1.0, ringPitch * AudioRingDotRadiusPitchRatio);

        int ringCount = Math.Clamp(
            (int)Math.Floor((2.0 * sphereR * Math.Sin(AudioRingLatitudeMaxDegrees * Math.PI / 180.0)) / ringPitch) + 1,
            6, 40);

        double yawRad = (ctx.Time * AudioRingParticleSpinDegrees) * Math.PI / 180.0;
        double tiltRad = AudioRingSphereTiltDegrees * Math.PI / 180.0;
        double cosTilt = Math.Cos(tiltRad);
        double sinTilt = Math.Sin(tiltRad);

        // Bucketed scratch for halo+core discs.
        var haloGeometries = ctx.GetScratchSKPathArray(AudioRingDotBuckets);
        var coreGeometries = ctx.GetScratchSKPathArray(AudioRingDotBuckets);
        for (int b = 0; b < AudioRingDotBuckets; b++)
        {
            haloGeometries[b] = ctx.GetScratchPath();
            coreGeometries[b] = ctx.GetScratchPath();
        }
        var haloAlphas = ctx.GetScratchDoubleArray(AudioRingDotBuckets);
        var coreAlphas = ctx.GetScratchDoubleArray(AudioRingDotBuckets);
        var used = ctx.GetScratchBoolArray(AudioRingDotBuckets);
        Array.Clear(used, 0, AudioRingDotBuckets);
        var haloRadii = ctx.GetScratchDoubleArray(AudioRingDotBuckets);

        for (int b = 0; b < AudioRingDotBuckets; b++)
        {
            double q = b / (double)(AudioRingDotBuckets - 1);
            haloRadii[b] = Math.Max(0.70, maxCoreRadius * (0.50 + (0.50 * q)));
            coreAlphas[b] = Math.Clamp(ctx.AlphaFactor * (0.45 + (0.55 * q)), 0.0, 1.0);
            haloAlphas[b] = Math.Clamp(ctx.GlowIntensity * AudioRingHaloAlphaRatio * (0.55 + (0.45 * q)), 0.0, 1.0);
        }

        try
        {
            for (int latticeRing = 0; latticeRing < ringCount; latticeRing++)
            {
                double t = ringCount == 1 ? 0.5 : latticeRing / (double)(ringCount - 1);
                double latRad = (-AudioRingLatitudeMaxDegrees + (t * 2.0 * AudioRingLatitudeMaxDegrees)) * Math.PI / 180.0;
                double cosLat = Math.Cos(latRad);
                double sinLat = Math.Sin(latRad);

                double ringEnergy = ctx.SampleBandRange(latticeRing, ringCount, 0, int.MaxValue, true);
                ringEnergy = Math.Clamp(ringEnergy * (1.0 + (0.18 * Math.Cos(2.0 * latRad))), 0.0, 1.0);

                int dotsOnRing = Math.Max(6, (int)Math.Round((2.0 * Math.PI * sphereR * Math.Max(0.12, cosLat)) / ringPitch));

                for (int d = 0; d < dotsOnRing; d++)
                {
                    double lon = yawRad + ((2.0 * Math.PI * d) / dotsOnRing);
                    double x = cosLat * Math.Cos(lon);
                    double y0 = sinLat;
                    double z0 = cosLat * Math.Sin(lon);

                    double y1 = (y0 * cosTilt) - (z0 * sinTilt);
                    double z1 = (y0 * sinTilt) + (z0 * cosTilt);

                    double persp = 1.0 / (1.0 - (AudioRingSpherePerspective * z1));
                    double px = center.X + (x * sphereR * persp);
                    double py = center.Y - (y1 * sphereR * persp);

                    double jitter = AudioRingJitterFloor +
                        ((1.0 - AudioRingJitterFloor) * DotJitter(latticeRing, d));
                    double energy = Math.Clamp(ringEnergy * jitter, 0.0, 1.0);

                    double disp = energy * AudioRingDisplaceFraction * sphereR;
                    double dx = px - center.X;
                    double dy = py - center.Y;
                    double dist = Math.Sqrt((dx * dx) + (dy * dy));
                    if (dist > 0.5)
                    {
                        px += (dx / dist) * disp;
                        py += (dy / dist) * disp;
                    }

                    double depth = Math.Clamp((z1 + 1.0) * 0.5, 0.0, 1.0);
                    double q = Math.Clamp(
                        energy * (AudioRingBackDimFloor + ((1.0 - AudioRingBackDimFloor) * depth)),
                        0.0, 1.0);

                    int bucket = (int)Math.Round(q * (AudioRingDotBuckets - 1));
                    if (bucket < 0)
                        bucket = 0;
                    else if (bucket > AudioRingDotBuckets - 1)
                        bucket = AudioRingDotBuckets - 1;

                    if (coreAlphas[bucket] <= 0.004 && haloAlphas[bucket] <= 0.004)
                        continue;

                    double coreRadius = haloRadii[bucket];
                    SKPoint position = new((float)px, (float)py);
                    if (coreAlphas[bucket] > 0.004)
                        AddEllipseFigure(coreGeometries[bucket], position, coreRadius, coreRadius);
                    if (haloAlphas[bucket] > 0.004)
                        AddEllipseFigure(haloGeometries[bucket], position,
                            coreRadius * AudioRingHaloToCoreRatio, coreRadius * AudioRingHaloToCoreRatio);
                    used[bucket] = true;
                }
            }

            // All halo discs first, then all core discs.
            for (int b = 0; b < AudioRingDotBuckets; b++)
            {
                if (!used[b] || haloAlphas[b] <= 0.004)
                    continue;
                canvas.DrawPath(haloGeometries[b],
                    FillPaint(WithAlpha(ctx.Resolved.Glow, haloAlphas[b])));
            }

            for (int b = 0; b < AudioRingDotBuckets; b++)
            {
                if (!used[b] || coreAlphas[b] <= 0.004)
                    continue;
                canvas.DrawPath(coreGeometries[b],
                    FillPaint(WithAlpha(ctx.Resolved.Core, coreAlphas[b])));
            }

            // Ambient dust field.
            const int DustGrains = 26;
            double dustSpin = ctx.Time * 360.0;
            double dustRadius = Math.Clamp(outer * 0.006, 1.0, 3.0);
            double dustPen = Math.Max(1.2, dustRadius * 1.2);
            var dustPath = ctx.GetScratchPath();
            for (int grain = 0; grain < DustGrains; grain++)
            {
                double slot0 = DotJitter(grain, 977);
                double slot1 = DotJitter(grain, 131);
                double slot2 = DotJitter(grain, 733);

                double radius = baseRadius * (1.06 + (0.55 * slot0));
                if (radius > outer * 0.97)
                    radius = outer * 0.97;

                int turns = slot1 > 0.5 ? 2 : 1;
                double direction = (grain & 1) == 0 ? 1.0 : -1.0;
                double angle = (slot2 * 360.0) + (dustSpin * turns * direction);
                SKPoint position = PointOnCircle(center, radius, angle);
                AddEllipseFigure(dustPath, position, dustRadius, dustRadius);
            }
            double dustAlpha = Math.Min(1.0, 0.42 * ctx.AlphaFactor * (1.0 + (0.35 * pulse)));
            canvas.DrawPath(dustPath, StrokePaint(WithAlpha(ctx.Resolved.Glow, dustAlpha), dustPen, SKStrokeCap.Round));
        }
        finally
        {
            for (int b = 0; b < AudioRingDotBuckets; b++)
            {
                haloGeometries[b]?.Dispose();
                coreGeometries[b]?.Dispose();
            }
        }
    }

    // ========================================================================
    // 7. DualRings
    // ========================================================================
    private static void DrawDualRings(SKCanvas canvas, FrameContext ctx)
    {
        SKRect plot = ctx.Plot;
        double outer = MaxRadius(plot);
        if (outer < 6.0)
            return;

        SKPoint center = PlotCenter(plot);
        int ringCount = Math.Clamp(ctx.Settings.RingCount, 2, 6);

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

        DrawAmbientBloom(canvas, ctx, center, outer);

        var rawRingLevels = ctx.GetScratchDoubleArray(barCount);
        var rays = ctx.GetScratchRaySpecList();
        double beat = RadialBeatScale(ctx);

        for (int ring = 0; ring < ringCount; ring++)
        {
            double ringOuter = outer - (ring * stride);
            double ringInner = ringOuter - ringThickness;
            if (ringInner < hub)
                ringInner = hub;
            if (ringOuter <= ringInner)
            {
                ringOuter = outer;
                ringInner = Math.Max(hub, outer - Math.Max(1.0, ringThickness));
                if (ringOuter <= ringInner)
                    continue;
            }

            double direction = (ring % 2 == 0) ? 1.0 : -1.0;
            double turns = ringCount <= 4 ? 2.0 : 1.0;
            double rotation = ctx.Time * 360.0 * direction * turns;

            var tipRadii = ctx.GetScratchDoubleArray(barCount);
            var angles = ctx.GetScratchDoubleArray(barCount);

            for (int i = 0; i < barCount; i++)
                rawRingLevels[i] = ctx.SampleBandRange(i, barCount, ring * slice, slice, true);

            double[] levels = ResolveRadialLevels(ctx, rawRingLevels);
            for (int i = 0; i < levels.Length; i++)
                levels[i] = Math.Min(1.0, levels[i] * beat);
            rays.Clear();

            FillRadialBarSet(levels, center, ringInner, ringOuter, rotation, span, tipRadii, angles, rays);

            int ringColorShift = (int)Math.Round(36.0 * ring / Math.Max(1, ringCount - 1));

            var ringBasePath = ctx.GetScratchPath();
            ringBasePath.AddCircle((float)center.X, (float)center.Y, (float)ringInner);
            DrawGlowPasses(canvas, ringBasePath, Math.Max(0.7, stride * 0.030), ctx.Resolved,
                ctx.GlowIntensity * 0.5, ctx.AlphaFactor, SKStrokeCap.Round,
                Math.Max(2.0, stride * 0.12));

            DrawRadialWedges(canvas, ctx, center, ringInner, tipRadii, angles, span, ringColorShift);

            double slotArc = (2.0 * Math.PI * Math.Max(1.0, (ringInner + ringOuter) * 0.5)) / Math.Max(1, barCount);
            double rayThickness = Math.Clamp(ringThickness * 0.28, 0.8, Math.Max(0.8, slotArc * 0.55));
            DrawRadialRays(canvas, ctx, rays, rayThickness, SKStrokeCap.Round, maxHalo);
        }
    }

    // ========================================================================
    // 8. RippleRings
    // ========================================================================
    private static void DrawRippleRings(SKCanvas canvas, FrameContext ctx)
    {
        SKRect plot = ctx.Plot;
        double outer = MaxRadius(plot);
        if (outer < 4.0)
            return;

        SKPoint center = PlotCenter(plot);
        double inner = outer * RippleInnerRadiusFraction;
        double thickness = Math.Max(1.5, outer * RippleThicknessFraction);
        int lifetime = Math.Max(6, ctx.FrameCount / 2);
        int maxLive = Math.Clamp(ctx.Settings.RingCount, 1, MaxRipples);

        if (ctx.Current?.Onset == true)
        {
            float energy = Math.Clamp((ctx.Current?.Bass ?? 0f) * ctx.Sensitivity, 0.35f, 1f);
            ctx.State?.NoteOnset(ctx.Index, maxLive, energy);
        }

        ctx.State?.ExpireRipples(ctx.Index, lifetime);

        var ringRadii = ctx.GetScratchDoubleList();
        var ringAlpha = ctx.GetScratchDoubleList();

        if (ctx.State != null)
        {
            foreach (SoundBarRenderState.Ripple ripple in ctx.State.Ripples)
            {
                int age = ctx.Index - ripple.SpawnFrame;
                if (age < 0)
                    continue;

                double t = Math.Clamp(age / (double)lifetime, 0.0, 1.0);
                double eased = 1.0 - Math.Pow(1.0 - t, 3.0);
                double voiceReach = eased * (1.0 + (VoiceRippleReachFraction * ctx.VoiceLevel));
                double radius = inner + ((outer - inner) * voiceReach);
                double fade = Math.Pow(1.0 - t, 1.6) * (0.35 + (0.65 * ripple.Energy));

                if (fade <= 0.05)
                    continue;

                ringRadii.Add(radius);
                ringAlpha.Add(Math.Clamp(fade, 0.0, 1.0));
            }
        }

        bool hasLiveRipple = ringRadii.Count > 0;

        if (!hasLiveRipple)
        {
            for (int i = 0; i < maxLive; i++)
            {
                double t = i / (double)maxLive;
                ringRadii.Add(inner + ((outer - inner) * t));
                ringAlpha.Add(0.30 * (1.0 - t));
            }
        }

        DrawAmbientBloom(canvas, ctx, center, outer);

        var pondEdgePath = ctx.GetScratchPath();
        pondEdgePath.AddCircle((float)center.X, (float)center.Y, (float)outer);
        DrawGlowPasses(canvas, pondEdgePath, Math.Max(0.8, outer * 0.006), ctx.Resolved,
            ctx.GlowIntensity * 0.30, ctx.AlphaFactor * 0.55, SKStrokeCap.Round,
            Math.Max(2.0, outer * 0.03));

        for (int i = 0; i < ringRadii.Count; i++)
        {
            double radius = Math.Clamp(ringRadii[i], 0.5, outer);

            double echoRadius = radius - (outer * 0.07);
            if (echoRadius > inner * 0.5 && ringAlpha[i] > 0.08)
            {
                var echoPath = ctx.GetScratchPath();
                echoPath.AddCircle((float)center.X, (float)center.Y, (float)echoRadius);
                DrawGlowPasses(canvas, echoPath, thickness * 0.7, ctx.Resolved, ctx.GlowIntensity, ctx.AlphaFactor,
                    SKStrokeCap.Round, RadialHaloExtent(outer),
                    coreAlways: true, alphaScale: ringAlpha[i] * 0.40, coreColor: ctx.Resolved.Accent);
            }

            var ellipsePath = ctx.GetScratchPath();
            ellipsePath.AddCircle((float)center.X, (float)center.Y, (float)radius);
            DrawGlowPasses(canvas, ellipsePath, thickness, ctx.Resolved, ctx.GlowIntensity, ctx.AlphaFactor,
                SKStrokeCap.Round, RadialHaloExtent(outer),
                coreAlways: true, alphaScale: ringAlpha[i], coreColor: ctx.Resolved.Accent);
        }

        double leadRadius = ringRadii[ringRadii.Count - 1];
        double leadAlpha = ringAlpha[ringAlpha.Count - 1];
        if (hasLiveRipple && leadAlpha > 0.02)
        {
            // Cache sở hữu lifetime → không dùng using.
            var bloomPaint = CreateRadialGlowPaint(ctx.Resolved.Glow,
                0.55 * leadAlpha * ctx.AlphaFactor, center, Math.Max(2.0, leadRadius * 1.6));
            canvas.DrawCircle((float)center.X, (float)center.Y, (float)Math.Max(2.0, leadRadius * 1.6), bloomPaint);
        }
    }

    // ========================================================================
    // 9. SpokeLines
    // ========================================================================
    private static void DrawSpokeLines(SKCanvas canvas, FrameContext ctx)
    {
        SKRect plot = ctx.Plot;
        double outer = MaxRadius(plot);
        if (outer < 4.0)
            return;

        SKPoint center = PlotCenter(plot);
        double inner = outer * 0.14;
        int barCount = ctx.BarCount;
        double span = 360.0 / barCount;
        double rotation = ctx.Time * -360.0;

        var rawLevels = ctx.GetScratchDoubleArray(barCount);
        for (int i = 0; i < barCount; i++)
            rawLevels[i] = ctx.SampleBand(i, barCount, true);

        double[] levels = ResolveRadialLevels(ctx, rawLevels);
        double beat = RadialBeatScale(ctx);
        for (int i = 0; i < levels.Length; i++)
            levels[i] = Math.Min(1.0, levels[i] * beat);

        var rays = ctx.GetScratchRaySpecList();
        FillRadialBarSet(levels, center, inner, outer, rotation, span, null, null, rays);

        DrawAmbientBloom(canvas, ctx, center, outer);

        var spokeBaselinePath = ctx.GetScratchPath();
        spokeBaselinePath.AddCircle((float)center.X, (float)center.Y, (float)inner);
        DrawGlowPasses(canvas, spokeBaselinePath, Math.Max(0.8, (outer - inner) * 0.012), ctx.Resolved,
            ctx.GlowIntensity * 0.6, ctx.AlphaFactor, SKStrokeCap.Round,
            Math.Max(2.0, (outer - inner) * 0.05));

        double hubR = Math.Max(2.0, inner * 1.1 * beat);
        // Cache sở hữu lifetime → không dùng using.
        var hubPaint = CreateRadialGlowPaint(ctx.Resolved.Glow,
            Math.Min(1.0, 0.40 * ctx.GlowIntensity) * ctx.AlphaFactor, center, hubR);
        canvas.DrawCircle((float)center.X, (float)center.Y, (float)hubR, hubPaint);

        double floorPx = ScaleFromHdReference(ctx, 2.0, 2.0, 8.0);
        double thickness = Math.Clamp((outer - inner) * 0.014, floorPx, floorPx * 3.0);

        DrawRadialRays(canvas, ctx, rays, thickness, SKStrokeCap.Butt, RadialHaloExtent(outer), coreAlways: true);

        // Tip dots.
        var tipsPath = ctx.GetScratchPath();
        for (int i = 0; i < rays.Count; i++)
            AddEllipseFigure(tipsPath, new SKPoint((float)rays[i].X1, (float)rays[i].Y1), thickness * 1.15, thickness * 1.15);
        canvas.DrawPath(tipsPath, FillPaint(WithAlpha(ctx.Resolved.Core, 0.85 * ctx.AlphaFactor)));
    }

    // ========================================================================
    // 10. WaveRibbon
    // ========================================================================
    private static void DrawWaveRibbon(SKCanvas canvas, FrameContext ctx)
    {
        SKRect plot = ctx.Plot;
        if (plot.Width < 4.0f || plot.Height < 4.0f)
            return;

        double midY = plot.Top + (plot.Height * 0.5);
        double ribbonBeat = 1.0
            + (0.05 * Math.Clamp((ctx.Current?.Bass ?? 0f) * ctx.Sensitivity, 0.0, 1.0))
            + (MidSwellFraction * ctx.MidLevel)
            + (VoiceSwellFraction * ctx.VoiceLevel)
            + (0.05 * ctx.OnsetPulse);
        double halfHeight = plot.Height * 0.48 * ribbonBeat;

        AudioFrameData? frame = ctx.Current;
        float[]? waveMin = frame?.WaveMin;
        float[]? waveMax = frame?.WaveMax;
        int available = Math.Min(waveMin?.Length ?? 0, waveMax?.Length ?? 0);

        int columns = Math.Clamp(ctx.BarCount * 2, 24, 512);
        var top = ctx.GetScratchSKPointArray(columns);
        var bottom = ctx.GetScratchSKPointArray(columns);
        double slot = plot.Width / columns;

        var magnitudes = ctx.GetScratchDoubleArray(columns);
        for (int i = 0; i < columns; i++)
        {
            float lo = 0f;
            float hi = 0f;
            if (available > 0)
            {
                int si = Math.Clamp((int)Math.Floor((i / (double)columns) * available), 0, available - 1);
                lo = Math.Clamp(waveMin![si], -1f, 1f);
                hi = Math.Clamp(waveMax![si], -1f, 1f);
            }
            magnitudes[i] = Math.Max(Math.Abs(lo), Math.Abs(hi));
        }

        double magnitudeSum = 0.0;
        for (int i = 0; i < columns; i++)
        {
            double m = magnitudes[i];
            if (i > 0)
                m = Math.Max(m, magnitudes[i - 1] * 0.55);
            if (i < columns - 1)
                m = Math.Max(m, magnitudes[i + 1] * 0.55);
            magnitudes[i] = m;
            magnitudeSum += m;

            double x = plot.Left + (slot * (i + 0.5));
            top[i] = new SKPoint((float)x, (float)(midY - (m * halfHeight)));
            bottom[i] = new SKPoint((float)x, (float)(midY + (m * halfHeight)));
        }

        var ribbonPath = ctx.GetScratchPath();
        ribbonPath.MoveTo(top[0]);
        for (int i = 1; i < columns; i++)
            ribbonPath.LineTo(top[i]);
        for (int i = columns - 1; i >= 0; i--)
            ribbonPath.LineTo(bottom[i]);
        ribbonPath.Close();

        double fillAlpha = Math.Min(1.0, 0.55 * ctx.AlphaFactor);
        SKColor glow = ctx.Resolved.Glow;
        SKColor rc0 = WithAlpha(glow, fillAlpha * 0.45f);
        SKColor rc1 = WithAlpha(glow, fillAlpha);
        SKColor rc2 = WithAlpha(glow, fillAlpha * 0.45f);
        var ribbonKey = LinearShaderKey(
            new SKPoint(0, (float)plot.Top),
            new SKPoint(0, (float)plot.Bottom),
            rc0, 0f, rc1, 0.5f, rc2, 1f, rc2, 1f, SKShaderTileMode.Clamp, 3);
        // Cache sở hữu lifetime → không dùng using.
        var fillPaint = GetShaderPaint(ribbonKey,
            GetLinearShader3(ribbonKey,
                new SKPoint(0, (float)plot.Top),
                new SKPoint(0, (float)plot.Bottom),
                rc0, 0f, rc1, 0.5f, rc2, 1f, SKShaderTileMode.Clamp));
        canvas.DrawPath(ribbonPath, fillPaint);

        double averageMagnitude = columns > 0 ? magnitudeSum / columns : 0.0;
        var seamPath = ctx.GetScratchPath();
        seamPath.MoveTo((float)plot.Left, (float)midY);
        seamPath.LineTo((float)plot.Right, (float)midY);
        canvas.DrawPath(seamPath, StrokePaint(WithAlpha(ctx.Resolved.Core,
            (0.22 + (0.55 * averageMagnitude)) * ctx.AlphaFactor),
            Math.Max(1.0, plot.Height * 0.010), SKStrokeCap.Round));

        double progress = Math.Clamp(ctx.Time, 0.0, 1.0);
        double headFade = Math.Clamp(Math.Min(progress / 0.06, (1.0 - progress) / 0.06), 0.0, 1.0);
        double headX = plot.Left + (plot.Width * progress);

        if (headFade > 0.01)
        {
            using var veilPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = new SKColor(0, 0, 0, (byte)Math.Round(0.16 * headFade * 255.0 * ctx.AlphaFactor))
            };
            canvas.DrawRect(new SKRect((float)headX, (float)plot.Top,
                (float)Math.Max(0.0, plot.Right - headX), (float)plot.Height), veilPaint);

            var headPath = ctx.GetScratchPath();
            headPath.MoveTo((float)headX, (float)plot.Top);
            headPath.LineTo((float)headX, (float)plot.Bottom);
            DrawGlowPasses(canvas, headPath, Math.Max(1.2, plot.Width * 0.0022), ctx.Resolved,
                ctx.GlowIntensity, ctx.AlphaFactor * headFade, SKStrokeCap.Round,
                Math.Max(3.0, plot.Width * 0.012));
        }

        // Ghost trail: previous frame's symmetric envelope.
        AudioFrameData? previous = ctx.Previous;
        float[]? prevMin = previous?.WaveMin;
        float[]? prevMax = previous?.WaveMax;
        int prevAvailable = Math.Min(prevMin?.Length ?? 0, prevMax?.Length ?? 0);
        if (prevAvailable > 0)
        {
            var ghostPath = ctx.GetScratchPath();
            double m0 = Math.Max(Math.Abs(Math.Clamp(prevMin![0], -1f, 1f)), Math.Abs(Math.Clamp(prevMax![0], -1f, 1f)));
            ghostPath.MoveTo((float)(plot.Left + (slot * 0.5)), (float)(midY - (m0 * halfHeight)));
            for (int i = 1; i < columns; i++)
            {
                int si = Math.Clamp((int)Math.Floor((i / (double)columns) * prevAvailable), 0, prevAvailable - 1);
                double m = Math.Max(Math.Abs(Math.Clamp(prevMax![si], -1f, 1f)), Math.Abs(Math.Clamp(prevMin![si], -1f, 1f)));
                ghostPath.LineTo((float)(plot.Left + (slot * (i + 0.5))), (float)(midY - (m * halfHeight)));
            }
            ghostPath.MoveTo((float)(plot.Left + (slot * 0.5)), (float)(midY + (m0 * halfHeight)));
            for (int i = 1; i < columns; i++)
            {
                int si = Math.Clamp((int)Math.Floor((i / (double)columns) * prevAvailable), 0, prevAvailable - 1);
                double m = Math.Max(Math.Abs(Math.Clamp(prevMax![si], -1f, 1f)), Math.Abs(Math.Clamp(prevMin![si], -1f, 1f)));
                ghostPath.LineTo((float)(plot.Left + (slot * (i + 0.5))), (float)(midY + (m * halfHeight)));
            }
            canvas.DrawPath(ghostPath, StrokePaint(WithAlpha(ctx.Resolved.Glow, 0.28 * ctx.AlphaFactor),
                Math.Max(0.5, plot.Height * 0.010), SKStrokeCap.Round));
        }

        var rimPath = ctx.GetScratchPath();
        rimPath.MoveTo(top[0]);
        for (int i = 1; i < columns; i++)
            rimPath.LineTo(top[i]);
        rimPath.MoveTo(bottom[0]);
        for (int i = 1; i < columns; i++)
            rimPath.LineTo(bottom[i]);
        DrawGlowPasses(canvas, rimPath, Math.Max(0.6, plot.Height * 0.012), ctx.Resolved, ctx.GlowIntensity, ctx.AlphaFactor, SKStrokeCap.Round);
    }

    // ========================================================================
    // 11. CenterColumn
    // ========================================================================
    private static void DrawCenterColumn(SKCanvas canvas, FrameContext ctx)
    {
        SKRect plot = ctx.Plot;
        if (plot.Width < 2.0f || plot.Height < 2.0f)
            return;

        double centerX = plot.Left + (plot.Width * 0.5);
        double thickness = Math.Clamp(plot.Height * 0.10, 2.0, Math.Max(2.0, plot.Width * 0.30));
        double rms = Math.Clamp(
            CenterColumnLevel(ctx.Current?.Rms ?? 0f) + (VoiceCenterColumnFraction * ctx.VoiceLevel),
            0.0, 1.0);

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
        double hold = CenterColumnLevel(rawHold);

        // Faint full-height track.
        var trackPath = ctx.GetScratchPath();
        trackPath.MoveTo((float)centerX, (float)plot.Bottom);
        trackPath.LineTo((float)centerX, (float)plot.Top);
        canvas.DrawPath(trackPath, StrokePaint(WithAlpha(ctx.Resolved.Glow, 0.14 * ctx.AlphaFactor),
            thickness * 0.45, SKStrokeCap.Round));

        // dB scale ticks.
        var tickMarksPath = ctx.GetScratchPath();
        double tickHalf = ClampSafe(thickness * 0.28, 2.0, Math.Max(2.0, plot.Width * 0.10));
        foreach (double db in new[] { -6.0, -12.0, -20.0 })
        {
            double t = (db - CenterColumnFloorDb) / (CenterColumnCeilDb - CenterColumnFloorDb);
            double h = CenterColumnMinFraction + ((1.0 - CenterColumnMinFraction) * Math.Clamp(t, 0.0, 1.0));
            double y = plot.Bottom - (h * plot.Height);
            tickMarksPath.MoveTo((float)(centerX - thickness * 0.85 - tickHalf), (float)y);
            tickMarksPath.LineTo((float)(centerX - thickness * 0.85 + tickHalf), (float)y);
        }
        canvas.DrawPath(tickMarksPath, StrokePaint(WithAlpha(ctx.Resolved.Core, 0.30 * ctx.AlphaFactor),
            Math.Max(0.8, thickness * 0.06), SKStrokeCap.Round));

        double columnReach = rms > 0.0 ? Math.Max(1.0, rms * plot.Height) : 0.0;
        if (columnReach > 0.0)
        {
            const int MeterSegments = 9;
            double slot = plot.Height / MeterSegments;
            double gap = Math.Max(1.5, slot * 0.18);
            double segmentHeight = Math.Max(2.0, slot - gap);
            double radius = Math.Min(thickness * 0.30, segmentHeight * 0.5);

            SKColor bc0 = WithAlpha(ctx.Resolved.Glow, 0.22f * (float)ctx.GlowIntensity * (float)ctx.AlphaFactor);
            SKColor bc1 = WithAlpha(ctx.Resolved.Glow, 0f);
            var blKey = LinearShaderKey(
                new SKPoint(0, (float)(plot.Bottom - columnReach)),
                new SKPoint(0, (float)plot.Bottom),
                bc0, 0f, bc1, 1f, bc1, 1f, bc1, 1f, SKShaderTileMode.Clamp, 2);
            var backlightPaint = GetShaderPaint(blKey,
                GetLinearShader2(blKey,
                    new SKPoint(0, (float)(plot.Bottom - columnReach)),
                    new SKPoint(0, (float)plot.Bottom),
                    bc0, 0f, bc1, 1f, SKShaderTileMode.Clamp));
            canvas.DrawRect(new SKRect((float)(centerX - thickness), (float)(plot.Bottom - columnReach),
                (float)(centerX + thickness), (float)plot.Bottom), backlightPaint);

            int topLit = -1;
            for (int k = 0; k < MeterSegments; k++)
            {
                double segmentTop = plot.Bottom - ((k + 1) * slot) + gap;
                if (plot.Bottom - segmentTop - gap > columnReach + slot * 0.25)
                    break;
                topLit = k;
            }

            for (int k = 0; k <= topLit; k++)
            {
                double y0 = plot.Bottom - ((k + 1) * slot) + gap;
                double mid = y0 + (segmentHeight * 0.5);
                double t = Math.Clamp((plot.Bottom - mid) / plot.Height, 0.0, 1.0);
                SKColor segment = SampleRamp(ctx.Resolved.RampOffsets, ctx.Resolved.RampColors, t);
                if (k == topLit)
                {
                    segment = new SKColor(
                        (byte)((segment.Red * 3 + ctx.Resolved.Core.Red * 7) / 10),
                        (byte)((segment.Green * 3 + ctx.Resolved.Core.Green * 7) / 10),
                        (byte)((segment.Blue * 3 + ctx.Resolved.Core.Blue * 7) / 10));
                }
                canvas.DrawRoundRect(new SKRect((float)(centerX - (thickness * 0.5)), (float)y0,
                    (float)(centerX + (thickness * 0.5)), (float)(y0 + segmentHeight)),
                    (float)radius, (float)radius, FillPaint(WithAlpha(segment, 0.92 * ctx.AlphaFactor)));
            }
        }

        // Base light pool.
        double poolRadius = Math.Max(6.0, thickness * (1.6 + (1.8 * rms)));
        // Cache sở hữu lifetime → không dùng using.
        var poolPaint = CreateRadialGlowPaint(ctx.Resolved.Glow,
            Math.Min(1.0, 0.20 * ctx.GlowIntensity) * ctx.AlphaFactor,
            new SKPoint((float)centerX, (float)plot.Bottom), poolRadius);
        canvas.DrawOval(new SKRect((float)(centerX - poolRadius), (float)(plot.Bottom - poolRadius * 0.6),
            (float)(centerX + poolRadius), (float)(plot.Bottom + poolRadius * 0.6)), poolPaint);

        double tickStroke = Math.Max(0.8, thickness * 0.10);
        double tickY = plot.Bottom - (hold * plot.Height);
        double tickCeiling = plot.Bottom - columnReach - (tickStroke * 3.0) - 2.0;
        tickY = Math.Min(tickY, tickCeiling);

        if (hold > 0.001 && tickY > plot.Top + 1.0)
        {
            double tickHalf2 = ClampSafe(thickness * 0.62, 3.0, Math.Max(3.0, plot.Width * 0.22));
            var tickPath = ctx.GetScratchPath();
            tickPath.MoveTo((float)(centerX - tickHalf2), (float)tickY);
            tickPath.LineTo((float)(centerX + tickHalf2), (float)tickY);
            DrawGlowPasses(canvas, tickPath, tickStroke, ctx.Resolved, ctx.GlowIntensity, ctx.AlphaFactor,
                SKStrokeCap.Butt, tickStroke * 3.0);

            // Cache sở hữu lifetime → không dùng using.
            var dotBrush = FillPaint(WithAlpha(ctx.Resolved.Accent, 0.95 * ctx.AlphaFactor));
            canvas.DrawCircle((float)(centerX + tickHalf2), (float)tickY, (float)tickStroke, dotBrush);
        }
    }

    // ========================================================================
    // 12. PeakStars
    // ========================================================================
    private static void DrawPeakStars(SKCanvas canvas, FrameContext ctx)
    {
        SKRect plot = ctx.Plot;
        if (plot.Width < 4.0f || plot.Height < 4.0f)
            return;

        float[]? bands = ctx.Current?.Bands;
        if (bands == null || bands.Length < 3)
            return;

        SoundBarRenderState? state = ctx.State;
        int lifetime = Math.Max(6, ctx.FrameCount / 2);

        var tilted = ctx.GetScratchFloatArray(bands.Length);
        for (int i = 0; i < bands.Length; i++)
            tilted[i] = ctx.TiltBand(bands[i], i, bands.Length);

        var candidates = ctx.GetScratchPeakBandList();
        CollectPeakBands(ctx, tilted, ctx.BarCount, candidates);

        if (state != null)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                (int index, float peak) = candidates[i];
                double x = plot.Left + (plot.Width * (index + 0.5) / tilted.Length);
                double y = plot.Bottom - (Math.Clamp(peak, 0f, 1f) * plot.Height);
                state.TrackStar(index, ctx.Index, peak, (float)x, (float)y);
            }
            state.ExpireStars(ctx.Index, lifetime);
        }

        var live = ctx.GetScratchStarList();
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
            for (int i = 0; i < candidates.Count; i++)
            {
                (int index, float peak) = candidates[i];
                double x = plot.Left + (plot.Width * (index + 0.5) / tilted.Length);
                double y = plot.Bottom - (Math.Clamp(peak, 0f, 1f) * plot.Height);
                live.Add(new SoundBarRenderState.Star(index, ctx.Index, peak, (float)x, (float)y));
            }
        }

        double starFlash = 1.0
            + (0.45 * ctx.OnsetPulse)
            + (VoiceStarFlashFraction * ctx.VoiceLevel);

        double maxStarRadius = Math.Max(2.0, plot.Height * 0.07);
        SKPaint starBrush = FillPaint(WithAlpha(ctx.Resolved.Core, Math.Min(1.0, ctx.AlphaFactor * starFlash)));

        for (int i = 0; i < live.Count; i++)
        {
            SoundBarRenderState.Star star = live[i];
            double age = state == null ? 0.0 : Math.Clamp((ctx.Index - star.SpawnFrame) / (double)lifetime, 0.0, 1.0);
            double fade = Math.Pow(1.0 - age, 1.6);
            if (fade <= 0.01)
                continue;

            double x = Math.Clamp(star.X, plot.Left, plot.Right);
            double y = Math.Clamp(star.Y, plot.Top, plot.Bottom);
            double radius = maxStarRadius * (0.45 + (0.55 * star.Peak)) * (0.65 + (0.35 * fade));
            SKPoint center = new((float)x, (float)y);

            // Cache sở hữu lifetime → không dùng using.
            var glowPaint = CreateRadialGlowPaint(
                star.Peak > 0.66f ? ctx.Resolved.Accent : ctx.Resolved.Glow,
                Math.Min(1.0, 0.85 * fade * ctx.AlphaFactor * starFlash),
                center, Math.Max(2.0, radius * 2.6));
            canvas.DrawCircle((float)x, (float)y, (float)Math.Max(2.0, radius * 2.6), glowPaint);

            double twinkle = 0.86 + (0.14 * Math.Sin(age * Math.PI * 2.0 * 1.5));
            double rotation = (star.BandIndex % 2 == 0) ? 0.0 : 45.0;
            SKPath sparkle = ctx.GetScratchPath();
            BuildSparkleGeometry(sparkle, center, radius * twinkle, rotation);
            canvas.DrawPath(sparkle, starBrush);
        }
    }

    // ========================================================================
    // Shared drawing helpers
    // ========================================================================

    private static void DrawColumns(SKCanvas canvas, FrameContext ctx, List<ColumnSpec> columns,
        double glowThickness, SKStrokeCap cap)
    {
        if (columns.Count == 0)
            return;

        var buckets = ctx.GetScratchColumnBuckets();
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnSpec column = columns[i];
            var key = ((int)Math.Round(Math.Min(column.Height, 4096.0)), column.TipY >= column.BaseY);
            if (!buckets.TryGetValue(key, out List<ColumnSpec>? bucket))
            {
                bucket = ctx.GetScratchBucketList();
                buckets.Add(key, bucket);
            }
            bucket.Add(column);
        }

        double roundness = Math.Clamp(ctx.Settings.Roundness, 0.0, 1.0);
        foreach (var entry in buckets)
        {
            List<ColumnSpec> bucket = entry.Value;
            if (bucket.Count == 0)
                continue;
            ColumnSpec first = bucket[0];
            double height = entry.Key.Height;
            double tipY = entry.Key.Downward ? first.BaseY + height : first.BaseY - height;
            SKPath body = ctx.GetScratchPath();
            BuildColumnGeometry(body, bucket, roundness);
            canvas.DrawPath(body, GetColumnPaint(ctx, first.BaseY, tipY, (int)ctx.Alpha));
        }

        SKPath lines = ctx.GetScratchPath();
        BuildLineGeometry(lines, columns);
        DrawGlowPasses(canvas, lines, glowThickness, ctx.Resolved, ctx.GlowIntensity, ctx.AlphaFactor, cap,
            coreWidthRatio: GlowColumnCoreWidthRatio);
    }

    private static double MinimumReach(double span)
        => ClampSafe(span * MinimumReachFraction, 0.0, 1.0);

    private static void DrawPeakCaps(SKCanvas canvas, FrameContext ctx,
        List<(double X, double Y, double Width)> caps, double strokeThickness)
    {
        if (caps.Count == 0)
            return;

        // Cache sở hữu lifetime → không dùng using.
        var geometry = ctx.GetScratchPath();
        for (int i = 0; i < caps.Count; i++)
        {
            (double x, double y, double width) = caps[i];
            geometry.MoveTo((float)(x - (width * 0.5)), (float)y);
            geometry.LineTo((float)(x + (width * 0.5)), (float)y);
        }

        double capStroke = Math.Max(1.2, strokeThickness);

        if (ctx.GlowIntensity > 0.002)
        {
            var capHalo = StrokePaint(WithAlpha(ctx.Resolved.Glow, 0.28 * ctx.GlowIntensity * ctx.AlphaFactor),
                capStroke * 2.4, SKStrokeCap.Round);
            canvas.DrawPath(geometry, capHalo);
        }

        var capPen = StrokePaint(WithAlpha(ctx.Resolved.Core, 0.85 * ctx.AlphaFactor),
            capStroke, SKStrokeCap.Round);
        canvas.DrawPath(geometry, capPen);
    }

    private static void DrawBaselineLine(SKCanvas canvas, FrameContext ctx, double y, SKRect plot)
    {
        // Cache sở hữu lifetime → không dùng using.
        var baseline = ctx.GetScratchPath();
        baseline.MoveTo((float)plot.Left, (float)y);
        baseline.LineTo((float)plot.Right, (float)y);
        DrawGlowPasses(canvas, baseline, Math.Max(1.0, plot.Height * 0.008), ctx.Resolved,
            ctx.GlowIntensity, ctx.AlphaFactor * 0.75, SKStrokeCap.Round,
            Math.Max(2.0, plot.Height * 0.03));
    }

    private static void DrawFloorSpill(SKCanvas canvas, FrameContext ctx,
        double baselineY, double plotWidth, double plotLeft)
    {
        if (ctx.GlowIntensity <= 0.002 || ctx.AlphaFactor <= 0.002)
            return;

        double stripHeight = Math.Max(4.0, ctx.Plot.Height * 0.055);
        double available = ctx.Height - baselineY;
        if (available < 2.0)
            return;
        stripHeight = Math.Min(stripHeight, available);

        double punch = ctx.OnsetPulse;
        double alpha = Math.Min(1.0, 0.15 * ctx.GlowIntensity * (1.0 + (0.6 * punch))) * ctx.AlphaFactor;
        SKColor glow = ctx.Resolved.Glow;
        SKColor sc0 = WithAlpha(glow, (float)alpha);
        SKColor sc1 = WithAlpha(glow, (float)(alpha * 0.35));
        SKColor sc2 = SKColors.Transparent;
        var spillKey = LinearShaderKey(
            new SKPoint(0, (float)baselineY),
            new SKPoint(0, (float)(baselineY + stripHeight)),
            sc0, 0f, sc1, 0.45f, sc2, 1f, sc2, 1f, SKShaderTileMode.Clamp, 3);
        var brushPaint = GetShaderPaint(spillKey,
            GetLinearShader3(spillKey,
                new SKPoint(0, (float)baselineY),
                new SKPoint(0, (float)(baselineY + stripHeight)),
                sc0, 0f, sc1, 0.45f, sc2, 1f, SKShaderTileMode.Clamp));
        canvas.DrawRect(new SKRect((float)plotLeft, (float)baselineY,
            (float)(plotLeft + plotWidth), (float)(baselineY + stripHeight)), brushPaint);
    }

    private static void DrawGlossyReflection(SKCanvas canvas, FrameContext ctx,
        List<ColumnSpec> columns, double baseY)
    {
        if (ctx.AlphaFactor <= 0.002)
            return;

        double maxReflection = Math.Max(6.0, ctx.Plot.Height * 0.22);
        var reflection = ctx.GetScratchPath();
        bool any = false;
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnSpec column = columns[i];
            double height = Math.Min(column.Height, maxReflection);
            if (height < 3.0)
                continue;
            double left = column.CenterX - (column.Width * 0.5);
            double top = baseY + 1.5;
            double bottom = top + (height * 0.45);
            reflection.MoveTo((float)left, (float)top);
            reflection.LineTo((float)(left + column.Width), (float)top);
            reflection.LineTo((float)(left + column.Width), (float)bottom);
            reflection.LineTo((float)left, (float)bottom);
            reflection.Close();
            any = true;
        }
        if (!any)
            return;

        double fade = Math.Min(1.0, 0.20 * ctx.AlphaFactor);
        SKColor glow = ctx.Resolved.Glow;
        SKColor gc0 = WithAlpha(glow, (float)fade);
        SKColor gc1 = SKColors.Transparent;
        var glossyKey = LinearShaderKey(
            new SKPoint(0, (float)baseY),
            new SKPoint(0, (float)(baseY + (maxReflection * 0.45))),
            gc0, 0f, gc1, 1f, gc1, 1f, gc1, 1f, SKShaderTileMode.Clamp, 2);
        var brushPaint = GetShaderPaint(glossyKey,
            GetLinearShader2(glossyKey,
                new SKPoint(0, (float)baseY),
                new SKPoint(0, (float)(baseY + (maxReflection * 0.45))),
                gc0, 0f, gc1, 1f, SKShaderTileMode.Clamp));
        canvas.DrawPath(reflection, brushPaint);
    }

    private static void DrawAmbientBloom(SKCanvas canvas, FrameContext ctx, SKPoint center, double outer)
    {
        if (ctx.GlowIntensity <= 0.002 || ctx.AlphaFactor <= 0.002)
            return;

        double bass = Math.Clamp((ctx.Current?.Bass ?? 0f) * ctx.Sensitivity, 0.0, 1.0);
        double punch = ctx.OnsetPulse;
        double breathe = 1.0 + (0.04 * bass) + (0.03 * punch);
        double radius = Math.Max(4.0, outer * 1.05 * breathe);
        double alpha = Math.Min(1.0, 0.085 * ctx.GlowIntensity * (1.0 + (0.5 * punch))) * ctx.AlphaFactor;

        // Cache sở hữu lifetime → không dùng using.
        var bloomPaint = CreateRadialGlowPaint(ctx.Resolved.Glow, (float)alpha, center, radius);
        canvas.DrawCircle((float)center.X, (float)center.Y, (float)radius, bloomPaint);
    }

    private static void DrawGlowPasses(SKCanvas canvas, SKPath geometry, double thickness,
        ResolvedPalette resolved, double intensity, double alphaFactor, SKStrokeCap cap,
        double maxHaloThickness = double.PositiveInfinity,
        bool coreAlways = false, double alphaScale = 1.0, SKColor? coreColor = null,
        double coreWidthRatio = 1.0)
    {
        if (alphaFactor <= 0.002)
            return;

        SKColor core = coreColor ?? resolved.Core;
        double fade = Math.Clamp(alphaScale, 0.0, 1.0);

        if (intensity <= 0.002)
        {
            if (coreAlways)
                canvas.DrawPath(geometry, StrokePaint(WithAlpha(core, alphaFactor * fade),
                    Math.Max(0.25, thickness), cap));
            return;
        }

        double t = Math.Max(0.25, thickness);
        double halo = t * GlowWideRatio;
        double scale = halo > maxHaloThickness ? (maxHaloThickness / halo) : 1.0;

        double ceiling = Math.Max(t * GlowMinWideRatio, maxHaloThickness);
        double wideThickness = Math.Max(t * GlowMinWideRatio, Math.Min(halo * scale, ceiling));
        double midThickness = Math.Min(wideThickness, Math.Max(t * GlowMinMidRatio, Math.Min((t * GlowMidRatio) * scale, ceiling)));

        // Cache sở hữu lifetime (giải phóng ở EndFrame) → không dùng using.
        var haloPen = StrokePaint(WithAlpha(resolved.Glow, GlowWideAlpha * intensity * fade), wideThickness, cap);
        var midPen = StrokePaint(WithAlpha(resolved.Glow, GlowMidAlpha * intensity * fade), midThickness, cap);
        double coreThickness = Math.Max(0.6, t * Math.Clamp(coreWidthRatio, 0.05, 1.0));
        var corePen = StrokePaint(WithAlpha(core, GlowCoreAlpha * intensity * fade), coreThickness, cap);

        canvas.DrawPath(geometry, haloPen);
        canvas.DrawPath(geometry, midPen);
        canvas.DrawPath(geometry, corePen);
    }

    private static void DrawRadialRays(SKCanvas canvas, FrameContext ctx, List<RaySpec> rays,
        double thickness, SKStrokeCap cap, double maxHaloThickness, bool coreAlways = false)
    {
        if (rays.Count == 0)
            return;
        SKPath geometry = ctx.GetScratchPath();
        BuildRayGeometry(geometry, rays);
        DrawGlowPasses(canvas, geometry, thickness, ctx.Resolved, ctx.GlowIntensity, ctx.AlphaFactor, cap,
            maxHaloThickness, coreAlways);
    }

    private static double RadialHaloExtent(double outer) => Math.Max(2.0, outer * RadialHaloFraction);

    private static double[] ResolveRadialLevels(FrameContext ctx, double[] rawLevels)
    {
        int count = rawLevels.Length;
        var levels = ctx.GetScratchDoubleArray(count);
        if (count <= 0)
            return levels;

        var sorted = ctx.GetScratchDoubleArray(count);
        Array.Copy(rawLevels, sorted, count);
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

    private static void CollectPeakBands(FrameContext ctx, float[] bands, int barCount, List<(int Index, float Peak)> results)
    {
        results.Clear();
        int length = bands.Length;
        if (length < 3 || barCount <= 0)
            return;

        int slices = Math.Clamp(Math.Min(barCount / 8, length), 4, 24);
        if (slices <= 0)
            return;

        var slicePeak = ctx.GetScratchFloatArray(slices);
        var sliceMean = ctx.GetScratchDoubleArray(slices);
        Array.Clear(slicePeak, 0, slices);
        Array.Clear(sliceMean, 0, slices);

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

    private static float ApplyPinkTilt(float value, int index, int bandCount)
    {
        if (value <= PinkSilenceFloor)
            return value;
        float[] tilt = GetPinkTilt(bandCount);
        float gained = value * tilt[Math.Clamp(index, 0, tilt.Length - 1)];
        return gained >= 1f ? 1f : gained;
    }

    // Pink-tilt chỉ phụ thuộc vào bandCount → cache tĩnh, tránh cấp float[] mới mỗi ApplyPinkTilt.
    // Mảng chỉ đọc (caller không modify), cùng bandCount luôn trả cùng tham chiếu.
    private static readonly Dictionary<int, float[]> _pinkTiltCache = new(16);

    private static float[] GetPinkTilt(int bandCount)
    {
        int count = Math.Max(1, bandCount);
        if (_pinkTiltCache.TryGetValue(count, out var cached))
            return cached;

        var tilt = new float[count];
        double spanOctaves = Math.Log2(PinkBandHighHz / PinkBandLowHz);
        for (int i = 0; i < count; i++)
        {
            double octaves = count <= 1 ? 0.0 : (spanOctaves * i / (count - 1));
            double db = Math.Clamp(PinkTiltDbPerOctave * octaves, 0.0, PinkTiltMaxDb);
            tilt[i] = (float)Math.Pow(10.0, db / 20.0);
        }
        _pinkTiltCache[count] = tilt;
        return tilt;
    }

    private static void FillRadialBarSet(double[] levels, SKPoint center, double inner, double outer,
        double rotation, double span, double[]? tipRadii, double[]? angles, List<RaySpec> rays)
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
            SKPoint from = PointOnCircle(center, inner, angle);
            SKPoint to = PointOnCircle(center, tip, angle);
            rays.Add(new RaySpec(from.X, from.Y, to.X, to.Y));
        }
    }

    private static void DrawRadialWedges(SKCanvas canvas, FrameContext ctx, SKPoint center, double inner,
        double[] tipRadii, double[] angles, double spanDegrees, int colorSectorShift = 0)
    {
        int count = Math.Min(tipRadii.Length, angles.Length);
        if (count <= 0)
            return;

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
                SKPath wedge = ctx.GetScratchPath();
                BuildWedge(wedge, center, inner, tip, b0, b1);
                SKColor color = ctx.Resolved.GetAngularBrush(b0, ctx.Alpha, colorSectorShift);
                canvas.DrawPath(wedge, FillPaint(color));
            }
        }
    }

    private static void BuildColumnGeometry(SKPath geometry, List<ColumnSpec> columns, double roundness)
    {
        geometry.Reset();
        double corner = Math.Clamp(roundness, 0.0, 1.0);
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnSpec column = columns[i];
            double halfWidth = Math.Max(0.30, column.Width * 0.5);
            double top = Math.Min(column.BaseY, column.TipY);
            double bottom = Math.Max(column.BaseY, column.TipY);
            float radius = (float)Math.Min(halfWidth * corner, (bottom - top) * 0.5);

            if (radius <= 0.6f)
            {
                geometry.MoveTo((float)(column.CenterX - halfWidth), (float)top);
                geometry.LineTo((float)(column.CenterX + halfWidth), (float)top);
                geometry.LineTo((float)(column.CenterX + halfWidth), (float)bottom);
                geometry.LineTo((float)(column.CenterX - halfWidth), (float)bottom);
                geometry.Close();
                continue;
            }

            SKRect rect = new((float)(column.CenterX - halfWidth), (float)top,
                (float)(column.CenterX + halfWidth), (float)bottom);
            geometry.AddRoundRect(rect, radius, radius);
        }
    }

    private static void BuildLineGeometry(SKPath geometry, List<ColumnSpec> segments)
    {
        geometry.Reset();
        for (int i = 0; i < segments.Count; i++)
        {
            ColumnSpec segment = segments[i];
            geometry.MoveTo((float)segment.CenterX, (float)segment.BaseY);
            geometry.LineTo((float)segment.CenterX, (float)segment.TipY);
        }
    }

    private static void BuildRayGeometry(SKPath geometry, List<RaySpec> segments)
    {
        geometry.Reset();
        for (int i = 0; i < segments.Count; i++)
        {
            RaySpec segment = segments[i];
            if (!IsFinite(segment.X0) || !IsFinite(segment.Y0) ||
                !IsFinite(segment.X1) || !IsFinite(segment.Y1))
                continue;
            geometry.MoveTo((float)segment.X0, (float)segment.Y0);
            geometry.LineTo((float)segment.X1, (float)segment.Y1);
        }
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static void BuildWedge(SKPath geometry, SKPoint center, double r0, double r1, double angleStart, double angleEnd)
    {
        geometry.Reset();
        double outerR = Math.Max(r1, r0 + 0.5);

        SKPoint startOuter = PointOnCircle(center, outerR, angleStart);
        SKPoint startInner = PointOnCircle(center, r0, angleStart);
        SKPoint endInner = PointOnCircle(center, r0, angleEnd);
        SKPoint endOuter = PointOnCircle(center, outerR, angleEnd);

        geometry.MoveTo(startOuter);
        geometry.LineTo(startInner);
        // Arc on the inner circle (r0).
        SKRect oval = new((float)(center.X - r0), (float)(center.Y - r0),
            (float)(center.X + r0), (float)(center.Y + r0));
        geometry.ArcTo(oval, (float)angleStart, (float)(angleEnd - angleStart), false);
        geometry.LineTo(endOuter);
        geometry.Close();
    }

    private static void BuildSparkleGeometry(SKPath geometry, SKPoint center, double radius, double rotateDegrees = 0.0)
    {
        geometry.Reset();
        double r = Math.Max(0.8, radius);
        const double Waist = 0.10;
        const double Diagonal = 0.42;

        double Sin(double deg) => Math.Sin(deg * Math.PI / 180.0);
        double Cos(double deg) => Math.Cos(deg * Math.PI / 180.0);

        geometry.MoveTo(new SKPoint((float)(center.X + (Cos(rotateDegrees) * r)),
            (float)(center.Y + (Sin(rotateDegrees) * r))));

        for (int k = 1; k <= 15; k++)
        {
            bool isTip = (k & 1) == 0;
            double angle = rotateDegrees + (k * 22.5);
            double length = !isTip
                ? r * Waist
                : ((k & 3) == 0 ? r : r * Diagonal);
            geometry.LineTo(new SKPoint((float)(center.X + (Cos(angle) * length)),
                (float)(center.Y + (Sin(angle) * length))));
        }
        geometry.Close();
    }

    private static void AddEllipseFigure(SKPath path, SKPoint center, double radiusX, double radiusY)
    {
        double rx = Math.Max(0.25, radiusX);
        double ry = Math.Max(0.25, radiusY);
        SKSize size = new((float)(rx * 2.0), (float)(ry * 2.0));
        path.AddOval(new SKRect((float)(center.X - rx), (float)(center.Y - ry),
            (float)(center.X + rx), (float)(center.Y + ry)));
    }

    private static SKPaint StrokePaint(SKColor color, double thickness, SKStrokeCap cap)
        => _paintCache.GetStroke(color, thickness, cap);

    private static SKPaint FillPaint(SKColor color)
        => _paintCache.GetFill(color);

    private static SKPaint CreateRadialGlowPaint(SKColor color, double alpha, SKPoint center, double radius)
        => _paintCache.GetRadialGlow(color, alpha, center, radius);

    // Cache SKPaint/SKShader trong một khung vẽ. Các instance được tạo lần đầu, dùng lại cho
    // toàn bộ khung (không được Dispose bởi caller), rồi giải phóng ở EndFrame.
    private sealed class PaintCache
    {
        private readonly Dictionary<StrokeKey, SKPaint> _strokeCache = new(32);
        private readonly Dictionary<uint, SKPaint> _fillCache = new(32);

        public void BeginFrame()
        {
            _strokeCache.Clear();
            _fillCache.Clear();
        }

        public void EndFrame()
        {
            foreach (var p in _strokeCache.Values) p.Dispose();
            foreach (var p in _fillCache.Values) p.Dispose();
            _strokeCache.Clear();
            _fillCache.Clear();
        }

        public SKPaint GetStroke(SKColor color, double thickness, SKStrokeCap cap)
        {
            var key = new StrokeKey(color, thickness, cap);
            if (!_strokeCache.TryGetValue(key, out var paint))
            {
                paint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    Color = color,
                    StrokeWidth = (float)Math.Max(0.25, thickness),
                    StrokeCap = cap,
                    StrokeJoin = SKStrokeJoin.Round
                };
                _strokeCache[key] = paint;
            }
            return paint;
        }

        public SKPaint GetFill(SKColor color)
        {
            uint key = ColorKey(color);
            if (!_fillCache.TryGetValue(key, out var paint))
            {
                paint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                    Color = color
                };
                _fillCache[key] = paint;
            }
            return paint;
        }

        // Radial glow shader cross-frame cache — shader được tạo lại 1 lần per tham số,
        // dùng lại qua nhiều khung. Mảng colors/offsets chỉ cấp khi cache miss.
        private static readonly Dictionary<uint, SKShader> _radialGlowShaderCache = new(64);

        private static uint RadialGlowShaderKey(SKColor color, double alpha, SKPoint center, double radius)
        {
            unchecked
            {
                uint h = 2166136261u;
                h = (h * 16777619u) ^ ColorKey(color);
                h = (h * 16777619u) ^ (uint)alpha.GetHashCode();
                h = (h * 16777619u) ^ (uint)center.GetHashCode();
                h = (h * 16777619u) ^ (uint)radius.GetHashCode();
                return h;
            }
        }

        private static SKShader GetRadialGlowShader(uint key, SKPoint center, double radius, SKColor color, double alpha)
        {
            if (_radialGlowShaderCache.TryGetValue(key, out var shader))
                return shader;
            double safeAlpha = Math.Clamp(alpha, 0.0, 1.0);
            float safeRadius = (float)Math.Max(0.5, radius);
            shader = SKShader.CreateRadialGradient(center, safeRadius,
                new[]
                {
                    WithAlpha(color, safeAlpha * 0.85),
                    WithAlpha(color, safeAlpha * 0.42),
                    WithAlpha(color, safeAlpha * 0.18),
                    WithAlpha(color, safeAlpha * 0.06),
                    WithAlpha(color, 0.0)
                },
                new[] { 0f, 0.28f, 0.55f, 0.78f, 1f },
                SKShaderTileMode.Clamp);
            if (_radialGlowShaderCache.Count > 64)
                _radialGlowShaderCache.Clear();
            _radialGlowShaderCache[key] = shader;
            return shader;
        }

        public SKPaint GetRadialGlow(SKColor color, double alpha, SKPoint center, double radius)
        {
            var key = RadialGlowShaderKey(color, alpha, center, radius);
            var shader = GetRadialGlowShader(key, center, radius, color, alpha);
            // Shader chia sẻ cross-frame, paint wrapper cũng được tái dùng qua _shaderPaintCache.
            return GetShaderPaint(key, shader);
        }

        private static uint ColorKey(SKColor c) =>
            ((uint)c.Red << 24) | ((uint)c.Green << 16) | ((uint)c.Blue << 8) | c.Alpha;

        private readonly struct StrokeKey : IEquatable<StrokeKey>
        {
            public readonly uint Color;
            public readonly float Thickness;
            public readonly SKStrokeCap Cap;

            public StrokeKey(SKColor color, double thickness, SKStrokeCap cap)
            {
                Color = ColorKey(color);
                Thickness = (float)Math.Max(0.25, thickness);
                Cap = cap;
            }

            public bool Equals(StrokeKey other) =>
                Color == other.Color && Thickness == other.Thickness && Cap == other.Cap;

            public override int GetHashCode() => HashCode.Combine(Color, Thickness, Cap);
        }

    }

    // Cross-frame SKShader cache: tạo shader 1 lần per tham số, dùng lại qua nhiều khung.
    // Giới hạn 64 entry, clear khi vượt ngưỡng (giống ResolvedPalette cache).
    private sealed class ShaderCache
    {
        private readonly Dictionary<uint, SKShader> _cache;

        public ShaderCache(int capacity)
        {
            _cache = new Dictionary<uint, SKShader>(capacity);
        }

        public SKShader GetOrCreate(uint key, Func<SKShader> create)
        {
            if (_cache.TryGetValue(key, out var shader))
                return shader;
            shader = create();
            if (_cache.Count > 64)
                _cache.Clear();
            _cache[key] = shader;
            return shader;
        }
    }

    private static SKColor WithAlpha(SKColor color, double alphaFactor)
    {
        byte alpha = (byte)Math.Clamp(Math.Round(alphaFactor * 255.0), 0, 255);
        return new SKColor(color.Red, color.Green, color.Blue, alpha);
    }

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

    private static SKPoint PointOnCircle(SKPoint center, double radius, double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180.0;
        return new SKPoint(
            (float)(center.X + (Math.Cos(radians) * radius)),
            (float)(center.Y + (Math.Sin(radians) * radius)));
    }

    private static double NormalizeAngle(double degrees)
    {
        double a = degrees % 360.0;
        if (a < 0.0)
            a += 360.0;
        return a;
    }

    private static SKPoint PlotCenter(SKRect plot)
        => new((float)(plot.Left + (plot.Width * 0.5)), (float)(plot.Top + (plot.Height * 0.5)));

    private static double MaxRadius(SKRect plot) => Math.Max(0.0, Math.Min(plot.Width, plot.Height) * 0.5 * 0.98);

    private static SKRect ResolvePlotRect(SoundBarSettings settings, int width, int height)
    {
        double w = Math.Max(2.0, width);
        double h = Math.Max(2.0, height);
        double widthPercent = Math.Clamp(settings.WidthPercent, 0.02, 1.0);
        double heightPercent = Math.Clamp(settings.HeightPercent, 0.02, 1.0);
        double plotW = widthPercent * w;
        double plotH = heightPercent * h;

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

        return new SKRect((float)left, (float)top, (float)(left + plotW), (float)(top + plotH));
    }

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

    // ResolvedPalette chỉ phụ thuộc vào palette (immutable record) → cache tĩnh theo Id,
    // tránh parse lại hex + build 180 angular brushes mỗi frame.
    private static readonly Dictionary<string, ResolvedPalette> _resolvedPaletteCache = new(32);

    private static ResolvedPalette ResolvePalette(ColorPreset preset)
    {
        ColorPreset safe = preset ?? ColorPresets.Default;
        string id = safe.Id ?? string.Empty;

        if (_resolvedPaletteCache.TryGetValue(id, out var cached))
            return cached;

        var resolved = BuildResolvedPalette(safe);
        _resolvedPaletteCache[id] = resolved;
        return resolved;
    }

    private static ResolvedPalette BuildResolvedPalette(ColorPreset safe)
    {
        var offsets = new List<double>();
        var colors = new List<SKColor>();
        IReadOnlyList<GradientStopSpec>? source = safe.Stops;
        if (source != null)
        {
            for (int i = 0; i < source.Count; i++)
            {
                GradientStopSpec spec = source[i];
                offsets.Add(Math.Clamp(spec.Offset, 0.0, 1.0));
                colors.Add(ParseHex(spec.Hex) ?? SKColors.White);
            }
        }
        if (offsets.Count == 0)
        {
            offsets.Add(0.0);
            colors.Add(SKColors.White);
            offsets.Add(1.0);
            colors.Add(SKColors.White);
        }

        var angular = new SKColor[AngularSectorCount];
        double[] ramp = offsets.ToArray();
        SKColor[] rampColors = colors.ToArray();
        for (int i = 0; i < AngularSectorCount; i++)
        {
            double t = 0.25 + (0.75 * (i / (double)(AngularSectorCount - 1)));
            angular[i] = SampleRamp(ramp, rampColors, t);
        }

        return new ResolvedPalette
        {
            Id = safe.Id ?? string.Empty,
            Core = ParseHex(safe.Core) ?? SKColors.White,
            Glow = ParseHex(safe.Glow) ?? SKColors.White,
            Accent = ParseHex(safe.Accent) ?? SKColors.White,
            RampOffsets = ramp,
            RampColors = rampColors,
            AngularBrushes = angular
        };
    }

    private static SKColor SampleRamp(double[] offsets, SKColor[] colors, double t)
    {
        double clamped = Math.Clamp(t, 0.0, 1.0);
        if (offsets.Length == 0)
            return SKColors.White;
        if (clamped <= offsets[0])
            return colors[0];
        for (int i = 1; i < offsets.Length; i++)
        {
            if (clamped > offsets[i])
                continue;
            double span = offsets[i] - offsets[i - 1];
            double local = span <= 0.0001 ? 0.0 : (clamped - offsets[i - 1]) / span;
            byte r = (byte)Math.Clamp(Math.Round((colors[i - 1].Red * (1.0 - local)) + (colors[i].Red * local)), 0.0, 255.0);
            byte g = (byte)Math.Clamp(Math.Round((colors[i - 1].Green * (1.0 - local)) + (colors[i].Green * local)), 0.0, 255.0);
            byte b = (byte)Math.Clamp(Math.Round((colors[i - 1].Blue * (1.0 - local)) + (colors[i].Blue * local)), 0.0, 255.0);
            return new SKColor(r, g, b);
        }
        return colors[colors.Length - 1];
    }

    // Column brush cache keyed by (baseY, tipY, alpha) - hoists gradient creation out of the per-bar loop.
    // Cache keyed by palette + geometry + alpha so different palettes never collide.
    // Evicts like the WPF original (clear above ~4096 entries) so long renders stay bounded.
    private sealed class ColumnPaintKey : IEquatable<ColumnPaintKey>
    {
        public string PaletteId { get; init; } = string.Empty;
        public int BaseY { get; init; }
        public int Height { get; init; }
        public bool Downward { get; init; }
        public int Alpha { get; init; }

        public bool Equals(ColumnPaintKey? other) =>
            other is not null &&
            PaletteId == other.PaletteId &&
            BaseY == other.BaseY &&
            Height == other.Height &&
            Downward == other.Downward &&
            Alpha == other.Alpha;

        public override int GetHashCode() =>
            HashCode.Combine(PaletteId, BaseY, Height, Downward, Alpha);
    }

    // Rendering tuần tự (không song tiến) → Dictionary thường đủ, tránh chi phí ConcurrentDictionary.
    private static readonly Dictionary<ColumnPaintKey, SKPaint> _columnPaintCache =
        new(1024);

    private static SKPaint GetColumnPaint(FrameContext ctx, double baseY, double tipY, int alpha)
    {
        double height = Math.Abs(tipY - baseY);
        bool downward = tipY >= baseY;
        var key = new ColumnPaintKey
        {
            PaletteId = ctx.Resolved.Id,
            BaseY = (int)Math.Round(Math.Clamp(baseY, -4096.0, 4096.0)),
            Height = (int)Math.Round(Math.Clamp(height, 1.0, 4096.0)),
            Downward = downward,
            Alpha = Math.Clamp(alpha, 0, 255),
        };

        if (_columnPaintCache.TryGetValue(key, out SKPaint? cached))
            return cached;

        SKPaint paint = BuildColumnPaint(ctx.Resolved, baseY, tipY, alpha, downward);
        _columnPaintCache[key] = paint;

        // Bound the cache like WPF (clear above ~4096 entries) to keep long renders bounded.
        if (_columnPaintCache.Count > 4096)
            _columnPaintCache.Clear();

        return paint;
    }

    private static SKPaint GetPlotGradientPaint(FrameContext ctx, double baseY, double tipY)
        => GetColumnPaint(ctx, baseY, tipY, (int)ctx.Alpha);

    private static SKPaint BuildColumnPaint(ResolvedPalette resolved, double baseY, double tipY, int alpha, bool downward)
    {
        double alphaFactor = alpha / 255.0;
        double[] offsets = resolved.RampOffsets;
        SKColor[] colors = resolved.RampColors;

        // Build the stops list first (White-hot tip technique), then create the shader once.
        var stopColors = new List<SKColor>();
        var stopOffsets = new List<float>();
        double lastOffset = -1.0;
        bool hasStopAtTipStart = false;

        for (int i = 0; i < offsets.Length; i++)
        {
            double offset = Math.Clamp(offsets[i], 0.0, TipStart);
            if (offset <= lastOffset)
                continue;
            stopColors.Add(WithAlpha(colors[i], alphaFactor));
            stopOffsets.Add((float)offset);
            lastOffset = offset;
            hasStopAtTipStart = offset >= TipStart;
        }

        if (!hasStopAtTipStart && TipStart > lastOffset)
        {
            stopColors.Add(WithAlpha(resolved.Core, alphaFactor));
            stopOffsets.Add((float)TipStart);
            lastOffset = TipStart;
        }

        if (lastOffset < 1.0)
        {
            stopColors.Add(new SKColor(255, 255, 255, (byte)Math.Clamp(alpha, 0, 255)));
            stopOffsets.Add(1f);
        }

        return new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, (float)baseY),
                new SKPoint(0, (float)tipY),
                stopColors.ToArray(),
                stopOffsets.ToArray(),
                SKShaderTileMode.Clamp)
        };
    }

    private static double MidLevelStatic(FrameContext ctx)
        => Math.Clamp((ctx.Current?.Mid ?? 0f) * ctx.Sensitivity, 0.0, 1.0);

    private static double VoiceLevelStatic(FrameContext ctx)
        => Math.Clamp(ctx.Current?.Voice ?? 0f, 0.0, 1.0);

    private static double OnsetPulseEnvelopeStatic(FrameContext ctx)
    {
        double pulse = 0.0;
        for (int k = 0; k <= AudioRingPulseTailFrames; k++)
        {
            AudioFrameData? back = FrameAt(ctx.Frames, ctx.Index - k);
            if (back?.Onset != true)
                continue;
            double level = k == 0
                ? AudioRingPulseAttackLevel
                : Math.Pow(AudioRingPulseDecayPerFrame, k - 1);
            if (level > pulse)
                pulse = level;
        }
        return pulse;
    }

    private static double SampleBandPeakStatic(FrameContext ctx, int barIndex, int barCount)
    {
        double peak = 0.0;
        for (int k = 0; k <= PeakCapMaxLookback; k++)
        {
            AudioFrameData? back = FrameAt(ctx.Frames, ctx.Index - k);
            if (back == null)
                break;
            double contribution = SampleBandFrameStatic(back, barIndex, barCount, ctx.Sensitivity)
                - (PeakCapFallPerFrame * Math.Max(0, k - PeakCapHoldFrames));
            if (contribution > peak)
                peak = contribution;
        }
        return Math.Clamp(peak, 0.0, 1.0);
    }

    private static float SampleBandFrameStatic(AudioFrameData? frame, int barIndex, int barCount, float sensitivity)
    {
        float[]? frameBands = frame?.Bands;
        if (frameBands == null || frameBands.Length == 0 || barCount <= 0)
            return 0f;
        int length = frameBands.Length;
        int index = Math.Clamp((int)Math.Floor((barIndex / (double)barCount) * length), 0, length - 1);
        return ApplyPinkTilt(Math.Clamp(frameBands[index] * sensitivity, 0f, 1f), index, length);
    }

    private static double RadialBeatScale(FrameContext ctx)
    {
        double bass = Math.Clamp((ctx.Current?.Bass ?? 0f) * ctx.Sensitivity, 0.0, 1.0);
        return 1.0
            + (RadialBassBreatheFraction * bass)
            + (MidSwellFraction * ctx.MidLevel)
            + (VoiceSwellFraction * ctx.VoiceLevel)
            + (RadialPulseKickFraction * ctx.OnsetPulse);
    }

    private static double DotJitter(int ring, int slot)
    {
        unchecked
        {
            uint h = ((uint)ring * 374761393u) + ((uint)slot * 668265263u);
            h ^= h >> 13;
            h *= 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFF) / 65535.0;
        }
    }

    private static double ScaleFromHdReference(FrameContext ctx, double hdReferenceValue, double minValue, double maxValue)
    {
        double minDim = Math.Max(160.0, Math.Min(ctx.Plot.Width, ctx.Plot.Height));
        double scale = Math.Clamp(minDim / 1080.0, 0.22, 4.0);
        return Math.Clamp(hdReferenceValue * scale, minValue, maxValue);
    }

    private static SKColor? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;
        string h = hex.TrimStart('#');
        try
        {
            if (h.Length == 6)
            {
                byte r = byte.Parse(h.Substring(0, 2), NumberStyles.HexNumber);
                byte g = byte.Parse(h.Substring(2, 2), NumberStyles.HexNumber);
                byte b = byte.Parse(h.Substring(4, 2), NumberStyles.HexNumber);
                return new SKColor(r, g, b);
            }
            if (h.Length == 3)
            {
                byte r = byte.Parse(h.Substring(0, 1) + h.Substring(0, 1), NumberStyles.HexNumber);
                byte g = byte.Parse(h.Substring(1, 1) + h.Substring(1, 1), NumberStyles.HexNumber);
                byte b = byte.Parse(h.Substring(2, 1) + h.Substring(2, 1), NumberStyles.HexNumber);
                return new SKColor(r, g, b);
            }
        }
        catch { return null; }
        return null;
    }

    /// <summary>
    /// Angular brush lookup for wedges: sector index -> opaque colour (alpha applied at draw time).
    /// </summary>
    private static SKColor GetAngularBrush(this ResolvedPalette resolved, double angleDegrees, int alpha, int sectorShift = 0)
    {
        int index = ((int)Math.Round(NormalizeAngle(angleDegrees) / AngularSectorDegrees) + sectorShift) % AngularSectorCount;
        if (index < 0)
            index += AngularSectorCount;
        SKColor source = resolved.AngularBrushes[index];
        if (alpha >= 255)
            return source;
        return WithAlpha(source, alpha / 255.0);
    }
}





