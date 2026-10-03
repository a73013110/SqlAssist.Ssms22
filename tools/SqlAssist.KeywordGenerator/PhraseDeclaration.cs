using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.KeywordGenerator;

/// <summary>物件種類的展開：CREATE 之後是新名字，ALTER、DROP 之後是既有的物件。</summary>
public enum ObjectKinds
{
    None,
    New,
    Existing,
}

// 片語的尾巴。執行期由 SqlClausePhrase 以同一份文字比對游標前的詞元：
//   {name}   一個名稱單位，可以含點號與方括號；保留字（ALTER INDEX ALL、ALTER DATABASE CURRENT）與變數也算
//   {value}  一個數值、字串、變數，或一整組括號
//   ()       一整組括號；探測代入 (a)，剖析器對括號裡的內容有要求時（RAISERROR 要訊息、嚴重性、狀態）由 Group 指定
//   (*       還沒關上的左括號清單，游標在左括號或逗號之後。字是左括號之後與「第一項的每一種寫法接逗號」
//            之後的聯集（執行期分不出是哪一格）；括號裡是一個子句、逗號屬於子句的（WITHIN GROUP (ORDER BY …)）寫 Clause = true。
//            後面還有項的話，那幾項是清單裡某一項的開頭（WITH (* TYPE =：清單裡任一項的 TYPE =），探測代入左括號；
//            清單有固定的第一項時，探測要墊的那幾項寫在 Items（FORMAT_TYPE = DELIMITEDTEXT, ）
//   ,        逗號本身，分隔同一句裡重複的一段（ADD EVENT a.b, ADD EVENT），比對同等號
//   ,*       標頭開的逗號清單，游標在逗號之後；在最後一項時前面那段是標頭，以字面字結尾。
//            標頭本身也立成片語，給第一項的字；逗號之後的字以「第一項的每一種寫法接逗號」探測取聯集，探到的新字再往下一項探。
//            清單由位置分析走訪（OptionItem），哪些敘述有這種清單只在這裡說。選項寫完之後還有位置要回報
//            （模組標頭的 AS）的，仍以位置為鍵。
//            寫在中段是標頭之後零到多項寫完的清單項，後面那幾項是游標所在那一項的一部分：檔案規格一組一組寫
//            （ADD FILE ,* (*：第幾組的括號裡都一樣）、SET 一次寫幾個選項（SET ,* DATEFORMAT）。探測代入 Gap，沒寫就是零項。
//            以逗號結尾的（SET ,* ,）是逗號之後的下一項，字照清單片語探，只是由尾巴比對、不經位置分析：
//            SET 寫成清單片語的話，分析器把 SET 之後判成 OptionItem，蓋掉 SetTarget（SET @ 要列變數）
//   ...      動詞之後、下一個字面字之前的其餘標頭（EXEC p @a = 1 WITH 的 p @a = 1、BACKUP 的裝置清單）；
//            前後都要是字面字，第一個字是這一句的動詞，由位置分析找。中間第一個詞元不能是關鍵字：
//            EXECUTE AS … WITH 是別的敘述。探測時代入 Gap 那段文字
// 尾巴可以是空的：只認位置，「這個位置接得了這些字」。游標選項這種會重複的格子尾巴寫不出來。
//
// 片語前面那一格由 After 與 Lead 二選一交代：
//   After  片語第一個字前面的位置，名稱取自 PositionTemplates。探測用那些位置的樣板，
//          執行期也只在前一格是這些位置時才算數——同一條尾巴在不同位置是不同的意思
//          （查詢之後的 FOR 接 XML，UPDATE t SET 的 SET 不是選項的 SET）。
//          兩個都不寫就是 StatementStart：沒有 Lead 的片語都從一句的開頭寫起。
//   Lead   位置分析判不出前一格、而尾巴本身就認得出意思時，探測要墊的文字；執行期不看前一格。
//          判得出來的一律寫 After：同一件事只由位置分析說一次。
//   AlsoLeads  同一條尾巴在別的敘述裡也判不出前一格、接的字卻不同時，另外要墊的文字，字取聯集：
//          伺服器稽核規格的 ADD ( 接伺服器層級的動作群組，資料庫稽核規格的接資料庫層級的群組與 SELECT 這類動作，
//          執行期分不出是哪一種。各寫一條的話鍵相同，後寫的蓋掉先寫的。
// Template 是 After 位置的第幾個樣板（從 0 起），預設第一個：同一個位置的樣板接得上的字不一定相同
// （WHEN MATCHED THEN 之後寫不出 INSERT）。
// Expand 往下再探幾層：每個接得上的字接在片語後面成為新的片語，直到那個字寫完語句為止。
// Values 是剖析器分不出來、只能手寫的字，一樣要剖析得過才收：SET DATEFORMAT 的值在
// 剖析器眼中就是名稱；語句已經完整的片語扣掉了下一句的開頭，同時也是子句字的要補回來
// （更長的片語寫得出那個字時不必：片語裡的每一個字由它前面那段列出，見 PhraseExplorer 的證據那一段）。
// Closed 由人宣告那一格只有這幾個值。
// Endings 是只有這條片語用得上的續尾（VECTOR_SEARCH 的 METRIC 只收 'cosine' 這種距離名稱、ABORT_AFTER_WAIT 的值
// 之後要關兩層括號）：探這條片語與它的清單項時接在共用續尾之後。共用續尾一變，每個片語的探測都要重剖。
// Classes 宣告這一格寫的是安全性實體的類別（GRANT … ON OBJECT::、ALTER AUTHORIZATION ON SCHEMA::）：多字的類別
// （SEARCH PROPERTY LIST、EXTERNAL MODEL）剖析器要看到整段才收，在第一個字就報錯，逐字探不出來。產生器拿 CREATE 展開
// 探到的多字物件種類一一代入，整段剖析得過的就是證據，每一個字由它前面那段列出（見 PhraseExplorer 的證據那一段）。
// Lagging 是清單片語才有的手寫選項：官方文件有、ScriptDom 還不收的（CREATE USER … WITH ALLOW_ENCRYPTED_VALUE_MODIFICATIONS = ON
// 在 TSql170 的值就報錯）。剖析器證明不了，所以不驗字本身，只驗標頭剖析得過；補進第一項與逗號之後兩格。
// 這是唯一一種不經剖析器證明的字，只收官方語法圖寫得出來、而剖析器落後的選項。
// 同一條尾巴、同一個位置後寫的覆蓋先寫的，所以 Expand 展開出來的片語可以在後面補 Values。

