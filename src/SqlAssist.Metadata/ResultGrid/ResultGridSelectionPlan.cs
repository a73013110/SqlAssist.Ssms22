using System;
using System.Collections.Generic;
using System.Globalization;

namespace SqlAssist.Metadata.ResultGrid;

/// <summary>選取範圍裡的一塊矩形，欄索引是 0 起算的<b>資料</b>欄。</summary>
/// <remarks>
/// 格線自己的欄索引第 0 欄是列號欄，與資料欄差一。換算只在
/// <c>Ssms22/ResultGrid/</c> 那一層做，這裡收到的一律已經是資料欄——
/// 兩層都在換算的話，中間任何一次改動都會讓整份資料錯開一欄，
/// 而每一格都還是「有值」，看起來完全正常。
/// </remarks>
public readonly struct ResultGridSelectionBlock
{
    public ResultGridSelectionBlock(long top, long height, int left, int width)
    {
        Top = top;
        Height = height;
        Left = left;
        Width = width;
    }

    public long Top { get; }

    public long Height { get; }

    public int Left { get; }

    public int Width { get; }
}

/// <summary>
/// 把使用者的選取範圍換算成「要取哪幾列、哪幾欄」。
/// </summary>
/// <remarks>
/// 選取範圍<b>不保證是一個矩形</b>。實測按住 Ctrl 點六格，拿到的就是六個
/// 1×1 的區塊；拖曳整欄再加選另一欄，拿到的是兩個高瘦的區塊。
/// 因此規則是取聯集：所有被選到的列 × 所有被選到的欄。
///
/// 聯集會涵蓋一些沒有被選到的格子——Ctrl 點了 (1,A) 與 (5,C) 就會拿到
/// 2 列 × 2 欄共四格。這是刻意的：<c>INSERT</c> 與 <c>IN</c> 都需要矩形的資料，
/// 而挖洞的那一份要嘛補 <c>NULL</c>（值就錯了），要嘛拆成好幾段（貼上去更難用）。
/// 產出的指令碼開頭會寫明實際的形狀，使用者一眼就看得出範圍被撐開了。
/// </remarks>
public static class ResultGridSelectionPlan
{
    /// <summary>
    /// 一次最多取幾格。
    /// </summary>
    /// <remarks>
    /// 用格數而不是列數：實測的查詢有 178 欄，1000 列就是 17.8 萬格，
    /// 產出的指令碼是幾十 MB——那不是使用者要的東西，而且組字串的時候
    /// SSMS 會整個沒有反應。列數的門檻在寬表與窄表上差太多，擋不住這一種。
    /// </remarks>
    public const int MaxCells = 200_000;

    /// <param name="blocks">
    /// 使用者選取的區塊；空的代表沒有選取，那時候取整份結果。
    /// </param>
    /// <param name="rows">要取的列索引，遞增。</param>
    /// <param name="columns">要取的資料欄索引，遞增。</param>
    /// <param name="isWholeResult">聯集是不是剛好涵蓋了整份結果。</param>
    /// <param name="failure">回傳 false 時說明原因。</param>
    public static bool TryResolve(
        IReadOnlyList<ResultGridSelectionBlock> blocks,
        long totalRows,
        int totalColumns,
        out IReadOnlyList<long> rows,
        out IReadOnlyList<int> columns,
        out bool isWholeResult,
        out string failure)
    {
        rows = Array.Empty<long>();
        columns = Array.Empty<int>();
        isWholeResult = false;
        failure = string.Empty;

        if (totalRows <= 0 || totalColumns <= 0)
        {
            failure = "這份結果沒有資料列可以取。";
            return false;
        }

        var rowSet = new SortedSet<long>();
        var columnSet = new SortedSet<int>();

        foreach (var block in blocks)
        {
            var firstRow = Math.Max(block.Top, 0);
            var lastRow = Math.Min(block.Top + block.Height - 1, totalRows - 1);

            for (var row = firstRow; row <= lastRow; row++)
            {
                rowSet.Add(row);

                // 選了幾百萬列的時候，光是把列號放進集合就已經太久了。
                // 這裡先擋一次，後面還會用實際的格數再擋一次。
                if (rowSet.Count > MaxCells)
                {
                    failure = TooLarge(rowSet.Count, totalColumns);
                    return false;
                }
            }

            var firstColumn = Math.Max(block.Left, 0);
            var lastColumn = Math.Min(block.Left + block.Width - 1, totalColumns - 1);

            for (var column = firstColumn; column <= lastColumn; column++)
            {
                columnSet.Add(column);
            }
        }

        // 沒有選取就是整份結果。這是刻意的預設：使用者剛跑完一個小查詢、
        // 什麼都還沒點就按右鍵，最可能想要的就是整份。
        if (rowSet.Count == 0 || columnSet.Count == 0)
        {
            rowSet.Clear();
            columnSet.Clear();

            for (var row = 0L; row < totalRows; row++)
            {
                rowSet.Add(row);
            }

            for (var column = 0; column < totalColumns; column++)
            {
                columnSet.Add(column);
            }
        }

        var cells = (long)rowSet.Count * columnSet.Count;

        if (cells > MaxCells)
        {
            failure = TooLarge(rowSet.Count, columnSet.Count);
            return false;
        }

        var resolvedRows = new long[rowSet.Count];
        rowSet.CopyTo(resolvedRows);
        var resolvedColumns = new int[columnSet.Count];
        columnSet.CopyTo(resolvedColumns);

        rows = resolvedRows;
        columns = resolvedColumns;
        isWholeResult = resolvedRows.Length == totalRows && resolvedColumns.Length == totalColumns;
        return true;
    }

    private static string TooLarge(long rows, long columns) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "選取範圍是 {0} 欄 × {1} 列，共 {2} 格，超過一次 {3} 格的上限。請先縮小選取範圍。",
            columns,
            rows,
            rows * columns,
            MaxCells);
}
