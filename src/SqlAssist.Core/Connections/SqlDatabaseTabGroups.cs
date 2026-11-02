using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.Core.Connections;

/// <summary>每個「伺服器＋資料庫」固定一個頁籤分組編號；SSMS 依編號決定顏色。</summary>
/// <remarks>
/// 編號照第一次見到的順序發，不用名稱的雜湊：SSMS 拿編號對 16 色取餘數，雜湊在五個資料庫時
/// 就有約一半機率撞色，而撞色的兩個頁籤會被看成同一個資料庫。編號是清單位置，所以清單只增不刪；
/// 使用者在頁籤選單改過的顏色也跟著編號存，刪掉一筆會讓後面每一筆換色。
/// </remarks>
public sealed class SqlDatabaseTabGroups
{
    // 名稱裡幾乎不會出現控制字元；真的撞上也只是那一筆換分組，編號仍是位置。
    private const char EntrySeparator = '\u001E';
    private const char FieldSeparator = '\u001F';

    private readonly List<SqlConnectionLabel> _labels = new();
    private readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);

    public static SqlDatabaseTabGroups Parse(string? state)
    {
        var groups = new SqlDatabaseTabGroups();

        if (string.IsNullOrEmpty(state)) return groups;

        foreach (var entry in state!.Split(EntrySeparator))
        {
            var fields = entry.Split(new[] { FieldSeparator }, 2);
            groups.Append(new SqlConnectionLabel(fields[0], fields.Length > 1 ? fields[1] : string.Empty));
        }

        return groups;
    }

    /// <summary>這條連線的分組編號；第一次見到時發一個新的，<paramref name="added"/> 為 true 表示要存檔。</summary>
    public int IdOf(SqlConnectionLabel label, out bool added)
    {
        added = !_ids.TryGetValue(Key(label), out var id);
        return added ? Append(label) : id;
    }

    /// <summary>分組的顯示名稱；沒發過的編號是空字串。</summary>
    public string DisplayName(int id) =>
        id >= 0 && id < _labels.Count ? _labels[id].DisplayName : string.Empty;

    public string Format() => string.Join(EntrySeparator.ToString(), _labels.Select(Key));

    private int Append(SqlConnectionLabel label)
    {
        _ids[Key(label)] = _labels.Count;
        _labels.Add(label);
        return _labels.Count - 1;
    }

    private static string Key(SqlConnectionLabel label) => label.Server + FieldSeparator + label.Database;
}
