using System;
using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Core.Statements;

/// <summary>
/// 把模組的參數排成一整句具名傳值的 <c>EXEC</c>。
/// </summary>
/// <remarks>
/// 續行對齊到第一個參數所在的欄，而不是固定縮排幾格。代價是名稱長的模組會把整段推向
/// 右邊——<c>EXEC dbo.usp_Announcement_ReadByDepartment </c> 一開始就吃掉四十幾欄，
/// 再加上參數與註解很容易越過一般的行寬。換來的是每一列的 <c>@</c> 對齊在同一欄，
/// 掃過去就知道有幾個參數、少填了哪一個。
/// </remarks>
public static class SqlProcedureCallText
{
    /// <summary>
    /// 組出 <c>EXEC 名稱 @參數 = 值, …</c>，必要時在前面補上 OUTPUT 參數的 <c>DECLARE</c>。
    /// </summary>
    /// <param name="executeKeyword">使用者原本寫的 <c>EXEC</c> 或 <c>EXECUTE</c>，照原文帶回去。</param>
    /// <param name="qualifiedName">已經加好結構描述與方括號的模組名稱。</param>
    /// <param name="parameters">參數，順序就是輸出順序。</param>
    /// <param name="indent">第二行起每一行的前導文字，通常是 <c>EXEC</c> 那一行的縮排。</param>
    /// <param name="newLine">緩衝區使用的換行字元。</param>
    /// <param name="caretOffset">回傳結果字串中第一個參數的值的位置。</param>
    public static string Build(
        string executeKeyword,
        string qualifiedName,
        IReadOnlyList<SqlStatementParameter> parameters,
        string indent,
        string newLine,
        out int caretOffset)
    {
        if (string.IsNullOrEmpty(executeKeyword))
        {
            throw new ArgumentException("EXEC 關鍵字不可為空。", nameof(executeKeyword));
        }

        if (string.IsNullOrEmpty(qualifiedName))
        {
            throw new ArgumentException("模組名稱不可為空。", nameof(qualifiedName));
        }

        if (parameters is null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        if (parameters.Count == 0)
        {
            throw new ArgumentException("沒有參數就不必展開。", nameof(parameters));
        }

        indent ??= string.Empty;
        newLine = string.IsNullOrEmpty(newLine) ? "\r\n" : newLine;

        var assignments = new string[parameters.Count];
        var widest = 0;

        for (var index = 0; index < parameters.Count; index++)
        {
            assignments[index] = Assignment(parameters[index]);
            var width = assignments[index].Length + (index == parameters.Count - 1 ? 0 : 1);

            if (width > widest)
            {
                widest = width;
            }
        }

        var builder = new StringBuilder();

        // OUTPUT 參數傳的必須是變數，光給字面值是語法錯誤。少了這幾行 DECLARE，
        // 展開出來的東西連編譯都過不了——那比什麼都不做糟糕。
        // 使用者已經宣告過同名變數時會撞名，但那是一個當場看得見的編譯錯誤。
        AppendOutputDeclarations(builder, parameters, indent, newLine);

        builder.Append(executeKeyword).Append(' ').Append(qualifiedName).Append(' ');

        // 只補「EXEC 名稱 」那一段的寬度：行首的縮排由下面的 Append(indent) 原樣重複，
        // 縮排裡有定位字元時才不會因為一個定位字元只算一個字元而歪掉。
        var continuation = new string(' ', executeKeyword.Length + 1 + qualifiedName.Length + 1);

        for (var index = 0; index < parameters.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(continuation);
            }

            var assignment = assignments[index] + (index == parameters.Count - 1 ? string.Empty : ",");
            builder.Append(assignment);
            builder.Append(' ', widest - assignment.Length + 1);
            builder.Append("-- ").Append(Comment(parameters[index]));

            if (index != parameters.Count - 1)
            {
                builder.Append(newLine).Append(indent);
            }
        }

        caretOffset = CaretOffset(executeKeyword, qualifiedName, parameters, indent, newLine);
        return builder.ToString();
    }

    /// <remarks>
    /// 第一個參數的值就在 <c>EXEC 名稱 @參數 = </c> 之後，而前面可能還有幾行 DECLARE。
    /// 這裡重算一次而不是在迴圈裡記，是因為 DECLARE 那一段的長度只有這裡算得準。
    /// </remarks>
    private static int CaretOffset(
        string executeKeyword,
        string qualifiedName,
        IReadOnlyList<SqlStatementParameter> parameters,
        string indent,
        string newLine)
    {
        var declarations = new StringBuilder();
        AppendOutputDeclarations(declarations, parameters, indent, newLine);

        return declarations.Length
            + executeKeyword.Length + 1
            + qualifiedName.Length + 1
            + parameters[0].Name.Length
            + " = ".Length;
    }

    private static void AppendOutputDeclarations(
        StringBuilder builder,
        IReadOnlyList<SqlStatementParameter> parameters,
        string indent,
        string newLine)
    {
        foreach (var parameter in parameters)
        {
            if (!parameter.IsOutput)
            {
                continue;
            }

            builder
                .Append("DECLARE ").Append(parameter.Name).Append(' ').Append(parameter.DataType).Append(';')
                .Append(newLine).Append(indent);
        }
    }

    /// <summary>OUTPUT 參數收的是變數，其餘依型別給預留字面值。</summary>
    private static string Assignment(SqlStatementParameter parameter)
    {
        return parameter.IsOutput
            ? $"{parameter.Name} = {parameter.Name} OUTPUT"
            : $"{parameter.Name} = {SqlLiteralDefaults.ForType(parameter.DataType)}";
    }

    /// <remarks>
    /// OUTPUT 不寫進註解——那個字已經在左邊的程式碼裡了。「選擇性」則沒有別的地方看得到，
    /// 而它正是使用者要決定「這一列能不能整列刪掉」的依據。
    /// </remarks>
    private static string Comment(SqlStatementParameter parameter)
    {
        return parameter.IsOptional
            ? parameter.DataType + "，選擇性"
            : parameter.DataType;
    }
}
