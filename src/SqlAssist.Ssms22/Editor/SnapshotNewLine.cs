using System;
using Microsoft.VisualStudio.Text;

namespace SqlAssist.Ssms22.Editor;

/// <summary>
/// 要寫進緩衝區的多行文字該用哪一種換行。
/// </summary>
/// <remarks>
/// 三條路徑都要問同一件事：萬用字元展開、提交後展開成整句、原生 Snippet 的
/// 降級與 XML。各寫一份的下場是同一份指令碼裡出現兩種換行——而混合換行不會
/// 報錯，只會讓下一次 diff 整段變紅。
///
/// 不用 <c>IEditorOptions</c> 的 <c>NewLineCharacter</c>：那是「按 Enter 要插什麼」
/// 的偏好，與「這個檔案現在用的是什麼」是兩件事。使用者打開一份 LF 的指令碼時，
/// 該跟著檔案而不是跟著設定。
/// </remarks>
internal static class SnapshotNewLine
{
    public static string Resolve(ITextSnapshot snapshot, int position)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var line = snapshot.GetLineFromPosition(Math.Min(Math.Max(position, 0), snapshot.Length));

        if (line.LineBreakLength > 0)
        {
            return line.GetLineBreakText();
        }

        // 檔案最後一行沒有換行字元。往前找第一個有換行的，才不會在一份 LF 的
        // 指令碼結尾插進 CRLF；整份只有一行時才輪到作業系統的預設值。
        for (var number = line.LineNumber - 1; number >= 0; number--)
        {
            var previous = snapshot.GetLineFromLineNumber(number);

            if (previous.LineBreakLength > 0)
            {
                return previous.GetLineBreakText();
            }
        }

        return Environment.NewLine;
    }
}
