using System;
using System.Collections.Generic;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.Crypto
{
    /// <summary>
    /// Rejects any query that uses an encrypted column as a FILTER, SORT, or AGGREGATE
    /// predicate. An encrypted column's stored value is a non-deterministic ciphertext,
    /// so filtering or ordering by it is either useless or — worse — an information
    /// oracle (a WHERE that changes the result set leaks whether a guessed value matches).
    /// Selecting the column for output is still allowed; it is decrypted or masked on
    /// read by <see cref="CryptoReadProjector"/>.
    ///
    /// Registered as an <see cref="IFilterTransformer"/> so it participates in the query
    /// transformer set and is discovered as an <see cref="IColumnFilterGuard"/>; it adds
    /// no filter of its own. Equality search on an encrypted column is intended to run
    /// through its <c>blind-index</c> sibling column (a separate, non-encrypted column) —
    /// server-side rewrite of an equality predicate onto the blind index is a later
    /// enhancement.
    /// </summary>
    public sealed class EncryptedColumnReadGuard : IFilterTransformer, IColumnFilterGuard, IModuleNamed
    {
        internal const string FilterDeniedMessage =
            "A requested column may not be used in a filter, sort, or aggregate.";

        // Security band, alongside the encrypt-on-write transformer.
        public int Priority => 40;

        public string ModuleName => "encrypted-column-guard";

        // Contributes no filter — it is a guard only.
        public bool AppliesTo(IDbTable table, QueryTransformContext context) => false;

        public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context) => null;

        public void AssertColumnsFilterable(
            IDbTable table, IEnumerable<string> filteredColumns, QueryTransformContext context)
        {
            if (table is null) throw new ArgumentNullException(nameof(table));
            if (filteredColumns is null) throw new ArgumentNullException(nameof(filteredColumns));

            foreach (var name in filteredColumns)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (!table.ColumnLookup.TryGetValue(name, out var column))
                    continue;

                if (!string.IsNullOrWhiteSpace(column.GetMetadataValue(MetadataKeys.Crypto.Encrypt)))
                    throw new BifrostExecutionError(FilterDeniedMessage)
                    { ErrorCode = BifrostExecutionError.AccessDeniedCode };
            }
        }
    }
}
