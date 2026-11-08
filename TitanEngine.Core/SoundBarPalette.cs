using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;

namespace TitanEngine
{
    /// <summary>
    /// One colour stop of a palette's vertical bar gradient.
    /// Offset 0.0 is the bottom (base) of a bar, offset 1.0 is the hot tip.
    /// </summary>
    public readonly record struct GradientStopSpec(double Offset, string Hex);

    /// <summary>
    /// A complete soundbar colour scheme. Presets are immutable value-like records so they can be
    /// cached across STA threads without defensive copying of the stop list.
    /// </summary>
    /// <param name="Id">Stable kebab-case id, e.g. "ncs-cyan". Used by the UI, the API and the cache key.</param>
    /// <param name="Name">Human readable display name.</param>
    /// <param name="Vibe">One-line mood description shown in the picker.</param>
    /// <param name="Genres">Comma separated genre hint shown in the picker.</param>
    /// <param name="Stops">Vertical bar gradient, bottom (0.0) to hot tip (1.0).</param>
    /// <param name="Core">Near-white colour used for the innermost glow pass.</param>
    /// <param name="Glow">Saturated colour used for the outer halo passes.</param>
    /// <param name="BgTint">Very dark companion tint, reserved for backing plates.</param>
    /// <param name="Accent">Secondary highlight colour (ticks, stars, sparks).</param>
    /// <param name="GlowStrength">Per-preset multiplier on every additive glow pass.</param>
    public sealed record ColorPreset(
        string Id,
        string Name,
        string Vibe,
        string Genres,
        IReadOnlyList<GradientStopSpec> Stops,
        string Core,
        string Glow,
        string BgTint,
        string Accent,
        double GlowStrength = 1.0)
    {
        /// <summary>
        /// Parses "#RGB", "#RRGGBB" or "#AARRGGBB" (an optional "0x" prefix is also accepted).
        /// The result is ALWAYS opaque: the alpha of an "#AARRGGBB" literal is discarded because the
        /// renderer applies alpha as a separate multiplicative pass. Never throws.
        /// </summary>
        /// <returns>True when the literal parsed as a hex colour; <paramref name="color"/> is then opaque.</returns>
        public static bool TryParseHex(string hex, out Color color)
        {
            color = Colors.White;
            if (string.IsNullOrWhiteSpace(hex))
                return false;

            string value = hex.Trim();
            if (value.StartsWith("#", StringComparison.Ordinal))
                value = value[1..];
            else if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = value[2..];

            if (value.Length != 3 && value.Length != 6 && value.Length != 8)
                return false;

            int r;
            int g;
            int b;

            if (value.Length == 3)
            {
                int n0 = Nibble(value[0]);
                int n1 = Nibble(value[1]);
                int n2 = Nibble(value[2]);
                if (n0 < 0 || n1 < 0 || n2 < 0)
                    return false;

                r = n0 * 17;
                g = n1 * 17;
                b = n2 * 17;
            }
            else
            {
                int n0 = Nibble(value[0]);
                int n1 = Nibble(value[1]);
                int n2 = Nibble(value[2]);
                int n3 = Nibble(value[3]);
                int n4 = Nibble(value[4]);
                int n5 = Nibble(value[5]);
                if (n0 < 0 || n1 < 0 || n2 < 0 || n3 < 0 || n4 < 0 || n5 < 0)
                    return false;

                r = (n0 * 16) + n1;
                g = (n2 * 16) + n3;
                b = (n4 * 16) + n5;

                // 8 digits is "#AARRGGBB". Alpha is intentionally dropped: glow passes apply alpha themselves.
            }

            color = Color.FromRgb((byte)r, (byte)g, (byte)b);
            return true;
        }

        /// <summary>
        /// Builds a fresh <see cref="GradientStopCollection"/> from <see cref="Stops"/>.
        /// A new set of <see cref="GradientStop"/> instances is produced every call because a gradient
        /// stop can only ever belong to one collection.
        /// </summary>
        public GradientStopCollection ToBrushStops()
        {
            var stops = new GradientStopCollection();

            IReadOnlyList<GradientStopSpec>? source = Stops;
            if (source != null)
            {
                for (int i = 0; i < source.Count; i++)
                {
                    GradientStopSpec spec = source[i];
                    Color parsed = TryParseHex(spec.Hex, out Color color) ? color : Colors.White;
                    stops.Add(new GradientStop(parsed, Math.Clamp(spec.Offset, 0.0, 1.0)));
                }
            }

            if (stops.Count == 0)
                stops.Add(new GradientStop(Colors.White, 0.0));

            return stops;
        }

        /// <summary>Returns the opaque <see cref="Color"/> for <see cref="Core"/>, falling back to white.</summary>
        public Color CoreColor => TryParseHex(Core, out Color color) ? color : Colors.White;

        /// <summary>Returns the opaque <see cref="Color"/> for <see cref="Glow"/>, falling back to white.</summary>
        public Color GlowColor => TryParseHex(Glow, out Color color) ? color : Colors.White;

        /// <summary>Returns the opaque <see cref="Color"/> for <see cref="BgTint"/>, falling back to black.</summary>
        public Color BgTintColor => TryParseHex(BgTint, out Color color) ? color : Colors.Black;

        /// <summary>Returns the opaque <see cref="Color"/> for <see cref="Accent"/>, falling back to white.</summary>
        public Color AccentColor => TryParseHex(Accent, out Color color) ? color : Colors.White;

        private static int Nibble(char c)
        {
            if (c >= '0' && c <= '9')
                return c - '0';
            if (c >= 'a' && c <= 'f')
                return (c - 'a') + 10;
            if (c >= 'A' && c <= 'F')
                return (c - 'A') + 10;
            return -1;
        }
    }

