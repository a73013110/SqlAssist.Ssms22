using System;

namespace SqlAssist.Core.Statements;

/// <summary>INSERT 骨架裡的一個欄位。</summary>
/// <remarks>
/// 只帶「挑字面值」與「排版」需要的四件事，不帶中繼資料層的模型：Core 不參照
/// SqlAssist.Metadata，而且「哪些欄位插得進去」是呼叫端已經判斷完的事——
/// 到得了這裡的每一個欄位都一定要出現在欄位清單上。分工與
/// <see cref="Wildcards.SqlWildcardExpansionText"/> 相同：名稱怎麼加括號由呼叫端決定。
/// </remarks>
public sealed class SqlStatementColumn
{
    public SqlStatementColumn(string name, string dataType, bool isNullable, bool hasDefault)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("欄位名稱不可為空。", nameof(name));
        }

        Name = name;
        DataType = dataType ?? string.Empty;
        IsNullable = isNullable;
        HasDefault = hasDefault;
    }

    /// <summary>已經加好方括號的欄位名稱。</summary>
    public string Name { get; }

    /// <summary>格式化過的型別，例如 <c>nvarchar(100)</c>；只寫進註解與挑字面值。</summary>
    public string DataType { get; }

    public bool IsNullable { get; }

    /// <summary>這個欄位有沒有 DEFAULT 條件約束。</summary>
    public bool HasDefault { get; }
}
