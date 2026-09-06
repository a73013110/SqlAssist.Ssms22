using System;
using System.Reflection;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Core.Scripting;
using SqlAssist.Metadata.Analysis;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;

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
    /// <param name="source">產生檔頭註解要用的來源物件；沒有時檔頭少那幾行。</param>
    public static SqlScriptContext Create(string? newLine, SqlObjectInfo? source = null) =>
        Build(newLine, source, forExecution: false);

    /// <summary>
    /// 要拿去執行的那一份：F12 送進新查詢視窗的指令碼。
    /// </summary>
    /// <remarks>
    /// 三項覆寫不是風格偏好，是可執行性的要求，所以不管使用者選了哪一組都要蓋掉：
    /// <c>ALTER PROCEDURE</c> 必須是批次裡的第一個敘述，少了 <c>GO</c> 就分不開；
    /// 計算資料行、篩選索引與索引檢視對那兩個 <c>SET</c> 的值有要求，
    /// 少了它們的 <c>CREATE TABLE</c> 在某些連線設定下會直接失敗；
    /// 而 F12 之後接著要做的事幾乎都是「改一下再執行」，給 <c>CREATE</c> 的話
    /// 每一次都要自己把第一個字改掉。
    ///
    /// <c>SetOptions</c> 蓋成 <see cref="SqlSetOptionOutput.FromCatalog"/> 而不是
    /// <see cref="SqlSetOptionOutput.AlwaysOn"/>：一張在 <c>OFF</c> 之下建起來的
    /// 資料表，用 <c>ON</c> 重建可能直接失敗，而目錄上就有當初的值。
    /// </remarks>
    public static SqlScriptContext CreateForExecution(string? newLine, SqlObjectInfo? source = null) =>
        Build(newLine, source, forExecution: true);

    private static SqlScriptContext Build(string? newLine, SqlObjectInfo? source, bool forExecution)
    {
        var settings = SqlAssistSettingsStore.Current;

        var options = SqlScriptOptions.ForStyle(settings.ScriptStyle) with
        {
            IncludeExtendedProperties = settings.ScriptIncludeExtendedProperties,
            IncludeAnalyzerComments = settings.ScriptIncludeAnalyzerComments,
            IncludeHeaderComment = settings.ScriptIncludeHeaderComment
        };

        if (forExecution)
        {
            options = options with
            {
                SetOptions = SqlSetOptionOutput.FromCatalog,
                BatchSeparation = SqlBatchSeparation.BetweenStatements,
                ModuleStatement = SqlModuleStatement.Alter
            };
        }

        return new SqlScriptContext(
            options,
            newLine: newLine,
            serverName: source?.ServerName,
            databaseName: source?.DatabaseName,
            toolVersion: options.IncludeHeaderComment ? ToolVersion() : null,
            generatedAt: options.IncludeHeaderComment ? DateTimeOffset.Now : null,

            // 關掉輸出時連跑都不跑：健檢要掃過每一個資料行與每一個索引，
            // 而那一份結果沒有人會看到。
            analyzer: settings.ScriptIncludeAnalyzerComments ? SqlSchemaAnalyzer.Default : null);
    }

    /// <remarks>
    /// 版本從組件的屬性讀，不寫死：唯一的來源是根目錄的 version.json 加上
    /// git height，而寫死的那一份會在下一次發行之後開始說謊。
    /// </remarks>
    private static string ToolVersion()
    {
        var assembly = typeof(SqlScriptPreferences).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        var version = SqlAssistBuildVersion.Create(
            informational,
            assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version,
            assembly.GetName().Version?.ToString());

        return "SqlAssist " + version.DisplayVersion;
    }
}