    /// <summary>The 30 shipped soundbar palettes, in display order.</summary>
    public static class ColorPresets
    {
        /// <summary>Id of the palette used whenever the requested one cannot be resolved.</summary>
        public const string DefaultId = "ncs-cyan";

        private static readonly ColorPreset[] Presets = BuildPresets();
        private static readonly Dictionary<string, ColorPreset> Index = BuildIndex(Presets);
        private static readonly string[] PresetNames = BuildNames(Presets);

        /// <summary>The fallback palette (id "ncs-cyan").</summary>
        public static readonly ColorPreset Default = Index[DefaultId];

        /// <summary>All presets in display order.</summary>
        public static IReadOnlyList<ColorPreset> All => Presets;

        /// <summary>Display names in the same order as <see cref="All"/>. A fresh array is returned each call.</summary>
        public static string[] Names => (string[])PresetNames.Clone();

        /// <summary>
        /// Resolves a palette by id (preferred) or by display name, case-insensitively and
        /// tolerating "_"/" " instead of "-". Returns null when nothing matches.
        /// </summary>
        public static ColorPreset? Find(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            string key = Normalize(id);

            for (int i = 0; i < Presets.Length; i++)
            {
                if (string.Equals(Normalize(Presets[i].Id), key, StringComparison.Ordinal))
                    return Presets[i];
            }

            for (int i = 0; i < Presets.Length; i++)
            {
                if (string.Equals(Normalize(Presets[i].Name), key, StringComparison.Ordinal))
                    return Presets[i];
            }

            return null;
        }

        /// <summary>Resolves a palette by id or name, falling back to <see cref="Default"/>.</summary>
        public static ColorPreset GetOrDefault(string? id) => Find(id) ?? Default;

        private static string Normalize(string value)
        {
            string trimmed = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (trimmed.Length == 0)
                return string.Empty;

            char[] buffer = new char[trimmed.Length];
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                buffer[i] = (c == '_' || c == ' ') ? '-' : c;
            }

            return new string(buffer);
        }

        private static Dictionary<string, ColorPreset> BuildIndex(ColorPreset[] presets)
        {
            var map = new Dictionary<string, ColorPreset>(presets.Length * 2, StringComparer.Ordinal);
            for (int i = 0; i < presets.Length; i++)
            {
                string key = Normalize(presets[i].Id);
                if (key.Length > 0 && !map.ContainsKey(key))
                    map.Add(key, presets[i]);
            }

            return map;
        }

        private static string[] BuildNames(ColorPreset[] presets)
        {
            var names = new string[presets.Length];
            for (int i = 0; i < presets.Length; i++)
                names[i] = presets[i].Name;

            return names;
        }

