using System;
using SqlAssist.Core.Scripting;
using SqlAssist.Metadata.Analysis;

namespace SqlAssist.Metadata.Formatting;

/// <summary>
/// 產生一份指令碼所需要的、物件本身以外的資訊。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlScriptOptions"/> 分開：選項是使用者調的，這裡放的是這一次連線
/// 與這個執行個體的事實。兩者混在一起的話，「資料庫定序」會變成一個使用者調得動
/// 的設定，而它一調就會讓 <see cref="SqlCollationOutput.WhenDifferentFromDatabase"/>
/// 拿錯誤的基準去比對。
/// </remarks>
public sealed class SqlScriptContext
{
    /// <summary>只帶選項的內容，其餘一律沒有——測試與預覽用得上。</summary>
    public static SqlScriptContext ForOptions(SqlScriptOptions options) => new(options);

    public SqlScriptContext(
        SqlScriptOptions options,
        string? databaseCollation = null,
        string? newLine = null,
        string? serverName = null,
        string? databaseName = null,
        string? toolVersion = null,
        DateTimeOffset? generatedAt = null,
        SqlSchemaAnalyzer? analyzer = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        DatabaseCollation = databaseCollation;
        NewLine = ResolveLineBreak(newLine);
        ServerName = serverName;
        DatabaseName = databaseName;
        ToolVersion = toolVersion;
        GeneratedAt = generatedAt;
        Analyzer = analyzer;
    }

    public SqlScriptOptions Options { get; }

    /// <summary>
    /// 目標資料庫的定序，供
    /// <see cref="SqlCollationOutput.WhenDifferentFromDatabase"/> 比對。
    /// </summary>
    /// <remarks>
    /// 查不到時是 null，而那時<b>一定要寫出</b>資料行定序：省略等於讓目的地
    /// 用自己的資料庫定序，而那可能不是來源的那一個——安靜地換掉定序會讓
    /// 排序、比較與唯一索引的行為跟著換，卻看不出任何差別。
    /// </remarks>
    public string? DatabaseCollation { get; }

    /// <summary>目的地文件的換行字元。</summary>
    public string NewLine { get; }

    public string? ServerName { get; }

    public string? DatabaseName { get; }

    public string? ToolVersion { get; }

    public DateTimeOffset? GeneratedAt { get; }

    /// <summary>
    /// 結構健檢；null 代表這一次不跑。
    /// </summary>
    /// <remarks>
    /// 帶分析器而不是帶一份現成的發現清單：多物件時每一張表的發現要跟著自己那張表，
    /// 而呼叫端先跑一輪再攤平成一份清單的話，那個對應關係就得靠名稱再湊回來。
    ///
    /// 跑不跑由這一格決定，寫不寫由 <see cref="SqlScriptOptions.IncludeAnalyzerComments"/>
    /// 決定。兩個開關不是重複：關掉輸出仍然可能要跑健檢（獨立的結果面板），
    /// 而關掉分析器則連跑都不跑。
    /// </remarks>
    public SqlSchemaAnalyzer? Analyzer { get; }

    public SqlScriptContext WithOptions(SqlScriptOptions options) =>
        new(
            options,
            DatabaseCollation,
            NewLine,
            ServerName,
            DatabaseName,
            ToolVersion,
            GeneratedAt,
            Analyzer);

    /// <remarks>
    /// 不是那三種換行之一時退回作業系統預設值。換行必須在算游標位置之前就定好，
    /// 改寫換行會讓後面每一個字元位移。
    /// </remarks>
    private static string ResolveLineBreak(string? newLine) =>
        newLine == "\r\n" || newLine == "\n" || newLine == "\r" ? newLine : Environment.NewLine;
}
