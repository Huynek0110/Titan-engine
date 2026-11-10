using System;

namespace TitanEngine
{
    /// <summary>
    /// Giai đoạn 3: escape đường dẫn nhúng vào filter graph FFmpeg (ass=, drawtext fontfile=,
    /// libplacebo custom_shader_path=, movie=).
    ///
    /// EscapeForFilterOption trả về GIÁ TRỊ OPTION HOÀN CHỈNH — call site nhét thẳng vào
    /// filter string, KHÔNG bọc thêm '...' (đã chuyển vào trong hàm):
    /// - Windows: '{path escape nguyên bản}' — byte-identical với chuỗi production cũ.
    /// - Linux: escape HAI MỨC theo "Notes on filtergraph escaping" của ffmpeg, KHÔNG bọc quote.
    ///
    /// Linux hai mức (đã chứng minh bằng ffmpeg thật, xem CLI test-escape + phân tích):
    /// parser graph strip quote/escape mức 2 rồi parser option parse lại mức 1, nên
    /// `\:` bọc trong '...' chỉ sống khi đường dẫn không chứa dấu ' — khi có dấu '
    /// mức 1 nhận quote thô rồi nuốt phần còn lại (bug gốc). Cách đúng:
    ///   mức 1 (option value): \ ' :  →  \\ \' \:
    ///   mức 2 (filtergraph):  \ ' , ; [ ]  →  \\ \' \, \; \[ \]
    /// Không dùng '...' ở mức 2 vì ' luôn đóng/mở quote ở cả hai mức, không thể đưa
    /// quote thô vào nội dung. Hạn chế đã biết: đường dẫn bắt đầu/kết thúc bằng
    /// khoảng trắng bị tokenizer trim (cả hai mức).
    /// Phép test thật (CLI `test-escape`) phải pass với đường dẫn có khoảng trắng,
    /// tiếng Việt, dấu ' và dấu : trước khi coi là xong.
    /// </summary>
    public static class FfmpegPathEscape
    {
        /// <summary>Escape 1 đường dẫn cho filter option (bao gồm quote bọc nếu OS cần) — chọn theo OS hiện tại.</summary>
        public static string EscapeForFilterOption(string path)
            => TitanPlatform.IsWindows ? $"'{EscapeWindows(path)}'" : EscapeLinux(path);

        /// <summary>
        /// Thuật toán Windows nguyên bản (đừng đổi — production đang chạy): \ → /, : → \:.
        /// Trả về NỘI DUNG, không kèm quote — call site cũ bọc '{...}' (BuildTemplateDrawtext
        /// vẫn dùng đường này qua ResolveFfmpegFontFile).
        /// </summary>
        public static string EscapeWindows(string path)
            => path.Replace("\\", "/").Replace(":", "\\:");

        /// <summary>
        /// Thuật toán Linux: escape hai mức, KHÔNG bọc quote (xem summary lớp).
        /// Thứ tự bắt buộc: \ trước ' và : (quote thêm \ mới không bị nhân đôi).
        /// </summary>
        public static string EscapeLinux(string path)
        {
            string level1 = path
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace(":", "\\:");
            return level1
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace(",", "\\,")
                .Replace(";", "\\;")
                .Replace("[", "\\[")
                .Replace("]", "\\]");
        }
    }
}