        private static GradientStopSpec[] ParseStops(params string[] tokens)
        {
            var stops = new GradientStopSpec[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                int split = token.IndexOf(':');
                if (split <= 0)
                {
                    stops[i] = new GradientStopSpec(i / (double)Math.Max(1, tokens.Length - 1), "#FFFFFF");
                    continue;
                }

                double offset = double.TryParse(token.AsSpan(0, split), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                    ? Math.Clamp(parsed, 0.0, 1.0)
                    : i / (double)Math.Max(1, tokens.Length - 1);

                stops[i] = new GradientStopSpec(offset, token[(split + 1)..]);
            }

            return stops;
        }

        private static ColorPreset Make(
            string id,
            string name,
            string vibe,
            string genres,
            GradientStopSpec[] stops,
            string core,
            string glow,
            string bgTint,
            string accent)
            => Make(id, name, vibe, genres, stops, core, glow, bgTint, accent, 1.0);

        private static ColorPreset Make(
            string id,
            string name,
            string vibe,
            string genres,
            GradientStopSpec[] stops,
            string core,
            string glow,
            string bgTint,
            string accent,
            double glowStrength)
            => new ColorPreset(id, name, vibe, genres, stops, core, glow, bgTint, accent, glowStrength);

        private static ColorPreset[] BuildPresets()
        {
            return new[]
            {
                Make("ncs-cyan", "NCS Cyan",
                    "Clean cyan energy - the signature NCS opener look.",
                    "Melodic House, Future Bounce, Pop",
                    ParseStops("0:#062B3A", "0.45:#00C8E0", "0.82:#00F0FF", "1:#C8FBFF"),
                    "#EAFEFF", "#00E5FF", "#04141C", "#00FFC8"),

                Make("ncs-turquoise", "NCS Turquoise",
                    "Tropical teal with a hot mint tip.",
                    "Tropical House, Moombahton, Chill",
                    ParseStops("0:#04302B", "0.45:#00D8B4", "0.82:#12FFE0", "1:#D6FFF8"),
                    "#E8FFFA", "#00FFC8", "#031A17", "#00FFD8"),

                Make("ncs-mint", "NCS Mint",
                    "Soft mint glow - gentle, airy and calm.",
                    "Lo-Fi, Chillhop, Ambient Pop",
                    ParseStops("0:#0B3324", "0.45:#3CE8A0", "0.82:#7CFFC4", "1:#E4FFF0"),
                    "#EFFFF6", "#5CFFC0", "#061D14", "#B0FFD8", 0.9),

                Make("ncs-lavender", "NCS Lavender",
                    "Dreamy violet bars with a pink accent kicker.",
                    "Future Bass, Melodic Dubstep, Synthwave",
                    ParseStops("0:#241540", "0.45:#9A6BFF", "0.82:#C6A0FF", "1:#F0E8FF"),
                    "#F6F2FF", "#A878FF", "#150C26", "#FFB0F0"),

                Make("ncs-magenta", "NCS Magenta",
                    "Hot magenta punch with an orange counter-accent.",
                    "Electro House, Big Room, Dance",
                    ParseStops("0:#2E0430", "0.45:#D4149E", "0.82:#FF3DC8", "1:#FFC8EE"),
                    "#FFEAF8", "#FF2BC8", "#1A031C", "#FF5C00", 1.1),

                Make("ncs-hardstyle", "NCS Hardstyle",
                    "Hard grey-to-white riser - cold and aggressive.",
                    "Hardstyle, Rawstyle, Hardcore",
                    ParseStops("0:#1C1E24", "0.45:#7C8494", "0.82:#C8D0DC", "1:#FFFFFF"),
                    "#FFFFFF", "#A8B4C4", "#101216", "#00D8FF", 0.9),

                Make("ncs-trap-green", "NCS Trap Green",
                    "Acid green bars built to sit behind heavy 808s.",
                    "Trap, Drumbstep, Hip-Hop",
                    ParseStops("0:#0A2E08", "0.45:#38D414", "0.82:#7CFF38", "1:#DCFFC4"),
                    "#F0FFDC", "#5CFF1C", "#061A05", "#C8FF00"),

                Make("ncs-house-yellow", "NCS House Yellow",
                    "Warm gold bars that cut through dark footage.",
                    "House, Future House, Dance",
                    ParseStops("0:#332B04", "0.45:#E0B414", "0.82:#FFDC38", "1:#FFF6C8"),
                    "#FFFAE0", "#FFC81C", "#1C1802", "#00E5FF"),

                Make("ncs-drumstep-red", "NCS Drumstep Red",
                    "Red-hot bars tuned for live DnB drops.",
                    "Drum and Bass, Jump-up, Neurofunk",
                    ParseStops("0:#330606", "0.45:#DC1414", "0.82:#FF3838", "1:#FFC8C8"),
                    "#FFEAEA", "#FF1C1C", "#1C0303", "#00B4FF", 1.1),

                Make("ncs-indie-dance", "NCS Indie Dance",
                    "Warm orange sunset bars with festival energy.",
                    "Indie Dance, Electro House, Pop",
                    ParseStops("0:#331A04", "0.45:#E87414", "0.82:#FFA838", "1:#FFE0C0"),
                    "#FFF0DC", "#FF8C1C", "#1C0E02", "#00D8FF"),

                Make("classic-synthwave", "Classic Synthwave",
                    "Purple-to-magenta retro neon on a violet floor.",
                    "Synthwave, Retrowave, Darkwave",
                    ParseStops("0:#1A0533", "0.35:#9400D3", "0.75:#FF00FF", "1:#FFB8FF"),
                    "#FFE8FF", "#FF00FF", "#0D0221", "#00FFFF", 1.2),

                Make("vaporwave", "Vaporwave Mall",
                    "Mall-vapour pink to cyan with a mint finish.",
                    "Vaporwave, Lo-Fi, Chillwave",
                    ParseStops("0:#B967FF", "0.34:#FF71CE", "0.68:#01CDFE", "1:#05FFA1"),
                    "#F0F4FF", "#FF71CE", "#1A1A2E", "#05FFA1"),

                Make("cyberpunk-yellow", "Cyberpunk Yellow",
                    "Acid taxi-cab yellow against deep black.",
                    "Cyberpunk, Industrial, Darksynth",
                    ParseStops("0:#1A1400", "0.45:#8C6E00", "0.80:#FFD400", "1:#FFF6C0"),
                    "#FFFCEC", "#FFD400", "#0D0B00", "#00FFFF", 1.1),

                Make("neon-dystopia", "Neon Dystopia",
                    "Crimson neon signage with amber counter-light.",
                    "Dark Ambient, Industrial, Techno",
                    ParseStops("0:#1A0A2E", "0.42:#B40038", "0.80:#FF003C", "1:#FFB8B8"),
                    "#FFECEC", "#FF003C", "#0D1117", "#FF9900", 1.1),

                Make("drift-phonk", "Drift Phonk",
                    "Cool teal drift with a mint tip and hard hits.",
                    "Phonk, Drift, Gym Trap",
                    ParseStops("0:#04231E", "0.45:#00B894", "0.82:#18E0B4", "1:#C4FFF0"),
                    "#E4FFF6", "#00D8A4", "#021412", "#00FF9C", 0.95),

                Make("toxic-lime", "Toxic Lime",
                    "Sickly radioactive lime with a magenta kicker.",
                    "Dubstep, Bass House, Riddim",
                    ParseStops("0:#1A2E00", "0.45:#6ECC00", "0.82:#A8FF00", "1:#ECFFC4"),
                    "#F4FFD8", "#8CFF00", "#0D1A00", "#FF00A8", 1.1),

                Make("matrix-green", "Matrix Green",
                    "Phosphor terminal green - deep and moody.",
                    "Techno, Glitch, IDM",
                    ParseStops("0:#001A08", "0.45:#00A018", "0.82:#38FF44", "1:#C8FFCE"),
                    "#E4FFE8", "#28FF34", "#000A04", "#B4FF00"),

                Make("fire-ember", "Fire Ember",
                    "Ember ramp from deep red through to white heat.",
                    "Rock, Metal, Trailer Music",
                    ParseStops("0:#2E0400", "0.35:#B41400", "0.68:#FF6C00", "0.88:#FFC400", "1:#FFF4D0"),
                    "#FFF8E8", "#FF5C00", "#1A0200", "#FF0040", 1.2),

                Make("neon-amber", "Neon Amber",
                    "Warm amber signage glow for late-night cuts.",
                    "Lo-Fi, Neo Soul, Jazz Hop",
                    ParseStops("0:#2E1C00", "0.45:#D68C00", "0.82:#FFB824", "1:#FFE8B0"),
                    "#FFF6DC", "#FFA800", "#1A1000", "#00D8FF"),

                Make("blood-red", "Blood Red",
                    "Deep arterial red, restrained and heavy.",
                    "Drum and Bass, Hardcore, Rock",
                    ParseStops("0:#1A0000", "0.45:#8C0000", "0.82:#E01C28", "1:#FFA8A8"),
                    "#FFE0E0", "#C40010", "#0D0000", "#00FF40"),

                Make("ice-steel", "Ice Steel",
                    "Cold steel blue with an icy highlight.",
                    "Techno, Ambient, Trailer Music",
                    ParseStops("0:#0C1826", "0.45:#3C7CB4", "0.82:#7CC4EC", "1:#E4F4FF"),
                    "#F0F8FF", "#5CA8E0", "#081018", "#C8E4FF", 0.95),

                Make("deep-ocean", "Deep Ocean",
                    "Abyssal navy rising to a pale surface shimmer.",
                    "Ambient, Downtempo, Deep House",
                    ParseStops("0:#020A1E", "0.42:#0A2E6E", "0.80:#1478B4", "1:#BCE8FF"),
                    "#E4F6FF", "#1C7CB4", "#01050F", "#00FFC8"),

                Make("sunset-gradient", "Sunset Gradient",
                    "Sunset orange rising into a golden horizon.",
                    "Pop, Indie Pop, Chill",
                    ParseStops("0:#B4145C", "0.34:#FF4C6C", "0.68:#FF8C3C", "1:#FFD86C"),
                    "#FFF0D0", "#FF5C8C", "#2A0418", "#7C3CFF", 1.1),

                Make("cotton-candy", "Cotton Candy",
                    "Soft pastel candy floss with cool blue tips.",
                    "K-Pop, Pop, Dream Pop",
                    ParseStops("0:#5C3C6E", "0.38:#FF9CC8", "0.74:#A8C4FF", "1:#E8F4FF"),
                    "#FFF4FC", "#FF9CC8", "#2A1E38", "#C8FFD8", 0.95),

                Make("aurora", "Aurora",
                    "Green-to-violet northern lights sweep.",
                    "Cinematic, Ambient, Classical",
                    ParseStops("0:#0A2E3C", "0.30:#00C87C", "0.62:#4C7CFF", "0.86:#9C4CFF", "1:#E8D0FF"),
                    "#E8F4FF", "#00E89C", "#061820", "#7C4CFF", 1.15),

                Make("monochrome", "Monochrome White",
                    "Clean white bars with no colour at all.",
                    "Minimal, Corporate, Lo-Fi",
                    ParseStops("0:#1A1A1A", "0.45:#8C8C8C", "0.82:#E4E4E4", "1:#FFFFFF"),
                    "#FFFFFF", "#C8C8C8", "#0A0A0A", "#FFFFFF", 0.85),

                Make("gold-chrome", "Gold Chrome",
                    "Polished gold bars with an ice-blue accent.",
                    "Luxury, Trap, Orchestral",
                    ParseStops("0:#3C2800", "0.32:#B4881C", "0.62:#F0D060", "0.85:#FFF0B4", "1:#FFFAE8"),
                    "#FFFCF0", "#E8B430", "#1C1400", "#B4E8FF"),

                Make("holographic", "Holographic Iridescent",
                    "Full rainbow shift across the whole bar height.",
                    "Pop, Anime, Gaming",
                    ParseStops("0:#00E5FF", "0.25:#7C4CFF", "0.50:#FF3C9C", "0.75:#FFD400", "1:#5CFFC8"),
                    "#FFFFFF", "#00E5FF", "#0D0A1E", "#FF3C9C", 1.2),

                Make("glitch-rgb", "Glitch RGB",
                    "Hard RGB channel split with broken colour.",
                    "Glitch, Digital Core, IDM",
                    ParseStops("0:#00FFFF", "0.40:#FF00FF", "0.72:#FF0000", "1:#FFFFFF"),
                    "#FFFFFF", "#FF00FF", "#000000", "#00FFFF"),

                Make("deep-purple-funk", "Deep Purple Funk",
                    "Deep purple funk with a gold accent.",
                    "Funk, P-Funk, Electro",
                    ParseStops("0:#1A0038", "0.45:#6C14C4", "0.82:#A44CFF", "1:#F0DCFF"),
                    "#F8F0FF", "#8C2CFF", "#0F0024", "#FFB400", 1.1),
            };
        }
    }
}