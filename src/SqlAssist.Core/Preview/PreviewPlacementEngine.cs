using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.Core.Preview;

/// <summary>
/// 預覽視窗的純定位規則。平台端只負責把 WPF 與螢幕座標轉成這裡的同一座標系。
/// </summary>
public static class PreviewPlacementEngine
{
    private const double Epsilon = 0.01;

    public static PreviewLayout Calculate(PreviewLayoutRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var available = Normalize(request.AvailableBounds);
        var anchor = Normalize(request.Anchor);
        if (available.IsEmpty || anchor.IsEmpty)
        {
            return new PreviewLayout(default, PreviewPlacementSide.Below);
        }

        var minimumWidth = Math.Min(Positive(request.MinimumWidth), available.Width);
        var minimumHeight = Math.Min(Positive(request.MinimumHeight), available.Height);
        var maximumWidth = Math.Min(PositiveOrInfinity(request.MaximumWidth), available.Width);
        var maximumHeight = Math.Min(PositiveOrInfinity(request.MaximumHeight), available.Height);
        var desiredHeight = Clamp(
            Positive(request.DesiredHeight),
            Math.Min(minimumHeight, maximumHeight),
            maximumHeight);

        var requestedWidth = request.StretchWidth
            ? Math.Max(minimumWidth, available.Right - Clamp(anchor.Left, available.Left, available.Right))
            : Positive(request.DesiredWidth);
        var width = Clamp(
            requestedWidth,
            Math.Min(minimumWidth, maximumWidth),
            maximumWidth);

        return Place(request, available, width, desiredHeight);
    }

    private static PreviewLayout Place(
        PreviewLayoutRequest request,
        PreviewRectangle available,
        double desiredWidth,
        double desiredHeight)
    {
        // 錨點靠近右界時只把整個視窗平移到界內，不把它交給 Popup 再做一次翻轉。
        var left = Clamp(request.Anchor.Left, available.Left, available.Right - desiredWidth);
        var horizontalRange = new Segment(left, left + desiredWidth);
        var free = FreeSegments(
            available.Top,
            available.Bottom,
            ValidObstacles(request)
                .Select(obstacle => obstacle.Inflate(request.Gap))
                .Where(obstacle => Overlaps(horizontalRange, new Segment(obstacle.Left, obstacle.Right)))
                .Select(obstacle => new Segment(obstacle.Top, obstacle.Bottom)));

        var belowStart = request.Anchor.Bottom + request.Gap;
        var belowFits = TryFindRight(free, belowStart, desiredHeight, out var below);
        var aboveEnd = request.Anchor.Top - request.Gap;
        var aboveFits = TryFindLeft(free, aboveEnd, desiredHeight, out var above);

        if (request.PreviousSide == PreviewPlacementSide.Above && aboveFits)
        {
            return Result(left, above.End - desiredHeight, desiredWidth, desiredHeight, PreviewPlacementSide.Above);
        }

        if (request.PreviousSide == PreviewPlacementSide.Below && belowFits)
        {
            return Result(left, below.Start, desiredWidth, desiredHeight, PreviewPlacementSide.Below);
        }

        if (belowFits)
        {
            return Result(left, below.Start, desiredWidth, desiredHeight, PreviewPlacementSide.Below);
        }

        if (aboveFits)
        {
            return Result(left, above.End - desiredHeight, desiredWidth, desiredHeight, PreviewPlacementSide.Above);
        }

        // 正常高度放不下後比較上下最大可用量；方向偏好只負責接近平手時的 tie-break。
        var belowRemainder = FindLargestRight(free, belowStart);
        var aboveRemainder = FindLargestLeft(free, aboveEnd);
        if (belowRemainder.Length > Epsilon || aboveRemainder.Length > Epsilon)
        {
            var belowHeight = Math.Min(desiredHeight, belowRemainder.Length);
            var aboveHeight = Math.Min(desiredHeight, aboveRemainder.Length);
            var hysteresis = Hysteresis(request);
            var chooseAbove = aboveHeight > belowHeight + hysteresis ||
                              request.PreviousSide == PreviewPlacementSide.Above &&
                              aboveHeight + hysteresis >= belowHeight;
            if (!chooseAbove)
            {
                return Result(
                    left,
                    belowRemainder.Start,
                    desiredWidth,
                    belowHeight,
                    PreviewPlacementSide.Below);
            }

            return Result(
                left,
                aboveRemainder.End - aboveHeight,
                desiredWidth,
                aboveHeight,
                PreviewPlacementSide.Above);
        }

        // 極小視窗的最後防線：挑上下兩個候選裡與既有浮窗重疊最少的一個。
        var constrainedHeight = Math.Min(desiredHeight, available.Height);
        var belowTop = Clamp(belowStart, available.Top, available.Bottom - constrainedHeight);
        var aboveTop = Clamp(aboveEnd - constrainedHeight, available.Top, available.Bottom - constrainedHeight);
        var belowBounds = new PreviewRectangle(left, belowTop, desiredWidth, constrainedHeight);
        var aboveBounds = new PreviewRectangle(left, aboveTop, desiredWidth, constrainedHeight);
        var obstacles = ValidObstacles(request).ToArray();
        var belowOverlap = OverlapArea(belowBounds, obstacles);
        var aboveOverlap = OverlapArea(aboveBounds, obstacles);

        return belowOverlap <= aboveOverlap
            ? Result(left, belowTop, desiredWidth, constrainedHeight, PreviewPlacementSide.Below)
            : Result(left, aboveTop, desiredWidth, constrainedHeight, PreviewPlacementSide.Above);
    }

