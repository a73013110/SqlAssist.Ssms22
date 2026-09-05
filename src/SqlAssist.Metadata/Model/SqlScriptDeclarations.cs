using System;
using System.Collections.Generic;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Metadata.Model;

/// <summary>
/// 這份指令碼自己宣告的資料來源：暫存資料表、資料表變數與 CTE。
/// </summary>
/// <remarks>
/// 三者在中繼資料裡一列都查不到——暫存資料表在 tempdb 裡、資料表變數不是
/// <c>sys.objects</c> 裡的物件、CTE 只存在於這份文字裡。滑鼠停留、Ctrl+F12 與
/// 建議清單的預覽因此都得先問這一份，而問法只有一個：拿名稱換明細。兩份實作的
/// 症狀是同一個名稱在提示裡有欄位、在預覽裡沒有，而且沒有任何徵兆。
///
/// 名冊與<b>欄位建議</b>共用同一個解析器（<see cref="SqlColumnSourceResolver"/>），
/// 「這個名稱宣告了哪些資料行」因此只有一份。解析器自己的收集是延後的，
/// 建立這個型別不掃描任何東西。
/// </remarks>
public sealed class SqlScriptDeclarations
{
    private readonly string _text;
    private readonly SqlColumnSourceResolver _resolver;

    private SqlScriptDeclarations(string text, IReadOnlyList<SqlToken> tokens)
    {
        _text = text;
        _resolver = new SqlColumnSourceResolver(tokens);
    }

    public static SqlScriptDeclarations Create(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        return new SqlScriptDeclarations(text, SqlTokenizer.Tokenize(text));
    }

    /// <param name="tokens">
    /// <paramref name="text"/> 的詞法單元。已經掃過一次的呼叫端把結果傳進來，
    /// 省下在滑鼠移動的軌跡上再把整份文字掃一遍。
    /// </param>
    public static SqlScriptDeclarations Create(string text, IReadOnlyList<SqlToken> tokens)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        return new SqlScriptDeclarations(text, tokens);
    }

    /// <summary>光看名稱就分得出來的那兩種。</summary>
    /// <remarks>
    /// 井號與小老鼠開頭在 T-SQL 裡各只有一個意思，一個字元就判得完。CTE 分不出來：
    /// 它的名稱與一般識別字長得一模一樣，得問過名冊才知道。
    /// </remarks>
    public static SqlObjectKind KindOf(string name)
    {
        return !string.IsNullOrEmpty(name) && name[0] == '@'
            ? SqlObjectKind.TableVariable
            : SqlObjectKind.TemporaryTable;
    }

    /// <summary>這個名稱是不是這份指令碼宣告的；是的話連明細一起讀出來。</summary>
    /// <remarks>
    /// 井號與小老鼠開頭是暫存資料表與資料表變數的必要條件，而那是一個字元的判斷：
    /// 絕大多數的名稱落在一般識別字上，那時連資料表名冊都不必建。
    /// </remarks>
    public SqlObjectDetail? Find(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (SqlIdentifier.IsScriptScoped(name))
        {
            // 資料行讀不出來的宣告（SELECT … INTO #Loan）名冊裡根本沒有，
            // 那時交回 null——名稱與資料行是兩件事。
            return _resolver.ScriptTables.TryGetValue(name, out var table)
                ? SqlScriptTableDetail.Create(table, _text)
                : null;
        }

        return _resolver.FindCommonTableExpression(name) is { } commonTableExpression
            ? SqlScriptTableDetail.Create(
                commonTableExpression,
                _resolver.ResolveCommonTableExpressionColumns(commonTableExpression),
                _text)
            : null;
    }
}
