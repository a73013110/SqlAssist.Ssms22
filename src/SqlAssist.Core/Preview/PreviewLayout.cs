using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Preview;

/// <summary>預覽最後落在錨點的哪一側。</summary>
public enum PreviewPlacementSide
{
    Below,
    Above
}

/// <summary>拖曳的是哪一個把手：抬頭搬動整個視窗，角落握把固定對角改尺寸。</summary>
public enum PreviewDragHandle
{
    Move,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>一次定位所需的完整且同座標系輸入。</summary>
public sealed class PreviewLayoutRequest
{
    public PreviewRectangle Anchor { get; init; }

    public PreviewRectangle AvailableBounds { get; init; }

    public IReadOnlyList<PreviewRectangle> Obstacles { get; init; } = Array.Empty<PreviewRectangle>();

    public double DesiredWidth { get; init; }

    public double DesiredHeight { get; init; }

    public double MinimumWidth { get; init; }

    public double MinimumHeight { get; init; }

    public double MaximumWidth { get; init; } = double.PositiveInfinity;

    public double MaximumHeight { get; init; } = double.PositiveInfinity;

    /// <summary>尚未手動調寬：從錨點自動延伸到右界，<see cref="DesiredWidth"/> 不用。</summary>
    public bool StretchWidth { get; init; }

    public double Gap { get; init; } = 4;

    /// <summary>同一次顯示先沿用上一個可行方向，避免 1 DIP 捨入或提示出現時來回翻面。</summary>
    public PreviewPlacementSide? PreviousSide { get; init; }
}

/// <summary>純定位計算的結果。</summary>
public readonly struct PreviewLayout
{
    public PreviewLayout(PreviewRectangle bounds, PreviewPlacementSide side)
    {
        Bounds = bounds;
        Side = side;
    }

    public PreviewRectangle Bounds { get; }

    public PreviewPlacementSide Side { get; }
}
