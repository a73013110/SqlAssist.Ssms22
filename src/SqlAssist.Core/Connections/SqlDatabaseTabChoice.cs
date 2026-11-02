namespace SqlAssist.Core.Connections;

/// <summary>頁籤著色偏好變動後，對「使用者選了依資料庫著色」這份記憶要做什麼。</summary>
public enum SqlDatabaseTabChoiceStep
{
    None,
    Remember,
    Forget,
    Reapply,
}

/// <summary>由目前的殼層偏好推出記憶的下一步。</summary>
/// <remarks>
/// 殼層只把內建的三種著色方式寫進設定，「資料庫」一律存成無著色，而且啟動時非同步套用回來。
/// 所以提供者還是資料庫卻被關掉，一律當成殼層的重播：使用者自己按「無著色」時，
/// 記憶在命令執行前就已清掉，走不到這一步。
/// </remarks>
public static class SqlDatabaseTabChoice
{
    public static SqlDatabaseTabChoiceStep Next(bool remembered, bool coloring, bool providerIsDatabase)
    {
        if (!providerIsDatabase) return remembered ? SqlDatabaseTabChoiceStep.Forget : SqlDatabaseTabChoiceStep.None;
        if (coloring) return remembered ? SqlDatabaseTabChoiceStep.None : SqlDatabaseTabChoiceStep.Remember;
        return remembered ? SqlDatabaseTabChoiceStep.Reapply : SqlDatabaseTabChoiceStep.None;
    }
}
