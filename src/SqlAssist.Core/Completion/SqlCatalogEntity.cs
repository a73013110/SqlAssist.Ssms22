using System;
using System.Collections.Generic;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

/// <summary>名稱住在哪一層：整台伺服器（登入、資料庫），或目前的資料庫（使用者、結構描述）。</summary>
public enum SqlCatalogScope
{
    Server,
    Database
}

/// <summary>
/// 一種只有目錄檢視列得出名稱的東西：登入、使用者、角色、結構描述、憑證、金鑰…。
/// </summary>
/// <remarks>
/// 它們與資料表不同，不在第一層的物件清單裡；名稱只有 <c>sys.server_principals</c>、
/// <c>sys.database_principals</c> 這些檢視知道。這裡只說「有哪幾種、各住在哪一層、叫什麼」，
/// 哪一格要哪一種由 <see cref="SqlCatalogEntityPosition"/> 推，名單怎麼查在中繼資料層。
///
/// 種類的字就是產生器探到的建立種類（CreatedKinds）：位置由「前面寫的是哪一種」推出，
/// 字對不上的話那一種永遠比對不到，測試逐一核對。名冊沒有的種類（資料表、程序）由第一層的物件清單給。
/// </remarks>
public sealed class SqlCatalogEntity
{
    public static readonly SqlCatalogEntity Login = new("LOGIN", SqlCatalogScope.Server, () => SqlKindText.Login);
    public static readonly SqlCatalogEntity ServerRole = new("SERVER ROLE", SqlCatalogScope.Server, () => SqlKindText.ServerRole);
    public static readonly SqlCatalogEntity Credential = new("CREDENTIAL", SqlCatalogScope.Server, () => SqlKindText.Credential);
    public static readonly SqlCatalogEntity Endpoint = new("ENDPOINT", SqlCatalogScope.Server, () => SqlKindText.Endpoint);
    public static readonly SqlCatalogEntity EventSession = new("EVENT SESSION", SqlCatalogScope.Server, () => SqlKindText.EventSession);
    public static readonly SqlCatalogEntity ServerAudit = new("SERVER AUDIT", SqlCatalogScope.Server, () => SqlKindText.ServerAudit);

    public static readonly SqlCatalogEntity ServerAuditSpecification =
        new("SERVER AUDIT SPECIFICATION", SqlCatalogScope.Server, () => SqlKindText.ServerAuditSpecification);

    /// <summary>資料庫住在伺服器那一層；它的擁有者是登入，權限卻授給它自己的使用者，見 <see cref="GranteeScope"/>。</summary>
    public static readonly SqlCatalogEntity Database = new("DATABASE", SqlCatalogScope.Server, () => SqlKindText.Database);

    public static readonly SqlCatalogEntity User = new("USER", SqlCatalogScope.Database, () => SqlKindText.User);
    public static readonly SqlCatalogEntity Role = new("ROLE", SqlCatalogScope.Database, () => SqlKindText.Role);

    public static readonly SqlCatalogEntity ApplicationRole =
        new("APPLICATION ROLE", SqlCatalogScope.Database, () => SqlKindText.ApplicationRole);

    public static readonly SqlCatalogEntity Schema = new("SCHEMA", SqlCatalogScope.Database, () => SqlKindText.Schema);
    public static readonly SqlCatalogEntity Certificate = new("CERTIFICATE", SqlCatalogScope.Database, () => SqlKindText.Certificate);
    public static readonly SqlCatalogEntity AsymmetricKey = new("ASYMMETRIC KEY", SqlCatalogScope.Database, () => SqlKindText.AsymmetricKey);
    public static readonly SqlCatalogEntity SymmetricKey = new("SYMMETRIC KEY", SqlCatalogScope.Database, () => SqlKindText.SymmetricKey);

    public static readonly SqlCatalogEntity DatabaseScopedCredential =
        new("DATABASE SCOPED CREDENTIAL", SqlCatalogScope.Database, () => SqlKindText.DatabaseScopedCredential);

    public static readonly SqlCatalogEntity DatabaseAuditSpecification =
        new("DATABASE AUDIT SPECIFICATION", SqlCatalogScope.Database, () => SqlKindText.DatabaseAuditSpecification);

    public static readonly SqlCatalogEntity PartitionFunction =
        new("PARTITION FUNCTION", SqlCatalogScope.Database, () => SqlKindText.PartitionFunction);

    public static readonly SqlCatalogEntity PartitionScheme =
        new("PARTITION SCHEME", SqlCatalogScope.Database, () => SqlKindText.PartitionScheme);

    public static readonly SqlCatalogEntity FulltextCatalog =
        new("FULLTEXT CATALOG", SqlCatalogScope.Database, () => SqlKindText.FulltextCatalog);

    public static readonly SqlCatalogEntity Assembly = new("ASSEMBLY", SqlCatalogScope.Database, () => SqlKindText.Assembly);

