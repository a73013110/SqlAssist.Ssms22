using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;

namespace SqlAssist.Ssms22.Completion;

/// <summary>語句要寫進哪一行；縮排與換行字元都由那一行決定。</summary>
internal readonly struct SqlStatementSite
{
    private SqlStatementSite(
        string indent,
        string newLine,
        string statementText,
        char nextCharacter)
    {
        Indent = indent;
        NewLine = newLine;
        StatementText = statementText;
        NextCharacter = nextCharacter;
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

    /// <summary>
    /// 緊接在這一段後面的那個字元；已經到緩衝區結尾時是 <c>\0</c>。
    /// </summary>
    /// <remarks>
    /// 查詢期間使用者仍在打字，而提交完一個函式名稱之後最順手的下一個鍵正是左括號。
    /// 追蹤範圍是 <see cref="SpanTrackingMode.EdgeExclusive"/>，他打的那個字元落在
    /// 範圍<b>外</b>，範圍裡的字一個都沒變——光看範圍內的文字看不出這件事，
    /// 補上去的結果會是 <c>dbo.fn_DueDate(NULL)(</c>。
    /// </remarks>
    public char NextCharacter { get; }

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
            SnapshotNewLine.Resolve(target.Snapshot, target.Start.Position),
            target.GetText(),
            target.End.Position < target.Snapshot.Length
                ? target.Snapshot[target.End.Position]
                : '\0');
    }
}

/// <summary>提交之後要換掉的是哪一段。</summary>
/// <remarks>
/// 只有這一件事在各種展開之間不一樣，其餘（切執行緒、取最新範圍、確認原文還在）
/// 完全共用。分成兩種而不是讓每一種自己算起點：起點只有這兩個答案，
/// 而算錯的症狀是把使用者前面那半句話一起蓋掉。
/// </remarks>
internal enum SqlCommitExpansionScope
{
    /// <summary>
    /// 從決定目標的那個關鍵字起，到剛插入的名稱結尾。
    /// </summary>
    /// <remarks>
    /// <c>ALTER PROCEDURE</c>、<c>INSERT INTO</c>、<c>MERGE</c>、<c>EXEC</c> 四種
    /// 都是整句換掉，因為要寫回去的東西本來就從那個關鍵字開始。
    /// </remarks>
    Statement,

    /// <summary>
    /// 只有剛插入的那個名稱。
    /// </summary>
    /// <remarks>
    /// 函式的引數清單接在名稱後面，前面是什麼子句都不影響它——<c>SELECT</c>、
    /// <c>WHERE</c>、<c>FROM</c>、<c>CROSS APPLY</c> 都可能，而其中大部分位置
    /// 根本沒有「決定目標的關鍵字」可以當起點（<c>TargetKeywordStart</c> 是 -1）。
    /// </remarks>
    InsertedName
}

/// <summary>提交建議後要把哪一段換成什麼。</summary>
/// <remarks>
/// 五種展開（ALTER 定義、INSERT 骨架、MERGE 骨架、EXEC 呼叫、函式引數）
/// 只有「換成什麼」與「換掉哪一段」不一樣，
/// 「怎麼安全地換」完全相同：切 UI 執行緒、檢查編輯器已關閉、從
/// <see cref="ITrackingSpan"/> 取最新範圍、確認等待期間原文還在原處。
/// 各寫一份的下場是其中一份少了一道，而少的那一道會覆蓋使用者的輸入。
/// </remarks>
internal interface ISqlCommitExpansion
{
    /// <summary>要被換掉的範圍從哪裡起算。</summary>
    SqlCommitExpansionScope Scope { get; }

    /// <summary>要展開的物件；決定去中繼資料層拿誰的細節。</summary>
    SqlObjectInfo Object { get; }

    /// <summary>
    /// 提交當下就已經知道的細節；為 null 才去中繼資料層查。
    /// </summary>
    /// <remarks>
    /// 指令碼自己宣告的暫存資料表與資料表變數走這一條：它們的資料行就寫在
    /// 使用者眼前的宣告裡，中繼資料反而一列都查不到。查得到與查不到只差在
    /// 「細節從哪裡來」，替換那一段完全相同，所以共用同一條路而不是另開一條。
    /// </remarks>
    SqlObjectDetail? KnownDetail { get; }

    /// <summary>寫進紀錄與復原堆疊的操作名稱，例如「ALTER 語句」。</summary>
    string OperationName { get; }

