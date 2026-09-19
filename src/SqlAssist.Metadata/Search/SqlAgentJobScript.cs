using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Threading;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 把一個作業（或它的其中一步）的命令組成可以送進查詢視窗的文字。
/// </summary>
/// <remarks>
/// 這是作業來源的「主要動作」。目錄物件那一邊按的是 F12——取結構、組
/// <c>CREATE</c>、開進新查詢視窗；作業沒有定義可以組，但它的步驟命令<b>就是</b>
/// 使用者想看的那段文字，所以主要動作對齊成同一件事：開進新的查詢視窗。
/// 換成「複製作業名稱」的話，雙擊在這個清單上的意思會隨著哪一列而變，
/// 而那是使用者每一次都要先想一下的那種不一致。
///
/// <b>不是 T-SQL 的步驟整段換成註解。</b>CmdExec 是命令列、PowerShell 是指令碼、
/// SSIS 是一串參數；它們貼進查詢視窗一樣看得懂，但按下 F5 是一個語法錯誤。
/// 這與「資料不齊時不輸出半份可以執行的東西」是同一條規則：貼得上去卻跑不動的東西
/// 比一段註解糟得多。註解只有 <see cref="SqlScriptComment"/> 一份，
/// 名稱或命令裡的換行不會因此變成可執行的 SQL。
///
/// <b>查不到就說查不到</b>，禁止退回「把清單上那一列的名稱再顯示一次」當結果。
/// </remarks>
public static class SqlAgentJobScript
{
    private const string OpeningConnection = "開啟 SQL Agent 作業連線";
    private const string LoadingSteps = "載入 SQL Agent 作業步驟";

    /// <summary>
    /// 取回這一筆指向的作業步驟，組成指令碼；資料庫說不行或查不到時回傳 null。
    /// </summary>
    /// <param name="target">要開哪一個作業；<see cref="SqlAgentJobSearchTarget.StepId"/> 有值時只開那一步。</param>
    /// <param name="newLine">目的地文件的換行字元。</param>
    /// <remarks>
    /// 在<b>背景</b>執行緒呼叫：這是一條跨資料庫的查詢加一次字串組裝，而目的地是還沒開出來
    /// 的空白視窗，沒有任何理由先等它。
    ///
    /// 與快照那一層同一條降級規則：<see cref="DbException"/> 不冒出去，
    /// 失敗帶著「哪一條查詢」走 <see cref="SqlMetadataFailure"/>。
    /// </remarks>
    public static string? TryBuild(
        ISqlConnectionSource connectionSource,
        SqlAgentJobSearchTarget target,
        string newLine,
        CancellationToken cancellationToken,
        int commandTimeoutSeconds = SqlCatalogSearchIndex.DefaultCommandTimeoutSeconds)
    {
        if (connectionSource is null) throw new ArgumentNullException(nameof(connectionSource));
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (newLine is null) throw new ArgumentNullException(nameof(newLine));

        var operation = OpeningConnection;

        try
        {
            using var connection = connectionSource.OpenConnection();

            operation = LoadingSteps;
            var steps = ReadSteps(connection, target, commandTimeoutSeconds, cancellationToken);

            // 一列都沒有回來而查詢本身沒有失敗，原因只有兩個：作業在清單被快取之後被刪掉，
            // 或這個登入對它的權限在那之後被收回。回 null 讓呼叫端說得出這兩個原因——
            // 組一份「這個作業沒有步驟」的空指令碼會把兩種情形說成第三種。
            return steps.Count == 0 ? null : Build(target, steps, newLine);
        }
        catch (DbException exception)
        {
            SqlMetadataFailure.Report(operation + "：" + target.JobName, exception);
            return null;
        }
    }

    /// <summary>把步驟排成一份帶檔頭的文字。</summary>
    /// <remarks>
    /// 檔頭要說得出「這是哪一台伺服器的哪一個作業」：一份開在新視窗裡的步驟命令與
    /// 使用者自己寫的 SQL 長得一模一樣，而新視窗沿用的是<b>查詢視窗</b>那條連線，
    /// 不一定是作業所在的那一台。少了這幾行，使用者會對著另一台伺服器按 F5。
    /// </remarks>
    public static string Build(
        SqlAgentJobSearchTarget target, IReadOnlyList<SqlAgentJobStep> steps, string newLine)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (steps is null) throw new ArgumentNullException(nameof(steps));
        if (newLine is null) throw new ArgumentNullException(nameof(newLine));

