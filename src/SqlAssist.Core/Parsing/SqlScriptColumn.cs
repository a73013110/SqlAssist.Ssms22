using System;

namespace SqlAssist.Core.Parsing;

/// <summary>指令碼宣告的資料表裡的一個資料行。</summary>
/// <remarks>
/// 帶的是「組得出 <c>INSERT</c> 骨架」需要的那幾件事，與中繼資料的
/// <c>SqlColumnInfo</c> 一一對得起來——兩者刻意不合併，因為 Core 不參照
/// SqlAssist.Metadata，而「哪些欄位插得進去」那條規則只有一份，在 Metadata 那邊。
///
/// 型別照原文帶走（<c>NVARCHAR(20)</c>、<c>DECIMAL(18,2)</c>），不做正規化：
/// 它只寫進展開後的註解與挑字面值，而使用者眼前的宣告就長那樣。
/// </remarks>
public sealed class SqlScriptColumn
{
    public SqlScriptColumn(
        string name,
        string dataType,
        bool isNullable,
        bool hasDefault,
        bool isIdentity,
        bool isComputed,
        bool isPrimaryKey)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("資料行名稱不可為空。", nameof(name));
        }

        Name = name;
        DataType = dataType ?? string.Empty;
        IsNullable = isNullable;
        HasDefault = hasDefault;
        IsIdentity = isIdentity;
        IsComputed = isComputed;
        IsPrimaryKey = isPrimaryKey;
    }

    public string Name { get; }

    /// <summary>宣告時寫的型別；計算資料行為空字串。</summary>
    /// <remarks>
    /// 計算資料行的型別要看運算式推導，光讀文字推不出來。它本來就插不進去，
    /// 空字串在這裡不是遺漏而是實話。
    /// </remarks>
    public string DataType { get; }

    public bool IsNullable { get; }

    /// <summary>這個資料行有沒有 <c>DEFAULT</c> 條件約束。</summary>
    public bool HasDefault { get; }

    public bool IsIdentity { get; }

    public bool IsComputed { get; }

    public bool IsPrimaryKey { get; }

    /// <summary>補上資料表層級 <c>PRIMARY KEY (…)</c> 指到的那一份旗標。</summary>
    /// <remarks>
    /// 資料表層級的條件約束寫在所有資料行後面，讀到它時前面的資料行都已經建好了。
    /// 順帶把可為 NULL 拿掉：主索引鍵在 T-SQL 裡一律是 <c>NOT NULL</c>，
    /// 留著的話展開出來的 <c>VALUES</c> 會給它填 <c>NULL</c>，一執行就錯。
    /// </remarks>
    internal SqlScriptColumn AsPrimaryKey()
    {
        return new SqlScriptColumn(
            Name,
            DataType,
            isNullable: false,
            HasDefault,
            IsIdentity,
            IsComputed,
            isPrimaryKey: true);
    }

    public override string ToString() => $"{Name} {DataType}";
}
