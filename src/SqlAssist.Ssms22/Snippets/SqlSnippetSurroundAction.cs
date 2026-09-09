using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Snippets;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Snippets;

/// <summary>
/// 「以片段包住選取範圍」的一次完整流程：確認範圍、挑片段、交給既有的展開路徑。
/// </summary>
/// <remarks>
/// 這裡刻意沒有第二套插入實作。包夾與從建議清單展開走的是同一條路
/// （<see cref="SqlSnippetExpansionController"/> 的原生 session 加上同一份降級），
/// 差別只在交給它的是一份已經把選取文字填進包夾欄位的衍生片段。
/// 另寫一條的代價是縮排、復原單位與欄位導覽各有一份行為，而分岔只會在
/// 「某幾個片段包起來的樣子不一樣」時才被發現。
/// </remarks>
internal static class SqlSnippetSurroundAction
{
    /// <summary>上一次篩出候選清單時的那一份片段庫。</summary>
    /// <remarks>
    /// <c>QueryStatus</c> 每次開選單、每次閒置都會問一遍狀態，而答案的一部分是
    /// 「有沒有可包夾的片段」。整份清單是不可變的穩定參考（存檔成功才換一份），
    /// 所以比對參考就足以知道要不要重篩——與建議清單重建整批候選項的判準同一條。
    ///
    /// 只在 UI 執行緒上存取（命令狀態與命令派送都只發生在那裡），不必同步。
    /// </remarks>
    private static SqlSnippetLibrary? _library;

    private static IReadOnlyList<SqlSnippet> _candidates = Array.Empty<SqlSnippet>();

    /// <summary>命令狀態：有 SQL 編輯器、SqlAssist 開著，而且選了東西。</summary>
    /// <remarks>
    /// 選單與濾鏡都要問這個問題。回報可用卻什麼都不做，跟按鍵沒反應在使用者
    /// 眼裡是同一件事；而回報停用時殼層根本不會派送，使用者綁在
    /// <c>Edit.SurroundWith</c> 上的那個鍵就落回 SSMS 原本的行為。
    /// </remarks>
    public static bool IsAvailable() => IsAvailable(ActiveSqlEditor.Current);

    /// <param name="view">
    /// 要問的編輯器。殼層命令濾鏡問的是<b>它自己掛著的那一個</b>，不是目前作用中的：
    /// 兩者平常是同一個，但濾鏡是每個查詢視窗一份，拿錯就會替別的視窗回答狀態。
    /// </param>
    public static bool IsAvailable(IWpfTextView? view)
    {
        return SqlAssistSettingsStore.Current.Enabled &&
               view is not null &&
               HasUsableSelection(view) &&
               Candidates().Count > 0;
    }

    /// <summary>開啟包夾清單。</summary>
    /// <param name="view">目標編輯器。</param>
    /// <param name="message">沒有開成時要讓使用者看到的原因。</param>
    public static bool TryBegin(IWpfTextView view, out string message)
    {
        if (view is null || view.IsClosed)
        {
            message = "查詢視窗已關閉。";
            return false;
        }

        if (view.Selection.IsEmpty)
        {
            message = "請先選取要包住的文字。";
            return false;
        }

        if (view.Selection.Mode == TextSelectionMode.Box)
        {
            // 框選是好幾段不連續的範圍，包起來之後每一段各自成句的假設不成立。
            message = "框選範圍無法包夾，請改用一般選取。";
            return false;
        }

        var candidates = Candidates();

        if (candidates.Count == 0)
        {
            message = "沒有可以包夾的片段；把片段裡的一格命名為 surround 就會出現在這裡。";
            return false;
        }

        var selection = view.Selection.StreamSelectionSpan.SnapshotSpan;
        if (string.IsNullOrWhiteSpace(selection.GetText()))
        {
            message = "選取範圍只有空白，請選取要包住的 SQL。";
            return false;
        }

        var resolved = SqlSnippetSurroundSelection.Resolve(new SnapshotTextSource(selection.Snapshot),
            selection.Start.Position, selection.Length);
        var target = new SnapshotSpan(selection.Snapshot, resolved.Start, resolved.Length);
        var buffer = target.Snapshot.TextBuffer;
        if (buffer.IsReadOnly(target.Span))
        {
            message = "選取範圍為唯讀，無法包夾。";
            return false;
        }

        var expected = target.GetText();
        var tracking = target.Snapshot.CreateTrackingSpan(target, SpanTrackingMode.EdgeExclusive);

        SqlSnippetSurroundPicker.Show(
            view,
            target.Start,
            candidates,
            SqlSnippetSurroundHistory.PreferredIndex(candidates),
            resolved,
            snippet => Insert(view, buffer, tracking, expected, resolved.Text, snippet));

        message = string.Empty;
        return true;
    }

    private static bool HasUsableSelection(IWpfTextView view)
    {
        return !view.IsClosed &&
               !view.Selection.IsEmpty &&
               view.Selection.Mode != TextSelectionMode.Box;
    }

