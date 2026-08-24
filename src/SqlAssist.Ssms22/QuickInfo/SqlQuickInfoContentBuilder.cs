using System.Collections.Generic;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text.Adornments;
using SqlAssist.Metadata;

namespace SqlAssist.Ssms22.QuickInfo;

/// <summary>
/// 把物件明細組成滑鼠停留提示的內容。
/// </summary>
/// <remarks>
/// 使用編輯器的 <see cref="ContainerElement"/>／<see cref="ClassifiedTextElement"/> 而非自製 WPF：
/// 分類過的文字會自動套用 SSMS 目前的佈景主題與字型設定，
/// 定位與大小也由編輯器負責，不必自己處理螢幕邊界。
/// </remarks>
internal static class SqlQuickInfoContentBuilder
{
    /// <summary>欄位過多時只顯示前面這些，避免提示視窗長到蓋住整個編輯器。</summary>
    private const int MaximumColumns = 40;

    public static ContainerElement Build(SqlObjectDetail detail)
    {
        var elements = new List<object>
        {
            BuildHeader(detail.Object)
        };

        if (detail.Object.Kind.HasColumns())
        {
            elements.AddRange(BuildColumns(detail.Columns));
        }
        else if (detail.Parameters.Count > 0)
        {
            elements.AddRange(BuildParameters(detail.Parameters));
        }

        if (detail.Object.Kind.IsModule() && string.IsNullOrWhiteSpace(detail.Definition))
        {
            elements.Add(Line(Comment("-- 無法取得定義（可能已加密或權限不足）")));
        }

        return new ContainerElement(ContainerElementStyle.Stacked, elements);
    }

    /// <summary>物件尚未載入完成時顯示的暫時內容。</summary>
    public static ContainerElement BuildLoading(SqlObjectInfo objectInfo)
    {
        return new ContainerElement(
            ContainerElementStyle.Stacked,
            BuildHeader(objectInfo),
            Line(Comment("-- 載入中…")));
    }

    private static ClassifiedTextElement BuildHeader(SqlObjectInfo objectInfo)
    {
        return new ClassifiedTextElement(
            Keyword(objectInfo.Kind.ToDisplayName()),
            Text(" "),
            Identifier(objectInfo.QualifiedName));
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
                yield return Line(Comment($"-- 另有 {columns.Count - shown} 個欄位未顯示"));
                yield break;
            }

            shown++;
            var runs = new List<ClassifiedTextRun>
            {
                Text("  "),
                Identifier(column.Name),
                Text("  "),
                Keyword(column.DataType)
            };

            if (!column.IsNullable)
            {
                runs.Add(Text("  "));
                runs.Add(Keyword("NOT NULL"));
            }

            if (column.IsIdentity)
            {
                runs.Add(Text("  "));
                runs.Add(Keyword("IDENTITY"));
            }

            if (column.IsComputed)
            {
                runs.Add(Text("  "));
                runs.Add(Keyword("COMPUTED"));
            }

            if (column.IsPrimaryKey)
            {
                runs.Add(Text("  "));
                runs.Add(Comment("PK"));
            }

            yield return new ClassifiedTextElement(runs);
        }
    }

    private static IEnumerable<object> BuildParameters(IReadOnlyList<SqlParameterInfo> parameters)
    {
        foreach (var parameter in parameters)
        {
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
