namespace SqlAssist.KeywordGenerator.Data;

/// <summary>第一、二階段：剖析器問不出來、只能條列的字，以及判保留字的樣板。</summary>
internal static class KeywordSupplements
{
    // 非保留字的補充清單。
    //
    // 這是關鍵字這一側唯一「條列」出來的東西（片語那一側另有 ScopedConfigurations 與各片語的 Values），
    // 而且是不得已的：非保留字在文法上本來就不是關鍵字，THROW 與 APPLY 對詞法器來說跟 Lib_Reader 沒有兩樣，因此 ScriptDom 的
    // TSqlTokenType 沒有它們、SqlParser 的 Scanner 也一律回報識別字。任何工具在這一塊
    // 都只能自己維護清單。
    //
    // 內容刻意等於「舊的手寫清單裡有、但 ScriptDom 認不得」的那些字——換掉手寫清單
    // 不能是退步。要新增非保留字就加在這裡，位置一樣由第三階段的探測自動決定。
    internal static readonly string[] NonReserved =
    [
        "APPLY", "CATCH", "NEXT", "NOLOCK", "OFFSET", "OUTPUT",
        "PARTITION", "ROWS", "THROW", "TRY", "USING", "WINDOW",
    ];

    // 插入識別字時要不要加方括號，問的是「這個字當名字寫，剖析器吃不吃」，
    // 跟位置分類是兩回事：OUTPUT 在文法上是關鍵字，但 SELECT Output FROM t
    // 完全合法；反過來 ORDER 當欄位名寫就是語法錯誤。所以另外探測一次。
    // 排在定位置之前，因為定位置要知道哪些字可以被當成名字吃下去。
    //
    // 洞在樣板的中間而不是結尾，因此這裡是前後綴成對。
    internal static readonly (string Prefix, string Suffix)[] IdentifierTemplates =
    [
        ("SELECT ", " FROM t"),
        ("SELECT * FROM ", ""),
        ("SELECT * FROM ", ".t"),
        ("SELECT t.", " FROM t"),
        ("CREATE TABLE t (", " int)"),
    ];

    // 保留字的補充清單，跟 NonReserved 是同一個問題的另一面：
    // IDENTITYCOL 與 ROWGUIDCOL 不在 TSqlTokenType 裡（詞法器把它們掃成識別字），
    // 但剖析器不接受它們當名字，不加括號插進去就壞掉。它們不進關鍵字清單——
    // 建議清單與自動大寫不該因為這個修正而多出兩個字——只影響括號判定。
    //
    // 第二階段會回驗這份清單：真的不需要括號就會警告，不會變成死條目。
    internal static readonly string[] Reserved = ["IDENTITYCOL", "ROWGUIDCOL"];
}
