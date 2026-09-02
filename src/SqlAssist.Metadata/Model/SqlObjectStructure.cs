using System;
using System.Collections.Generic;
using System.Text;
using SqlAssist.Metadata.Formatting;

namespace SqlAssist.Metadata.Model;

/// <summary>
/// 物件的完整結構：第二層的欄位與參數，加上只有結構面板才需要的索引與外來鍵。
/// </summary>
/// <remarks>
/// 索引與外來鍵刻意不放進 <see cref="SqlObjectDetail"/>：第二層在按鍵路徑上，
/// 使用者輸入 <c>a.</c> 要的是欄位清單，為此多付兩次查詢並不值得。
/// 這一層只有使用者主動打開結構面板時才載入，那時等得起。
/// </remarks>
public sealed class SqlObjectStructure
{
    private static readonly SqlIndexInfo[] NoIndexes = Array.Empty<SqlIndexInfo>();
    private static readonly SqlForeignKeyInfo[] NoForeignKeys = Array.Empty<SqlForeignKeyInfo>();

    public SqlObjectStructure(
        SqlObjectDetail detail,
        IReadOnlyList<SqlIndexInfo>? indexes = null,
        IReadOnlyList<SqlForeignKeyInfo>? foreignKeys = null)
    {
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        Indexes = indexes ?? NoIndexes;
        ForeignKeys = foreignKeys ?? NoForeignKeys;
    }

    public SqlObjectDetail Detail { get; }

    public SqlObjectInfo Object => Detail.Object;

    public IReadOnlyList<SqlColumnInfo> Columns => Detail.Columns;

    public IReadOnlyList<SqlParameterInfo> Parameters => Detail.Parameters;

    public string? Definition => Detail.Definition;

    public IReadOnlyList<SqlIndexInfo> Indexes { get; }

    public IReadOnlyList<SqlForeignKeyInfo> ForeignKeys { get; }

