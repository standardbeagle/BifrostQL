using System.Collections;
using System.Data.Common;

namespace BifrostQL.Core.Resolvers.BulkBatch
{
    /// <summary>
    /// Minimal forward-only reader feeding a provider's streaming bulk-load API
    /// (SqlBulkCopy, MySqlBulkCopy) one staged row at a time: the four control columns
    /// first (<c>__seq</c>, <c>__op</c>, <c>__grp</c>, <c>__conflict</c>), then each staged
    /// data column under its <c>__c_</c> name, in <see cref="BulkBatchPlan.StagingColumns"/>
    /// order — the same order the staging DDL creates them. Values pass through as-is; the
    /// bulk-load API converts them against the staging table's cloned target types.
    /// <paramref name="conflictAsInt"/> emits the conflict flag as 1/0 for engines whose
    /// staging clone types it as an integer and whose bulk protocol will not coerce a CLR
    /// bool (MySQL's LOAD DATA text serialization).
    /// </summary>
    public sealed class StagedRowDataReader : DbDataReader
    {
        private readonly IReadOnlyList<string> _columns;
        private readonly IReadOnlyList<BulkStagedAction> _rows;
        private readonly bool _conflictAsInt;
        private int _index = -1;

        public StagedRowDataReader(IReadOnlyList<string> columns, IReadOnlyList<BulkStagedAction> rows, bool conflictAsInt = false)
        {
            _columns = columns;
            _rows = rows;
            _conflictAsInt = conflictAsInt;
        }

        private BulkStagedAction Current => _rows[_index];

        public override int FieldCount => 4 + _columns.Count;

        public override bool Read() => ++_index < _rows.Count;

        public override string GetName(int ordinal) => ordinal switch
        {
            0 => StagedBulkBatchExecutorBase.SeqColumn,
            1 => StagedBulkBatchExecutorBase.OpColumn,
            2 => StagedBulkBatchExecutorBase.GroupColumn,
            3 => StagedBulkBatchExecutorBase.ConflictColumn,
            _ => StagedBulkBatchExecutorBase.StagedColumn(_columns[ordinal - 4]),
        };

        public override int GetOrdinal(string name)
        {
            for (var i = 0; i < FieldCount; i++)
                if (string.Equals(GetName(i), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            throw new IndexOutOfRangeException(name);
        }

        public override object GetValue(int ordinal)
        {
            var value = ordinal switch
            {
                0 => Current.Seq,
                1 => StagedBulkBatchExecutorBase.OpLetter(Current.Op).ToString(),
                2 => (object)(byte)Current.Group,
                3 => _conflictAsInt ? (Current.ConflictOnNoRows ? 1 : 0) : Current.ConflictOnNoRows,
                _ => Current.Values.TryGetValue(_columns[ordinal - 4], out var v) ? v : null,
            };
            return value ?? DBNull.Value;
        }

        public override bool IsDBNull(int ordinal) => GetValue(ordinal) == DBNull.Value;

        // Bulk-load APIs drive the reader through Read/FieldCount/GetValue/GetName only;
        // the remaining DbDataReader surface is deliberately unsupported.
        public override bool NextResult() => false;
        public override int Depth => 0;
        public override bool HasRows => _rows.Count > 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => -1;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override IEnumerator GetEnumerator() => throw new NotSupportedException();
        public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
        public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override char GetChar(int ordinal) => (char)GetValue(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();
        public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
        public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
        public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
        public override Type GetFieldType(int ordinal) => GetValue(ordinal) is var v && v != DBNull.Value ? v.GetType() : typeof(object);
        public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
        public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
        public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
        public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
        public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
        public override string GetString(int ordinal) => (string)GetValue(ordinal);
        public override int GetValues(object[] values)
        {
            var count = Math.Min(values.Length, FieldCount);
            for (var i = 0; i < count; i++)
                values[i] = GetValue(i);
            return count;
        }
    }
}
