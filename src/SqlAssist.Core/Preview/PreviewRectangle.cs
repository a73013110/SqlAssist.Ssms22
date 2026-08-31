using System;

namespace SqlAssist.Core.Preview;

/// <summary>
/// 與 UI 框架無關的矩形；預覽定位在 Core 裡只處理同一座標系的數值。
/// </summary>
public readonly struct PreviewRectangle : IEquatable<PreviewRectangle>
{
    public PreviewRectangle(double left, double top, double width, double height)
    {
        Left = left;
        Top = top;
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
    }

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }

    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Intersects(PreviewRectangle other) =>
        !IsEmpty &&
        !other.IsEmpty &&
        Left < other.Right &&
        other.Left < Right &&
        Top < other.Bottom &&
        other.Top < Bottom;

    public PreviewRectangle Inflate(double amount)
    {
        var safeAmount = Math.Max(0, amount);
        return new PreviewRectangle(
            Left - safeAmount,
            Top - safeAmount,
            Width + safeAmount * 2,
            Height + safeAmount * 2);
    }

    public bool Equals(PreviewRectangle other) =>
        Left.Equals(other.Left) &&
        Top.Equals(other.Top) &&
        Width.Equals(other.Width) &&
        Height.Equals(other.Height);

    public override bool Equals(object? obj) => obj is PreviewRectangle other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + Left.GetHashCode();
            hash = hash * 31 + Top.GetHashCode();
            hash = hash * 31 + Width.GetHashCode();
            hash = hash * 31 + Height.GetHashCode();
            return hash;
        }
    }

    public static bool operator ==(PreviewRectangle left, PreviewRectangle right) => left.Equals(right);

    public static bool operator !=(PreviewRectangle left, PreviewRectangle right) => !left.Equals(right);
}
