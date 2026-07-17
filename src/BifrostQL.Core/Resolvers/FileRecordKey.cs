using BifrostQL.Core.Model;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Decodes a file resolver's single <c>recordId</c> argument into the owning
    /// row's primary-key values, binding EACH key column to its OWN value in the
    /// table's declared key order.
    ///
    /// <para>A composite key is carried in <c>recordId</c> as its components joined
    /// with '-', the same rendering <see cref="Storage.FileObjectSeam"/> produces
    /// (<c>string.Join("-", primaryKey)</c>); it is split back into exactly one
    /// value per key column here. A single-column key is never split — the whole
    /// <c>recordId</c> is that column's value, so a hyphenated scalar id (e.g. a
    /// GUID) survives intact.</para>
    ///
    /// <para>This never broadcasts the single scalar across every key column — the
    /// bug that built the mis-scoped <c>id = X AND tenant_id = X</c> predicate,
    /// which silently matched a coincident row. A <c>recordId</c> whose component
    /// count does not equal the key arity fails fast rather than under-constrain
    /// the WHERE clause.</para>
    /// </summary>
    internal static class FileRecordKey
    {
        public static Dictionary<string, object?> BuildKeyData(IDbTable table, string recordId)
        {
            var keyColumns = table.KeyColumns.ToList();
            if (keyColumns.Count == 0)
                throw new BifrostExecutionError($"Table '{table.DbName}' has no primary key");

            // Single-column key: the whole recordId is the value (never split, so a
            // hyphen inside the id is preserved). Composite key: split into exactly
            // one component per column, in declared key order.
            var parts = keyColumns.Count == 1
                ? new[] { recordId }
                : recordId.Split('-');

            if (parts.Length != keyColumns.Count)
                throw new BifrostExecutionError(
                    $"recordId '{recordId}' does not address a row of '{table.DbName}': its composite primary key " +
                    $"expects {keyColumns.Count} value(s) ({string.Join(", ", keyColumns.Select(c => c.ColumnName))}) " +
                    $"joined by '-', but received {parts.Length}.");

            var keyData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < keyColumns.Count; i++)
                keyData[keyColumns[i].ColumnName] = Convert.ChangeType(parts[i], GetClrType(keyColumns[i].DataType));

            return keyData;
        }

        private static Type GetClrType(string dataType)
        {
            var normalized = dataType.ToLowerInvariant();
            return normalized switch
            {
                "int" or "integer" => typeof(int),
                "bigint" => typeof(long),
                "smallint" => typeof(short),
                "tinyint" => typeof(byte),
                "uniqueidentifier" or "uuid" => typeof(Guid),
                _ => typeof(string)
            };
        }
    }
}
