using System;

namespace SqlAssist.Core.Preview;

/// <summary>依固定起始矩形與游標總位移計算預覽尺寸，不累積任何上一幀狀態。</summary>
public static class PreviewResizeEngine
{
    /// <summary>
    /// 依被拖曳的角落改變矩形；相對的水平與垂直邊界保持不動。
    /// </summary>
    public static PreviewRectangle Resize(
        PreviewRectangle initial,
        PreviewResizeCorner corner,
        double horizontalChange,
        double verticalChange,
        PreviewRectangle availableBounds,
        double minimumWidth,
        double minimumHeight,
        double maximumWidth,
        double maximumHeight)
    {
        var available = Normalize(availableBounds);
        var normalizedInitial = Normalize(initial);
        if (available.IsEmpty || normalizedInitial.IsEmpty)
        {
            return normalizedInitial;
        }

        initial = normalizedInitial;
        horizontalChange = double.IsNaN(horizontalChange) ? 0 : horizontalChange;
        verticalChange = double.IsNaN(verticalChange) ? 0 : verticalChange;

        var onTop = corner is PreviewResizeCorner.TopLeft or PreviewResizeCorner.TopRight;
        var onLeft = corner is PreviewResizeCorner.TopLeft or PreviewResizeCorner.BottomLeft;
        double top;
        double height;
        if (onTop)
        {
            var fixedBottom = Math.Min(initial.Bottom, available.Bottom);
            var maxHeight = Math.Min(PositiveOrInfinity(maximumHeight), fixedBottom - available.Top);
            height = Clamp(
                initial.Height - verticalChange,
                Math.Min(Positive(minimumHeight), maxHeight),
                maxHeight);
            top = fixedBottom - height;
        }
        else
        {
            var maxHeight = Math.Min(PositiveOrInfinity(maximumHeight), available.Bottom - initial.Top);
            height = Clamp(
                initial.Height + verticalChange,
                Math.Min(Positive(minimumHeight), maxHeight),
                maxHeight);
            top = initial.Top;
        }

        if (onLeft)
        {
            var fixedRight = Math.Min(initial.Right, available.Right);
            var maxWidth = Math.Min(PositiveOrInfinity(maximumWidth), fixedRight - available.Left);
            var width = Clamp(
                initial.Width - horizontalChange,
                Math.Min(Positive(minimumWidth), maxWidth),
                maxWidth);
            return new PreviewRectangle(fixedRight - width, top, width, height);
        }

        var fixedLeft = Math.Max(initial.Left, available.Left);
        var rightMaximumWidth = Math.Min(PositiveOrInfinity(maximumWidth), available.Right - fixedLeft);
        var rightWidth = Clamp(
            initial.Width + horizontalChange,
            Math.Min(Positive(minimumWidth), rightMaximumWidth),
            rightMaximumWidth);
        return new PreviewRectangle(fixedLeft, top, rightWidth, height);
    }

    private static PreviewRectangle Normalize(PreviewRectangle rectangle)
    {
        return IsFinite(rectangle.Left) &&
               IsFinite(rectangle.Top) &&
               IsFinite(rectangle.Width) &&
               IsFinite(rectangle.Height)
            ? rectangle
            : default;
    }

    private static double Positive(double value) => IsFinite(value) && value > 0 ? value : 1;

    private static double PositiveOrInfinity(double value) =>
        double.IsPositiveInfinity(value) || IsFinite(value) && value > 0
            ? value
            : double.PositiveInfinity;

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static double Clamp(double value, double minimum, double maximum)
    {
        if (maximum < minimum)
        {
            minimum = maximum;
        }

        return Math.Min(Math.Max(value, minimum), maximum);
    }
}
