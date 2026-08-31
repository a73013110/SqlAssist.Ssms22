using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Settings;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;

namespace SqlAssist.Ssms22.Completion;

/// <summary>語句要寫進哪一行；縮排與換行字元都由那一行決定。</summary>
internal readonly struct SqlStatementSite
{
    private SqlStatementSite(string indent, string newLine, string statementText)
    {
        Indent = indent;
        NewLine = newLine;
        StatementText = statementText;
    }

    /// <summary>語句所在行的前導空白，原樣重複到展開出來的每一行。</summary>
    /// <remarks>
    /// 刻意不把定位字元換成空白：這一段是<b>整段重複</b>的，
    /// 每一行前面放的都是同一串字元，在定位寬度不是 4 的機器上也對得齊。
    /// </remarks>
    public string Indent { get; }

    public string NewLine { get; }

    /// <summary>要被換掉的那一段原文；使用者寫的關鍵字大小寫與寫法都在裡面。</summary>
    public string StatementText { get; }

    public static SqlStatementSite From(SnapshotSpan target)
    {
        var line = target.Snapshot.GetLineFromPosition(target.Start.Position);
        var text = line.GetText();
        var length = 0;

        while (length < text.Length && (text[length] == ' ' || text[length] == '\t'))
        {
            length++;
        }

        return new SqlStatementSite(
            text.Substring(0, length),
            line.LineBreakLength > 0 ? line.GetLineBreakText() : Environment.NewLine,
            target.GetText());
    }
}

/// <summary>提交建議後要把整個語句換成什麼。</summary>
/// <remarks>
/// 三種展開（ALTER 定義、INSERT 骨架、EXEC 呼叫）只有「換成什麼」不一樣，
/// 「怎麼安全地換」完全相同：切 UI 執行緒、檢查編輯器已關閉、從
/// <see cref="ITrackingSpan"/> 取最新範圍、確認等待期間原文還在原處。
/// 各寫一份的下場是其中一份少了一道，而少的那一道會覆蓋使用者的輸入。
/// </remarks>
internal interface ISqlCommitExpansion
{
    /// <summary>要展開的物件；決定去中繼資料層拿誰的細節。</summary>
    SqlObjectInfo Object { get; }

    /// <summary>寫進紀錄與復原堆疊的操作名稱，例如「ALTER 語句」。</summary>
    string OperationName { get; }

    /// <summary>
    /// 語句必須仍以這個關鍵字開頭，否則放棄。
    /// </summary>
    /// <remarks>
    /// 查詢期間使用者可能已經把整句刪掉或改寫了。範圍的起點就是那個關鍵字，
    /// 起點不再是它就代表要換的東西已經不在原處——這時候把文字蓋上去等於改到別人的語句。
    /// </remarks>
    string LeadingKeyword { get; }

    /// <param name="insertedName">提交時已經寫進緩衝區的物件名稱，含結構描述與方括號。</param>
    /// <returns>要寫回去的內容；null 代表這一次不展開，維持只插入名稱。</returns>
    TextReplacement? Build(SqlObjectDetail detail, SqlStatementSite site, string insertedName);
}

/// <summary>
/// 提交建議之後，在背景取得物件細節並把整個語句換掉。
/// </summary>
/// <remarks>
/// 細節屬於中繼資料的第二、三層，取得需要另一次查詢，期間使用者仍可能編輯緩衝區，
/// 因此要替換的範圍以 <see cref="ITrackingSpan"/> 記住，不能用固定位置。
/// </remarks>
internal sealed class SqlCommitExpander
{
    private readonly ITextView _textView;
    private readonly SqlMetadataService _metadataService;
    private readonly Action<bool>? _setSuppressBufferChange;

    public SqlCommitExpander(
        ITextView textView,
        SqlMetadataService metadataService,
        Action<bool>? setSuppressBufferChange = null)
    {
        _textView = textView;
        _metadataService = metadataService;
        _setSuppressBufferChange = setSuppressBufferChange;
    }

