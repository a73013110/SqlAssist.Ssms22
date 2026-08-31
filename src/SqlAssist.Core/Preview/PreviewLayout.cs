using System;
using System.Collections.Generic;
using SqlAssist.Core.Settings;

namespace SqlAssist.Core.Preview;

/// <summary>預覽最後實際落在哪個方向。</summary>
public enum PreviewPlacementSide
{
    Below,
    Above,
    Right,
    Left
}

/// <summary>拖曳中的角落；左右角各自固定另一側邊界。</summary>
public enum PreviewResizeCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>一次定位所需的完整且同座標系輸入。</summary>
public sealed class PreviewLayoutRequest
{
    public SqlPreviewPlacement Placement { get; init; }

    public PreviewRectangle Anchor { get; init; }

    public PreviewRectangle AvailableBounds { get; init; }

    public IReadOnlyList<PreviewRectangle> Obstacles { get; init; } = Array.Empty<PreviewRectangle>();

    public double DesiredWidth { get; init; }

    public double DesiredHeight { get; init; }

    public double MinimumWidth { get; init; }

    public double MinimumHeight { get; init; }

    public double MaximumWidth { get; init; } = double.PositiveInfinity;

    public double MaximumHeight { get; init; } = double.PositiveInfinity;

    /// <summary>上下擺放尚未手動調寬時，從錨點自動延伸到右界。</summary>
    public bool StretchStackedWidth { get; init; }

    public double Gap { get; init; } = 4;

    /// <summary>同一次顯示先沿用上一個可行方向，避免 1 DIP 捨入或提示出現時來回翻面。</summary>
    public PreviewPlacementSide? PreviousSide { get; init; }
}

/// <summary>純定位計算的結果。</summary>
public readonly struct PreviewLayout
{
    public PreviewLayout(
        PreviewRectangle bounds,
        PreviewPlacementSide side,
        bool usedFallback,
        bool sizeConstrained)
    {
        Bounds = bounds;
        Side = side;
        UsedFallback = usedFallback;
        SizeConstrained = sizeConstrained;
    }

    public PreviewRectangle Bounds { get; }

    public PreviewPlacementSide Side { get; }

    /// <summary>選了側邊擺放，但兩側都不可用而改放上下。</summary>
    public bool UsedFallback { get; }

    public bool SizeConstrained { get; }
}
