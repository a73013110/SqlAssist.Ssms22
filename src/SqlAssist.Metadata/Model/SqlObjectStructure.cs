using System;
using System.Collections.Generic;
using System.Text;
using SqlAssist.Core.Parsing;
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
    private static readonly SqlExtendedProperty[] NoExtendedProperties = Array.Empty<SqlExtendedProperty>();
    private static readonly SqlCheckConstraint[] NoCheckConstraints = Array.Empty<SqlCheckConstraint>();

    public SqlObjectStructure(
        SqlObjectDetail detail,
        IReadOnlyList<SqlIndexInfo>? indexes = null,
        IReadOnlyList<SqlForeignKeyInfo>? foreignKeys = null,
        IReadOnlyList<SqlExtendedProperty>? extendedProperties = null,
        IReadOnlyList<SqlCheckConstraint>? checkConstraints = null,
        SqlTableStorage? storage = null)
    {
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        Indexes = indexes ?? NoIndexes;
        ForeignKeys = foreignKeys ?? NoForeignKeys;
        ExtendedProperties = extendedProperties ?? NoExtendedProperties;
        CheckConstraints = checkConstraints ?? NoCheckConstraints;
        Storage = storage ?? SqlTableStorage.None;
    }

    public SqlObjectDetail Detail { get; }

    public SqlObjectInfo Object => Detail.Object;

    public IReadOnlyList<SqlColumnInfo> Columns => Detail.Columns;

    public IReadOnlyList<SqlParameterInfo> Parameters => Detail.Parameters;

    public string? Definition => Detail.Definition;

    public IReadOnlyList<SqlIndexInfo> Indexes { get; }

    public IReadOnlyList<SqlForeignKeyInfo> ForeignKeys { get; }

    /// <summary>資料表、資料行、索引與條件約束上的擴充屬性。</summary>
    /// <remarks>
    /// 與索引、外來鍵同屬第四層：只有使用者主動打開結構或要一份指令碼時才載入。
    /// 併進按鍵路徑上的第二層等於讓每一次輸入 <c>a.</c> 多付一次查詢，
    /// 而建議清單一個字都用不到它。
    /// </remarks>
    public IReadOnlyList<SqlExtendedProperty> ExtendedProperties { get; }

    /// <summary>資料表上的 CHECK 條件約束。</summary>
    public IReadOnlyList<SqlCheckConstraint> CheckConstraints { get; }

    /// <summary>資料表本身的儲存位置與建立當時的 SET 選項；查不到時每個欄位都是「沒有」。</summary>
    public SqlTableStorage Storage { get; }

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
    /// 排版整個交給 <see cref="TSqlScriptRenderer"/>：全擴充只有那一份組
    /// <c>CREATE TABLE</c> 的地方，同一張資料表在預覽裡與按下 F12 之後才會一樣。
    /// 留在這裡的是「這一次的資料夠不夠」與「不夠時要說什麼」，那兩件事只有
    /// 這個型別答得出來。
    /// </remarks>
    public string BuildScript(SqlScriptContext context) =>
        TSqlScriptRenderer.Default.Render(this, context);

    /// <summary>
    /// 資料不齊時的輸出；齊全時回傳 <c>false</c> 且不產生任何文字。
    /// </summary>
    /// <remarks>
    /// 由 <see cref="TSqlScriptRenderer"/> 在組任何東西之前先問。半份指令碼是最糟的
    /// 結果：少了欄位的 <c>CREATE TABLE</c> 只剩一對空括號，卻仍然貼得上去。
    /// </remarks>
    internal bool TryBuildUnavailableScript(out string script)
    {
        switch (CheckAvailability())
        {
            // 寫不出 T-SQL 的種類（現在只剩認不出來的那些）只給一段給人看的摘要。
            // 那份文字貼在唯讀的預覽窗格裡沒有問題，要拿去執行的 F12 那一端會再
            // 把它整段註解掉。
            case ScriptAvailability.UnscriptableKind:
                script = Detail.BuildPreview();
                return true;

            // 指令碼宣告的東西不經過 OBJECT_DEFINITION，也不經過目錄檢視，
            // 底下那兩種說法對它都是錯的——使用者會去查加密與 VIEW DEFINITION
            // 權限，而它從來不經過那兩關。
            case ScriptAvailability.MissingDefinition when Object.Kind.IsScriptDeclared():
                script = BuildUnavailableScript(
                    "宣告原文",
                    "這個名稱是這份指令碼自己宣告的，宣告的位置卻已經不在目前的文字裡了——",
                    "多半是提示顯示之後、開啟結構之前，那幾行被改掉或刪掉了。");
                return true;

            // 檢視同時是模組也有欄位。定義取不到時原本會掉進 CREATE TABLE 那一支，
            // 於是一個檢視被寫成一張資料表——那不只是排版難看，是指令碼在說謊：
            // 照著執行會多出一張同名的資料表。
            //
            // 缺定義的原因分兩種說法，因為兩種物件的定義根本不從同一個地方來：
            // 說錯的話使用者會去查加密與 VIEW DEFINITION 權限，而同義字的定義
            // 從來不經過那兩關。
            case ScriptAvailability.MissingDefinition:
                script = Object.Kind.HasSynthesizedDefinition()
                    ? BuildUnavailableScript(
                        "定義",
                        "sys.synonyms／sys.sequences 一列都沒有回來，而查詢本身沒有失敗——",
                        "原因只有兩個：物件在建議清單被快取之後卸除，",
                        "或是這個登入對它的權限在那之後被收回。")
                    : BuildUnavailableScript(
                        "定義",
                        "OBJECT_DEFINITION 傳回 NULL 的原因只有兩個：物件是 WITH ENCRYPTION 建立的，",
                        "或是目前的登入沒有它的 VIEW DEFINITION 權限。");
                return true;

            // 一個欄位都沒有時組出來的是一對空括號，而那仍然是一段貼得上去的
            // CREATE TABLE：執行下去建出一張沒有欄位的資料表，比什麼都不做糟。
            case ScriptAvailability.MissingColumns:
                script = BuildUnavailableScript(
                    "欄位",
                    "sys.columns 一列都沒有回來，而查詢本身沒有失敗——原因只有兩個：物件在",
                    "建議清單被快取之後卸除，或是這個登入對它的權限在那之後被收回。");
                return true;
        }

        script = string.Empty;
        return false;
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
}
