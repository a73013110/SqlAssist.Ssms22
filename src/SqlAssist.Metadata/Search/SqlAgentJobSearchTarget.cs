using System;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 一筆作業搜尋結果指向的東西；點下去要開什麼由它決定。
/// </summary>
/// <remarks>
/// 掛在 <c>SearchHit.ActivatePayload</c> 上，與 <see cref="SqlCatalogSearchTarget"/> 是<b>兩個</b>
/// 型別而不是一個加旗標。共用一個的話，那個型別上會同時出現 <c>ObjectId</c> 與
/// <c>JobId</c>，而每一個接收端都要先問「這一次是哪一種」——忘記問的那一條路會拿
/// <c>ObjectId</c> 為 0 的作業去查目錄，得到的是一個看起來很正常的錯物件。
/// 分成兩型之後，忘記處理的那一種在啟動那一支是「這一筆沒有可以開啟的東西」，
/// 而那句話至少是誠實的。
///
/// <see cref="JobId"/> 是識別，<see cref="JobName"/> 是給人看的。名稱可以改（<c>sp_update_job</c>
/// 只動名字不動 GUID），拿名稱當識別的症狀是清單快取了一輪之後改過名，
/// 啟動時查不到而畫面上只說「找不到」。
///
/// 作業<b>只在它自己那台伺服器上</b>存在，而一份結果清單會比搜尋範圍活得久（工具窗的
/// 伺服器下拉會換，查詢視窗也會換）。伺服器因此有兩個欄位，各管一件事：
/// <see cref="Origin"/> 是連線字串那一種寫法，導航、沿用連線與取命令一律照它；
/// <see cref="ServerName"/> 是伺服器自報的名字，只給人看與組去重鍵。拿後者去找物件總管上
/// 那一台的症狀是具名執行個體與別名連線上永遠找不到；拿前者給人看則會在同一台上
/// 顯示兩種名字，端看這一輪是從哪條連線搜的。
///
/// 不可變：同一筆結果會同時被清單、預覽與啟動讀到。
/// </remarks>
public sealed class SqlAgentJobSearchTarget : ISqlSearchTarget
{
    /// <param name="serverName">伺服器自報的名字（<c>SERVERPROPERTY('ServerName')</c>）；給人看的。</param>
    /// <param name="stepId">步驟命中時的 <c>step_id</c>；作業本身命中時為 null。</param>
    /// <param name="stepName">步驟命中時的步驟名稱；作業本身命中時為 null。</param>
    /// <param name="subsystem">步驟的子系統（<c>TSQL</c>、<c>CmdExec</c>…）；作業命中時為空字串。</param>
    /// <param name="databaseName">
    /// 步驟執行時所在的資料庫；只有 <c>TSQL</c> 子系統說得出來，其餘為空字串。
    /// </param>
    public SqlAgentJobSearchTarget(
        SqlSearchOrigin origin,
        string serverName,
        Guid jobId,
        string jobName,
        bool isEnabled,
        int? stepId = null,
        string? stepName = null,
        string subsystem = "",
        string databaseName = "")
    {
        if (string.IsNullOrEmpty(serverName))
        {
            throw new ArgumentException("伺服器名稱不可為空。", nameof(serverName));
        }

        if (string.IsNullOrEmpty(jobName))
        {
            throw new ArgumentException("作業名稱不可為空。", nameof(jobName));
        }

        Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        ServerName = serverName;
        JobId = jobId;
        JobName = jobName;
        IsEnabled = isEnabled;
        StepId = stepId;
        StepName = stepName;
        Subsystem = subsystem ?? throw new ArgumentNullException(nameof(subsystem));
        DatabaseName = databaseName ?? throw new ArgumentNullException(nameof(databaseName));
    }

    /// <summary>這一筆是在哪一台伺服器上搜到的；導航與取命令照它。</summary>
    public SqlSearchOrigin Origin { get; }

    /// <summary>伺服器自報的名字；給人看的，不拿來比對是不是同一台。</summary>
    public string ServerName { get; }

    /// <summary><c>sysjobs.job_id</c>；改名不會動到它。</summary>
    public Guid JobId { get; }

    public string JobName { get; }

    /// <summary>作業是啟用的嗎；停用的作業仍然搜得到，只是膠囊上會說。</summary>
    public bool IsEnabled { get; }

    /// <summary>步驟命中時的 <c>step_id</c>；作業本身命中時為 null。</summary>
    /// <remarks>
    /// 與作業命中共用同一個型別而不是另開一個：啟動的目標都是這個作業，步驟只是多說一句
    /// 「只開這一步」。分成兩型的話，接收端要先問是哪一種，而忘記問的那一條路會在
    /// 使用者點步驟時把整個作業的二十個步驟一起開出來。
    /// </remarks>
    public int? StepId { get; }

    /// <summary>步驟命中時的步驟名稱；作業本身命中時為 null。</summary>
    public string? StepName { get; }

    /// <summary>步驟的子系統；作業命中時是空字串。</summary>
    /// <remarks>
    /// 啟動路徑拿它決定「這段命令是不是 T-SQL」——不是的話整段要換成註解，
    /// 否則使用者在新查詢視窗按 F5 執行的是一段 PowerShell。
    /// </remarks>
    public string Subsystem { get; }

    /// <summary>步驟執行時所在的資料庫；說不出來時是空字串。</summary>
    public string DatabaseName { get; }

    /// <summary>這一筆指的是某一個步驟，而不是整個作業。</summary>
    public bool IsStep => StepId.HasValue;

    public override string ToString() =>
        IsStep ? $"{ServerName}!{JobName}#{StepId}" : $"{ServerName}!{JobName}";
}
