using System;
using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Core.Statements;

/// <summary>簽章文字裡某一個參數佔的那一段。</summary>
public sealed class SqlSignatureParameter
{
    public SqlSignatureParameter(string name, string documentation, int start, int length)
    {
        Name = name;
        Documentation = documentation;
        Start = start;
        Length = length;
    }

    /// <summary>含 <c>@</c> 前綴的參數名稱。</summary>
    public string Name { get; }

    /// <summary>輪到這個參數時要多說的那一句：型別，以及可不可以省略。</summary>
    public string Documentation { get; }

    /// <summary>在 <see cref="SqlSignatureText.Content"/> 裡的起點。</summary>
    /// <remarks>
    /// 平台的簽章提示要靠這一段把目前的引數標成粗體（<c>IParameter.Locus</c>）。
    /// 由組字串的這一支一併算出來，不在顯示端用 <c>IndexOf</c> 找回去——
    /// 同名或同型別的參數（<c>@from datetime, @to datetime</c>）會找到前面那一個。
    /// </remarks>
    public int Start { get; }

    public int Length { get; }
}

/// <summary>一份可以顯示的函式簽章。</summary>
public sealed class SqlSignatureText
{
    public SqlSignatureText(string content, IReadOnlyList<SqlSignatureParameter> parameters)
    {
        Content = content;
        Parameters = parameters;
    }

    public string Content { get; }

    public IReadOnlyList<SqlSignatureParameter> Parameters { get; }
}

/// <summary>
/// 把函式的參數排成一行給人看的簽章：<c>dbo.dtoc(@ddate smalldatetime) RETURNS varchar(10)</c>。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlFunctionCallText"/> 是兩支而不是一支，因為要的東西相反：那一支寫的是
/// <b>要插進編輯器</b>的呼叫，所以只有值、沒有名稱；這一支寫的是<b>給人看</b>的簽章，
/// 所以只有名稱與型別、沒有值。合成一支的話，其中一邊一定要在輸出上再切一次字串。
///
/// 排成一行而不是每個參數一列：這一份浮在游標旁邊，高度直接吃掉使用者正在看的程式碼。
/// 參數多到一行放不下時交給顯示端自己換行——換在哪裡是排版，不是這一層的事。
/// </remarks>
public static class SqlFunctionSignatureText
{
    /// <param name="qualifiedName">已經加好結構描述的函式名稱。</param>
    /// <param name="parameters">輸入參數，順序就是引數順序。</param>
    /// <param name="returnType">
    /// 傳回型別；空字串代表寫不出 <c>RETURNS</c> 那一段（資料表值函式，
    /// 或讀不到傳回值那一列）。
    /// </param>
    public static SqlSignatureText Build(
        string qualifiedName,
        IReadOnlyList<SqlStatementParameter> parameters,
        string? returnType = null)
    {
        if (string.IsNullOrEmpty(qualifiedName))
        {
            throw new ArgumentException("函式名稱不可為空。", nameof(qualifiedName));
        }

        if (parameters is null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        var builder = new StringBuilder(qualifiedName.Length + (parameters.Count * 24) + 24);
        var spans = new List<SqlSignatureParameter>(parameters.Count);
        builder.Append(qualifiedName).Append('(');

        for (var index = 0; index < parameters.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            var parameter = parameters[index];
            var start = builder.Length;
            builder.Append(parameter.Name);

            if (parameter.DataType.Length > 0)
            {
                builder.Append(' ').Append(parameter.DataType);
            }

            spans.Add(new SqlSignatureParameter(
                parameter.Name,
                Describe(parameter),
                start,
                builder.Length - start));
        }

        builder.Append(')');

        if (!string.IsNullOrEmpty(returnType))
        {
            builder.Append(" RETURNS ").Append(returnType);
        }

        return new SqlSignatureText(builder.ToString(), spans);
    }

    /// <remarks>
    /// 「選擇性」講的是可以寫成 <c>DEFAULT</c>，不是可以整個不寫：函式只收位置引數，
    /// 省略的寫法是 <c>DEFAULT</c> 這個關鍵字，位置照留。與 <c>EXEC</c> 那一支
    /// 「整列刪掉」的意思不同，所以這裡的字也不同。
    /// </remarks>
    private static string Describe(SqlStatementParameter parameter)
    {
        var type = parameter.DataType.Length > 0 ? parameter.DataType : "型別未知";

        return parameter.IsOptional ? $"{type}，可寫 DEFAULT" : type;
    }
}
