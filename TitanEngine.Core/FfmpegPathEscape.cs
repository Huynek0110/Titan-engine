using System;

namespace TitanEngine
{
    /// <summary>
    /// Giai đoạn 3: escape đường dẫn nhúng vào filter graph FFmpeg (ass=, drawtext fontfile=,
    /// libplacebo custom_shader_path=, movie=).
    /// Windows: GIỮ NGUYÊN thuật toán đang chạy production: \ → /, : → \: (nhét trong '...').
    /// Linux: cùng lớp escape \: cho dấu :, cộng xử lý dấu ' (đường dẫn Linux hợp lệ có dấu '
    /// nhưng không thể Replace \ → / vì \ là ký tự hợp lệ trong tên file Linux).
    /// Kết quả cuối luôn được call site bọc trong '...' như bản gốc.
    /// Phép test thật (CLI `test-escape`) phải pass với đường dẫn có khoảng trắng,
    /// tiếng Việt, dấu ' và dấu : trước khi coi là xong.
    /// </summary>
    public static class FfmpegPathEscape
    {
        /// <summary>Escape 1 đường dẫn cho filter option — chọn thuật toán theo OS hiện tại.</summary>
        public static string EscapeForFilterOption(string path)
            => TitanPlatform.IsWindows ? EscapeWindows(path) : EscapeLinux(path);

        /// <summary>Thuật toán Windows nguyên bản (đừng đổi — production đang chạy).</summary>
        public static string EscapeWindows(string path)
            => path.Replace("\\", "/").Replace(":", "\\:");

        /// <summary>
        /// Thuật toán Linux: giữ nguyên \ (hợp lệ trong tên file), escape : như bản gốc,
        /// escape ' theo cơ chế parser đã được chứng minh bằng test thật (xem CLI test-escape).
        /// </summary>
        public static string EscapeLinux(string path)
            => path.Replace(":", "\\:").Replace("'", "'\\''");
    }
}
