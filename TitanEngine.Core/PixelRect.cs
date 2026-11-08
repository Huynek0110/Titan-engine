using System;

namespace TitanEngine
{
    /// <summary>
    /// Struct hình chữ nhật pixel của Core (thay kiểu Rect của WPF trong OverlayContentLayout).
    /// Chỉ chứa những gì preset/field code dùng: X, Y, Width, Height.
    /// </summary>
    public readonly struct PixelRect
    {
        public double X { get; init; }
        public double Y { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }

        public PixelRect(double x, double y, double width, double height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>Giống semantics kiểu Rect WPF: Left = X, Right = X + Width.</summary>
        public double Left => X;
        public double Top => Y;
        public double Right => X + Width;
        public double Bottom => Y + Height;
    }

    /// <summary>
    /// Vector 2D của Core (thay kiểu Vector của WPF cho recipe DreamyDot).
    /// Giữ tên `Vector` trong namespace TitanEngine để code moved verbatim không phải sửa;
    /// trong file WPF có using tới namespace WPF, tên trong namespace đóng win over using.
    /// Mutable + Length/LengthSquared/Normalize in-place, đúng semantics kiểu Vector WPF.
    /// </summary>
    public struct Vector
    {
        public double X { get; set; }
        public double Y { get; set; }

        public Vector(double x, double y)
        {
            X = x;
            Y = y;
        }

        /// <summary>Length — giống kiểu Vector WPF: sqrt(X² + Y²).</summary>
        public double Length => Math.Sqrt((X * X) + (Y * Y));

        /// <summary>LengthSquared — giống kiểu Vector WPF: X² + Y².</summary>
        public double LengthSquared => (X * X) + (Y * Y);

        /// <summary>
        /// Normalize — Y HỆT kiểu Vector WPF: nhân với 1/max(|x|,|y|) TRƯỚC
        /// (tránh overflow của X²+Y²), rồi chia cho Length. in-place.
        /// Kết quả số phải khớp bản Windows vì seed/particle/cache SHA-256 phụ thuộc nó.
        /// Caller trong code gốc luôn guard LengthSquared < 0.0001 trước khi gọi nên không chia 0.
        /// </summary>
        public void Normalize()
        {
            double invMax = 1.0 / Math.Max(Math.Abs(X), Math.Abs(Y));
            X *= invMax;
            Y *= invMax;

            double length = Length;
            X /= length;
            Y /= length;
        }
    }
}
