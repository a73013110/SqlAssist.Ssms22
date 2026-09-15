using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SqlAssist.Core.SqlMemory;

/// <summary>
/// 多個選取範圍（方塊選取的每一行、多重插入點）依文件順序串成一份執行文字。
/// </summary>
/// <remarks>
/// 不能把第一個範圍的起點到最後一個範圍的終點當成一段：方塊選取時中間的欄外文字沒有被執行，
/// 記進歷程就是在保存使用者沒有送出的 SQL。範圍之間用文件自己的換行，與殼層送出的文字一致。
/// 和其他快照一樣只在背景展開全文，建立時只加總長度。
/// </remarks>
public sealed class SqlSelectionText : ISqlTextSnapshot
{
    private readonly ISqlTextSnapshot[] _parts;
    private readonly string _separator;

    private SqlSelectionText(ISqlTextSnapshot[] parts, string separator)
    {
        _parts = parts;
        _separator = separator;
        Length = checked(parts.Sum(part => part.Length) + separator.Length * (parts.Length - 1));
    }

    public int Length { get; }

    /// <param name="parts">依文件順序排列的範圍；呼叫端負責排序。</param>
    /// <param name="separator">範圍之間的換行。</param>
    /// <returns>沒有範圍時為 null；只有一個範圍時直接回傳它。</returns>
    public static ISqlTextSnapshot? Combine(IReadOnlyList<ISqlTextSnapshot> parts, string separator)
    {
        if (parts == null) throw new ArgumentNullException(nameof(parts));
        if (separator == null) throw new ArgumentNullException(nameof(separator));
        if (parts.Any(part => part == null)) throw new ArgumentException("選取範圍含有空項目。", nameof(parts));
        return parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => new SqlSelectionText(parts.ToArray(), separator),
        };
    }

    public string GetText()
    {
        var builder = new StringBuilder(Length);
        for (var i = 0; i < _parts.Length; i++)
        {
            if (i > 0) builder.Append(_separator);
            builder.Append(_parts[i].GetText());
        }
        return builder.ToString();
    }
}
