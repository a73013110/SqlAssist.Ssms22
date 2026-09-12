using System;
using System.Globalization;
using System.Text;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.QueryMemory.Sqlite;

internal sealed class SqliteMaintenanceCursor
{
    private readonly string _prefix;
    public int Stage { get; set; }
    public string After { get; set; } = "";
    public bool MadeProgress { get; set; }

    public SqliteMaintenanceCursor(string storeId, QueryMemoryMaintenanceRequest request)
    {
        var policy = request.Policy;
        var fingerprint = string.Join(";", policy.DraftBefore?.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture) ?? "-",
            policy.ExecutionBefore?.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture) ?? "-",
            policy.MaxContentBytes?.ToString(CultureInfo.InvariantCulture) ?? "-");
        _prefix = "maintenance1|" + storeId + "|" + QueryContent.Create(fingerprint).ContentHash + "|";
        if (request.Cursor == null) return;
        if (request.Cursor.Length > 1024) throw InvalidCursor();
        string value;
        try { value = Encoding.UTF8.GetString(Convert.FromBase64String(request.Cursor)); }
        catch (FormatException) { throw InvalidCursor(); }
        if (!value.StartsWith(_prefix, StringComparison.Ordinal)) throw InvalidCursor();
        var fields = value.Substring(_prefix.Length).Split('|');
        if (fields.Length != 3 || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var stage) ||
            stage < 0 || stage >= 5 || fields[1].Length > 128 || (fields[2] != "0" && fields[2] != "1")) throw InvalidCursor();
        Stage = stage;
        After = fields[1];
        MadeProgress = fields[2] == "1";
    }

    public string Encode() => Convert.ToBase64String(Encoding.UTF8.GetBytes(_prefix +
        Stage.ToString(CultureInfo.InvariantCulture) + "|" + After + "|" + (MadeProgress ? "1" : "0")));

    private static ArgumentException InvalidCursor() => new("維護游標無效，或不屬於目前儲存庫及政策。", "cursor");
}
