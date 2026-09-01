using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using SqlAssist.Core.Wildcards;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Wildcards;

/// <summary>
/// 把選取清單裡的 <c>*</c> 換成完整的欄位清單。
/// </summary>
/// <remarks>
/// 判斷「這個星號是不是萬用字元、欄位從哪幾個來源來」全部在
/// <see cref="SqlWildcardAnalyzer"/>，那一段只看文字，可以完整單元測試。
/// 這裡只負責兩件編輯器才做得到的事：向中繼資料層要欄位名稱，以及把文字換掉。
///
/// 欄位有沒有在快取裡決定走哪條路。命中就在按鍵的同一個回合裡改完，
/// 使用者看到的是「按 Tab 就變了」；沒命中才去查資料庫，那一次 Tab 仍然算被
/// 處理掉——不吞掉的話編輯器會先插入一個定位字元，等查詢回來時要展開的位置
/// 早就被那個定位字元推走了。
/// </remarks>
internal sealed class SqlWildcardExpander
{
    private readonly ITextView _textView;
    private readonly SqlMetadataService _metadataService;

    public SqlWildcardExpander(ITextView textView, SqlMetadataService metadataService)
    {
        _textView = textView;
        _metadataService = metadataService;
    }

    /// <summary>
    /// 游標前方有沒有一個展得開的萬用字元。
    /// </summary>
    /// <remarks>
    /// 提示與 Tab 走同一份判斷：看得到提示卻按不動，比兩者都不出現更難解釋。
    /// </remarks>
    public static SqlWildcardTarget? Find(ITextSnapshot snapshot, int caretPosition, SqlAssistSettings settings)
    {
        if (!settings.Enabled || !settings.ExpandWildcardOnTab)
        {
            return null;
        }

        // 先用一次字元比較擋掉絕大多數的呼叫：這條路徑同時掛在 Tab 與游標移動上，
        // 而取整份文字在大檔案上不是免費的。
        if (caretPosition <= 0 || caretPosition > snapshot.Length || snapshot[caretPosition - 1] != '*')
        {
            return null;
        }

        var target = SqlWildcardAnalyzer.Analyze(snapshot.GetText(), caretPosition);

        if (target is null)
        {
            return null;
        }

        // 關掉「列出資料庫物件與欄位」等於不對資料庫送出任何查詢，
        // 那時只有欄位名稱寫在指令碼裡的來源（子查詢、CTE）展得開。
        if (!settings.IncludeDatabaseObjects && NeedsMetadata(target))
        {
            return null;
        }

        return target;
    }

    /// <summary>展開游標前方的萬用字元。</summary>
    /// <returns>這次按鍵是否已經由這裡處理掉。</returns>
    public bool TryExpand()
    {
        if (_textView.IsClosed || !_textView.Selection.IsEmpty)
        {
            return false;
        }

        var settings = SqlAssistSettingsStore.Current;
        var caret = _textView.Caret.Position.BufferPosition;
        var target = Find(caret.Snapshot, caret.Position, settings);

        if (target is null)
        {
            return false;
        }

        // 查詢期間使用者仍可能編輯，要換掉的範圍不能用固定位置記。
        var span = caret.Snapshot.CreateTrackingSpan(
            new Span(target.Start, target.Length),
            SpanTrackingMode.EdgeExclusive);

        if (TryResolveCached(target, settings) is { } columns)
        {
            Replace(span, columns, settings);
            return true;
        }

        // 一定要換到背景執行緒：解析連線那一步有 UI 執行緒相依性，實測塞住時要 1908 ms，
        // 在原地開始等於按一次 Tab 就讓編輯器停格將近兩秒。
        SqlAssistPlatformGuard.Begin(
            "展開萬用字元",
            () => Task.Run(() => ExpandAsync(target, span, settings)));
        return true;
    }

