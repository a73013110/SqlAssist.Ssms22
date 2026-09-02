using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text.Adornments;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Ssms22.QuickInfo;

/// <summary>
/// 把物件明細組成滑鼠停留提示的內容。
/// </summary>
/// <remarks>
/// 使用編輯器的 <see cref="ContainerElement"/>／<see cref="ClassifiedTextElement"/> 而非自製 WPF：
/// 分類過的文字會自動套用 SSMS 目前的佈景主題與字型設定，
/// 定位與大小也由編輯器負責，不必自己處理螢幕邊界。
///
/// 提示刻意只給一眼看得完的份量。提示視窗不能捲動也不能選取，放再多也讀不完；
/// 真的要看完整結構的人點最後一行的連結，那裡有可捲動、可選取、可複製的浮動視窗。
/// </remarks>
internal static class SqlQuickInfoContentBuilder
{
    /// <summary>提示裡最多顯示的欄位數。</summary>
    private const int MaximumColumns = 8;

    /// <summary>最多顯示的參數數。</summary>
    private const int MaximumParameters = 8;

    /// <summary>直接顯示定義本文時，最多顯示幾行。</summary>
    /// <remarks>
    /// 只有同義字與序列走這條路，兩者最長就是八行；上限在這裡是為了擋住
    /// 「有一天別的種類也走進來」，而不是為了截斷這兩種。
    /// </remarks>
    private const int MaximumDefinitionLines = 10;

    private const string OpenStructureText = "開啟完整結構";

    private const string OpenStructureTooltip = "開啟浮動結構視窗：可捲動、可用滑鼠選取複製，Esc 關閉";

    /// <param name="openStructure">
    /// 「開啟完整結構」要執行的動作；建議清單的說明面板沒有可點擊的地方，傳 null 即可。
    /// </param>
    public static ContainerElement Build(SqlObjectDetail detail, Action? openStructure = null)
    {
        // 同義字與序列的定義是本擴充自己從目錄檢視組出來的一小段 CREATE，
        // 而那段文字本身就是最好的標題：「Synonym [dbo].[syn_Loan]」說不出它指向誰，
        // 而「它指向誰」正是使用者把滑鼠停在同義字上時唯一要問的事。
        //
        // 這裡刻意不自己組一次 CREATE SYNONYM，而是把 Definition 畫出來：
        // 兩份格式的症狀是提示與 F12 開出來的指令碼寫法不同，而且沒有任何徵兆。
        if (detail.Object.Kind.HasSynthesizedDefinition() &&
            !string.IsNullOrWhiteSpace(detail.Definition))
        {
            return BuildDefinition(detail.Definition!, openStructure);
        }

        // 標題帶上欄位總數：清單被截斷時，使用者至少知道自己看到的是幾分之幾。
        var elements = new List<object>
        {
            detail.Columns.Count > 0
                ? BuildHeader(detail.Object, $"（{detail.Columns.Count} 個欄位）")
                : BuildHeader(detail.Object)
        };

        var hidden = 0;

        if (detail.Object.Kind.HasColumns())
        {
            hidden = Math.Max(0, detail.Columns.Count - MaximumColumns);
            elements.AddRange(BuildColumns(detail.Columns));
        }
        else if (detail.Parameters.Count > 0)
        {
            hidden = Math.Max(0, detail.Parameters.Count - MaximumParameters);
            elements.AddRange(BuildParameters(detail.Parameters));
        }

        if (detail.Object.Kind.IsModule() && string.IsNullOrWhiteSpace(detail.Definition))
        {
            elements.Add(Line(Comment("-- 無法取得定義（可能已加密或權限不足）")));
        }

        if (BuildFooter(openStructure, hidden) is { } footer)
        {
            elements.Add(footer);
        }

        return new ContainerElement(ContainerElementStyle.Stacked, elements);
    }

