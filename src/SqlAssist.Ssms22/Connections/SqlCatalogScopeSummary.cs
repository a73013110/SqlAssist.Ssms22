using SqlAssist.Core.Connections;
using SqlAssist.Core.Localization;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Connections;

/// <summary>
/// 範圍列伺服器與資料庫兩顆按鈕上的摘要；SQL Search 與資料字典共用一份寫法。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlCatalogScopeBar"/> 分開放，只因為這一半零 VS 相依，寫得進回歸測試。
/// 單選時空名單是「未選擇」，多選時是「全部」，與面板第一列共用同一份字：摘要寫著「全部」而
/// 面板上一個勾都沒有時，使用者會以為條件弄丟了。
/// </remarks>
internal static class SqlCatalogScopeSummary
{
    /// <summary>多選資料庫一個都沒勾時，按鈕與面板第一列上的字：這台伺服器上進得去的每一個。</summary>
    public static string AllDatabasesLabel => CommonText.All;

    /// <summary>伺服器按鈕上的摘要：選定的那一台，還沒選時說「未選擇」。</summary>
    public static string Server(SqlConnectionScope scope) =>
        scope.Servers.Count == 0 ? ConnectionText.NotSelected : scope.Servers[0];

    /// <summary>資料庫按鈕上的摘要；過濾按鈕的摘要只有一份寫法，見 <see cref="SqlFilterSummary"/>。</summary>
    public static string Databases(SqlConnectionScope scope) =>
        scope.MultipleDatabases
            ? SqlFilterSummary.Of(scope.Databases.Count, AllDatabasesLabel,
                scope.Databases.Count == 1 ? scope.Databases[0] : null, count => ChromeText.DatabaseCount(count))
            : scope.Databases.Count == 0 ? ConnectionText.NotSelected : scope.Databases[0];
}
