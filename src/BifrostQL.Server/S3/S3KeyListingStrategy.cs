using System.Runtime.CompilerServices;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Server.S3
{
    /// <summary>
    /// How <see cref="S3Listing"/> pages a bucket, decided once per table model from the
    /// declared primary-key types. This is the only place the choice is made.
    ///
    /// <para><see cref="SortsInSql"/>: the dialect renders every key column as the exact text
    /// <see cref="Core.Storage.S3ObjectKeyMap"/> builds, so the database orders and bounds
    /// the keys (keyset seek). Otherwise (<see cref="InMemoryReason"/> says why, for example
    /// a float key no engine prints as .NET's shortest round-trip text) the listing reads
    /// the bucket's raw key values, formats each key in C#, and sorts ordinally, bounded by
    /// <see cref="S3Options.MaxInMemoryListKeys"/>.</para>
    ///
    /// <para>The decision asks <see cref="ISqlDialect.ObjectKeyRefusal"/>, which runs the
    /// same per-type rendering <see cref="ISqlDialect.ObjectKeyExpression"/> does, so the two
    /// cannot drift apart. It never depends on a query outcome.</para>
    /// </summary>
    internal sealed class S3KeyListingStrategy
    {
        private S3KeyListingStrategy(string? inMemoryReason) => InMemoryReason = inMemoryReason;

        /// <summary>Why the keys cannot be ordered in SQL, or null when they can.</summary>
        public string? InMemoryReason { get; }

        /// <summary>True when the database orders the keys (keyset seek).</summary>
        public bool SortsInSql => InMemoryReason == null;

        /// <summary>
        /// The strategy for <paramref name="table"/> of <paramref name="model"/>, computed on
        /// first use and kept with the table model (a schema reload decides again).
        /// </summary>
        public static S3KeyListingStrategy For(IDbModel model, IDbTable table)
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(table);
            return Decided.GetValue(table, t => Decide(model, t));
        }

        private static S3KeyListingStrategy Decide(IDbModel model, IDbTable table)
        {
            var dialect = model.Dialect
                ?? throw new InvalidOperationException(
                    "The model has no SQL dialect; S3 listing needs one to decide its key order strategy.");
            // A placeholder table reference: the refusal depends on column types, not on
            // where the columns live.
            var segments = ObjectKeySegmentColumn.For(table, "t", model, dialect, readTypes: null);
            return new S3KeyListingStrategy(dialect.ObjectKeyRefusal(segments));
        }

        private static readonly ConditionalWeakTable<IDbTable, S3KeyListingStrategy> Decided = new();
    }
}