    /// <summary>快取裡還沒有明細時顯示的內容：標題加上開啟面板的連結。</summary>
    public static ContainerElement BuildLoading(SqlObjectInfo objectInfo, Action? openStructure = null)
    {
        var elements = new List<object> { BuildHeader(objectInfo) };

        if (BuildFooter(openStructure, hiddenCount: 0) is { } footer)
        {
            elements.Add(footer);
        }
        else
        {
            elements.Add(Line(Comment("-- 載入中…")));
        }

        return new ContainerElement(ContainerElementStyle.Stacked, elements);
    }

    /// <summary>單一欄位的提示內容，標題顯示它屬於哪個物件。</summary>
    public static ContainerElement BuildColumn(
        SqlObjectInfo owner,
        SqlColumnInfo column,
        Action? openStructure = null)
    {
        var elements = new List<object>
        {
            new ClassifiedTextElement(
                Keyword("COLUMN"),
                Text(" "),
                Identifier(owner.QualifiedName)),
            new ClassifiedTextElement(BuildColumnRuns(column, "  "))
        };

        if (BuildFooter(openStructure, hiddenCount: 0) is { } footer)
        {
            elements.Add(footer);
        }

        return new ContainerElement(ContainerElementStyle.Stacked, elements);
    }

    /// <summary>
    /// 提示最後一行的連結。
    /// </summary>
    /// <remarks>
    /// <see cref="ClassifiedTextRun"/> 接受 navigationAction，編輯器會把它畫成可點擊的連結——
    /// 不必自製 WPF 就能從提示走到面板。
    /// </remarks>
    private static ClassifiedTextElement? BuildFooter(Action? openStructure, int hiddenCount)
    {
        if (openStructure is null)
        {
            return hiddenCount > 0 ? Line(Comment($"-- 另有 {hiddenCount} 項未顯示")) : null;
        }

        var runs = new List<ClassifiedTextRun>();

        if (hiddenCount > 0)
        {
            runs.Add(Comment($"另有 {hiddenCount} 項未顯示　"));
        }

        runs.Add(new ClassifiedTextRun(
            PredefinedClassificationTypeNames.Identifier,
            OpenStructureText,
            openStructure,
            OpenStructureTooltip,
            ClassifiedTextRunStyle.Underline));

        return new ClassifiedTextElement(runs);
    }

    /// <summary>
    /// 把一段定義本文畫成提示內容。
    /// </summary>
    /// <remarks>
    /// 著色走 <see cref="SqlTokenizer"/> 與 <see cref="SqlKeywordCatalog"/>，
    /// 與浮動預覽的指令碼分頁同一組出處——照關鍵字字面值再列一份的話，
    /// 新增一個關鍵字時只會有一邊跟著變。
    ///
    /// 逐行分開成 <see cref="ClassifiedTextElement"/>：提示視窗不會自己斷行，
    /// 整段塞進一個元素會排成一長行而被螢幕邊界切掉。
    /// </remarks>
    private static ContainerElement BuildDefinition(string definition, Action? openStructure)
    {
        var lines = definition.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var elements = new List<object>();
        var shown = 0;

        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (shown == MaximumDefinitionLines)
            {
                break;
            }

            shown++;
            elements.Add(new ClassifiedTextElement(BuildCodeRuns(line)));
        }

        if (BuildFooter(openStructure, hiddenCount: 0) is { } footer)
        {
            elements.Add(footer);
        }

