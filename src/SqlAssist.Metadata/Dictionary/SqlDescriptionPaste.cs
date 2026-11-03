using System;
using System.Collections.Generic;

namespace SqlAssist.Metadata.Dictionary;

/// <summary>貼上的一個落點：畫面上的一列（一個資料行），連同比對名稱時認的寫法。</summary>
public sealed class SqlDescriptionPasteSlot
{
    public SqlDescriptionPasteSlot(SqlDictionaryObject owner, SqlDictionaryColumn column)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Column = column ?? throw new ArgumentNullException(nameof(column));
        Target = owner.TargetOf(column);
    }

    public SqlDictionaryObject Owner { get; }

    public SqlDictionaryColumn Column { get; }

    public SqlDescriptionTarget Target { get; }

    /// <summary>
    /// 第一格的名稱指的是不是這一列：資料行名稱、<c>表.資料行</c> 或 <c>結構描述.表.資料行</c>，不分大小寫。
    /// </summary>
    public bool IsNamed(string key) =>
        string.Equals(key, Column.Name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, Owner.Name + "." + Column.Name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, Owner.DisplayName + "." + Column.Name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>貼上的結果：每一項要寫到哪裡、寫什麼，以及有幾列對不到。</summary>
public sealed class SqlDescriptionPastePlan
{
    internal SqlDescriptionPastePlan(IReadOnlyList<KeyValuePair<SqlDescriptionPasteSlot, string>> assignments, int skipped)
    {
        Assignments = assignments;
        Skipped = skipped;
    }

    public IReadOnlyList<KeyValuePair<SqlDescriptionPasteSlot, string>> Assignments { get; }

    /// <summary>貼上的列裡有幾列沒有落點（名稱對不到、或超出畫面上的最後一列）。</summary>
    public int Skipped { get; }
}

/// <summary>
/// 把剪貼簿上的表格貼進說明欄。
/// </summary>
/// <remarks>
/// 兩種形狀，由貼上的內容自己決定，不另設開關：
/// <list type="bullet">
/// <item><b>一欄</b>（從 Excel 複製一整欄說明）：從選定的那一列開始往下一格一列，畫面上剩幾列就填幾列。</item>
/// <item><b>兩欄以上</b>（名稱＋說明）：照第一格的名稱找列，第二格是說明；同一個名稱在畫面上有幾列就填幾列——
/// 資料字典檢視裡貼 <c>ReaderNo⇥讀者編號</c> 會把每張表的 <c>ReaderNo</c> 一起補上，這正是統一說明要的。
/// 標頭列對不到任何名稱，算進 <see cref="SqlDescriptionPastePlan.Skipped"/>，不需要先刪掉。</item>
/// </list>
/// 看第一列決定形狀：混著一欄與兩欄的內容多半是手打的，照第一列猜比逐列切換好預料。
/// </remarks>
public static class SqlDescriptionPaste
{
    public static SqlDescriptionPastePlan Plan(
        IReadOnlyList<SqlDescriptionPasteSlot> slots,
        int startIndex,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (slots is null) throw new ArgumentNullException(nameof(slots));
        if (rows is null) throw new ArgumentNullException(nameof(rows));

        var assignments = new List<KeyValuePair<SqlDescriptionPasteSlot, string>>();
        var skipped = 0;

        if (rows.Count == 0) return new SqlDescriptionPastePlan(assignments, 0);

        if (rows[0].Count < 2)
        {
            var index = Math.Max(0, startIndex);

            foreach (var row in rows)
            {
                if (index >= slots.Count)
                {
                    skipped++;
                    continue;
                }

                assignments.Add(new KeyValuePair<SqlDescriptionPasteSlot, string>(slots[index++], row.Count == 0 ? string.Empty : row[0]));
            }

            return new SqlDescriptionPastePlan(assignments, skipped);
        }

        foreach (var row in rows)
        {
            var key = row.Count == 0 ? string.Empty : row[0].Trim();
            var value = row.Count < 2 ? string.Empty : row[1];
            var found = false;

            if (key.Length > 0)
            {
                foreach (var slot in slots)
                {
                    if (!slot.IsNamed(key)) continue;
                    assignments.Add(new KeyValuePair<SqlDescriptionPasteSlot, string>(slot, value));
                    found = true;
                }
            }

            if (!found) skipped++;
        }

        return new SqlDescriptionPastePlan(assignments, skipped);
    }
}
