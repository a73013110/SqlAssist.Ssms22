using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 依游標處的上下文決定哪些建議項能出現在這個位置。
/// </summary>
/// <remarks>
/// 只做一次、在建立清單時：之後每一鍵的前綴比對、排名與可見度在 <see cref="SuggestionList"/>。
/// </remarks>
public static class SuggestionContextFilter
{
    /// <summary>
    /// 只做上下文過濾，不做前綴比對與排名。
    /// </summary>
    /// <remarks>
    /// 原生引擎把清單交給平台快取，之後每一次按鍵只重新比對前綴，
    /// 因此上下文過濾必須在建立清單時就做完。
    /// </remarks>
    public static IReadOnlyList<SqlSuggestion> Filter(
        IEnumerable<SqlSuggestion> suggestions,
        SqlCompletionContext context)
    {
        if (suggestions is null)
        {
            throw new ArgumentNullException(nameof(suggestions));
        }

        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var results = new List<SqlSuggestion>();

        foreach (var suggestion in suggestions)
        {
            if (IsAllowed(suggestion, context))
            {
                results.Add(suggestion);
            }
        }

        return results;
    }

    /// <summary>上下文過濾的四道條件。</summary>
    private static bool IsAllowed(SqlSuggestion suggestion, SqlCompletionContext context)
    {
        return IsAllowedForTarget(suggestion.Kind, context.Target) &&
               IsAllowedForPosition(suggestion, context) &&
               IsAllowedForSchema(suggestion, context) &&
               IsAllowedSystemSchema(suggestion, context);
    }

    /// <summary>
    /// <c>sys</c> 與 <c>INFORMATION_SCHEMA</c> 只出現在拿得到系統物件的位置。
    /// </summary>
    /// <remarks>
    /// 哪些位置算數是上下文的判斷（<see cref="SqlCompletionContext.WantsSystemSchemas"/>），
    /// 這裡只負責套用。認的是名稱而不是來源：第一層查詢不收這兩個結構描述，
    /// 但哪天收了，同一條規則照樣成立。
    /// </remarks>
    private static bool IsAllowedSystemSchema(SqlSuggestion suggestion, SqlCompletionContext context)
    {
        return suggestion.Kind != SuggestionKind.Schema ||
               context.WantsSystemSchemas ||
               !SqlSystemSchemas.IsSystem(suggestion.DisplayText);
    }