        return new ContainerElement(ContainerElementStyle.Stacked, elements);
    }

    /// <remarks>
    /// 詞元之間的原文（空白、換行）照原樣補回去，靠的是每個詞元自己的位置——
    /// 依詞元種類重新拼一次空白的話，<c>FOR [Lib].[dbo].[Loan]</c> 會變成
    /// <c>FOR [Lib] . [dbo] . [Loan]</c>。
    /// </remarks>
    private static List<ClassifiedTextRun> BuildCodeRuns(string line)
    {
        var runs = new List<ClassifiedTextRun>();
        var position = 0;

        foreach (var token in SqlTokenizer.TokenizeWithComments(line))
        {
            if (token.Start > position)
            {
                runs.Add(Text(line.Substring(position, token.Start - position)));
            }

            // 畫出去的是 Text 不是 Value：後者去掉了方括號，
            // [dbo].[syn_Loan] 會被畫成 dbo.syn_Loan——那是另一個名稱。
            runs.Add(new ClassifiedTextRun(ClassificationFor(token), token.Text));
            position = token.Start + token.Length;
        }

        if (position < line.Length)
        {
            runs.Add(Text(line.Substring(position)));
        }

        return runs;
    }

    /// <remarks>加了方括號的名稱一律不是關鍵字：<c>[KEY]</c> 是欄位名，不是 <c>KEY</c>。</remarks>
    private static string ClassificationFor(SqlToken token)
    {
        return token.Kind switch
        {
            SqlTokenKind.Comment => PredefinedClassificationTypeNames.Comment,
            SqlTokenKind.String => PredefinedClassificationTypeNames.String,
            SqlTokenKind.Number => PredefinedClassificationTypeNames.Number,
            SqlTokenKind.Identifier when !token.IsQuoted &&
                SqlKeywordCatalog.IsKeywordOrDataType(token.Value) =>
                PredefinedClassificationTypeNames.Keyword,
            _ => PredefinedClassificationTypeNames.Identifier
        };
    }

    private static ClassifiedTextElement BuildHeader(SqlObjectInfo objectInfo, string? suffix = null)
    {
        var runs = new List<ClassifiedTextRun>
        {
            Keyword(objectInfo.Kind.ToDisplayName()),
            Text(" "),
            Identifier(objectInfo.QualifiedName)
        };

        if (!string.IsNullOrEmpty(suffix))
        {
            runs.Add(Text("  "));
            runs.Add(Comment(suffix!));
        }

        return new ClassifiedTextElement(runs);
    }

    private static IEnumerable<object> BuildColumns(IReadOnlyList<SqlColumnInfo> columns)
    {
        if (columns.Count == 0)
        {
            yield return Line(Comment("-- 沒有欄位"));
            yield break;
        }

        var shown = 0;

        foreach (var column in columns)
        {
            if (shown == MaximumColumns)
            {
                yield break;
            }

            shown++;
            yield return new ClassifiedTextElement(BuildColumnRuns(column, "  "));
        }
    }

    private static List<ClassifiedTextRun> BuildColumnRuns(SqlColumnInfo column, string indent)
    {
        var runs = new List<ClassifiedTextRun>
        {
            Text(indent),
            Identifier(column.Name),
            Text("  "),
            Keyword(column.DataType)
        };

        foreach (var flag in SqlColumnPresentation.Flags(column))
        {
            runs.Add(Text("  "));

            // 主索引鍵不是型別的一部分，用註解色與 NOT NULL 這些限制分開。
            runs.Add(flag == SqlColumnFlag.PrimaryKey
                ? Comment(flag.ToDisplayName())
                : Keyword(flag.ToDisplayName()));
        }

        return runs;
    }

    private static IEnumerable<object> BuildParameters(IReadOnlyList<SqlParameterInfo> parameters)
    {
        var shown = 0;

        foreach (var parameter in parameters)
        {
            if (shown == MaximumParameters)
            {
                yield break;
            }

            shown++;

            var runs = new List<ClassifiedTextRun>
            {
                Text("  "),
                Identifier(parameter.Name),
                Text("  "),
                Keyword(parameter.DataType)
            };

            if (parameter.IsOutput)
            {
                runs.Add(Text("  "));
                runs.Add(Keyword("OUTPUT"));
            }

            yield return new ClassifiedTextElement(runs);
        }
    }

    private static ClassifiedTextElement Line(ClassifiedTextRun run) => new(run);

    private static ClassifiedTextRun Keyword(string text) =>
        new(PredefinedClassificationTypeNames.Keyword, text);

    private static ClassifiedTextRun Identifier(string text) =>
        new(PredefinedClassificationTypeNames.Identifier, text);

    private static ClassifiedTextRun Comment(string text) =>
        new(PredefinedClassificationTypeNames.Comment, text);

    private static ClassifiedTextRun Text(string text) =>
        new(PredefinedClassificationTypeNames.WhiteSpace, text);
}
