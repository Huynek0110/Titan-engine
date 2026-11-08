using System;
using System.Windows.Media;

namespace TitanEngine
{
    /// <summary>
    /// WPF-stay phần màu của SoundBarPalette (Giai đoạn 1 đính chính): TryParseHex + 4 thuộc tính màu
    /// chuyển từ record ColorPreset (Core) sang hàm tĩnh nhận ColorPreset — WPF assembly không thể
    /// thêm member vào record của Core. Cú pháp gọi đổi từ `preset.CoreColor` thành
    /// `SoundBarPaletteArt.CoreColor(preset)` (4 call site trong SoundBarStyleRenderer, đã sed).
    /// ToBrushStops() gốc (GradientStopCollection) KHÔNG chuyển: 0 caller trong repo, phase 4 (Skia)
    /// sẽ dựng gradient mới. Nibble là helper riêng của TryParseHex.
    /// </summary>
    public static class SoundBarPaletteArt
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

        /// <summary>Returns the opaque <see cref="Color"/> for <see cref="ColorPreset.Core"/>, falling back to white.</summary>
        public static Color CoreColor(ColorPreset preset) => TryParseHex(preset.Core, out Color color) ? color : Colors.White;

        /// <summary>Returns the opaque <see cref="Color"/> for <see cref="ColorPreset.Glow"/>, falling back to white.</summary>
        public static Color GlowColor(ColorPreset preset) => TryParseHex(preset.Glow, out Color color) ? color : Colors.White;

        /// <summary>Returns the opaque <see cref="Color"/> for <see cref="ColorPreset.BgTint"/>, falling back to black.</summary>
        public static Color BgTintColor(ColorPreset preset) => TryParseHex(preset.BgTint, out Color color) ? color : Colors.Black;

        /// <summary>Returns the opaque <see cref="Color"/> for <see cref="ColorPreset.Accent"/>, falling back to white.</summary>
        public static Color AccentColor(ColorPreset preset) => TryParseHex(preset.Accent, out Color color) ? color : Colors.White;

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
}
