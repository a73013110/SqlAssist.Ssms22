using System;
using System.Globalization;
using System.Text;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.SqlMemory.Sqlite;

/// <summary>
/// 綁 StoreId、政策與掃描方式的維護位置：階段、分組鍵、(時間, 鍵) 與本輪是否有進展。
/// </summary>
/// <remarks>
/// 時間階段只用 Time／Key；分組階段 Group 為空表示還沒進任何組，Time 為 null 表示該組已巡完；
/// 逐鍵階段只用 Key。游標可以存進狀態表跨程序接續，所以格式錯誤與跨政策一律拒絕，不猜測修補。
/// </remarks>
internal sealed class SqliteMaintenanceCursor
{
    private readonly string _prefix;
    private readonly int _stageCount;

    public SqliteMaintenanceCursor(string storeId, SqlMemoryMaintenanceRequest request, int stageCount)
    {
        var policy = request.Policy;
        _stageCount = stageCount;
        var fingerprint = string.Join(";", Ticks(policy.DraftBefore), Ticks(policy.ExecutionBefore),
            Number(policy.MaxContentBytes), Number(policy.MaxExecutionEvents), Number(policy.MaxAutoRevisionsPerSession),
            Number(policy.MaxRevisionsPerFavorite), Ticks(policy.RecoveryBefore));
        _prefix = "maintenance3|" + storeId + "|" + SqlContent.Create(fingerprint).ContentHash + "|" +
            ((int)request.Scan).ToString(CultureInfo.InvariantCulture) + "|";
        if (request.Cursor == null) return;
        if (request.Cursor.Length > 1024) throw InvalidCursor();
        string value;
        try { value = Encoding.UTF8.GetString(Convert.FromBase64String(request.Cursor)); }
        catch (FormatException) { throw InvalidCursor(); }
        if (!value.StartsWith(_prefix, StringComparison.Ordinal)) throw InvalidCursor();
        var fields = value.Substring(_prefix.Length).Split('|');
        long time = 0;
        if (fields.Length != 5 || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var stage) ||
            stage < 0 || stage >= stageCount || fields[1].Length > 128 || fields[3].Length > 128 ||
            (fields[2].Length != 0 && !long.TryParse(fields[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out time)) ||
            (fields[4] != "0" && fields[4] != "1")) throw InvalidCursor();
        Stage = stage;
        Group = fields[1];
        Time = fields[2].Length == 0 ? null : time;
        Key = fields[3];
        MadeProgress = fields[4] == "1";
    }

    public int Stage { get; private set; }
    public string Group { get; set; } = "";
    public long? Time { get; set; }
    public string Key { get; set; } = "";
    public bool MadeProgress { get; set; }

    public bool Completed => Stage >= _stageCount;

    public void NextStage()
    {
        Stage++;
        Group = "";
        Time = null;
        Key = "";
    }

    public string Encode() => Convert.ToBase64String(Encoding.UTF8.GetBytes(_prefix +
        Stage.ToString(CultureInfo.InvariantCulture) + "|" + Group + "|" +
        (Time?.ToString(CultureInfo.InvariantCulture) ?? "") + "|" + Key + "|" + (MadeProgress ? "1" : "0")));

    private static string Ticks(DateTimeOffset? time) => time?.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static SqlMemoryStorageException InvalidCursor() =>
        new(SqlMemoryStorageErrorKind.InvalidCursor, "維護游標無效，或不屬於目前儲存庫、政策及掃描方式。");
}