    /// <summary>主索引鍵；沒有時為 null。</summary>
    public SqlIndexInfo? PrimaryKey
    {
        get
        {
            foreach (var index in Indexes)
            {
                if (index.IsPrimaryKey)
                {
                    return index;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// 這一份結構夠不夠組出可以執行的指令碼。
    /// </summary>
    /// <remarks>
    /// 與種類層級的 <see cref="SqlObjectKinds.HasExecutableScript"/> 配成一對：
    /// 那一條回答「這一類寫得出來嗎」，這一條回答「這一次的資料夠嗎」。
    ///
    /// 查詢成功卻一列都沒有回來不是例外情形。物件清單是快取的，物件可能在那之後
    /// 被卸除；而中繼資料的可見度是照權限過濾的——權限被收回時 <c>sys.columns</c>
    /// 只是少幾列，不會報錯。查詢真的失敗（連不上、逾時、語法不合）在
    /// <c>SqlMetadataCatalog.TryLoad</c> 就降級成 <c>null</c>，根本走不到這裡。
    ///
    /// 兩道都過才組指令碼，任何一道不過就整段換成註解。半份指令碼是最糟的結果：
    /// 少了欄位的 <c>CREATE TABLE</c> 只剩一對空括號，卻仍然貼得上去——
    /// 與 <c>SELECT *</c> 不做部分展開是同一條理由。
    /// </remarks>
    public bool CanBuildExecutableScript => CheckAvailability() == ScriptAvailability.Ready;

    /// <summary>
    /// 組出可以直接執行的完整指令碼。
    /// </summary>
    /// <remarks>
    /// 模組類物件直接給定義本文——那本來就是可執行的原文，重組只會失真。
    /// 同義字與序列走同一支：它們的定義不在 <c>sys.sql_modules</c> 裡，
    /// 而是由 <see cref="SqlCatalogScript"/> 從目錄檢視組回 <c>CREATE</c>，
    /// 但到了這裡兩者沒有差別。
    /// 資料表則重建 CREATE TABLE，並把主索引鍵寫進條件約束，
    /// 其餘索引與外來鍵接在後面，順序與 SSMS 的指令碼一致。
    /// 資料表型別另有一支，見 <see cref="BuildCreateTypeScript"/>。
    ///
    /// 寫不出來的情形全部在 <see cref="CheckAvailability"/> 判掉，
    /// 沒有一種會掉進後面的組字串。
    /// </remarks>
    public string BuildScript()
    {
        switch (CheckAvailability())
        {
            // 寫不出 T-SQL 的種類（現在只剩認不出來的那些）只給一段給人看的摘要。
            // 那份文字貼在唯讀的預覽窗格裡沒有問題，要拿去執行的 F12 那一端會再
            // 把它整段註解掉。
            case ScriptAvailability.UnscriptableKind:
                return Detail.BuildPreview();

            // 檢視同時是模組也有欄位。定義取不到時原本會掉進 CREATE TABLE 那一支，
            // 於是一個檢視被寫成一張資料表——那不只是排版難看，是指令碼在說謊：
            // 照著執行會多出一張同名的資料表。
            //
            // 缺定義的原因分兩種說法，因為兩種物件的定義根本不從同一個地方來：
            // 說錯的話使用者會去查加密與 VIEW DEFINITION 權限，而同義字的定義
            // 從來不經過那兩關。
            case ScriptAvailability.MissingDefinition:
                return Object.Kind.HasSynthesizedDefinition()
                    ? BuildUnavailableScript(
                        "定義",
                        "sys.synonyms／sys.sequences 一列都沒有回來，而查詢本身沒有失敗——",
                        "原因只有兩個：物件在建議清單被快取之後卸除，",
                        "或是這個登入對它的權限在那之後被收回。")
                    : BuildUnavailableScript(
                        "定義",
                        "OBJECT_DEFINITION 傳回 NULL 的原因只有兩個：物件是 WITH ENCRYPTION 建立的，",
                        "或是目前的登入沒有它的 VIEW DEFINITION 權限。");

            // 一個欄位都沒有時組出來的是一對空括號，而那仍然是一段貼得上去的
            // CREATE TABLE：執行下去建出一張沒有欄位的資料表，比什麼都不做糟。
            case ScriptAvailability.MissingColumns:
                return BuildUnavailableScript(
                    "欄位",
                    "sys.columns 一列都沒有回來，而查詢本身沒有失敗——原因只有兩個：物件在",
                    "建議清單被快取之後卸除，或是這個登入對它的權限在那之後被收回。");
        }

        if (Object.Kind.ScriptsFromDefinition())
        {
            return Definition!;
        }

        return Object.Kind == SqlObjectKind.TableType
            ? BuildCreateTypeScript()
            : BuildCreateTableScript();
    }

    /// <remarks>
    /// 判斷只有這一份，<see cref="CanBuildExecutableScript"/> 與
    /// <see cref="BuildScript"/> 都問它。分成兩份的症狀是屬性說寫得出來、
    /// 組出來的卻是一段註解，而那種不一致沒有任何徵兆。
    /// </remarks>
    private ScriptAvailability CheckAvailability()
    {
        if (!Object.Kind.HasExecutableScript())
        {
            return ScriptAvailability.UnscriptableKind;
        }

        // 以定義為指令碼的那一族必須整個接走，不能只在「拿得到定義」時接：
        // 檢視同時是模組也有欄位，漏掉就會掉進資料表那一支。
        if (Object.Kind.ScriptsFromDefinition())
        {
            return string.IsNullOrWhiteSpace(Definition)
                ? ScriptAvailability.MissingDefinition
                : ScriptAvailability.Ready;
        }

        return Columns.Count == 0 ? ScriptAvailability.MissingColumns : ScriptAvailability.Ready;
    }

    /// <summary>指令碼寫不寫得出來，以及寫不出來時缺的是什麼。</summary>
    private enum ScriptAvailability
    {
        Ready,
        UnscriptableKind,
        MissingDefinition,
        MissingColumns
    }

    private string BuildCreateTableScript()
    {
        var builder = new StringBuilder();
        var name = Object.QualifiedName;
        builder.Append("CREATE TABLE ").Append(name).AppendLine();
        builder.AppendLine("(");
        AppendColumnDefinitions(builder);

        if (PrimaryKey is { } primaryKey)
        {
            builder.Append("    CONSTRAINT ").Append(SqlIdentifier.Quote(primaryKey.Name))
                .Append(" PRIMARY KEY ").Append(primaryKey.TypeDescription)
                .Append(" (").Append(primaryKey.BuildKeyColumnList()).AppendLine(")");
        }

        builder.AppendLine(");");

        foreach (var index in Indexes)
        {
            if (index.IsPrimaryKey)
            {
                continue;
            }

            builder.AppendLine();
            builder.AppendLine(index.ToScript(name));
        }

        foreach (var foreignKey in ForeignKeys)
        {
            builder.AppendLine();
            builder.AppendLine(foreignKey.ToScript(name));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 資料表型別的 <c>CREATE TYPE ... AS TABLE</c>。
    /// </summary>
    /// <remarks>
    /// 資料表型別有欄位，落到 CREATE TABLE 那一支就是指令碼在說謊：照著執行
    /// 會多出一張同名的資料表，與檢視取不到定義時不能掉進 CREATE TABLE 是
    /// 同一條理由。
    ///
    /// 主索引鍵寫成<b>不具名</b>的內嵌條件約束。<c>CREATE TYPE</c> 的括號裡
    /// 不收 <c>CONSTRAINT 名稱</c>——型別的條件約束一律命名不得，查到的那個名字
    /// 本來就是引擎自己配的——照資料表那一支搬過來會語法錯誤。
    ///
    /// 其餘索引整組不寫：<c>CREATE INDEX</c> 與 <c>ALTER TABLE</c> 對型別都不合法，
    /// 而括號裡的內嵌 <c>INDEX</c> 收不下 INCLUDE 與篩選條件，硬寫就是一段跑不動的
    /// 指令碼。省略的數量寫在結尾的註解裡，否則這份文字看起來就像那個型別
    /// 只有主索引鍵。外來鍵不必處理，型別上根本建不起來。
    /// </remarks>
    private string BuildCreateTypeScript()
    {
        var builder = new StringBuilder();
        builder.Append("CREATE TYPE ").Append(Object.QualifiedName).AppendLine(" AS TABLE");
        builder.AppendLine("(");
        AppendColumnDefinitions(builder);

        if (PrimaryKey is { } primaryKey)
        {
            builder.Append("    PRIMARY KEY ").Append(primaryKey.TypeDescription)
                .Append(" (").Append(primaryKey.BuildKeyColumnList()).AppendLine(")");
        }

        builder.AppendLine(");");

        var skipped = CountSecondaryIndexes();

        if (skipped > 0)
        {
            builder.AppendLine();
            builder.Append("-- 另有 ").Append(skipped)
                .AppendLine(" 個索引沒有寫進來：CREATE INDEX 與 ALTER TABLE 對型別都不合法，");
            builder.AppendLine("-- 而 CREATE TYPE 的括號裡只放得下不具名的條件約束。");
        }

        return builder.ToString();
    }

    private int CountSecondaryIndexes()
    {
        var count = 0;

        foreach (var index in Indexes)
        {
            if (!index.IsPrimaryKey)
            {
                count++;
            }
        }

        return count;
    }

    /// <remarks>
    /// 最後一個欄位後面要不要逗號，看的是它後面還有沒有主索引鍵那一行；
    /// CREATE TABLE 與 CREATE TYPE 兩支的規則相同。
    /// </remarks>
    private void AppendColumnDefinitions(StringBuilder builder)
    {
        for (var index = 0; index < Columns.Count; index++)
        {
            builder.Append("    ").Append(BuildColumnDefinition(Columns[index]));
            builder.AppendLine(index == Columns.Count - 1 && PrimaryKey is null ? string.Empty : ",");
        }
    }

    /// <summary>
    /// 資料不齊時的輸出：說明缺了什麼、為什麼，並把查得到的欄位與參數以註解列出來。
    /// </summary>
    /// <remarks>
    /// 整段都是註解，因為這裡沒有一行是可以執行的。猜一個 CREATE VIEW 的骨架
    /// 出來反而更糟——那是本擴充沒有讀到的東西，與 <c>SELECT *</c> 不做部分展開
    /// 是同一條理由。
    ///
    /// 原因一定要寫進輸出裡。只說「這個物件沒有指令碼」的話，使用者查不出該去看
    /// 權限、看物件還在不在，還是看連線；而查得到的欄位與參數也一起列出來，
    /// 那是這一輪唯一真的問到的東西。
    ///
    /// 缺定義與缺欄位共用這一份格式，新的一種缺法也照這裡加：兩份格式的症狀是
    /// 其中一份改了另一份沒改，而使用者看到的是兩種說法。
    /// </remarks>
    private string BuildUnavailableScript(string missing, params string[] reasons)
    {
        var builder = new StringBuilder();
        builder.Append("-- 取不到 ").Append(Object.QualifiedName)
            .Append(" 的").Append(missing).AppendLine("。");

        foreach (var reason in reasons)
        {
            builder.Append("-- ").AppendLine(reason);
        }

        if (Columns.Count > 0)
        {
            builder.AppendLine();
            builder.Append("-- ").Append(Object.Kind.ToDisplayName()).Append(" 的欄位（")
                .Append(Columns.Count).AppendLine(" 個）：");

            foreach (var column in Columns)
            {
                builder.Append("--     ").AppendLine(column.ToScriptLine());
            }
        }

        if (Parameters.Count > 0)
        {
            builder.AppendLine();
            builder.Append("-- 參數（").Append(Parameters.Count).AppendLine(" 個）：");

            foreach (var parameter in Parameters)
            {
                builder.Append("--     ").AppendLine(parameter.ToScriptLine());
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// CREATE TABLE 內的單行欄位定義。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="SqlColumnInfo.ToScriptLine"/> 的差別在於這裡要能執行：
    /// 不加 <c>-- PK</c> 註解（主索引鍵另外寫成條件約束），
    /// 計算欄位也不能寫型別，否則整段指令碼貼上去就會失敗。
    /// </remarks>
    private static string BuildColumnDefinition(SqlColumnInfo column)
    {
        var builder = new StringBuilder();
        builder.Append(SqlIdentifier.Quote(column.Name)).Append(' ');

        if (column.IsComputed)
        {
            builder.Append("AS ").Append(column.ComputedDefinition ?? "(/* 無法取得運算式 */)");
            return builder.ToString();
        }

        builder.Append(column.DataType);

        if (column.IsIdentity)
        {
            builder.Append(" IDENTITY");
        }

        builder.Append(column.IsNullable ? " NULL" : " NOT NULL");

        if (!string.IsNullOrEmpty(column.DefaultDefinition))
        {
            builder.Append(" DEFAULT ").Append(column.DefaultDefinition);
        }

        return builder.ToString();
    }
}
