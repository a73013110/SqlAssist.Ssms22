using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text.Adornments;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22.UI;

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
            return BuildDefinition(detail, openStructure);
        }

        // 資料表值函式的資料行也載入了，但這裡列的仍然是參數：滑鼠停在
        // dbo.fn_LoansByReader 上的人正要呼叫它，該填什麼引數才是他問的事。
        // 回傳幾個資料行併入摘要——那句話值得說，但不值得佔掉整個提示。
        var showsColumns = detail.Object.Kind.IsTableShaped();

        // 摘要與實際列出的內容一致；函式的參數與回傳資料行不能混成同一個數量。
        var summary = new List<string>();
        if (showsColumns)
        {
            summary.Add(detail.Columns.Count > 0 ? $"{detail.Columns.Count} 個欄位" : "欄位明細不可用");
        }
        else
        {
            if (detail.Parameters.Count > 0)
            {
                summary.Add($"{detail.Parameters.Count} 個參數");
            }
            if (detail.Columns.Count > 0)
            {
                summary.Add($"回傳 {detail.Columns.Count} 個資料行");
            }
        }

        var elements = new List<object>
        {
            BuildHeader(detail.Object, string.Join(" · ", summary), detail.Description)
        };
        var body = new List<object>();

        var hidden = 0;

        if (showsColumns)
        {
            hidden = Math.Max(0, detail.Columns.Count - MaximumColumns);
            body.AddRange(BuildColumns(detail.Columns));
        }
        else if (detail.Parameters.Count > 0)
        {
            hidden = Math.Max(0, detail.Parameters.Count - MaximumParameters);
            body.AddRange(BuildParameters(detail.Parameters));
        }

        if (detail.Object.Kind.IsModule() && string.IsNullOrWhiteSpace(detail.Definition))
        {
            body.Add(Line(Comment("無法取得定義（可能已加密或權限不足）")));
        }

        if (body.Count > 0)
        {
            elements.Add(new ContainerElement(ContainerElementStyle.Stacked, body));
        }

        if (BuildFooter(openStructure, hidden) is { } footer)
        {
            elements.Add(footer);
        }

        return Sections(elements);
    }

    /// <summary>快取裡還沒有明細時顯示的內容：標題加上開啟面板的連結。</summary>
    public static ContainerElement BuildLoading(SqlObjectInfo objectInfo, Action? openStructure = null)
    {
        var elements = new List<object> { BuildHeader(objectInfo, "明細載入中…") };

        if (BuildFooter(openStructure, hiddenCount: 0) is { } footer)
        {
            elements.Add(footer);
        }
        return Sections(elements);
    }

    /// <summary>單一欄位的提示內容，以欄位名稱為抬頭，摘要標示所屬物件。</summary>
    public static ContainerElement BuildColumn(
        SqlObjectInfo owner,
        SqlColumnInfo column,
        Action? openStructure = null)
    {
        // 型別與旗標說得出這一行「是什麼」，說不出它「為什麼在」——
        // 停在一個叫 Status 的 tinyint 上時，要問的正好是後者，所以說明緊接著名稱，
        // 所屬物件退到它下面。說明與名稱放進同一疊，不另外開一段：那一段留白會讓
        // 「這是誰」與「它是什麼型別」之間多出一道看不出理由的分隔。
        var caption = new List<object>
        {
            new ContainerElement(
                ContainerElementStyle.Wrapped,
                SqlIcons.GetImageElement(SuggestionKind.Column),
                Line(Title(column.Name)))
        };

        if (BuildDescription(column.Description) is { } description)
        {
            caption.Add(description);
        }

        caption.Add(Line(Comment($"欄位 · {owner.QualifiedName}")));

        var elements = new List<object>
        {
            new ContainerElement(ContainerElementStyle.Stacked, caption),
            new ClassifiedTextElement(BuildColumnRuns(column, includeName: false))
        };

        if (BuildFooter(openStructure, hiddenCount: 0) is { } footer)
        {
            elements.Add(footer);
        }

        return Sections(elements);
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
            return hiddenCount > 0 ? Line(Comment($"另有 {hiddenCount} 項未顯示")) : null;
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
    private static ContainerElement BuildDefinition(SqlObjectDetail detail, Action? openStructure)
    {
        var objectInfo = detail.Object;
        var lines = detail.Definition!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
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

            var text = new ClassifiedTextElement(BuildCodeRuns(line));
            // 定義本身已包含種類與名稱，只在首個非空白列加圖示，不重複標題。
            elements.Add(shown == 0
                ? new ContainerElement(ContainerElementStyle.Wrapped, SqlIcons.GetImageElement(objectInfo.Kind), text)
                : (object)text);
            shown++;
        }

        // 說明是使用者自己寫的一句話，定義本文說不出來；同義字指向誰與它為什麼
        // 存在是兩件事，兩件都要。
        if (BuildDescription(detail.Description) is { } description)
        {
            elements.Add(description);
        }

        var sections = new List<object> { new ContainerElement(ContainerElementStyle.Stacked, elements) };
        if (BuildFooter(openStructure, hiddenCount: 0) is { } footer)
        {
            sections.Add(footer);
        }

        return Sections(sections);
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

    private static ContainerElement BuildHeader(
        SqlObjectInfo objectInfo,
        string? suffix = null,
        string? description = null)
    {
        var summary = objectInfo.Kind.ToDisplayName();
        if (!string.IsNullOrEmpty(suffix))
        {
            summary += " · " + suffix;
        }

        var lines = new List<object>
        {
            new ContainerElement(ContainerElementStyle.Wrapped,
                SqlIcons.GetImageElement(objectInfo.Kind),
                Line(Title(objectInfo.QualifiedName)))
        };

        // 說明緊接著名稱，種類與規模退到它下面：停在一個沒看過的資料表上時要問的是
        // 「這張表在做什麼」，而「Table · 23 個欄位」擋在中間會讓那一句晚一行才讀到。
        if (BuildDescription(description) is { } text)
        {
            lines.Add(text);
        }

        lines.Add(Line(Comment(summary)));

        return new ContainerElement(ContainerElementStyle.Stacked, lines);
    }

    /// <summary>
    /// 說明那一行；沒有掛說明時回傳 null。
    /// </summary>
    /// <remarks>
    /// 收斂空白與截斷都走 <see cref="SqlDescriptionText"/>：提示視窗不會自己斷行，
    /// 一段帶換行的說明會排成一長行而被螢幕邊界切掉，而看的人看不出後面還有東西。
    /// 全文留給結構預覽——那裡有 Tooltip，也捲得動。
    /// </remarks>
    private static ClassifiedTextElement? BuildDescription(string? description)
    {
        return SqlDescriptionText.Summarize(description) is { } text ? Line(Comment(text)) : null;
    }

    private static IEnumerable<object> BuildColumns(IReadOnlyList<SqlColumnInfo> columns)
    {
        if (columns.Count == 0)
        {
            yield return Line(Comment("沒有可顯示的欄位明細"));
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
            yield return new ClassifiedTextElement(BuildColumnRuns(column));
        }
    }

    private static List<ClassifiedTextRun> BuildColumnRuns(SqlColumnInfo column, bool includeName = true)
    {
        var runs = new List<ClassifiedTextRun>();
        if (includeName)
        {
            runs.Add(Identifier(column.Name));
            runs.Add(Text("  "));
        }
        runs.Add(Keyword(column.DataType));

        foreach (var flag in SqlColumnPresentation.Flags(column))
        {
            runs.Add(Text("  "));

            // 一般旗標退到摘要層級；主索引鍵另以字重辨識，不只依賴顏色。
            runs.Add(flag == SqlColumnFlag.PrimaryKey
                ? Title(flag.ToDisplayName())
                : Comment(flag.ToDisplayName()));
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
                Identifier(parameter.Name),
                Text("  "),
                Keyword(parameter.DataType)
            };

            if (parameter.IsOutput)
            {
                runs.Add(Text("  "));
                runs.Add(Comment("OUTPUT"));
            }

            yield return new ClassifiedTextElement(runs);
        }
    }

    private static ClassifiedTextElement Line(ClassifiedTextRun run) => new(run);

    // 只在名稱／明細／入口之間留白，資料列仍緊湊；字型與實際間距交給原生呈現器。
    private static ContainerElement Sections(IEnumerable<object> elements) =>
        new(ContainerElementStyle.Stacked | ContainerElementStyle.VerticalPadding, elements);

    private static ClassifiedTextRun Title(string text) =>
        new(PredefinedClassificationTypeNames.Identifier, text, ClassifiedTextRunStyle.Bold);

    private static ClassifiedTextRun Keyword(string text) =>
        new(PredefinedClassificationTypeNames.Keyword, text);

    private static ClassifiedTextRun Identifier(string text) =>
        new(PredefinedClassificationTypeNames.Identifier, text);

    private static ClassifiedTextRun Comment(string text) =>
        new(PredefinedClassificationTypeNames.Comment, text);

    private static ClassifiedTextRun Text(string text) =>
        new(PredefinedClassificationTypeNames.WhiteSpace, text);
}