    private static bool IsAllowedForTarget(SuggestionKind kind, CompletionTarget target)
    {
        // 資料庫與連結伺服器是多段式名稱的第一段，不是某一種物件。凡是寫得出
        // 多段式名稱的位置就該有它們，而那是由「這個位置接不接得住一個物件」
        // 決定的，不是由目標的名字決定的——逐個目標補的話，漏掉的那一個
        // 沒有徵兆：使用者只會看到「這裡沒有建議」，而語法明明合法。
        //
        // 結構描述是同一種東西的第二段，規則相同。曾經只補了前兩類，於是
        // FROM／EXEC／APPLY 之後列得出 LibArchive 卻列不出 dbo 與 INFORMATION_SCHEMA。
        if (kind is SuggestionKind.Database
            or SuggestionKind.LinkedServer
            or SuggestionKind.Schema)
        {
            return IsQualifiedNameStart(kind, target);
        }

        return target switch
        {
            // 指令碼宣告的 CTE 與暫存資料表在這個位置與資料表完全同格：
            // FROM 後面接得了它們，而它們不在中繼資料裡。
            //
            // 資料表值函式也在這裡：FROM dbo.fn_LoansByReader(1) 是合法的資料來源，
            // 而中繼資料層的 SqlObjectKinds.IsDataSource 早就這樣認了。
            // 連結伺服器與資料庫也在這裡：四段式名稱是合法的資料來源，而它的
            // 第一、二段在上面的 IsQualifiedNameStart 就已經放行；它們能不能出現在
            // 游標<b>這一格</b>是另一個問題，由 IsAllowedForSchema 依限定字停在哪一格
            // 決定——這裡只說「這個目標接不接得住這個類別」。
            CompletionTarget.DataSource => kind is SuggestionKind.Table
                or SuggestionKind.View
                or SuggestionKind.TableFunction
                or SuggestionKind.ScriptDataSource,
            CompletionTarget.Procedure => kind == SuggestionKind.Procedure,

            // ALTER／DROP FUNCTION 兩種函式都改得動也刪得掉。
            CompletionTarget.Function => kind is SuggestionKind.Function
                or SuggestionKind.TableFunction,

            // APPLY 之後只有資料表值函式接得上。
            CompletionTarget.TableFunction => kind == SuggestionKind.TableFunction,
            CompletionTarget.Column => kind == SuggestionKind.Column,
            CompletionTarget.Database => kind == SuggestionKind.Database,
            CompletionTarget.GlobalVariable => kind == SuggestionKind.GlobalVariable,
            // EXEC p @| 同時接得了他自己的變數與那個程序的參數，兩者都對。
            CompletionTarget.Variable => kind is SuggestionKind.Variable or SuggestionKind.Parameter,
            // 使用者自訂的資料表型別與內建型別在同一個位置。
            CompletionTarget.DataType => kind is SuggestionKind.DataType
                or SuggestionKind.UserDefinedType,
            CompletionTarget.View => kind == SuggestionKind.View,
            CompletionTarget.Trigger => kind == SuggestionKind.Trigger,
            CompletionTarget.Sequence => kind == SuggestionKind.Sequence,
            CompletionTarget.DatePart => kind == SuggestionKind.DatePart,
            CompletionTarget.TableHint => kind == SuggestionKind.TableHint,
            CompletionTarget.QueryHint => kind == SuggestionKind.QueryHint,

            // 兩類是同一種東西、不同的來源，排名才分開；能不能出現在這個位置
            // 沒有差別。
            CompletionTarget.Collation => kind is SuggestionKind.Collation
                or SuggestionKind.CollationInUse,

            // 哪幾個關鍵字由片語決定，見 IsAllowedForPosition。
            CompletionTarget.ClauseKeyword => kind == SuggestionKind.Keyword,

            // 沒有限定字時仍然可以有欄位：SELECT | FROM PUBLISHER a 這種位置，
            // 敘述裡看得到的欄位比整個資料庫的物件清單更接近使用者要的東西。
            // 候選清單是依上下文組出來的，沒有範圍就不會有欄位，這裡不必再擋。
            //
            // 小老鼠開頭的兩類與資料型別是被排除的：它們只出現在自己那一個位置，
            // 混進一般清單的話，每一次按鍵都要多比對一批一定比不中的名稱。
            _ => kind is not (SuggestionKind.GlobalVariable
                or SuggestionKind.Variable
                or SuggestionKind.Parameter
                or SuggestionKind.DataType
                or SuggestionKind.UserDefinedType
                or SuggestionKind.Trigger
                or SuggestionKind.Sequence
                or SuggestionKind.DatePart
                or SuggestionKind.TableHint
                or SuggestionKind.QueryHint
                or SuggestionKind.Collation
                or SuggestionKind.CollationInUse)
        };
    }

    /// <summary>
    /// 每一種建議項都要落在文法允許它出現的位置。
    /// </summary>
    /// <remarks>
    /// 關鍵字、內建函式與 Snippet 各自帶著旗標比對，規則只有
    /// <see cref="SqlKeywordPositionExtensions.Allows"/> 一份。Snippet 在只有三筆時也是 Any，
    /// 擴充到幾十筆後必須共用這套過濾，否則 CREATE TABLE 會出現在 SELECT 欄位清單
    /// 中間；內建函式一起收在這裡的理由相同：語句開頭與 DDL 物件位置不該冒出
    /// <c>COUNT</c>。
    ///
    /// 其餘的都是<b>名稱</b>（資料表、程序、欄位、CTE…），規則是
    /// <see cref="SqlKeywordPositionExtensions.AcceptsNames"/>。少了這一半的症狀是
    /// <c>GROUP BY a D</c> 列出整個資料庫的欄位、函式與預存程序，而文法上一個都寫不上去。
    /// </remarks>
    private static bool IsAllowedForPosition(SqlSuggestion suggestion, SqlCompletionContext context)
    {
        // 子句片語比對得到時，這一格的關鍵字只來自那個片語：位置旗標是整個子句的粗分層，
        // 片語是更靠近游標的答案——SET DATEFORMAT 之後不是 ON／OFF。反過來，片語的字
        // 只屬於那個片語，不會漏到別的位置。認的是 Tag 而不是顯示文字：READ 同時在
        // 關鍵字目錄與片語裡，比文字的話兩份都會出現。
        if (suggestion.Kind == SuggestionKind.Keyword &&
            (context.ClausePhrase is not null || suggestion.Tag is SqlClausePhrase))
        {
            return ReferenceEquals(suggestion.Tag, context.ClausePhrase);
        }

        if (suggestion.Kind is SuggestionKind.Keyword or
            SuggestionKind.BuiltInFunction or
            SuggestionKind.Snippet)
        {
            return suggestion.Positions.Allows(context.KeywordPosition);
        }

        // 批次第一句可以省略 EXEC：sp_helptext 't' 單獨一行就能執行。
        return context.KeywordPosition.AcceptsNames() ||
               (context.StartsBatch && suggestion.Kind == SuggestionKind.Procedure);
    }

