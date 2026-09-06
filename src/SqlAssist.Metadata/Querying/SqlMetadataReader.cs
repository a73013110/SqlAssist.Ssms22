using System;
using System.Data;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Querying;

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
    /// <param name="databaseName">
    /// 這一批物件所屬的資料庫；查詢視窗自己那條連線的傳 null。
    /// <c>object_id</c> 只在單一資料庫裡唯一，跨資料庫查快取一定要先換目錄。
    /// </param>
    /// <param name="serverName">
    /// 這一批物件所在的連結伺服器；目前這台伺服器上的傳 null。
    /// </param>
    public static SqlObjectInfo ReadObject(
        IDataRecord record,
        string? databaseName = null,
        string? serverName = null)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        return new SqlObjectInfo(
            record.GetInt32(0),
            record.GetString(1),
            record.GetString(2),
            SqlObjectKinds.FromSysObjectType(record.GetString(3)),
            databaseName,
            serverName);
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

        var script = new SqlColumnScriptDetail(
            record.GetString(2),
            record.GetInt16(3),
            record.GetByte(4),
            record.GetByte(5),
            ReadOptionalString(record, 13),
            ReadOptionalString(record, 14),
            ReadOptionalString(record, 15),
            ReadOptionalString(record, 16),
            ReadOptionalBoolean(record, 17),
            ReadOptionalBoolean(record, 18),
            record.FieldCount > 19 && record.GetBoolean(19),
            record.FieldCount > 20 && record.GetBoolean(20));

        return new SqlColumnInfo(
            record.GetInt32(0),
            record.GetString(1),
            dataType,
            record.GetBoolean(6),
            record.GetBoolean(7),
            record.GetBoolean(8),
            record.GetBoolean(9),
            record.IsDBNull(10) ? null : record.GetString(10),
            record.IsDBNull(11) ? null : record.GetString(11),
            record.GetBoolean(12),
            script);
    }

    /// <remarks>
    /// 先問 <see cref="IDataRecord.FieldCount"/> 再讀：指令碼宣告的資料表與測試用的
    /// 假資料列只組得出前面那幾欄，而多讀一欄拿到的是
    /// <see cref="System.IndexOutOfRangeException"/>——那不是 <c>DbException</c>，
    /// 不會被降級成「這一輪沒有資料」，而會一路冒到平台邊界去。
    /// </remarks>
    private static string? ReadOptionalString(IDataRecord record, int ordinal) =>
        record.FieldCount > ordinal && !record.IsDBNull(ordinal) ? record.GetString(ordinal) : null;

    private static bool ReadOptionalBoolean(IDataRecord record, int ordinal) =>
        record.FieldCount > ordinal && !record.IsDBNull(ordinal) && record.GetBoolean(ordinal);

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

    public static SqlCheckConstraint ReadCheckConstraint(IDataRecord record)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        return new SqlCheckConstraint(
            record.GetString(0),
            record.IsDBNull(1) ? string.Empty : record.GetString(1),
            record.GetBoolean(2),
            record.GetBoolean(3),
            record.GetBoolean(4),
            record.IsDBNull(5) ? null : record.GetString(5));
    }

    /// <remarks>
    /// <c>level</c> 是查詢自己編的號（0 資料表、1 資料行、2 索引、3 條件約束），
    /// 不是目錄檢視上的欄位。認不得的號一律當成資料表層級——多寫一筆掛在資料表上
    /// 的說明，比整份指令碼因為一個沒見過的類別而失敗好。
    /// </remarks>
    public static SqlExtendedProperty ReadExtendedProperty(IDataRecord record)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        var level = record.GetInt32(0) switch
        {
            1 => SqlExtendedPropertyLevel.Column,
            2 => SqlExtendedPropertyLevel.Index,
            3 => SqlExtendedPropertyLevel.Constraint,
            _ => SqlExtendedPropertyLevel.Table
        };

        return new SqlExtendedProperty(
            level,
            record.GetString(1),
            record.IsDBNull(2) ? string.Empty : record.GetString(2),
            record.IsDBNull(4) ? null : record.GetString(4));
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

    /// <remarks>
    /// 型別走與資料行、參數同一支 <see cref="SqlTypeFormatter.Format"/>：
    /// <c>sys.sequences</c> 只認得整數與 <c>decimal</c>／<c>numeric</c>，
    /// 而後者非帶精確度與小數位不可，<c>AS decimal</c> 建出來的序列不是同一個東西。
    /// 長度傳 0——那組型別一個都不看它。
    /// </remarks>
    public static SqlSequenceInfo ReadSequence(IDataRecord record)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        var dataType = SqlTypeFormatter.Format(
            record.GetString(0),
            maxLength: 0,
            record.GetByte(1),
            record.GetByte(2));

        return new SqlSequenceInfo(
            dataType,
            record.GetString(3),
            record.GetString(4),
            record.GetString(5),
            record.GetString(6),
            record.GetBoolean(7),
            record.GetBoolean(8),
            record.IsDBNull(9) ? null : record.GetInt32(9));
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
