using SqlAssist.Core.Scripting;
using SqlAssist.Metadata.Formatting;

namespace SqlAssist.Ssms22.Settings;

/// <summary>
/// 這一次要用哪一組指令碼選項。
/// </summary>
/// <remarks>
/// F12 與浮動預覽的指令碼分頁都問這裡。兩邊各自組一份 <see cref="SqlScriptContext"/>
/// 的症狀是同一張資料表在兩個表面上長得不一樣，而使用者會以為其中一條壞了——
/// 那正是把組字串收斂成單一 <see cref="TSqlScriptRenderer"/> 要解決的事，
/// 選項在呼叫端分岔的話等於白收斂。
/// </remarks>
internal static class SqlScriptPreferences
{
    /// <summary>拿來對照的那一份：浮動預覽的指令碼分頁。</summary>
    /// <param name="newLine">目的地文件使用的換行字元。</param>
    public static SqlScriptContext Create(string? newLine) =>
        new(SqlScriptOptions.Fidelity, newLine: newLine);

    /// <summary>
    /// 要拿去執行的那一份：F12 送進新查詢視窗的指令碼。
    /// </summary>
    /// <remarks>
    /// 兩項覆寫不是風格偏好，是可執行性的要求，所以不管使用者選了哪一組都要蓋掉：
    /// <c>ALTER PROCEDURE</c> 必須是批次裡的第一個敘述，少了 <c>GO</c> 就分不開；
    /// 而計算資料行、篩選索引與索引檢視對那兩個 <c>SET</c> 的值有要求，
    /// 少了它們的 <c>CREATE TABLE</c> 在某些連線設定下會直接失敗。
    ///
    /// 模組寫成 <c>ALTER</c> 也一樣：F12 之後接著要做的事幾乎都是「改一下再執行」，
    /// 給 <c>CREATE</c> 的話每一次都要自己把第一個字改掉。
    /// </remarks>
    public static SqlScriptContext CreateForExecution(string? newLine) =>
        new(
            SqlScriptOptions.Fidelity with
            {
                SetOptions = SqlSetOptionOutput.AlwaysOn,
                BatchSeparation = SqlBatchSeparation.BetweenStatements,
                ModuleStatement = SqlModuleStatement.Alter
            },
            newLine: newLine);
}
