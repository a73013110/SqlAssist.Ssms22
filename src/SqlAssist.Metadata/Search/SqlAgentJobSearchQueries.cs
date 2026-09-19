namespace SqlAssist.Metadata.Search;

/// <summary>
/// SQL Agent 作業搜尋用的查詢，分成識別一段、步驟命令一段，外加啟動時按需取回的一段。
/// </summary>
/// <remarks>
/// <b>三段式名稱是這一族與目錄查詢最大的差別。</b>目錄那一邊一律寫成不加限定的
/// <c>sys.</c>，決定查哪一個資料庫的是連線；作業則相反——它是<b>伺服器層級</b>的東西，
/// 資料固定住在 <c>msdb</c>，而使用者的查詢視窗連在哪一個資料庫與它無關。
/// 改成換目錄（<see cref="Querying.SqlDatabaseScopedConnectionSource"/>）也做得到，
/// 但那等於為一件永遠不變的事多一次 <c>ChangeDatabase</c> 的失敗點，
/// 而那一次失敗與「msdb 讀不到」在畫面上長得一模一樣。
///
/// <b>識別與命令分兩段</b>，理由與 <see cref="SqlCatalogSearchQueries"/> 的兩段一樣：
/// <c>sysjobsteps.command</c> 是 <c>nvarchar(max)</c>，一個步驟塞進幾千行 T-SQL 是常態。
/// 這一輪不搜本文（<see cref="Core.Search.SearchTargets"/> 不含 <c>Text</c>）時第二段連送
/// 都不送。併成一條 <c>LEFT JOIN</c> 的話省不掉——伺服器仍然要讀，網路仍然要傳，
/// 而那一份會在讀取端被丟掉。
///
/// <b>權限不靠這裡過濾。</b><c>msdb.dbo.sysjobs</c> 本身就照登入過濾列
/// （SQLAgentUserRole 只看得到自己擁有的作業），所以查得到幾列取決於是誰在問；
/// 完全沒有 msdb 存取權時整條查詢是 <see cref="System.Data.Common.DbException"/>，
/// 由 <see cref="SqlAgentJobSearchSnapshot.TryLoad"/> 降級。兩件事的處置不同：
/// 前者是「這台伺服器上你看得到的作業就這些」，後者是「這個來源這一輪讀不到」。
///
/// 一個參數都不留給呼叫端忘記綁：只有 <see cref="JobSteps"/> 吃
/// <see cref="JobIdParameterName"/>，而那一條是啟動路徑上指名一個作業用的。
/// 把 GUID 直接串進 SQL 字面值的作法被放掉了——作業名稱是使用者取的，
/// 而串字串這條路一開就再也關不上。
/// </remarks>
public static class SqlAgentJobSearchQueries
{
    /// <summary>啟動路徑指名作業用的參數。</summary>
    public const string JobIdParameterName = "@jobId";

    /// <summary>
    /// 第一段：作業與步驟的識別欄位；不含命令本文。
    /// </summary>
    /// <remarks>
    /// 欄位順序：job_id、job_name、job_enabled、step_id、step_name、subsystem、
    /// database_name、server_name。
    ///
    /// <c>LEFT JOIN</c> 而不是 <c>INNER JOIN</c>：一個還沒有步驟的作業仍然搜得到名稱，
    /// 而 <c>INNER JOIN</c> 會讓它整個消失——那與「這台伺服器上沒有這個作業」
    /// 在畫面上一模一樣。沒有步驟的那一列 step_id 是 NULL，讀取端據此只產生作業那一筆。
    ///
    /// <c>enabled</c> 是 <c>tinyint</c>，不是 <c>bit</c>。直接 <c>GetBoolean</c> 讀會拿到
    /// <see cref="System.InvalidCastException"/>，而那不是 <c>DbException</c>，降級接不住；
    /// 所以在伺服器端 <c>CAST</c> 成 <c>bit</c>，與目錄那一邊對 <c>OBJECTPROPERTY</c>
    /// 的處置同一條理由。
    ///
    /// <c>SERVERPROPERTY('ServerName')</c> 每一列都回同一個值，看起來浪費，但換到的是
    /// 少一次來回。膠囊上要顯示「這是哪一台」，而 <c>ISqlConnectionSource</c> 手上只有
    /// 正規化過的連線字串當快取鍵，那個字串不能給人看。取 <c>ServerName</c> 而不是
    /// <c>@@SERVERNAME</c>：後者在機器改名而沒有跑
    /// <c>sp_dropserver</c>／<c>sp_addserver</c> 時仍然回舊名字。它理論上答得出 NULL，
    /// 那一種由 <see cref="SqlAgentJobSearchSnapshot.UnknownServerName"/> 接住——
    /// 在這裡 <c>COALESCE</c> 一個 <c>@@SERVERNAME</c> 進來的話，
    /// 查詢裡就多出一個沒有人綁值的 <c>@</c> 開頭字串，而擋漏綁參數的那道測試分不出它。
    ///
    /// <c>ORDER BY</c> 讓同一台伺服器每次撈出來的順序一樣——少了它，同分的結果先後
    /// 由伺服器決定，清單會自己跳。
    /// </remarks>
    public const string Jobs = @"
SELECT
    j.job_id,
    j.name AS job_name,
    CAST(j.enabled AS bit) AS job_enabled,
    s.step_id,
    s.step_name,
    s.subsystem,
    s.database_name,
    CAST(SERVERPROPERTY('ServerName') AS nvarchar(128)) AS server_name
FROM msdb.dbo.sysjobs AS j
LEFT JOIN msdb.dbo.sysjobsteps AS s ON s.job_id = j.job_id
ORDER BY j.name, s.step_id;";

    /// <summary>
    /// 第二段：每一個步驟的命令本文。
    /// </summary>
    /// <remarks>
    /// 欄位順序：job_id、step_id、command。
    ///
    /// <c>INNER JOIN sysjobs</c> 不是為了取欄位，是為了讓兩段看到的作業<b>是同一組</b>：
    /// <c>sysjobs</c> 照登入過濾列，<c>sysjobsteps</c> 不會自己跟著濾。少了這一道，
    /// 權限受限的登入會拿到一批對不上任何作業的命令本文，而那些命中點下去沒有東西可開。
    /// </remarks>
    public const string StepCommands = @"
SELECT
    s.job_id,
    s.step_id,
    s.command
FROM msdb.dbo.sysjobsteps AS s
INNER JOIN msdb.dbo.sysjobs AS j ON j.job_id = s.job_id
ORDER BY s.job_id, s.step_id;";

    /// <summary>
    /// 啟動一筆結果時，指名一個作業取回它的步驟。
    /// </summary>
    /// <remarks>
    /// 欄位順序：step_id、step_name、subsystem、database_name、command。
    ///
    /// 命令本文<b>不</b>放進 <c>SearchHit.ActivatePayload</c> 帶著走，而是在使用者真的
    /// 按下去之後重問一次：酬載會被整份結果清單一起釘在記憶體裡（一輪上限一百筆），
    /// 而每一筆都可能是幾千行。順帶換到的是「開出來的是現在那一版」——清單是快取的，
    /// 中間有人改過步驟時，帶著走的那一份會安靜地過期。
    /// </remarks>
    public const string JobSteps = @"
SELECT
    s.step_id,
    s.step_name,
    s.subsystem,
    s.database_name,
    s.command
FROM msdb.dbo.sysjobsteps AS s
INNER JOIN msdb.dbo.sysjobs AS j ON j.job_id = s.job_id
WHERE s.job_id = @jobId
ORDER BY s.step_id;";
}