    private static PreviewLayout Result(
        double left,
        double top,
        double width,
        double height,
        PreviewPlacementSide side) =>
        new(new PreviewRectangle(left, top, width, height), side);

    /// <summary>
    /// 方向偏好只在差距明顯時才讓位。
    /// </summary>
    /// <remarks>
    /// 正常路徑與剩餘量比較都用同一個量：分開寫的話，改了其中一個而另一個沒改，
    /// 症狀是「某些高度會抖，另一些不會」，而且看不出關聯。
    /// 下限 8 是為了 Gap 很小時仍然擋得住 DPI 捨入的一兩個像素。
    /// </remarks>
    private static double Hysteresis(PreviewLayoutRequest request) =>
        Math.Max(8, Positive(request.Gap) * 2);

    private static IReadOnlyList<Segment> FreeSegments(
        double start,
        double end,
        IEnumerable<Segment> blockedSegments)
    {
        var blocked = blockedSegments
            .Select(segment => new Segment(Math.Max(start, segment.Start), Math.Min(end, segment.End)))
            .Where(segment => segment.Length > Epsilon)
            .OrderBy(segment => segment.Start)
            .ToArray();

        var merged = new List<Segment>();
        foreach (var segment in blocked)
        {
            if (merged.Count == 0 || segment.Start > merged[merged.Count - 1].End + Epsilon)
            {
                merged.Add(segment);
                continue;
            }

            var previous = merged[merged.Count - 1];
            merged[merged.Count - 1] = new Segment(previous.Start, Math.Max(previous.End, segment.End));
        }

        var free = new List<Segment>();
        var cursor = start;
        foreach (var segment in merged)
        {
            if (segment.Start > cursor + Epsilon)
            {
                free.Add(new Segment(cursor, segment.Start));
            }

            cursor = Math.Max(cursor, segment.End);
        }

        if (cursor < end - Epsilon)
        {
            free.Add(new Segment(cursor, end));
        }

        return free;
    }

    /// <summary>從 <paramref name="start"/> 起往正向找第一個放得下 <paramref name="length"/> 的空檔。</summary>
    private static bool TryFindRight(
        IReadOnlyList<Segment> free,
        double start,
        double length,
        out Segment result)
    {
        foreach (var segment in free)
        {
            var candidateStart = Math.Max(start, segment.Start);
            if (segment.End - candidateStart + Epsilon >= length)
            {
                result = new Segment(candidateStart, Math.Min(segment.End, candidateStart + length));
                return true;
            }
        }

        result = default;
        return false;
    }