/// <summary>一條子句片語的宣告：手寫的只有這些，接得上的字全由剖析器決定。寫法見上方註解。</summary>
public sealed record PhraseDeclaration(string Pattern)
{
    public string[]? After { get; init; }

    public string? Lead { get; init; }

    public string[]? AlsoLeads { get; init; }

    public int Template { get; init; }

    public int Expand { get; init; }

    public string[]? Values { get; init; }

    public bool? Closed { get; init; }

    public ObjectKinds Kinds { get; init; }

    public string[]? Endings { get; init; }

    public string? Group { get; init; }

    public string? Gap { get; init; }

    public string? Items { get; init; }

    public bool Clause { get; init; }

    public bool Classes { get; init; }

    public string[]? Lagging { get; init; }

    internal bool IsList => Pattern.EndsWith(" ,*", StringComparison.Ordinal);

    /// <summary>以尾巴比對的清單：中段的 ,* 之後是逗號（SET ,* ,），字照清單片語探。</summary>
    internal bool IsTailList => Pattern.EndsWith(" ,* ,", StringComparison.Ordinal);

    /// <summary>清單的標頭：清單片語與以尾巴比對的清單去掉清單那一段。</summary>
    internal string ListHead => Pattern.Substring(0, Pattern.Length - (IsTailList ? " ,* ," : " ,*").Length);

    internal bool IsOpenList => Pattern.EndsWith(" (*", StringComparison.Ordinal);

    /// <summary>沒有 Lead 的片語探測的位置；兩個都不寫就是語句開頭。</summary>
    internal string[] Positions => After ?? ["StatementStart"];

