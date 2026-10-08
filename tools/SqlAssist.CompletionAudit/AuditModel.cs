using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;

namespace SqlAssist.CompletionAudit;

/// <summary>語料的一段：一份完整的指令碼，連同它來自哪裡、該配哪個資料庫的中繼資料。</summary>
public sealed class AuditFragment
{
    /// <param name="source">語料來源的名稱（<c>recall</c>、<c>memory</c>、<c>modules</c>…）。</param>
    /// <param name="id">在來源裡穩定的識別；快取與重驗用它找回同一段。</param>
    /// <param name="text">指令碼全文。</param>
    /// <param name="database">要配的資料庫；沒有連線或不知道時為 null。</param>
    /// <param name="wordsInUpperCase">
    /// 這份語料守「字寫大寫、名稱大小寫混合」的慣例，全大寫的詞一律當字稽核（見 <see cref="AuditWords.IsWrittenAsWord"/>）。
    /// </param>
    public AuditFragment(string source, string id, string text, string? database = null, bool wordsInUpperCase = false)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Database = database;
        WordsInUpperCase = wordsInUpperCase;
    }

    public string Source { get; }

    public string Id { get; }

    public string Text { get; }

    public string? Database { get; }

    public bool WordsInUpperCase { get; }
}

/// <summary>一種語料：召回語料、SQL Memory 歷史、資料庫模組定義、微軟公開語料。</summary>
public interface IAuditCorpusSource
{
    /// <summary>來源名稱，也是 <c>-Sources</c> 用的名字。</summary>
    string Name { get; }

    /// <summary>逐段讀出；讀不到（檔案不在、沒有連線）就一段都不給，理由由來源自己回報。</summary>
    IEnumerable<AuditFragment> Read(CancellationToken cancellationToken);
}

/// <summary>作者寫的那個詞是哪一種；名稱要知道它存在才稽核。</summary>
public enum AuditTokenClass
{
    /// <summary>關鍵字、片語的字、資料型別、提示、日期部分與內建函式。</summary>
    Word,

    /// <summary><c>@@ROWCOUNT</c> 這一類。</summary>
    GlobalVariable,

    /// <summary>這份指令碼前面取過的名稱：別名、CTE、變數、暫存資料表、資料指標。</summary>
    ScriptName,

    /// <summary>資料庫裡的物件。</summary>
    Object,

    /// <summary>資料庫裡的結構描述。</summary>
    Schema,

    /// <summary>伺服器上的資料庫。</summary>
    Database,

    /// <summary>敘述裡某個資料表的欄位。</summary>
    Column,
}

/// <summary>本來就不該列的詞元，分類排除、不算漏。</summary>
public enum AuditExclusion
{
    /// <summary>
    /// 字串與數字常值、緊貼數值的字（<c>20MB</c> 的 MB），以及程序參數預設值與 EXEC 引數寫成識別字的值
    /// （<c>= false</c>、<c>, true</c>）。
    /// </summary>
    Literal,

    /// <summary>新取的名稱：別名定義、CREATE 目標、宣告。</summary>
    NewName,

    /// <summary>
    /// 名稱但查不到它存在（沒有連線、別的資料庫的物件、使用者打錯）；點號之後的名稱，限定字或別名指的資料表
    /// 查不到也算——只看名字的話，別的資料庫的表碰巧有同名欄位就被當成漏。
    /// </summary>
    Unresolved,

    /// <summary>
    /// 要靠游標之後才寫的部分才認得出來：選取清單寫在 FROM 之前，或限定字是之後才取的別名。
    /// 截斷的地方還沒有資料來源，產品與 SSMS 都列不出來。
    /// </summary>
    Truncated,

    /// <summary>
    /// 範例的佔位符（<c>&lt;login_name&gt;</c>、範本的 <c>&lt;Author,,Name&gt;</c>）起到那一句結束：不是 T-SQL，
    /// 產品把 <c>&lt;</c> 讀成比較運算子，之後的位置都不算數。日期部分那一格寫的不是日期部分
    /// （<c>DATENAME(datepart, …)</c>）也是佔位符，只排除那一個詞。
    /// </summary>
    Placeholder,

    /// <summary>
    /// 剖析不過的那一句，從剖析器停下的那個詞起到那一句結束（<see cref="AuditDefinitions.IsUnparsed"/>）：
    /// 不是 T-SQL（文件裡的語法片段、打錯的指令碼），產品判斷不了那裡要什麼，硬列就是猜。
    /// 錯之前不是保留字的字也算：不守大寫慣例的語料靠語法樹分字與名稱，那一句不在樹上。
    /// </summary>
    Unparsed,
}

/// <summary>漏的樣子。</summary>
public enum AuditMissKind
{
    /// <summary>這一格清單根本不開（被判成新名字或不可補）。</summary>
    Closed,

    /// <summary>清單開了，候選裡沒有這個詞。</summary>
    Absent,

    /// <summary>候選裡有，但打了第一個字元之後看不到（被篩掉或排在上限之外）。</summary>
    Hidden,
}

/// <summary>名稱是否存在：資料庫的物件、結構描述、資料庫與敘述裡資料表的欄位。</summary>
public interface IAuditNameIndex
{
    /// <summary>這個名稱是哪一種；不認得回傳 null。</summary>
    AuditTokenClass? Find(string name);
}

/// <summary>稽核一段語料時的資料庫：建議清單問的那一份，加上「名稱存不存在」。</summary>
public interface IAuditCatalog
{
    ISqlCompletionMetadata Metadata { get; }

    /// <summary>這個批次用得到的名稱索引；欄位只載敘述裡寫到的資料表。</summary>
    Task<IAuditNameIndex> IndexAsync(string batch, IReadOnlyList<SqlToken> tokens, CancellationToken cancellationToken);
}

/// <summary>現成的 <see cref="IAuditCatalog"/>。</summary>
public static class AuditCatalog
{
    /// <summary>沒有資料庫：只剩字與指令碼自己取的名稱可以稽核。</summary>
    public static IAuditCatalog None { get; } = new NoCatalog();

    private sealed class NoCatalog : IAuditCatalog, IAuditNameIndex
    {
        public ISqlCompletionMetadata Metadata => SqlCompletionMetadata.None;

        public Task<IAuditNameIndex> IndexAsync(string batch, IReadOnlyList<SqlToken> tokens, CancellationToken cancellationToken) =>
            Task.FromResult<IAuditNameIndex>(this);

        public AuditTokenClass? Find(string name) => null;
    }
}
