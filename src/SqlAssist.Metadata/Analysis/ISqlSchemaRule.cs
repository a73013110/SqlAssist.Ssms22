using System.Collections.Generic;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Analysis;

/// <summary>
/// 一條結構健檢規則。
/// </summary>
/// <remarks>
/// 一條規則一個型別，不是一個巨大的 <c>switch</c>：規則要能單獨關掉，而
/// 「關得掉」是這整個功能可以預設開啟的前提。新增一條規則因此只有兩個動作，
/// 寫一個類別、加進 <see cref="SqlSchemaAnalyzer.BuiltInRules"/>，
/// 不必動到任何既有的程式碼。
///
/// 規則<b>不</b>負責決定要不要跑自己：那是分析器與選項的事。這裡只回答
/// 「照我這一條看，這張表有什麼問題」。
/// </remarks>
public interface ISqlSchemaRule
{
    /// <summary>例如 <c>SCHEMA-001</c>；使用者用它關掉單獨一條。</summary>
    string Id { get; }

    /// <summary>這條規則在說什麼，供設定畫面顯示。</summary>
    string Title { get; }

    IEnumerable<SqlSchemaFinding> Analyze(SqlObjectStructure structure);
}
