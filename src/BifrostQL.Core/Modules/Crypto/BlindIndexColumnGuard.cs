using System;
using System.Collections.Generic;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.Crypto
{
    /// <summary>
    /// Rejects any query that references a blind-index shadow column directly —
    /// selecting it, filtering on it, sorting by it, or aggregating it. The token
    /// is a deterministic HMAC of the encrypted source's plaintext: reading it
    /// enables equality correlation across visible rows, and predicating on it
    /// probes the index with attacker-chosen tokens. The ONLY sanctioned reference
    /// is the server's own equality rewrite, whose injected filter nodes are marked
    /// <c>ServerDerived</c> and skipped by guard collection — so this guard never
    /// fires on a routed <c>_eq</c>/<c>_in</c>, only on client-authored references.
    ///
    /// The write-side counterpart lives in <see cref="EncryptOnWriteMutationTransformer"/>
    /// (rejects direct writes); schema emission omits the column from every type.
    /// This guard is the read-path backstop for programmatic callers (adapters,
    /// intents) that never pass through GraphQL validation.
    /// </summary>
    public sealed class BlindIndexColumnGuard : IFilterTransformer, IColumnReadGuard, IColumnFilterGuard, IModuleNamed
    {
        internal const string DeniedMessage =
            "A requested column may not be read.";

        // Security band, alongside the other crypto guards.
        public int Priority => 40;

        public string ModuleName => "blind-index-column-guard";

        // Contributes no filter — it is a guard only.
        public bool AppliesTo(IDbTable table, QueryTransformContext context) => false;

        public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context) => null;

        public void AssertColumnsReadable(
            IDbTable table, IEnumerable<string> requestedColumns, QueryTransformContext context)
            => AssertNoBlindIndexColumns(table, requestedColumns);

        public void AssertColumnsFilterable(
            IDbTable table, IEnumerable<string> filteredColumns, QueryTransformContext context)
            => AssertNoBlindIndexColumns(table, filteredColumns);

        private static void AssertNoBlindIndexColumns(IDbTable table, IEnumerable<string> columns)
        {
            if (table is null) throw new ArgumentNullException(nameof(table));
            if (columns is null) throw new ArgumentNullException(nameof(columns));

            var targets = BlindIndexColumns.TargetsOf(table);
            if (targets.Count == 0)
                return;

            foreach (var name in columns)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (targets.Contains(name))
                    throw new BifrostExecutionError(DeniedMessage)
                    { ErrorCode = BifrostExecutionError.AccessDeniedCode };
            }
        }
    }
}
