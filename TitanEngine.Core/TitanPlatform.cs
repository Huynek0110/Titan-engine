using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TitanEngine
{
    /// <summary>
    /// Giai đoạn 3: thông tin platform + helper theo OS.
    /// Windows giữ nguyên hành vi bản gốc (tên binary có .exe, đường dẫn cạnh app);
    /// Linux thêm tên binary không đuôi + kiểm execute bit + tìm trên PATH.
    /// </summary>
    public static class TitanPlatform
    {
        public enum OsKind { Windows, Linux, MacOS, Unknown }

        public static OsKind CurrentOs { get; }

        public static bool IsWindows => CurrentOs == OsKind.Windows;
        public static bool IsLinux => CurrentOs == OsKind.Linux;
        public static bool IsMacOS => CurrentOs == OsKind.MacOS;

        static TitanPlatform()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) CurrentOs = OsKind.Windows;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) CurrentOs = OsKind.Linux;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) CurrentOs = OsKind.MacOS;
            else CurrentOs = OsKind.Unknown;
        }

        /// <summary>Tên OS cho doctor/log ("windows" / "linux" / "osx" / "unknown").</summary>
        public static string OsName => CurrentOs switch
        {
            OsKind.Windows => "windows",
            OsKind.Linux => "linux",
            OsKind.MacOS => "osx",
            _ => "unknown",
        };

        /// <summary>
        /// Tên binary thực thi theo OS: Windows thêm ".exe" (ffmpeg.exe như bản gốc),
        /// Linux/macOS giữ tên thường (ffmpeg, whisper-cli).
        /// </summary>
        public static string BinaryName(string baseName)
            => IsWindows ? baseName + ".exe" : baseName;

        /// <summary>
        /// Linux: kiểm bit execute của file (đòi hỏi của đề bài Giai đoạn 3).
        /// Trả true khi không cần kiểm (Windows/Unknown) hoặc file có bit execute.
        /// </summary>
        public static bool IsExecutable(string filePath)
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                return true;
            try
            {
                var mode = File.GetUnixFileMode(filePath);
                const UnixFileMode AnyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                return (mode & AnyExecute) != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Tìm file thực thi trên PATH (Linux: "ffmpeg"; Windows: thêm ".exe" như FindExecutableOnPath gốc).</summary>
        public static string? FindOnPath(string baseName)
        {
            string fileName = BinaryName(baseName);
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate) && IsExecutable(candidate))
                        return candidate;
                }
                catch { }
            }
            return null;
        }
    }
}