    /// <summary>
    /// 探測前一次驗完全部宣告：只看宣告本身就判得出的錯，不必等前面的片語探了好幾分鐘才中止。
    /// 要剖析器回答的（手寫值剖析不過、整段剖析不過）仍在探測時判。
    /// </summary>
    public static void Validate(IReadOnlyList<PhraseDeclaration> phrases, IReadOnlyDictionary<string, string[]> templates)
    {
        var problems = phrases.SelectMany(phrase => phrase.Problems(templates)).ToList();

        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"片語宣告有 {problems.Count} 處錯誤：\n" + string.Join("\n", problems));
        }
    }

    internal IEnumerable<string> Problems(IReadOnlyDictionary<string, string[]> templates)
    {
        var hasGap = Pattern.Contains("...");
        var items = Pattern.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        var midList = Array.IndexOf(items, ",*") is var list && list >= 0 && list != items.Length - 1;

        // ... 的寫法由執行期的 SqlClausePhrase 驗；探測只要有一段代入的文字。中段的 ,* 可以是零項，Gap 可寫可不寫。
        if (hasGap != !string.IsNullOrEmpty(Gap) && !(midList && !hasGap))
        {
            yield return $"片語「{Pattern}」的 ... 與 Gap 要一起寫：Gap 是探測時代入 ... 的文字。";
        }

        if (hasGap && (Expand != 0 || midList))
        {
            yield return $"片語「{Pattern}」有 ... 就不收 Expand 與中段的 ,*：展開出來的片語照樣要 Gap，逐條寫清楚。";
        }

        if (items.Count(item => item == ",*") > 1)
        {
            yield return $"片語「{Pattern}」的 ,* 只能有一個：前面那段是清單的標頭。";
        }

        if (Group != null && !items.Contains("()"))
        {
            yield return $"片語「{Pattern}」寫了 Group，卻沒有 () 可以代入。";
        }

        // Items 墊在 (* 之後，只在後面還有項時才用得到。
        if (Items != null && Array.IndexOf(items, "(*") is var open && (open < 0 || open == items.Length - 1))
        {
            yield return $"片語「{Pattern}」寫了 Items，卻沒有後面還有項的 (*。";
        }

        if (Clause && !IsOpenList)
        {
            yield return $"片語「{Pattern}」寫了 Clause，卻不是以 (* 結尾的括號清單。";
        }

        if (AlsoLeads != null && Lead == null)
        {
            yield return $"片語「{Pattern}」寫了 AlsoLeads，卻沒有 Lead：位置判得出來的由 After 分開。";
        }

        if (Lead != null)
        {
            if (After != null)
            {
                yield return $"片語「{Pattern}」的 Lead 與 After 只能寫一個。";
            }

            if (Pattern.Length == 0)
            {
                yield return "沒有尾巴的片語只能以 After 交代位置：執行期不看前一格的話，它哪裡都成立。";
            }

            if (Pattern.Contains(",*"))
            {
                yield return $"清單片語「{Pattern}」要以 After 交代位置：位置分析拿標頭認清單，得判得出標頭前一格。";
            }

            yield break;
        }

        foreach (var position in Positions)
        {
            if (!templates.TryGetValue(position, out var group))
            {
                yield return $"片語「{Pattern}」的 After 寫了不存在的位置 {position}。";
            }
            else if (Template < 0 || Template >= group.Length)
            {
                yield return $"片語「{Pattern}」的 Template 是 {Template}，位置 {position} 只有 {group.Length} 個樣板。";
            }
        }

        if ((IsList || IsTailList) && (Expand != 0 || Values is { Length: > 0 } || Closed != null))
        {
            yield return $"清單片語「{Pattern}」的字全由探測決定，不收 Expand、Values、Closed。";
        }

        if (Lagging is { Length: > 0 } && !IsList)
        {
            yield return $"片語「{Pattern}」寫了 Lagging，卻不是以 ,* 結尾的清單片語。";
        }
    }
}

/// <summary>第三階段的一個位置與它的樣板；名稱與 SqlKeywordPosition 的成員一致。</summary>
public sealed record PositionTemplate(string Position, params string[] Templates);
