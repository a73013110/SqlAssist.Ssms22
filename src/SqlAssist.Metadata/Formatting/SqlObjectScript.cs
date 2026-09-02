using System;
using System.Text;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Formatting;

/// <summary>組好的指令碼，以及游標該停在它的第幾個字元。</summary>
public readonly struct SqlObjectScriptText
{
    public SqlObjectScriptText(string text, int caretOffset)
    {
        Text = text ?? string.Empty;
        CaretOffset = Math.Min(Math.Max(caretOffset, 0), Text.Length);
    }

    public string Text { get; }

    /// <summary>
    /// 游標落點。
    /// </summary>
    /// <remarks>
    /// 永遠是有效位置，呼叫端不必再判斷負值——一份剛開的定義停在結尾等於
    /// 一打開就被捲到最後一行，那是使用者得自己捲回去的那種難用。
    /// </remarks>
    public int CaretOffset { get; }
}

/// <summary>
/// 把物件結構組成一份可以直接貼進查詢視窗執行的指令碼。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlObjectStructure.BuildScript"/> 的分工：那裡負責「這個物件的
/// 定義長什麼樣」，這裡負責「要讓它單獨執行還缺什麼」——批次分隔、SET 選項，
/// 以及模組要改寫成 <c>ALTER</c>。合成一個方法的話，浮動預覽的指令碼分頁
/// 就會跟著多出兩行 SET 與兩個 GO，那份文字是拿來對照的，不是拿來執行的。
/// </remarks>
public static class SqlObjectScript
{
    /// <summary>
    /// 指令碼開頭固定的批次。
    /// </summary>
    /// <remarks>
    /// 兩個 SET 不是裝飾：<c>ALTER PROCEDURE</c> 必須是批次裡的第一個敘述，
    /// 所以它們後面一定要有 <c>GO</c> 才分得開；而計算欄位、篩選索引與索引檢視
    /// 對這兩個選項的值有要求，少了它們的 <c>CREATE TABLE</c> 在某些連線設定下
    /// 會直接失敗。SSMS 自己的「編寫指令碼為」也是照這三行開頭的。
    /// </remarks>
    private static readonly string[] HeaderLines =
    {
        "SET QUOTED_IDENTIFIER ON",
        "SET ANSI_NULLS ON",
        "GO"
    };

    private const string BatchSeparator = "GO";

    /// <param name="newLine">
    /// 目的地文件使用的換行字元。不是 <c>\r\n</c>、<c>\n</c>、<c>\r</c> 其中之一時
    /// 退回作業系統預設值。
    /// </param>
    public static SqlObjectScriptText BuildEditable(SqlObjectStructure structure, string? newLine)
    {
        if (structure is null)
        {
            throw new ArgumentNullException(nameof(structure));
        }

        var lineBreak = ResolveLineBreak(newLine);

        // 換行統一要在算游標位置<b>之前</b>做完：改寫換行會讓後面每一個字元位移，
        // 在原文上算出來的落點會掉在名稱中間。
        var body = Rewrite(BuildBody(structure), lineBreak);
        var builder = new StringBuilder(body.Length + 64);

        foreach (var line in HeaderLines)
        {
            builder.Append(line).Append(lineBreak);
        }

        var headerLength = builder.Length;
        builder.Append(body);

        if (!body.EndsWith(lineBreak, StringComparison.Ordinal))
        {
            builder.Append(lineBreak);
        }

        builder.Append(BatchSeparator).Append(lineBreak);

        // 認不出標頭（取不到定義時整段是註解）就停在本體的第一個字元，
        // 不是停在結尾——見 SqlObjectScriptText.CaretOffset。
        var nameEnd = SqlModuleScript.FindHeaderNameEnd(body);
        return new SqlObjectScriptText(builder.ToString(), headerLength + Math.Max(nameEnd, 0));
    }

    /// <remarks>
    /// 三支，差別在「這一類物件寫得出可以執行的指令碼嗎」：
    ///
    /// <list type="bullet">
    /// <item><b>模組</b>——定義原文改寫成 <c>ALTER</c>，讓它可以直接改完就執行。
    /// 取不到定義時 <see cref="SqlObjectStructure.BuildScript"/> 給的是整段註解，
    /// <see cref="SqlModuleScript.TryConvertCreateToAlter"/> 認不出開頭的關鍵字而
    /// 回報失敗，於是原樣保留——那正是要的結果。</item>
    /// <item><b>資料表與資料表型別</b>——維持 <c>CREATE TABLE</c> 與
    /// <c>CREATE TYPE ... AS TABLE</c>。這兩者都沒有對應的 <c>ALTER</c> 整體寫法，
    /// 改下去得到的是一段執行到一半才失敗的指令碼。</item>
    /// <item><b>同義字與序列</b>——本擴充自己組的 <c>CREATE SYNONYM</c>／
    /// <c>CREATE SEQUENCE</c>（見 <see cref="SqlCatalogScript"/>）。這兩種沒有
    /// <c>ALTER</c> 的整體寫法，維持 <c>CREATE</c>。</item>
    /// <item><b>其餘</b>（認不出來的種類）——整段註解。</item>
    /// </list>
    ///
    /// 「哪一類寫得出來」由 <see cref="SqlObjectKinds.HasExecutableScript"/> 一份說了算，
    /// 不在這裡另列種類：這條路徑與浮動預覽的指令碼分頁各留一份判斷的症狀，
    /// 就是同一個物件在兩邊得到不同的東西。
    ///
    /// 「這一次的資料夠不夠」則不必在這裡判。種類過得了關、資料卻不齊時
    /// （模組沒有定義、資料表沒有欄位），<see cref="SqlObjectStructure.BuildScript"/>
    /// 給的已經是整段註解，原樣送出去就是對的。那一份註解寫的是缺什麼與為什麼，
    /// 與這裡「這一類物件本來就組不出來」是兩件事，不能互相取代。
    /// </remarks>
    private static string BuildBody(SqlObjectStructure structure)
    {
        var kind = structure.Object.Kind;

        if (kind.IsModule())
        {
            var script = structure.BuildScript();

            return SqlModuleScript.TryConvertCreateToAlter(script, out var altered) ? altered : script;
        }

        return kind.HasExecutableScript() ? structure.BuildScript() : BuildUnscriptableBody(structure);
    }