        var builder = new StringBuilder();

        SqlScriptComment.AppendLine(builder, "SQL Agent 作業：" + target.JobName, newLine);
        SqlScriptComment.AppendLine(builder, "伺服器：" + target.ServerName, newLine);

        if (!target.IsEnabled)
        {
            SqlScriptComment.AppendLine(builder, "這個作業目前是停用的。", newLine);
        }

        SqlScriptComment.AppendLine(
            builder,
            "以下是步驟命令本身，不是排程或通知設定；改動這裡的文字不會回寫到作業上。",
            newLine);

        foreach (var step in steps)
        {
            builder.Append(newLine);
            AppendStep(builder, step, newLine);
        }

        return builder.ToString();
    }

    private static void AppendStep(StringBuilder builder, SqlAgentJobStep step, string newLine)
    {
        var order = step.StepId.ToString(CultureInfo.InvariantCulture);
        var heading = step.Name.Length == 0 ? "第 " + order + " 步" : "第 " + order + " 步：" + step.Name;

        if (step.Subsystem.Length > 0) heading += "（" + step.Subsystem + "）";
        if (step.DatabaseName.Length > 0) heading += "　資料庫：" + step.DatabaseName;

        SqlScriptComment.AppendLine(builder, heading, newLine);

        var command = step.Command ?? "";

        if (command.Length == 0)
        {
            SqlScriptComment.AppendLine(builder, "這一步沒有命令內容。", newLine);
            return;
        }

        if (!step.IsTransactSql)
        {
            // 非 T-SQL 的子系統整段註解掉，並說明原因。留成可執行文字的話，
            // 使用者按 F5 得到的是一個語法錯誤，而錯誤訊息指不出「這本來就不是 SQL」。
            SqlScriptComment.AppendLine(
                builder,
                "這一步的子系統不是 TSQL，下面的命令原文不能直接執行，已整段保留為註解。",
                newLine);
            SqlScriptComment.AppendLine(builder, command, newLine);
            return;
        }

        // 換行統一成目的地文件那一種：msdb 裡存的可能是 CRLF，而目的地可能是 LF，
        // 混在一起的症狀是查詢視窗裡每一行結尾多一個看不見的字元。
        builder.Append(Normalize(command, newLine)).Append(newLine);
    }

    private static string Normalize(string text, string newLine)
    {
        var builder = new StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];

            if (current != '\r' && current != '\n')
            {
                builder.Append(current);
                continue;
            }

            builder.Append(newLine);
            if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
        }

        return builder.ToString();
    }

    /// <remarks>
    /// 指名步驟時仍然在讀取端過濾，而不是在 SQL 上多一個 <c>AND s.step_id = @stepId</c>：
    /// 兩份幾乎一樣的查詢一定會有一份忘記跟著改，而步驟數以個位數計，多讀幾列不值得
    /// 再維護一條路。
    /// </remarks>
    private static List<SqlAgentJobStep> ReadSteps(
        IDbConnection connection,
        SqlAgentJobSearchTarget target,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SqlAgentJobSearchQueries.JobSteps;
        command.CommandTimeout = commandTimeoutSeconds;

        var parameter = command.CreateParameter();
        parameter.ParameterName = SqlAgentJobSearchQueries.JobIdParameterName;
        parameter.DbType = DbType.Guid;
        parameter.Value = target.JobId;
        command.Parameters.Add(parameter);

        var steps = new List<SqlAgentJobStep>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stepId = reader.GetInt32(0);

            if (target.StepId is { } only && only != stepId) continue;

            steps.Add(new SqlAgentJobStep(
                stepId,
                ReadText(reader, 1),
                ReadText(reader, 2),
                ReadText(reader, 3),
                ReadText(reader, 4)));
        }

        return steps;
    }

    private static string ReadText(IDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
}
