using System;
using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 一個 SQL 名稱的完整位置：<c>伺服器.資料庫.結構描述.名稱</c>。
/// </summary>
/// <remarks>
/// 這個型別出現之前，名稱在四個地方各自被截成後兩段：敘述裡的資料來源、
/// <c>EXEC</c> 呼叫的模組、滑鼠停留處的識別字、建議清單的限定字。截掉的那幾段
/// 不是可有可無的修飾——<c>LibArchive.dbo.Loan</c> 被截成 <c>dbo.Loan</c> 之後，
/// 欄位建議、F12 與 <c>SELECT *</c> 展開會拿<b>目前連線裡同名的那一張表</b>來回答，
/// 而使用者完全看不出來。那比什麼都不做糟：什麼都不做至少是沉默。
///
/// 因此這裡是「一個名稱有幾段、哪一段是什麼」的唯一出處。右對齊、空的中間段、
/// 段數上限這三條規則各寫一份的話，分岔的症狀是同一個四段式名稱在建議清單裡
/// 認得、在 F12 那條路上不認得。
/// </remarks>
public sealed class SqlObjectPath
{
    /// <summary>完整名稱最多四段。</summary>
    public const int MaximumNameParts = 4;

    /// <summary>限定字最多三段：名稱的四段扣掉名稱自己。</summary>
    public const int MaximumQualifierParts = MaximumNameParts - 1;

    private SqlObjectPath(
        string? serverName,
        string? databaseName,
        string? schemaName,
        string name,
        int qualifierSlotCount,
        SqlQualifierSlot qualifierEnd)
    {
        ServerName = serverName;
        DatabaseName = databaseName;
        SchemaName = schemaName;
        Name = name;
        QualifierSlotCount = qualifierSlotCount;
        QualifierEnd = qualifierEnd;
    }

    /// <summary>連結伺服器名稱；沒寫時為 null。</summary>
    /// <remarks>
    /// 內容不保證是識別字的形狀——連結伺服器可以直接以位址命名
    /// （<c>[192.0.2.10]</c>），那時它只有加了方括號才寫得出來。
    /// 因此這裡一律當成不透明字串，任何「這看起來像不像名稱」的判斷都不成立。
    /// </remarks>
    public string? ServerName { get; }

    /// <summary>資料庫名稱；沒寫時為 null。</summary>
    public string? DatabaseName { get; }

    /// <summary>結構描述名稱；沒寫或寫成空的中間段（<c>db..obj</c>）時為 null。</summary>
    public string? SchemaName { get; }

    /// <summary>名稱本體；限定字路徑沒有名稱，為空字串。</summary>
    /// <remarks>
    /// 限定字（<c>LibArchive.dbo.</c>）與完整名稱（<c>LibArchive.dbo.Loan</c>）
    /// 共用這一個型別而不是各開一個，是因為兩者的規則完全相同：右對齊、
    /// 空的中間段當成沒寫、超過上限就是不合法。分成兩個型別要把這三條抄兩次，
    /// 而那正是這個型別要收掉的東西。差別只有「有沒有最後一段」，
    /// 由 <see cref="HasName"/> 回答。
    /// </remarks>
    public string Name { get; }

    /// <summary>限定字實際寫出來的段數，空的中間段也算一段。</summary>
    /// <remarks>
    /// 與「有幾格不是 null」不同：<c>LibArchive..</c> 是兩段，而結構描述那一格是
    /// null。<see cref="TryRealign"/> 要靠這個數字才知道最左邊那一段落在哪一格。
    /// </remarks>
    public int QualifierSlotCount { get; }

    /// <summary>限定字最右邊那一段是哪一格。</summary>
    /// <remarks>
    /// 剛解析出來一律是 <see cref="SqlQualifierSlot.Schema"/>，因為右對齊就是這樣定的。
    /// 中繼資料認出最左邊那一段其實是資料庫或連結伺服器之後，由
    /// <see cref="TryRealign"/> 整條往左挪，這裡跟著改。
    ///
    /// 它與「<see cref="SchemaName"/> 是不是 null」不能互相推導，而兩者的差別正是
    /// 插入文字要不要自己補上結構描述：<c>LibArchive..</c> 停在結構描述那一格
    /// （使用者已經用第二個點號說了「照預設解析」），補上去會寫出四段式的
    /// <c>LibArchive..[dbo].[Loan]</c>；<c>LibArchive.</c> 停在資料庫那一格，
    /// 不補則會寫出被讀成「結構描述.物件」的兩段式名稱，而那個結構描述並不存在。
    /// </remarks>
    public SqlQualifierSlot QualifierEnd { get; }