    /// <summary>
    /// 這個位置寫得出多段式名稱嗎。
    /// </summary>
    /// <remarks>
    /// 寫得出的是「會接一個資料庫物件」的位置：<c>FROM</c>、<c>JOIN</c> 之外，
    /// 運算式裡的純量函式（<c>SELECT LibArchive.dbo.fn_Fee(1)</c>）、<c>EXEC</c>
    /// 的程序、<c>APPLY</c> 的資料表值函式、<c>NEXT VALUE FOR</c> 的序列，
    /// 以及 <c>DROP VIEW</c> 這一類都算。
    ///
    /// 排除的是根本接不到物件的位置：欄位（<c>a.</c> 之後）、變數與全域變數、
    /// 資料型別、日期部分與兩種提示。那些位置放進來的話，每一次按鍵都要多背
    /// 一份一定比不中的名單。
    ///
    /// <c>USE</c> 是唯一的特例：那裡的資料庫名稱是整句的<b>終點</b>而不是名稱的
    /// 第一段，連結伺服器與結構描述在那裡根本接不上（<c>USE</c> 換不了伺服器，
    /// 也不接結構描述）。
    /// </remarks>
    private static bool IsQualifiedNameStart(SuggestionKind kind, CompletionTarget target)
    {
        if (target == CompletionTarget.Database)
        {
            return kind == SuggestionKind.Database;
        }

        return target is CompletionTarget.Any
            or CompletionTarget.DataSource
            or CompletionTarget.Procedure
            or CompletionTarget.Function
            or CompletionTarget.TableFunction
            or CompletionTarget.Sequence
            or CompletionTarget.View;
    }

    /// <summary>
    /// 限定字要當成結構描述來過濾。
    /// </summary>
    /// <remarks>
    /// 限定字已經解析成資料來源時（<c>u.</c>），建議清單裡放的是欄位，
    /// 欄位沒有結構描述可比，這時不再套用結構描述過濾。
    /// </remarks>
    private static bool IsAllowedForSchema(SqlSuggestion suggestion, SqlCompletionContext context)
    {
        if (context.Target == CompletionTarget.Column)
        {
            return true;
        }

        // 沒有限定字時哪些類別出得來，由位置決定（IsQualifiedNameStart），
        // 不在這裡再判一次——兩處各判一份的話，放行的那一處會被另一處擋掉，
        // 而症狀是「這個位置就是沒有建議」，看不出是誰擋的。
        if (context.QualifierPath is null)
        {
            return true;
        }

        // 連結伺服器之後只能是資料庫：LIBSQL02. 的下一段沒有別的東西可以接。
        // 物件與結構描述要再往右一格才出得來，這裡放行的話清單會列出一批
        // 選了就寫成三段式、而那台伺服器上根本查不到的名稱。
        if (context.QualifierPath.QualifierEnd == SqlQualifierSlot.Server)
        {
            return suggestion.Kind == SuggestionKind.Database;
        }

        // 連結伺服器只在最左邊那一格對，往右一格起都不對。
        if (suggestion.Kind == SuggestionKind.LinkedServer)
        {
            return false;
        }

        // 資料庫名稱在其餘任何一個點號之後都不對：USE 與連結伺服器之後才是它的位置。
        // 這一條要問路徑而不是問 Qualifier——LibArchive.. 有路徑卻沒有結構描述那一段，
        // 問 Qualifier 的話整份過濾會被跳過，那個資料庫的名稱清單就跟著列出來。
        if (suggestion.Kind == SuggestionKind.Database)
        {
            return false;
        }

        // 限定字停在資料庫那一格（LibArchive.）時，下一段是結構描述或物件，兩者都對；
        // 省略結構描述的 LibArchive.. 則是沒有東西可以比對。兩種都不做結構描述過濾。
        if (context.QualifierPath.QualifierEnd == SqlQualifierSlot.Database ||
            string.IsNullOrEmpty(context.Qualifier))
        {
            return true;
        }

        return suggestion.Kind != SuggestionKind.Schema &&
               string.Equals(suggestion.SchemaName, context.Qualifier, StringComparison.OrdinalIgnoreCase);
    }
}
