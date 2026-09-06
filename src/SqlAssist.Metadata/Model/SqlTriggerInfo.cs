using System;

namespace SqlAssist.Metadata.Model;

/// <summary>掛在資料表上的一個觸發程序。</summary>
/// <remarks>
/// 定義取的是原文，不重建：觸發程序是模組，重組出來的版本一定會與作者寫的
/// 不一樣，而那份文字是他要拿去改的。取不到定義（加密，或沒有
/// <c>VIEW DEFINITION</c> 權限）時整個跳過——猜一個空的 <c>CREATE TRIGGER</c>
/// 骨架出來是指令碼在說謊。
/// </remarks>
public sealed class SqlTriggerInfo
{
    public SqlTriggerInfo(string name, string? definition, bool isDisabled = false)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("觸發程序名稱不可為空。", nameof(name));
        }

        Name = name;
        Definition = definition;
        IsDisabled = isDisabled;
    }

    public string Name { get; }

    /// <summary><c>sys.sql_modules.definition</c> 的原文；加密或沒有權限時為 null。</summary>
    public string? Definition { get; }

    /// <summary>
    /// 觸發程序目前是停用的。
    /// </summary>
    /// <remarks>
    /// 一定要寫進指令碼。停用的觸發程序在重建出來的資料表上如果變成啟用的，
    /// 那張表會開始執行一段來源上不會執行的邏輯——而那多半是當初把它停掉的原因。
    /// </remarks>
    public bool IsDisabled { get; }

    /// <summary>定義取不到時寫不出可以執行的指令碼。</summary>
    public bool CanScript => !string.IsNullOrWhiteSpace(Definition);

    public override string ToString() => IsDisabled ? Name + "（已停用）" : Name;
}
