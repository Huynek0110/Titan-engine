using Microsoft.Win32;
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
using System.Media;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace TitanEngine
{
    public static partial class EngineCore
    {


        public static bool HasNvencSupport()
        {
            if (_hasNvencChecked) return _hasNvencSupport;
            _hasNvencChecked = true;

            try
            {
                if (string.IsNullOrEmpty(_ffmpegPath) || !File.Exists(_ffmpegPath))
                    return false;

                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = "-encoders",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null) return false;
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    _hasNvencSupport = output.Contains("h264_nvenc", StringComparison.OrdinalIgnoreCase);
                    _hasHevcNvencSupport = output.Contains("hevc_nvenc", StringComparison.OrdinalIgnoreCase);
                    _hasAv1NvencSupport = output.Contains("av1_nvenc", StringComparison.OrdinalIgnoreCase);
                    _hasAv1AmfSupport = output.Contains("av1_amf", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }

            return _hasNvencSupport;
        }

        public static bool HasHevcNvencSupport()
        {
            if (!_hasNvencChecked)
                _ = HasNvencSupport();

            return _hasHevcNvencSupport;
        }

        public static bool HasAv1NvencSupport()
        {
            if (!_hasNvencChecked) _ = HasNvencSupport();
            return _hasAv1NvencSupport;
        }

        public static bool HasAv1AmfSupport()
        {
            if (!_hasNvencChecked) _ = HasNvencSupport();
            return _hasAv1AmfSupport;
        }

        public static bool HasNgxDlssSupport()
        {
            bool hasDlvsrExe = !string.IsNullOrWhiteSpace(_ngxDlvsrPath) && File.Exists(_ngxDlvsrPath);
            if (hasDlvsrExe)
                return true;

            RefreshNvidiaUpscaleBackendPaths();
            return !string.IsNullOrWhiteSpace(_vfxUpscaleExePath) && File.Exists(_vfxUpscaleExePath);
        }

        private static bool ShouldUseFullPortraitCanvasForImageOverlays(RenderJob job, int canvasWidth, int canvasHeight)
        {
            if (canvasWidth <= 0 || canvasHeight <= 0 || canvasHeight <= canvasWidth)
                return false;

            IEnumerable<string> inputPaths = (job.MergeVideos && job.MergeInputPaths.Count > 0)
                ? job.MergeInputPaths
                : (string.IsNullOrWhiteSpace(job.SourcePath)
                    ? Array.Empty<string>()
                    : new[] { job.SourcePath });

            if (!inputPaths.Any() || !inputPaths.All(IsImageSourcePath))
                return false;

            if (job.TargetWidth == 1080 && job.TargetHeight == 1920)
                return true;

            return !string.IsNullOrWhiteSpace(job.Resolution) &&
                job.Resolution.Contains("Portrait Image Default", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldUsePortraitHdDefaultForImages(IReadOnlyCollection<string>? inputPaths)
        {
            return inputPaths != null &&
                   inputPaths.Count > 0 &&
                   inputPaths.All(IsImageSourcePath);
        }
    }
}
