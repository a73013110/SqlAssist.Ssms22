using System;
using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Core.Statements;

/// <summary>
/// 把函式的參數排成一段可以直接執行的引數清單。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlProcedureCallText"/> 是兩支而不是一支，因為 T-SQL 對兩者的要求
/// 正好相反：
///
/// <list type="bullet">
/// <item><c>EXEC</c> 收具名傳值，因此那一支每個參數一列、對齊 <c>@</c>、
/// 在右邊註明型別，而且有預設值的參數可以整列刪掉。</item>
/// <item>函式只收<b>位置</b>引數。<c>dbo.fn_DueDate(@days = 1)</c> 不合法，
/// 有預設值的參數也不能省略——省略的寫法是 <c>DEFAULT</c> 這個關鍵字，位置照留。
/// 所以這裡排成一行、只有值，連參數名稱都寫不進去。</item>
/// </list>
///
/// 一行是刻意的：函式出現在運算式中間（<c>WHERE dbo.fn_DueDate(1) &lt; @today</c>），
/// 拆成多列會把使用者正在寫的那句話撐開。型別看不到不是資訊遺失——
/// 滑鼠停留提示與浮動預覽本來就列著整份參數清單，在編輯器裡再寫一次
/// 只是讓他多刪一次註解。
/// </remarks>
public static class SqlFunctionCallText
{
    /// <summary>
    /// 組出 <c>名稱(值, 值…)</c>。
    /// </summary>
    /// <param name="qualifiedName">已經加好結構描述與方括號的函式名稱。</param>
    /// <param name="parameters">參數，順序就是引數順序；沒有參數時得到一對空括號。</param>
    /// <param name="caretOffset">
    /// 回傳結果字串中第一個引數的位置；沒有參數時是兩個括號之間。
    /// </param>
    /// <remarks>
    /// 沒有參數也要組：<c>SELECT dbo.fn_Today</c> 是語法錯誤，
    /// <c>SELECT dbo.fn_Today()</c> 才不是，而那對括號正是使用者少按的兩次鍵。
    /// </remarks>
    public static string Build(
        string qualifiedName,
        IReadOnlyList<SqlStatementParameter> parameters,
        out int caretOffset)
    {
        if (string.IsNullOrEmpty(qualifiedName))
        {
            throw new ArgumentException("函式名稱不可為空。", nameof(qualifiedName));
        }

        if (parameters is null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        var builder = new StringBuilder(qualifiedName.Length + (parameters.Count * 8) + 2);
        builder.Append(qualifiedName).Append('(');
        caretOffset = builder.Length;

        for (var index = 0; index < parameters.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(SqlLiteralDefaults.ForType(parameters[index].DataType));
        }

        builder.Append(')');
        return builder.ToString();
    }
}
