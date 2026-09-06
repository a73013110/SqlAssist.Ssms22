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
/// 與 <see cref="TSqlScriptRenderer"/> 的分工：那裡負責「這個物件的定義長什麼樣」，
/// 這裡負責「送進一個新的查詢視窗還缺什麼」——換行要統一成目的地文件的那一種，
/// 游標要停在名稱之後，以及認不出來的種類要整段註解掉。
///
/// 批次分隔、SET 選項與模組的 <c>CREATE</c> → <c>ALTER</c> 改寫都<b>不</b>在這裡：
/// 那三件事在 <see cref="SqlScriptOptions"/> 上各有一個選項，而排版只有 renderer
/// 一份。曾經在這一層另外加一組樣板，症狀是選項開著時同一份指令碼有兩行 SET
/// 與兩個結尾的 GO。
/// </remarks>
public static class SqlObjectScript
{
    /// <param name="context">
    /// 排版選項與目的地文件的換行字元。換行由 <see cref="SqlScriptContext"/> 收斂，
    /// 這裡不再自己判斷——兩份判斷會在其中一份改了之後給出不同的換行。
    /// </param>
    public static SqlObjectScriptText BuildEditable(SqlObjectStructure structure, SqlScriptContext context)
    {
        if (structure is null)
        {
            throw new ArgumentNullException(nameof(structure));
        }

        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var lineBreak = context.NewLine;

        // 換行統一要在算游標位置之前做完：改寫換行會讓後面每一個字元位移，
        // 在原文上算出來的落點會掉在名稱中間。
        var text = Rewrite(BuildBody(structure, context), lineBreak);

        if (!text.EndsWith(lineBreak, StringComparison.Ordinal))
        {
            text += lineBreak;
        }

        return new SqlObjectScriptText(text, FindCaret(text));
    }

    /// <summary>游標該停在哪裡。</summary>
    /// <remarks>
    /// 開頭的 <c>SET</c> 批次要先跳過再問名稱：<see cref="SqlModuleScript.FindHeaderNameEnd"/>
    /// 要求第一個詞元就是 <c>CREATE</c> 或 <c>ALTER</c>，前面多兩行設定它就一律回報
    /// 找不到，而那會讓每一次 F12 都停在整份指令碼的最前面。
    ///
    /// 認不出標頭（取不到定義時整段是註解）就停在本體的第一個字元，不是停在結尾
    /// ——見 <see cref="SqlObjectScriptText.CaretOffset"/>。
    /// </remarks>
    private static int FindCaret(string text)
    {
        var offset = SkipLeadingSetBatches(text);
        var nameEnd = SqlModuleScript.FindHeaderNameEnd(text.Substring(offset));

        return nameEnd < 0 ? offset : offset + nameEnd;
    }

    /// <summary>回傳第一個不是 <c>SET</c> 也不是 <c>GO</c> 的那一行從哪裡開始。</summary>
    private static int SkipLeadingSetBatches(string text)
    {
        var index = 0;

        while (index < text.Length)
        {
            // 檔頭與健檢註解可能排在 SET 之前；沿用共用的註解掃描，不另造一份詞法器。
            index = SqlTrivia.Skip(text, index, text.Length);
            if (index == text.Length)
            {
                return 0;
            }
            var lineEnd = text.IndexOf('\n', index);
            var stop = lineEnd < 0 ? text.Length : lineEnd;
            var line = text.Substring(index, stop - index).Trim();

            if (line.Length != 0 &&
                !line.StartsWith("SET ", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(line, "GO", StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }

            if (lineEnd < 0)
            {
                return 0;
            }

            index = lineEnd + 1;
        }

        return 0;
    }

    /// <remarks>
    /// 兩支，差別在「這一類物件寫得出可以執行的指令碼嗎」。
    ///
    /// 「哪一類寫得出來」由 <see cref="SqlObjectKinds.HasExecutableScript"/> 一份說了算，
    /// 不在這裡另列種類：這條路徑與浮動預覽的指令碼分頁各留一份判斷的症狀，
    /// 就是同一個物件在兩邊得到不同的東西。
    ///
    /// 「這一次的資料夠不夠」則不必在這裡判。種類過得了關、資料卻不齊時
    /// （模組沒有定義、資料表沒有欄位），renderer 給的已經是整段註解，
    /// 原樣送出去就是對的。那一份註解寫的是缺什麼與為什麼，與這裡
    /// 「這一類物件本來就組不出來」是兩件事，不能互相取代。
    /// </remarks>
    private static string BuildBody(SqlObjectStructure structure, SqlScriptContext context) =>
        structure.Object.Kind.HasExecutableScript()
            ? structure.BuildScript(context)
            : BuildUnscriptableBody(structure);

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
        SqlScriptComment.AppendLine(builder, "無法為 " + structure.Object.QualifiedName +
            "（" + structure.Object.Kind.ToDisplayName() + "）產生可以執行的指令碼。", Environment.NewLine);
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
