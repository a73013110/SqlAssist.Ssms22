using System;
using System.Data;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Tests.Querying;

/// <summary>
/// 以固定的欄位值陣列模擬一列查詢結果，讓中繼資料的對應邏輯不必連資料庫就能測試。
/// 只實作 <see cref="SqlMetadataReader"/> 實際會用到的成員。
/// </summary>
internal sealed class FakeDataRecord : IDataRecord
{
    private readonly object?[] _values;

    public FakeDataRecord(params object?[] values)
    {
        _values = values;
    }

    public int FieldCount => _values.Length;

    public object this[int index] => _values[index] ?? DBNull.Value;

    public object this[string name] => throw new NotSupportedException();

    public bool IsDBNull(int index) => _values[index] is null or DBNull;

    public bool GetBoolean(int index) => (bool)_values[index]!;

    public byte GetByte(int index) => (byte)_values[index]!;

    public short GetInt16(int index) => (short)_values[index]!;

    public int GetInt32(int index) => (int)_values[index]!;

    public string GetString(int index) => (string)_values[index]!;

    public object GetValue(int index) => this[index];

    public char GetChar(int index) => throw new NotSupportedException();

    public long GetInt64(int index) => throw new NotSupportedException();

    public float GetFloat(int index) => throw new NotSupportedException();

    public double GetDouble(int index) => throw new NotSupportedException();

    public decimal GetDecimal(int index) => throw new NotSupportedException();

    public DateTime GetDateTime(int index) => throw new NotSupportedException();

    public Guid GetGuid(int index) => throw new NotSupportedException();

    public string GetName(int index) => throw new NotSupportedException();

    public int GetOrdinal(string name) => throw new NotSupportedException();

    public string GetDataTypeName(int index) => throw new NotSupportedException();

    public Type GetFieldType(int index) => throw new NotSupportedException();

    public int GetValues(object[] values) => throw new NotSupportedException();

    public IDataReader GetData(int index) => throw new NotSupportedException();

    public long GetBytes(int index, long fieldOffset, byte[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    public long GetChars(int index, long fieldOffset, char[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();
}
