using System;

namespace SqlAssist.Core.Statements;

/// <summary>EXEC 骨架裡的一個參數。</summary>
/// <remarks>
/// <see cref="IsOptional"/> 與 <see cref="SqlStatementColumn.HasDefault"/> 看起來是同一件事，
/// 來源卻完全不同：欄位的預設值在 <c>sys.default_constraints</c> 裡，
/// 參數的預設值<b>不在</b> <c>sys.parameters</c> 裡——<c>has_default_value</c> 那一欄
/// 只對 CLR 模組有效，T-SQL 模組一律是 0。要知道哪些參數可以不傳，只能去讀模組定義，
/// 那是 <see cref="SqlModuleParameterDefaults"/> 的工作。
/// </remarks>
public sealed class SqlStatementParameter
{
    public SqlStatementParameter(string name, string dataType, bool isOutput, bool isOptional)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("參數名稱不可為空。", nameof(name));
        }

        Name = name;
        DataType = dataType ?? string.Empty;
        IsOutput = isOutput;
        IsOptional = isOptional;
    }

    /// <summary>含 <c>@</c> 前綴的參數名稱。</summary>
    public string Name { get; }

    public string DataType { get; }

    public bool IsOutput { get; }

    /// <summary>模組定義裡寫了預設值，因此呼叫時可以整個省略。</summary>
    public bool IsOptional { get; }
}
