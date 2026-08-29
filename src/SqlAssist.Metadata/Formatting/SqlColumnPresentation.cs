using System;
using System.Collections.Generic;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Formatting;

/// <summary>欄位上值得標出來的性質。</summary>
public enum SqlColumnFlag
{
    PrimaryKey,
    NotNull,
    Identity,
    Computed
}

/// <summary>
/// 欄位在介面上的語意事實。
/// </summary>
/// <remarks>
/// 滑鼠提示、結構表格的徽章與建議清單各自判斷「這個欄位要標什麼」，
/// 連 <c>NOT NULL</c> 這幾個字都各寫一次。新增一種欄位性質時，
/// 漏掉哪個表面沒有任何徵兆——那個表面就只是少標了一項。
///
/// 這裡只回答「有哪些性質、它叫什麼」。怎麼畫由各表面自己決定：
/// 提示用分類文字、預覽用膠囊徽章，而預覽的計算欄位另有一整欄，
/// 不需要再標一次。硬湊成一個共用的格式化器只會逼每個呼叫端傳一堆開關。
/// </remarks>
public static class SqlColumnPresentation
{
    /// <summary>欄位成立的性質，順序固定：先講身分，再講限制。</summary>
    public static IReadOnlyList<SqlColumnFlag> Flags(SqlColumnInfo column)
    {
        if (column is null)
        {
            throw new ArgumentNullException(nameof(column));
        }

        var flags = new List<SqlColumnFlag>(4);

        if (column.IsPrimaryKey)
        {
            flags.Add(SqlColumnFlag.PrimaryKey);
        }

        if (!column.IsNullable)
        {
            flags.Add(SqlColumnFlag.NotNull);
        }

        if (column.IsIdentity)
        {
            flags.Add(SqlColumnFlag.Identity);
        }

        if (column.IsComputed)
        {
            flags.Add(SqlColumnFlag.Computed);
        }

        return flags;
    }

    /// <summary>徽章與提示上顯示的文字，一律用 T-SQL 自己的說法。</summary>
    public static string ToDisplayName(this SqlColumnFlag flag)
    {
        return flag switch
        {
            SqlColumnFlag.PrimaryKey => "PK",
            SqlColumnFlag.NotNull => "NOT NULL",
            SqlColumnFlag.Identity => "IDENTITY",
            SqlColumnFlag.Computed => "COMPUTED",
            _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "未涵蓋的欄位性質。")
        };
    }
}
