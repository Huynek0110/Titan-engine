using System;
using System.Collections.Generic;

namespace TitanEngine
{
    public enum TitanTransitionType
    {
        Fade,
        Dissolve,
        FadeBlack,
        FadeWhite,
        FadeBlur,
        FadeOpacity,
        WhipLeft,
        WhipRight,
        WhipUp,
        WhipDown,
        Zoom,
        CircleCrop,
        RectCrop,
        RadialClock,
        VertOpen,
        HorzOpen,
        Pixelize,
        LightLeakBurn,
        HBlur,
        DiagTL,
        DiagTR,
        SqueezeV
    }

    public sealed class TitanTransitionSettings
    {
        public bool Enabled { get; set; } = false;
        public TitanTransitionType TransitionType { get; set; } = TitanTransitionType.Dissolve;
        public double DurationSeconds { get; set; } = 0.6;
    }

    public struct TitanClipMetadata
    {
        public string FilePath { get; set; }
        public double DurationSeconds { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool HasAudio { get; set; }
    }
}
