using System;
using System.Collections.Generic;

namespace TitanEngine
{
    public static class TitanTransitionLibrary
    {
        private static readonly Dictionary<TitanTransitionType, string> XfadeMap = new(EnumComparer)
        {
            { TitanTransitionType.Fade, "fade" },
            { TitanTransitionType.Dissolve, "dissolve" },
            { TitanTransitionType.FadeBlack, "fadeblack" },
            { TitanTransitionType.FadeWhite, "fadewhite" },
            { TitanTransitionType.FadeBlur, "dissolve" },
            { TitanTransitionType.FadeOpacity, "fade" },
            { TitanTransitionType.WhipLeft, "slideleft" },
            { TitanTransitionType.WhipRight, "slideright" },
            { TitanTransitionType.WhipUp, "slideup" },
            { TitanTransitionType.WhipDown, "slidedown" },
            { TitanTransitionType.Zoom, "zoomin" },
            { TitanTransitionType.CircleCrop, "circlecrop" },
            { TitanTransitionType.RectCrop, "rectcrop" },
            { TitanTransitionType.RadialClock, "radial" },
            { TitanTransitionType.VertOpen, "vertopen" },
            { TitanTransitionType.HorzOpen, "horzopen" },
            { TitanTransitionType.Pixelize, "pixelize" },
            { TitanTransitionType.LightLeakBurn, "fadewhite" },
            { TitanTransitionType.HBlur, "hblur" },
            { TitanTransitionType.DiagTL, "diagtl" },
            { TitanTransitionType.DiagTR, "diagtr" },
            { TitanTransitionType.SqueezeV, "squeezev" }
        };

        private static readonly IEqualityComparer<TitanTransitionType> EnumComparer = EqualityComparer<TitanTransitionType>.Default;

        public static string ResolveXfadeName(TitanTransitionType type)
        {
            if (XfadeMap.TryGetValue(type, out string? name))
                return name;
            return "dissolve";
        }

        public static TitanTransitionType ParseTransitionType(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return TitanTransitionType.Dissolve;

            string normalized = input.Trim().Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();

            return normalized switch
            {
                "fade" => TitanTransitionType.Fade,
                "dissolve" or "crossfade" => TitanTransitionType.Dissolve,
                "fadeblack" or "black" => TitanTransitionType.FadeBlack,
                "fadewhite" or "white" => TitanTransitionType.FadeWhite,
                "fadeblur" or "blurfade" or "fade(blur)" or "blur" => TitanTransitionType.FadeBlur,
                "fadeopacity" or "opacity" or "fade(opacity)" or "opacityfade" => TitanTransitionType.FadeOpacity,
                "whipleft" or "slideleft" => TitanTransitionType.WhipLeft,
                "whipright" or "slideright" => TitanTransitionType.WhipRight,
                "whipup" or "slideup" => TitanTransitionType.WhipUp,
                "whipdown" or "slidedown" => TitanTransitionType.WhipDown,
                "zoom" or "zoomin" or "zoomout" or "crosszoom" or "zoompunch" => TitanTransitionType.Zoom,
                "circlecrop" or "circle" or "iris" => TitanTransitionType.CircleCrop,
                "rectcrop" or "rect" => TitanTransitionType.RectCrop,
                "radialclock" or "radial" or "clock" => TitanTransitionType.RadialClock,
                "vertopen" or "vert" => TitanTransitionType.VertOpen,
                "horzopen" or "horz" => TitanTransitionType.HorzOpen,
                "pixelize" or "pixel" or "glitch" or "glitchrgb" => TitanTransitionType.Pixelize,
                "lightleakburn" or "lightleak" or "filmburn" => TitanTransitionType.LightLeakBurn,
                "hblur" => TitanTransitionType.HBlur,
                "diagtl" => TitanTransitionType.DiagTL,
                "diagtr" => TitanTransitionType.DiagTR,
                "squeezev" or "squeeze" => TitanTransitionType.SqueezeV,
                _ => TitanTransitionType.Dissolve
            };
        }
    }
}