    /// <summary>
    /// 這一段必須仍以這個字串開頭，否則放棄。
    /// </summary>
    /// <remarks>
    /// 查詢期間使用者可能已經把整句刪掉或改寫了。範圍的起點是那個關鍵字
    /// （整句範圍）或剛插入的名稱（名稱範圍），起點不再是它就代表要換的東西
    /// 已經不在原處——這時候把文字蓋上去等於改到別人的語句。
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
    ///
    /// 四種整句展開由<b>位置</b>決定（<see cref="CompletionIntent"/>），函式的引數清單
    /// 則由<b>被選中的東西</b>決定：括號要不要補，跟前面是 <c>SELECT</c> 還是
    /// <c>FROM</c> 無關，只跟它是不是函式有關。因此它接在意圖判斷之後，
    /// 而不是再多一個意圖。
    /// </remarks>
    /// <param name="insertedName">
    /// 這次提交寫進緩衝區的名稱，含結構描述與方括號；等待期間的原文比對要用它。
    /// </param>
    public static ISqlCommitExpansion? Resolve(
        SqlSuggestion selected,
        SqlCompletionContext context,
        int caretPosition,
        SqlAssistSettings settings,
        string insertedName)
    {
        // 整句展開要蓋掉「關鍵字 → 名稱」那一段，算不出起點就整個不做。
        // 函式的引數接在名稱後面，與這個起點無關，所以不在這一關擋。
        var canReplaceStatement =
            context.TargetKeywordStart >= 0 && caretPosition >= context.TargetKeywordStart;

        // 指令碼自己宣告的資料表：資料行在提交當下就全部讀完了，不必再問誰。
        // 只有 INSERT 與 MERGE 兩種意圖用得到——它們要的就是資料行，
        // 而 ALTER 的定義與 EXEC 的參數這兩種名稱一個都給不出來。
        if (selected.Tag is SqlScriptTable scriptTable)
        {
            if (!canReplaceStatement)
            {
                return null;
            }

            var detail = SqlScriptTableDetail.Create(scriptTable);

            return context.Intent switch
            {
                CompletionIntent.InsertStatement => settings.ExpandInsertStatement
                    ? new SqlInsertStatementExpansion(detail.Object, settings, detail)
                    : null,

                CompletionIntent.MergeStatement => settings.ExpandMergeStatement
                    ? new SqlMergeStatementExpansion(detail.Object, settings, detail)
                    : null,

                _ => null
            };
        }

        if (selected.Tag is not SqlObjectInfo objectInfo)
        {
            return null;
        }

        switch (context.Intent)
        {
            case CompletionIntent.AlterDefinition:
                return canReplaceStatement && objectInfo.Kind.IsModule()
                    ? new SqlAlterStatementExpansion(objectInfo)
                    : null;

            // 問的是「插得進去嗎」而不是「查得到資料行嗎」：資料表值函式兩者都答
            // 得出資料行，但 INSERT INTO dbo.fn_LoansByReader 剖析不過，
            // 展開只是把一段跑不動的骨架整句蓋在使用者打的那一行上。
            case CompletionIntent.InsertStatement:
                return canReplaceStatement &&
                       settings.ExpandInsertStatement &&
                       objectInfo.Kind.IsInsertTarget()
                    ? new SqlInsertStatementExpansion(objectInfo, settings)
                    : null;

            case CompletionIntent.MergeStatement:
                return canReplaceStatement &&
                       settings.ExpandMergeStatement &&
                       objectInfo.Kind.IsInsertTarget()
                    ? new SqlMergeStatementExpansion(objectInfo, settings)
                    : null;

            case CompletionIntent.ExecuteCall:
                return canReplaceStatement &&
                       settings.ExpandProcedureCall &&
                       objectInfo.Kind.IsExecutable()
                    ? new SqlProcedureCallExpansion(objectInfo)
                    : null;
        }

        // 到這裡只剩 CompletionIntent.Reference。函式在這些位置一律是「呼叫」，
        // 而 T-SQL 的函式呼叫非有括號不可，因此補上引數清單。
        //
        // 唯一的例外是 ALTER／DROP FUNCTION 那個位置（CompletionTarget.Function）：
        // 那裡要的是名稱本身，補上括號會讓那句 DDL 語法錯誤。ALTER 走的是上面的
        // AlterDefinition，DROP 與它同一個目標卻是 Reference，
        // 所以擋的是目標而不是意圖。
        return settings.ExpandFunctionCall &&
               objectInfo.Kind.IsFunction() &&
               context.Target != CompletionTarget.Function
            ? new SqlFunctionCallExpansion(objectInfo, insertedName)
            : null;
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
        var detail = expansion.KnownDetail ?? await _metadataService
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

    /// <remarks>
    /// 比的是「開頭還是不是原來那個字」而不是整段相等：整句範圍的後半在等待期間
    /// 本來就可能被使用者改過（他一邊等一邊在打字），而那不是放棄的理由。
    /// </remarks>
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
