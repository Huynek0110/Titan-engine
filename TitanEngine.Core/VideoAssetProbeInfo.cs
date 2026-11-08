using System;

namespace TitanEngine
{
    public sealed class VideoAssetProbeInfo
    {
        public string FilePath { get; set; } = string.Empty;
        public bool Exists { get; set; }
        public bool ProbeSucceeded { get; set; }
        public bool HasVideoStream { get; set; }
        public long FileSize { get; set; }
        public DateTime LastModifiedTimeUtc { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double Duration { get; set; }
        public double Fps { get; set; }
        public string CodecName { get; set; } = string.Empty;
        public string PixelFormat { get; set; } = string.Empty;
        public string FailureReason { get; set; } = string.Empty;
    }
}
