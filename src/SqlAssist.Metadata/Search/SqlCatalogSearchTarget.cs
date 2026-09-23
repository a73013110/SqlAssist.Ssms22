using System;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 一筆目錄搜尋結果指向的東西；點下去要導航到哪裡由它決定。
/// </summary>
/// <remarks>
/// 掛在 <c>SearchHit.ActivatePayload</c> 上。Core 不解讀它，Ssms22 之後拿它去開
/// 結構預覽或產生指令碼——中間這一層因此必須自己說得完整。
///
/// <see cref="DatabaseName"/> 是必填的，理由與 <see cref="SqlObjectInfo.DatabaseName"/>
/// 完全相同：<see cref="ObjectId"/> <b>只在它自己那個資料庫裡唯一</b>，拿著它去問
/// 另一份目錄會拿到剛好同號的另一個物件的欄位，而那份欄位清單看起來完全正常。
/// 下游一定要先換到這個資料庫的目錄，再用編號。
///
/// <see cref="Origin"/> 同樣必填，而且比資料庫更要緊：跨伺服器的同一個編號毫無關係，
/// 而搜尋範圍換過之後，清單上的舊列仍然指向上一台。理由見 <see cref="SqlSearchOrigin"/>。
///
/// 不可變：同一筆結果會同時被清單、預覽與導航讀到，其中任何一邊改到它，
/// 另外兩邊指向的就不是使用者點的那一個。
/// </remarks>
public sealed class SqlCatalogSearchTarget : ISqlSearchTarget
{
    /// <param name="columnName">資料行命中時的資料行名稱；物件命中時為 null。</param>
    public SqlCatalogSearchTarget(
        SqlSearchOrigin origin,
        string databaseName,
        string schemaName,
        string name,
        SqlObjectKind kind,
        int objectId,
        string? columnName = null)
    {
        if (string.IsNullOrEmpty(databaseName))
        {
            throw new ArgumentException("資料庫名稱不可為空。", nameof(databaseName));
        }

        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("物件名稱不可為空。", nameof(name));
        }

        Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        DatabaseName = databaseName;
        SchemaName = schemaName ?? throw new ArgumentNullException(nameof(schemaName));
        Name = name;
        Kind = kind;
        ObjectId = objectId;
        ColumnName = columnName;
    }

    /// <summary>這一筆是在哪一台伺服器上搜到的。</summary>
    public SqlSearchOrigin Origin { get; }

    /// <summary>這個物件所屬的資料庫；換目錄用。</summary>
    public string DatabaseName { get; }

    public string SchemaName { get; }

    public string Name { get; }

    public SqlObjectKind Kind { get; }

    /// <summary><c>object_id</c>；只在 <see cref="DatabaseName"/> 那個資料庫裡唯一。</summary>
    public int ObjectId { get; }

    /// <summary>資料行命中時的資料行名稱；物件命中時為 null。</summary>
    /// <remarks>
    /// 與物件命中共用同一個型別而不是另開一個：導航的目標都是那個物件，
    /// 資料行只是多說一句「捲到這一行」。分成兩個型別的話，接收端要先問是哪一種，
    /// 而忘記問的那一條路會把資料行命中導到物件的第一行。
    /// </remarks>
    public string? ColumnName { get; }

    public override string ToString() =>
        ColumnName is null
            ? $"{DatabaseName}.{SchemaName}.{Name}"
            : $"{DatabaseName}.{SchemaName}.{Name}.{ColumnName}";
}