    /// <summary>這一輪清單裡可以包夾的片段，維持設定檔的順序。</summary>
    /// <remarks>
    /// 不重新排名：包夾清單只有幾筆，而使用者記得的是它們在管理介面裡的順序。
    /// 預選哪一筆由 <see cref="SqlSnippetSurroundHistory"/> 回答，因此新增一筆可包夾
    /// 的片段不會換掉「開起來按 Enter」會拿到的那一筆。
    /// 危險片段的規則也不適用——那條規則管的是「沒有輸入前綴時不主動顯示」，
    /// 而這份清單是使用者自己按鍵叫出來的。
    /// </remarks>
    private static IReadOnlyList<SqlSnippet> Candidates()
    {
        var library = SqlSnippetStore.Current;

        if (ReferenceEquals(library, _library))
        {
            return _candidates;
        }

        var result = new List<SqlSnippet>();

        for (var index = 0; index < library.Snippets.Count; index++)
        {
            if (library.Snippets[index].CanSurround)
            {
                result.Add(library.Snippets[index]);
            }
        }

        _candidates = result;
        _library = library;
        return result;
    }

    /// <summary>把選取文字接進片段，再交給既有的展開路徑。</summary>
    private static void Insert(
        IWpfTextView view,
        ITextBuffer buffer,
        ITrackingSpan tracking,
        string expected,
        string surroundText,
        SqlSnippet snippet)
    {
        var request = new SqlSnippetExpansionRequest(
            buffer,
            tracking,
            expected,
            snippet.WithSurroundText(surroundText));

        // 排到這一輪之後：清單剛關掉，焦點正要還給編輯器，而原生引擎要的是
        // 已經定案的焦點與緩衝區。在原地做的症狀是引擎插得進去、游標卻留在別處。
        TextViewDispatch.AfterCurrentCommand(view, "以片段包住選取範圍", target =>
        {
            var current = tracking.GetSpan(buffer.CurrentSnapshot);
            // 清單是非模態的；內容變動後不能清掉使用者的新選取，也不能記成成功使用。
            if (!string.Equals(current.GetText(), expected, StringComparison.Ordinal) || buffer.IsReadOnly(current.Span))
            {
                SqlAssistStatusBar.Show(Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider,
                    "選取內容已變更或改為唯讀，未套用包夾；請重新選取。");
                return;
            }

            // 要包的範圍已經存成追蹤範圍，選取本身在這裡只剩顯示用途。留著的話：
            // 降級路徑只移動游標、不動選取，使用者會看到一段跟著文字長大的反白；
            // 原生路徑則是引擎選第一格時才順手蓋掉，兩條的收尾因此不一致。
            target.Selection.Clear();

            if (request.Snippet.ExpansionMode != SqlSnippetExpansionMode.TabStops)
            {
                // 包夾欄位填掉之後沒有剩下任何可導航欄位（be、trn 就是這一種）：
                // 一次緩衝區編輯即可，復原是一格，也不必付原生引擎那趟 COM。
                InsertCaret(target, request, snippet);
                return;
            }

            var controller = SqlSnippetExpansionController.Peek(target);
            var before = buffer.CurrentSnapshot;
            var result = controller is null
                ? NativeSnippetInsertionResult.FailedWithoutChange
                : controller.TryInsert(request);

            if (result == NativeSnippetInsertionResult.FailedWithoutChange &&
                !ReferenceEquals(before, buffer.CurrentSnapshot))
            {
                result = NativeSnippetInsertionResult.FailedAfterChange;
            }

            if (result == NativeSnippetInsertionResult.FailedWithoutChange)
            {
                InsertCaret(target, request, snippet);
                return;
            }

            if (result == NativeSnippetInsertionResult.Succeeded)
            {
                SqlSnippetSurroundHistory.Record(snippet);
            }

            if (result == NativeSnippetInsertionResult.FailedAfterChange)
            {
                // 引擎已改過緩衝區時再插一次降級內容會重複；保留它的結果並記錄。
                SqlAssistDiagnostics.WriteAlways(
                    $"原生 Snippet 在回報失敗前已改動文字，已略過降級插入：{snippet.Shortcut}",
                    target);
                SqlAssistStatusBar.Show(Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider,
                    "原生片段引擎未完成包夾；為避免重複插入已停止，請檢查內容，必要時復原。");
            }
        });
    }

    private static void InsertCaret(ITextView view, SqlSnippetExpansionRequest request, SqlSnippet original)
    {
        var before = request.Buffer.CurrentSnapshot;
        SqlSnippetExpansionController.InsertFallback(view, request);
        if (!ReferenceEquals(before, request.Buffer.CurrentSnapshot))
        {
            SqlSnippetSurroundHistory.Record(original);
        }
        else
        {
            SqlAssistStatusBar.Show(Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider,
                "未能寫入包夾內容；請確認查詢可編輯後重試。");
        }
    }
}