    /// <summary>限定字最左邊那一段；沒有限定字或那一段是空的時為 null。</summary>
    /// <remarks>
    /// 這是唯一需要被中繼資料認一次的字：往右的每一段都由它決定要怎麼讀。
    /// 拿最右邊那一段去認的話，<c>SQL209.GD_HOTAI.</c> 會問「有沒有一個資料庫
    /// 叫 GD_HOTAI」——答案在那台伺服器上，而不在目前這條連線上。
    /// </remarks>
    public string? LeftmostQualifier => HasName || QualifierSlotCount == 0
        ? null
        : SlotValue(LeftmostSlot);

    /// <summary>限定字最左邊那一段落在哪一格。</summary>
    /// <remarks>
    /// 這就是 <see cref="TryRealign"/> 的參數：中繼資料認出最左邊那一段是什麼之後
    /// 得到的答案，就記在這裡。提交時上下文是從文字重新分析的，不可能再認一次
    /// （認一次要送查詢，而提交在按鍵路徑上），因此建立清單時把這一格帶著走，
    /// 提交時照同一個方法挪回去——各挪各的話，症狀是清單列得出來、
    /// Tab 下去卻少一段。
    /// </remarks>
    public SqlQualifierSlot LeftmostSlot =>
        (SqlQualifierSlot)(QualifierSlotCount - 1 + (int)QualifierEnd);

    /// <summary>這是完整名稱（true）還是只有限定字（false）。</summary>
    public bool HasName => Name.Length > 0;

    /// <summary>要到別台伺服器才查得到。</summary>
    public bool IsCrossServer => ServerName is not null;

    /// <summary>要到同一台伺服器的別的資料庫才查得到。</summary>
    public bool IsCrossDatabase => DatabaseName is not null;

    /// <summary>目前這條連線就查得到。</summary>
    public bool IsLocal => ServerName is null && DatabaseName is null;

    /// <summary>
    /// 讀出一個完整名稱，最多四段。
    /// </summary>
    /// <remarks>
    /// 右對齊是 T-SQL 自己的規則：省略一律從左邊省，所以最後一段永遠是名稱，
    /// 往左依序是結構描述、資料庫、伺服器。兩段式的 <c>dbo.Loan</c> 不會被誤讀成
    /// 資料庫加名稱，靠的就是這一條。
    ///
    /// 超過四段回傳 false 而不是取後四段：那不是一個寫錯了還救得回來的名稱，
    /// 猜一個出來只會讓下游拿去查一個使用者沒有指名的東西。
    /// </remarks>
    public static bool TryParseName(IReadOnlyList<string>? parts, out SqlObjectPath? path)
    {
        path = null;

        if (parts is null || parts.Count == 0 || parts.Count > MaximumNameParts)
        {
            return false;
        }

        var name = parts[parts.Count - 1];

        // 空的最後一段是 db.dbo. 這種還沒打完的名稱，那是限定字不是名稱。
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        path = Build(parts, parts.Count - 1, name);
        return true;
    }

    /// <summary>
    /// 讀出一個限定字（點號前方的那幾段），最多三段。
    /// </summary>
    /// <remarks>
    /// 限定字同樣右對齊，只是右端是結構描述而不是名稱：一段是結構描述或別名，
    /// 兩段是資料庫加結構描述，三段是伺服器加資料庫加結構描述。
    ///
    /// 一段的那個情形無法只靠文字分辨結構描述與別名——<c>dbo.</c> 與 <c>u.</c>
    /// 長得一樣，要知道敘述看得到哪些資料來源才分得出來。這裡不做那個判斷，
    /// 一律先放進 <see cref="SchemaName"/>，由帶語句範圍的那一層改寫。
    /// </remarks>
    public static bool TryParseQualifier(IReadOnlyList<string>? parts, out SqlObjectPath? path)
    {
        path = null;

        if (parts is null || parts.Count == 0 || parts.Count > MaximumQualifierParts)
        {
            return false;
        }

        // 全部都是空段（. 或 ..）不是限定字，那只是一串點號。
        var hasContent = false;

        foreach (var part in parts)
        {
            if (!string.IsNullOrEmpty(part))
            {
                hasContent = true;
                break;
            }
        }

        if (!hasContent)
        {
            return false;
        }

        path = Build(parts, parts.Count, string.Empty);
        return true;
    }