    public static readonly SqlCatalogEntity ExternalDataSource =
        new("EXTERNAL DATA SOURCE", SqlCatalogScope.Database, () => SqlKindText.ExternalDataSource);

    public static readonly SqlCatalogEntity ExternalFileFormat =
        new("EXTERNAL FILE FORMAT", SqlCatalogScope.Database, () => SqlKindText.ExternalFileFormat);

    /// <summary>全部的種類；中繼資料層以此核對每一種都有查詢。</summary>
    public static IReadOnlyList<SqlCatalogEntity> All { get; } = new[]
    {
        Login, ServerRole, Credential, Endpoint, EventSession, ServerAudit, ServerAuditSpecification, Database,
        User, Role, ApplicationRole, Schema, Certificate, AsymmetricKey, SymmetricKey, DatabaseScopedCredential,
        DatabaseAuditSpecification, PartitionFunction, PartitionScheme, FulltextCatalog, Assembly,
        ExternalDataSource, ExternalFileFormat
    };

    private static readonly IReadOnlyList<SqlCatalogEntity> ServerPrincipals = new[] { Login, ServerRole };
    private static readonly IReadOnlyList<SqlCatalogEntity> DatabasePrincipals = new[] { User, Role };
    private static readonly IReadOnlyList<SqlCatalogEntity> AllPrincipals = new[] { User, Role, Login, ServerRole };

    private static readonly Dictionary<string, SqlCatalogEntity> ByKind = BuildIndex();

    private readonly Func<string> _kindText;

    private SqlCatalogEntity(string kind, SqlCatalogScope scope, Func<string> kindText)
    {
        Kind = kind;
        Scope = scope;
        _kindText = kindText;
    }

    /// <summary>種類的字，與 CREATE 之後寫的一樣（<c>SERVER ROLE</c>）。</summary>
    public string Kind { get; }

    /// <summary>名稱住在哪一層；擁有者（<c>AUTHORIZATION</c>、<c>ALTER AUTHORIZATION … TO</c>）取這一層的主體。</summary>
    public SqlCatalogScope Scope { get; }

    /// <summary>
    /// 這一種的權限授給哪一層的主體（<c>GRANT … ON 類別:: … TO</c>）。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="Scope"/> 只差資料庫一種：資料庫本身住在伺服器上、擁有者是登入，
    /// 權限卻授給那個資料庫裡的使用者（<c>GRANT CONNECT ON DATABASE::d TO u</c>）。
    /// </remarks>
    public SqlCatalogScope GranteeScope => this == Database ? SqlCatalogScope.Database : Scope;

    /// <summary>種類名稱；建議清單右側的說明與通知的主體。</summary>
    public string KindText => _kindText();

    /// <summary>種類的字對應的那一種；名冊沒有時為 <c>null</c>。</summary>
    public static SqlCatalogEntity? ForKind(string kind)
    {
        if (kind is null)
        {
            throw new ArgumentNullException(nameof(kind));
        }

        return ByKind.TryGetValue(kind, out var entity) ? entity : null;
    }

    /// <summary>一層的主體：資料庫是使用者與角色，伺服器是登入與伺服器角色。</summary>
    public static IReadOnlyList<SqlCatalogEntity> Principals(SqlCatalogScope scope) =>
        scope == SqlCatalogScope.Server ? ServerPrincipals : DatabasePrincipals;

    /// <summary>兩層的主體；說不出是哪一層時（<c>GRANT SELECT TO </c> 沒有 ON）兩邊都列。</summary>
    public static IReadOnlyList<SqlCatalogEntity> PrincipalsOfBothScopes => AllPrincipals;

    /// <summary>中繼資料查到的名稱轉成建議項；插入文字留給 <see cref="SqlInsertionText"/> 依設定加括號。</summary>
    /// <remarks>Tag 帶著種類本身：每一種共用 <see cref="SuggestionKind.CatalogEntity"/>，圖示要分得出是哪一種。</remarks>
    public IReadOnlyList<SqlSuggestion> Suggestions(IReadOnlyList<string> names)
    {
        if (names is null)
        {
            throw new ArgumentNullException(nameof(names));
        }

        var kindText = KindText;
        var suggestions = new SqlSuggestion[names.Count];

        for (var index = 0; index < suggestions.Length; index++)
        {
            var name = names[index];
            suggestions[index] = new SqlSuggestion(
                name,
                name,
                kindText,
                SqlKindText.Named(kindText, SqlIdentifier.QuoteIfNeeded(name)),
                SuggestionKind.CatalogEntity,
                tag: this);
        }

        return suggestions;
    }

    public override string ToString() => Kind;

    private static Dictionary<string, SqlCatalogEntity> BuildIndex()
    {
        var index = new Dictionary<string, SqlCatalogEntity>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in All)
        {
            index.Add(entity.Kind, entity);
        }

        return index;
    }
}