    /// <summary>
    /// 寫不出可執行指令碼的物件：整段註解，並說明為什麼。
    /// </summary>
    /// <remarks>
    /// <see cref="SqlObjectStructure.BuildScript"/> 在這裡給的是一段給人看的摘要
    /// （<c>Object [dbo].[Foo]</c> 這種），那份文字貼在唯讀的預覽窗格裡沒有問題，
    /// 但這裡產生的是要拿去執行的指令碼——原樣送出去就是一句不是 T-SQL 的東西。
    ///
    /// 現在只剩認不出來的種類會走到這裡，但這一支不能拿掉：
    /// <c>SqlObjectKinds.FromSysObjectType</c> 對沒見過的型別代碼回傳
    /// <c>Unknown</c>，而 SQL Server 的物件型別只會愈來愈多。
    ///
    /// 同義字、序列與資料表型別曾經都走這一支。前兩者的定義現在由
    /// <see cref="SqlCatalogScript"/> 從目錄檢視組回 <c>CREATE</c>；
    /// 資料表型別則直接組 <c>CREATE TYPE ... AS TABLE</c>——它有欄位，
    /// 當時落到資料表那一支會被寫成 <c>CREATE TABLE</c>，而那是指令碼在說謊，
    /// 照著執行會多出一張同名的資料表。
    /// </remarks>
    private static string BuildUnscriptableBody(SqlObjectStructure structure)
    {
        var builder = new StringBuilder();
        builder.Append("-- 無法為 ").Append(structure.Object.QualifiedName)
            .Append('（').Append(structure.Object.Kind.ToDisplayName())
            .AppendLine("）產生可以執行的指令碼。");
        builder.AppendLine("-- SqlAssist 認不得這個物件的種類，因此不知道它的定義該長什麼樣。");
        builder.AppendLine("-- 以下是查得到的部分：");
        builder.AppendLine();

        foreach (var line in SplitLines(structure.Detail.BuildPreview()))
        {
            builder.Append("--").Append(line.Length == 0 ? string.Empty : " ").AppendLine(line);
        }

        return builder.ToString();
    }

    /// <remarks>
    /// 只用 <c>\n</c> 分行並把 <c>\r</c> 修掉：這一段的來源是本擴充自己組的摘要，
    /// 但換行統一是後面那一道的事，這裡先不要依賴它已經做過。
    /// </remarks>
    private static string[] SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // 摘要以換行結尾時最後會多一個空字串，註解掉會變成一行孤零零的「--」。
        return lines.Length > 0 && lines[lines.Length - 1].Length == 0
            ? Trim(lines)
            : lines;
    }

    private static string[] Trim(string[] lines)
    {
        var trimmed = new string[lines.Length - 1];
        Array.Copy(lines, trimmed, trimmed.Length);
        return trimmed;
    }

    private static string ResolveLineBreak(string? newLine)
    {
        return newLine == "\r\n" || newLine == "\n" || newLine == "\r"
            ? newLine
            : Environment.NewLine;
    }

    /// <summary>把整份文字的換行統一成 <paramref name="lineBreak"/>。</summary>
    /// <remarks>
    /// 這一段是唯一會把兩種來源接在一起的地方：樣板是本擴充寫死的，本體則來自
    /// <c>OBJECT_DEFINITION</c>，而資料庫裡存的定義用哪一種換行完全看當初是誰建的。
    /// 混合換行不會報錯，只會讓這份指令碼存檔之後的第一次 diff 整段變紅。
    ///
    /// 先掃一遍再決定要不要重建：絕大多數定義本來就跟目的地一致，而這裡處理的是
    /// 動輒數萬行的字串，白白複製一份是使用者按下 F12 之後要等的時間。
    /// </remarks>
    private static string Rewrite(string text, string lineBreak)
    {
        if (string.IsNullOrEmpty(text) || !NeedsRewrite(text, lineBreak))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];

            if (current != '\r' && current != '\n')
            {
                builder.Append(current);
                continue;
            }

            builder.Append(lineBreak);

            // CRLF 是一個換行不是兩個；不跳過 LF 的話每一行都會變成兩行。
            if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }
        }

        return builder.ToString();
    }

    private static bool NeedsRewrite(string text, string lineBreak)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];

            if (current != '\r' && current != '\n')
            {
                continue;
            }

            var length = current == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;

            if (length != lineBreak.Length ||
                string.CompareOrdinal(text, index, lineBreak, 0, length) != 0)
            {
                return true;
            }

            index += length - 1;
        }

        return false;
    }
}