    /// <summary>
    /// 把整條限定字往左挪，讓最左邊那一段落在 <paramref name="leftmost"/> 這一格。
    /// </summary>
    /// <remarks>
    /// 右對齊是唯一只看文字就做得出的假設，但它對 <c>LibArchive.</c> 與 <c>SQL209.</c>
    /// 都會猜成結構描述。要分辨得知道這台伺服器上有哪些資料庫、掛了哪些連結伺服器，
    /// 那是中繼資料的事；中繼資料只回答最左邊那一段是什麼，段位怎麼挪算在這裡。
    /// 兩邊各算一份的話，症狀是清單列得出來、插入文字卻少了一段。
    ///
    /// 只吃剛解析出來的限定字（<see cref="QualifierEnd"/> 還是
    /// <see cref="SqlQualifierSlot.Schema"/>），挪過的不再挪第二次——重複套用會把
    /// 已經正確的路徑推出上限。挪不動時回傳 false 並維持原樣，呼叫端拿到的
    /// 就是原本那個右對齊的解讀。
    /// </remarks>
    public bool TryRealign(SqlQualifierSlot leftmost, out SqlObjectPath realigned)
    {
        realigned = this;

        if (HasName || QualifierEnd != SqlQualifierSlot.Schema)
        {
            return false;
        }

        var steps = (int)leftmost - (QualifierSlotCount - 1);

        if (steps == 0)
        {
            return true;
        }

        if (steps < 0 || QualifierSlotCount + steps > MaximumQualifierParts)
        {
            return false;
        }

        realigned = new SqlObjectPath(
            SlotValue((SqlQualifierSlot)((int)SqlQualifierSlot.Server - steps)),
            SlotValue((SqlQualifierSlot)((int)SqlQualifierSlot.Database - steps)),
            SlotValue((SqlQualifierSlot)((int)SqlQualifierSlot.Schema - steps)),
            Name,
            QualifierSlotCount,
            (SqlQualifierSlot)steps);

        return true;
    }

    /// <summary>
    /// 把最右邊 <paramref name="slotCount"/> 段對到結構描述、資料庫、伺服器。
    /// </summary>
    /// <remarks>
    /// 空的中間段一律存成 null，因為 <c>LibArchive..Loan</c> 的意思是
    /// 「這個資料庫，結構描述照預設解析」，不是「結構描述叫做空字串」。
    /// 存成空字串的話下游會拿它去比對，而沒有任何結構描述叫做空字串，
    /// 症狀是這個寫法永遠一筆都比不中。
    /// </remarks>
    private static SqlObjectPath Build(IReadOnlyList<string> parts, int slotCount, string name)
    {
        var schema = SlotAt(parts, slotCount - 1);
        var database = SlotAt(parts, slotCount - 2);
        var server = SlotAt(parts, slotCount - 3);
        return new SqlObjectPath(server, database, schema, name, slotCount, SqlQualifierSlot.Schema);
    }

    private static string? SlotAt(IReadOnlyList<string> parts, int index)
    {
        if (index < 0 || index >= parts.Count)
        {
            return null;
        }

        var value = parts[index];
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>某一格現在放的是什麼；超出範圍的格子當成沒寫。</summary>
    private string? SlotValue(SqlQualifierSlot slot)
    {
        return slot switch
        {
            SqlQualifierSlot.Schema => SchemaName,
            SqlQualifierSlot.Database => DatabaseName,
            SqlQualifierSlot.Server => ServerName,
            _ => null
        };
    }

    /// <summary>兩個路徑指的是不是同一台伺服器的同一個資料庫。</summary>
    /// <remarks>
    /// 只比位置、不比名稱：問這個問題的是「這兩筆要不要走同一份中繼資料」，
    /// 而那與物件叫什麼無關。null 與 null 相等，代表兩者都是目前這條連線。
    /// </remarks>
    public bool HasSameSource(SqlObjectPath? other)
    {
        return other is not null &&
               string.Equals(ServerName, other.ServerName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(DatabaseName, other.DatabaseName, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString()
    {
        var builder = new StringBuilder();

        // 中間段省略時點號不能跟著省：伺服器加名稱要寫成 srv...Loan，
        // 少一個點就變成另一個名稱。反過來，限定字停在哪一格就只寫到哪一格
        // ——挪到伺服器那一格的 SQL209. 補滿點號會變成 SQL209...，
        // 那是「伺服器加預設資料庫加預設結構描述」，不是使用者打的東西。
        var outermost = ServerName is not null
            ? SqlQualifierSlot.Server
            : DatabaseName is not null
                ? SqlQualifierSlot.Database
                : SchemaName is not null
                    ? SqlQualifierSlot.Schema
                    : (SqlQualifierSlot?)null;

        if (outermost is { } start)
        {
            var last = HasName ? SqlQualifierSlot.Schema : QualifierEnd;

            for (var slot = (int)start; slot >= (int)last; slot--)
            {
                builder.Append(SlotValue((SqlQualifierSlot)slot)).Append('.');
            }
        }

        builder.Append(Name);
        return builder.ToString();
    }
}
