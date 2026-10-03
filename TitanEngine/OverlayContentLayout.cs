using System;
using System.Windows;

namespace TitanEngine
{
    public sealed class OverlayContentLayout
    {
        public int CanvasWidth { get; init; }
        public int CanvasHeight { get; init; }
        public Rect ContentRect { get; init; }

        public double ContentX => ContentRect.X;
        public double ContentY => ContentRect.Y;
        public double ContentWidth => ContentRect.Width;
        public double ContentHeight => ContentRect.Height;
        public double ContentMinDimension => Math.Min(ContentRect.Width, ContentRect.Height);
    }

    public static class OverlayLayoutHelper
    {
        public const double HdReferenceMinDimension = 1080.0;

        public static OverlayContentLayout Create(int canvasWidth, int canvasHeight, int sourceWidth, int sourceHeight)
        {
            int safeCanvasWidth = Math.Max(2, canvasWidth);
            int safeCanvasHeight = Math.Max(2, canvasHeight);

            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                return new OverlayContentLayout
                {
                    CanvasWidth = safeCanvasWidth,
                    CanvasHeight = safeCanvasHeight,
                    ContentRect = new Rect(0, 0, safeCanvasWidth, safeCanvasHeight)
                };
            }

            double scale = Math.Min(
                safeCanvasWidth / (double)sourceWidth,
                safeCanvasHeight / (double)sourceHeight);

            double contentWidth = Math.Max(2.0, sourceWidth * scale);
            double contentHeight = Math.Max(2.0, sourceHeight * scale);
            double contentX = (safeCanvasWidth - contentWidth) / 2.0;
            double contentY = (safeCanvasHeight - contentHeight) / 2.0;

            return new OverlayContentLayout
            {
                CanvasWidth = safeCanvasWidth,
                CanvasHeight = safeCanvasHeight,
                ContentRect = new Rect(contentX, contentY, contentWidth, contentHeight)
            };
        }

        public static double GetHdReferenceScale(OverlayContentLayout layout)
        {
            double minDimension = Math.Max(160.0, layout?.ContentMinDimension ?? HdReferenceMinDimension);
            return Math.Clamp(minDimension / HdReferenceMinDimension, 0.22, 4.0);
        }

        public static double ScaleFromHdReference(OverlayContentLayout layout, double hdReferenceValue, double minValue, double maxValue)
        {
            double scaled = hdReferenceValue * GetHdReferenceScale(layout);
            return Math.Clamp(scaled, minValue, maxValue);
        }
    }
}