    private static bool NeedsMetadata(SqlWildcardTarget target)
    {
        foreach (var source in target.Sources)
        {
            if (source.Kind == SqlColumnSourceKind.Table)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>只用快取湊出欄位清單；任何一個來源沒命中就整份放棄。</summary>
    private IReadOnlyList<string>? TryResolveCached(SqlWildcardTarget target, SqlAssistSettings settings)
    {
        var columns = new List<string>();

        foreach (var source in target.Sources)
        {
            var names = source.Kind == SqlColumnSourceKind.Names
                ? source.Names
                : _metadataService.PeekColumnNames(source.Table!);

            if (names is null)
            {
                return null;
            }

            Append(columns, names, source, target, settings);
        }

        return columns.Count > 0 ? columns : null;
    }

    private async Task ExpandAsync(SqlWildcardTarget target, ITrackingSpan span, SqlAssistSettings settings)
    {
        var columns = new List<string>();

        foreach (var source in target.Sources)
        {
            var names = source.Kind == SqlColumnSourceKind.Names
                ? source.Names
                : await _metadataService
                    .GetColumnNamesAsync(source.Table!, CancellationToken.None)
                    .ConfigureAwait(false);

            if (names is null)
            {
                SqlAssistDiagnostics.WriteAlways(
                    $"取不到 {Describe(source)} 的欄位，維持原本的 *");
                return;
            }

            Append(columns, names, source, target, settings);
        }

        if (columns.Count > 0)
        {
            Replace(span, columns, settings);
        }
    }

    private static void Append(
        List<string> columns,
        IReadOnlyList<string> names,
        SqlColumnSource source,
        SqlWildcardTarget target,
        SqlAssistSettings settings)
    {
        // 使用者自己寫的限定字照原文帶回去，其餘情形才用敘述裡解析出來的名稱。
        var qualifier = !target.Qualify
            ? null
            : target.QualifierText
                ?? (source.Qualifier is null ? null : SqlInsertionText.Quote(source.Qualifier, settings));

        foreach (var name in names)
        {
            var column = SqlInsertionText.Quote(name, settings);
            columns.Add(qualifier is null ? column : qualifier + "." + column);
        }
    }

    private static string Describe(SqlColumnSource source)
    {
        return source.Table is { } table
            ? (table.SchemaName is null ? table.ObjectName : $"{table.SchemaName}.{table.ObjectName}")
            : "衍生資料表";
    }

    /// <param name="settings">
    /// 按下 Tab 那一刻的設定快照，與欄位名稱用的是同一份：中途改設定不該讓
    /// 同一次展開的名稱與排版來自兩個不同的版本。
    /// </param>
    private void Replace(ITrackingSpan span, IReadOnlyList<string> columns, SqlAssistSettings settings)
    {
        new TextViewEditCoordinator(_textView).ReplaceTracked(
            span,
            "萬用字元",
            target => BuildReplacement(target, columns, settings));
    }

    private static TextReplacement? BuildReplacement(
        SnapshotSpan target,
        IReadOnlyList<string> columns,
        SqlAssistSettings settings)
    {
        // 查詢期間使用者可能已經把那個星號刪掉或改寫了。
        if (target.IsEmpty || target.GetText().IndexOf('*') < 0)
        {
            SqlAssistDiagnostics.Write("要展開的萬用字元已經不在原處，放棄這次展開");
            return null;
        }

        var snapshot = target.Snapshot;
        var line = snapshot.GetLineFromPosition(target.Start.Position);
        var text = SqlWildcardExpansionText.Build(
            columns,
            SqlWildcardExpansionText.BuildIndent(
                snapshot.GetText(line.Start.Position, target.Start.Position - line.Start.Position)),
            settings.WildcardLayout,
            SqlAssistLimits.MaximumWildcardLineWidth,
            SnapshotNewLine.Resolve(snapshot, target.Start.Position));

        return new TextReplacement(
            text,
            SqlAssistActivityKind.WildcardExpanded,
            $"已把萬用字元展開成 {columns.Count} 個欄位",
            affectedItemCount: columns.Count);
    }
}