    private static Segment FindLargestRight(IReadOnlyList<Segment> free, double start)
    {
        var best = default(Segment);
        foreach (var segment in free)
        {
            var candidate = new Segment(Math.Max(start, segment.Start), segment.End);
            if (candidate.Length > best.Length)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static Segment FindLargestLeft(IReadOnlyList<Segment> free, double end)
    {
        var best = default(Segment);
        foreach (var segment in free)
        {
            var candidate = new Segment(segment.Start, Math.Min(end, segment.End));
            if (candidate.Length > best.Length)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>從 <paramref name="end"/> 起往負向找第一個放得下 <paramref name="length"/> 的空檔。</summary>
    private static bool TryFindLeft(
        IReadOnlyList<Segment> free,
        double end,
        double length,
        out Segment result)
    {
        for (var index = free.Count - 1; index >= 0; index--)
        {
            var segment = free[index];
            var candidateEnd = Math.Min(end, segment.End);
            if (candidateEnd - segment.Start + Epsilon >= length)
            {
                result = new Segment(Math.Max(segment.Start, candidateEnd - length), candidateEnd);
                return true;
            }
        }

        result = default;
        return false;
    }

    private static double OverlapArea(
        PreviewRectangle bounds,
        IReadOnlyList<PreviewRectangle> obstacles)
    {
        var area = 0.0;
        foreach (var obstacle in obstacles)
        {
            var width = Math.Max(0, Math.Min(bounds.Right, obstacle.Right) - Math.Max(bounds.Left, obstacle.Left));
            var height = Math.Max(0, Math.Min(bounds.Bottom, obstacle.Bottom) - Math.Max(bounds.Top, obstacle.Top));
            area += width * height;
        }

        return area;
    }

    private static bool Overlaps(Segment left, Segment right) =>
        left.Start < right.End - Epsilon && right.Start < left.End - Epsilon;

    /// <summary>
    /// 把整個錨點蓋住的保留區不算障礙。
    /// </summary>
    /// <remarks>
    /// 建議清單開著時，平台會連錨點所在的一整行一起保留——橫跨整個編輯器而只有一個行高。
    /// 它表達的是「錨點在這裡」，不是「這裡有一個浮窗」；上下擺放的起點本來就從錨點上下緣
    /// 算起，再把它當障礙只會讓間距多算一次。只排除「完全蓋住」錨點的那一種：改用相交會把
    /// 緊貼錨點下緣、DPI 捨入後重疊一兩個像素的建議清單也一起排掉，那才是真正必須讓開的東西。
    /// </remarks>
    private static IEnumerable<PreviewRectangle> ValidObstacles(PreviewLayoutRequest request) =>
        (request.Obstacles ?? Array.Empty<PreviewRectangle>())
        .Where(obstacle => !Normalize(obstacle).IsEmpty)
        .Where(obstacle => !Covers(obstacle, request.Anchor));

    private static bool Covers(PreviewRectangle obstacle, PreviewRectangle anchor) =>
        obstacle.Left <= anchor.Left + Epsilon &&
        obstacle.Top <= anchor.Top + Epsilon &&
        obstacle.Right + Epsilon >= anchor.Right &&
        obstacle.Bottom + Epsilon >= anchor.Bottom;

    private static PreviewRectangle Normalize(PreviewRectangle rectangle)
    {
        if (!IsFinite(rectangle.Left) ||
            !IsFinite(rectangle.Top) ||
            !IsFinite(rectangle.Width) ||
            !IsFinite(rectangle.Height))
        {
            return default;
        }

        return rectangle;
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

    private readonly struct Segment
    {
        public Segment(double start, double end)
        {
            Start = start;
            End = end;
        }

        public double Start { get; }

        public double End { get; }

        public double Length => Math.Max(0, End - Start);
    }
}
