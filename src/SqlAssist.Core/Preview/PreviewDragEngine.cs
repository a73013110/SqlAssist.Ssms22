using System;

namespace SqlAssist.Core.Preview;

/// <summary>
/// 依固定起始矩形與游標總位移計算拖曳結果，不累積任何上一幀狀態。
/// </summary>
/// <remarks>
/// 搬動與改尺寸是同一件事的兩種把手：都從按下瞬間凍結的矩形出發、都收在同一個可用範圍裡。
/// 分成兩套的話，其中一套遲早少了「路徑無關」或「邊界安全」其中一條。
/// </remarks>
public static class PreviewDragEngine
{
    /// <summary>依把手分派：抬頭平移整個視窗，角落握把固定對角改尺寸。</summary>
    public static PreviewRectangle Drag(
        PreviewRectangle initial,
        PreviewDragHandle handle,
        double horizontalChange,
        double verticalChange,
        PreviewRectangle availableBounds,
        double minimumWidth,
        double minimumHeight,
        double maximumWidth,
        double maximumHeight) =>
        handle == PreviewDragHandle.Move
            ? Move(initial, horizontalChange, verticalChange, availableBounds)
            : Resize(
                initial,
                handle,
                horizontalChange,
                verticalChange,
                availableBounds,
                minimumWidth,
                minimumHeight,
                maximumWidth,
                maximumHeight);

    /// <summary>平移整個矩形，尺寸不變，推到邊界就停在邊界上。</summary>
    public static PreviewRectangle Move(
        PreviewRectangle initial,
        double horizontalChange,
        double verticalChange,
        PreviewRectangle availableBounds)
    {
        var available = Normalize(availableBounds);
        var start = Normalize(initial);
        if (available.IsEmpty || start.IsEmpty)
        {
            return start;
        }

        horizontalChange = double.IsNaN(horizontalChange) ? 0 : horizontalChange;
        verticalChange = double.IsNaN(verticalChange) ? 0 : verticalChange;
        return Contain(
            new PreviewRectangle(start.Left + horizontalChange, start.Top + verticalChange, start.Width, start.Height),
            available,
            start.Width,
            start.Height);
    }

    /// <summary>
    /// 把矩形收進可用範圍：先平移，平移不夠才縮小，但不縮到最小尺寸以下。
    /// </summary>
    /// <remarks>
    /// 釘住的視窗在編輯器換大小、結果窗格拉高或換螢幕之後用這一條回到界內；先平移是因為
    /// 使用者選的是尺寸，位置只是「大概放在那裡」。
    /// </remarks>
    public static PreviewRectangle Contain(
        PreviewRectangle rectangle,
        PreviewRectangle availableBounds,
        double minimumWidth,
        double minimumHeight)
    {
        var available = Normalize(availableBounds);
        var value = Normalize(rectangle);
        if (available.IsEmpty || value.IsEmpty)
        {
            return value;
        }

        var width = Clamp(value.Width, Math.Min(Positive(minimumWidth), available.Width), available.Width);
        var height = Clamp(value.Height, Math.Min(Positive(minimumHeight), available.Height), available.Height);
        return new PreviewRectangle(
            Clamp(value.Left, available.Left, available.Right - width),
            Clamp(value.Top, available.Top, available.Bottom - height),
            width,
            height);
    }

    /// <summary>
    /// 依被拖曳的角落改變矩形；相對的水平與垂直邊界保持不動。
    /// </summary>
    public static PreviewRectangle Resize(
        PreviewRectangle initial,
        PreviewDragHandle corner,
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

        var onTop = corner is PreviewDragHandle.TopLeft or PreviewDragHandle.TopRight;
        var onLeft = corner is PreviewDragHandle.TopLeft or PreviewDragHandle.BottomLeft;
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