    /// <summary>這次提交要展開成什麼；不展開時回傳 null。</summary>
    /// <remarks>
    /// 三個閘門的順序固定：先問上下文（他在哪個位置提交），再問設定（他要不要這個展開），
    /// 最後問物件本身（這個東西展得開嗎）。設定放中間是因為關掉之後就不必再判斷物件，
    /// 而物件那一關擋掉的是「同義字沒有欄位」「擴充預存程序沒有參數」這一類。
    /// </remarks>
    public static ISqlCommitExpansion? Resolve(
        SqlSuggestion selected,
        SqlCompletionContext context,
        int caretPosition,
        SqlAssistSettings settings)
    {
        if (context.TargetKeywordStart < 0 ||
            caretPosition < context.TargetKeywordStart ||
            selected.Tag is not SqlObjectInfo objectInfo)
        {
            return null;
        }

        switch (context.Intent)
        {
            case CompletionIntent.AlterDefinition:
                return objectInfo.Kind.IsModule()
                    ? new SqlAlterStatementExpansion(objectInfo)
                    : null;

            case CompletionIntent.InsertStatement:
                return settings.ExpandInsertStatement && objectInfo.Kind.HasColumns()
                    ? new SqlInsertStatementExpansion(objectInfo, settings)
                    : null;

            case CompletionIntent.ExecuteCall:
                return settings.ExpandProcedureCall && objectInfo.Kind.IsExecutable()
                    ? new SqlProcedureCallExpansion(objectInfo)
                    : null;

            default:
                return null;
        }
    }

    /// <summary>記住要被整句換掉的那一段。</summary>
    /// <remarks>
    /// <b>一定要在名稱插進緩衝區之後才呼叫</b>，範圍的結尾也必須是插入後的實際結尾。
    ///
    /// 曾經在提交前就用「關鍵字起點 → 游標」建好這個範圍，結果是展開後編輯器裡多出
    /// 一截 <c>dbo.uspFoo</c>：<see cref="SpanTrackingMode.EdgeExclusive"/> 的結尾邊界
    /// 往負方向追蹤，而提交的取代動作剛好就結束在那個邊界上，於是新插入的名稱被判在
    /// 範圍**外**。範圍縮回「ALTER PROCEDURE 」，定義蓋掉的只有這一段，名稱原封不動
    /// 留在後面——正好停在游標之後，看起來就像展開完又補了一次名稱。
    ///
    /// 結尾邊界改追蹤正方向可以讓這一次對，但接下來使用者在游標處打的每一個字也會被
    /// 算進範圍裡，定義回來時連同打好的字一起被吃掉。所以維持 EdgeExclusive，
    /// 改成在編輯之後才建立範圍。
    /// </remarks>
    public static ITrackingSpan CreateStatementSpan(
        ITextSnapshot snapshot,
        int statementStart,
        int statementEnd)
    {
        return snapshot.CreateTrackingSpan(
            Span.FromBounds(statementStart, Math.Min(statementEnd, snapshot.Length)),
            SpanTrackingMode.EdgeExclusive);
    }

    /// <summary>在背景取得物件細節並替換整個語句。</summary>
    public void Begin(ISqlCommitExpansion expansion, ITrackingSpan statementSpan, string insertedName)
    {
        SqlAssistPlatformGuard.Begin(
            $"展開{expansion.OperationName}",
            () => ExpandAsync(expansion, statementSpan, insertedName));
    }

    private async Task ExpandAsync(
        ISqlCommitExpansion expansion,
        ITrackingSpan statementSpan,
        string insertedName)
    {
        var detail = await _metadataService
            .GetDetailAsync(expansion.Object, CancellationToken.None)
            .ConfigureAwait(false);

        if (detail is null)
        {
            SqlAssistDiagnostics.WriteAlways(
                $"取不到 {expansion.Object.QualifiedName} 的細節，維持只插入名稱");
            return;
        }

        new TextViewEditCoordinator(_textView).ReplaceTracked(
            statementSpan,
            expansion.OperationName,
            target => BuildGuarded(expansion, detail, target, insertedName),
            _setSuppressBufferChange);
    }

    private static TextReplacement? BuildGuarded(
        ISqlCommitExpansion expansion,
        SqlObjectDetail detail,
        SnapshotSpan target,
        string insertedName)
    {
        if (target.IsEmpty ||
            !target.GetText().TrimStart().StartsWith(expansion.LeadingKeyword, StringComparison.OrdinalIgnoreCase))
        {
            SqlAssistDiagnostics.WriteAlways($"要展開的{expansion.OperationName}已經不在原處，放棄這次展開");
            return null;
        }

        return expansion.Build(detail, SqlStatementSite.From(target), insertedName);
    }
}
