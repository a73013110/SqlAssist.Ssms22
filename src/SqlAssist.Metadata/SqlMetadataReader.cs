using System;
using System.Data;

namespace SqlAssist.Metadata;

/// <summary>
/// 把 <see cref="IDataRecord"/> 的一列對應成模型物件。
/// </summary>
/// <remarks>
/// 刻意與連線及命令執行分離，欄位順序、型別格式化與 NULL 處理這些最容易出錯的
/// 部分才能用假的 <see cref="IDataRecord"/> 單獨測試。
/// 欄位順序必須與 <see cref="SqlMetadataQueries"/> 的 SELECT 清單一致。
/// </remarks>
public static class SqlMetadataReader
{
    public static SqlObjectInfo ReadObject(IDataRecord record)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        return new SqlObjectInfo(
            record.GetInt32(0),
            record.GetString(1),
            record.GetString(2),
            SqlObjectKinds.FromSysObjectType(record.GetString(3)));
    }

    public static SqlColumnInfo ReadColumn(IDataRecord record)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        var dataType = SqlTypeFormatter.Format(
            record.GetString(2),
            record.GetInt16(3),
            record.GetByte(4),
            record.GetByte(5));

        return new SqlColumnInfo(
            record.GetInt32(0),
            record.GetString(1),
            dataType,
            record.GetBoolean(6),
            record.GetBoolean(7),
            record.GetBoolean(8),
            record.GetBoolean(9),
            record.IsDBNull(10) ? null : record.GetString(10),
            record.IsDBNull(11) ? null : record.GetString(11));
    }

    public static SqlIndexRow ReadIndexRow(IDataRecord record)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        return new SqlIndexRow(
            record.GetInt32(0),
            record.GetString(1),
            record.GetBoolean(2),
            record.GetBoolean(3),
            record.GetBoolean(4),
            record.GetString(5),
            record.IsDBNull(6) ? null : record.GetString(6),
            record.GetString(7),
            record.GetBoolean(8),
            record.GetBoolean(9));
    }

    public static SqlForeignKeyRow ReadForeignKeyRow(IDataRecord record)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        return new SqlForeignKeyRow(
            record.GetString(0),
            record.GetString(1),
            record.GetString(2),
            record.GetString(3),
            record.GetString(4),
            record.GetString(5),
            record.GetString(6));
    }

    public static SqlParameterInfo ReadParameter(IDataRecord record)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        var dataType = SqlTypeFormatter.Format(
            record.GetString(2),
            record.GetInt16(3),
            record.GetByte(4),
            record.GetByte(5));

        // 純量函式的傳回值在 sys.parameters 中名稱為空字串，不是 NULL。
        var name = record.IsDBNull(1) ? string.Empty : record.GetString(1);

        return new SqlParameterInfo(
            record.GetInt32(0),
            string.IsNullOrEmpty(name) ? "(傳回值)" : name,
            dataType,
            record.GetBoolean(6));
    }
}
