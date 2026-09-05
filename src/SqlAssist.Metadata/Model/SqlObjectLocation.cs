using SqlAssist.Core.Parsing;

namespace SqlAssist.Metadata.Model;

/// <summary>游標位置所指的資料庫物件，以及（若游標停在欄位上）該欄位。</summary>
public sealed class SqlObjectLocation
{
    public SqlObjectLocation(
        SqlIdentifierReference reference,
        SqlObjectInfo objectInfo,
        SqlColumnInfo? column = null)
    {
        Reference = reference;
        Object = objectInfo;
        Column = column;
    }

    public SqlIdentifierReference Reference { get; }

    /// <summary>物件本身；游標停在欄位上時，是該欄位所屬的物件。</summary>
    public SqlObjectInfo Object { get; }

    /// <summary>游標停在欄位上時的欄位描述，否則為 null。</summary>
    public SqlColumnInfo? Column { get; }
}
