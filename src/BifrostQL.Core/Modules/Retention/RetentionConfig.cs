using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using BifrostQL.Core.Model;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Modules.Retention
{
    /// <summary>
    /// Parsed per-table data-retention configuration. Built from the <c>retain</c> /
    /// <c>ttl</c> table metadata. A table with neither key returns <see cref="None"/> — there
    /// is NO implicit default retention window ever, so a table that does not opt in is never
    /// purged. Mirrors the <c>HistoryConfig.FromTable</c> / <c>FtsConfig.FromTable</c> factory
    /// shape: a per-table metadata read, cached by table identity, that fails fast on a config
    /// that would otherwise silently do the wrong thing.
    ///
    /// The two keys have deliberately different blast radii, and the type reflects it:
    /// <list type="bullet">
    ///   <item><b>retain</b> = HARD-PURGE of rows that are ALREADY soft-deleted, anchored on the
    ///   table's <see cref="MetadataKeys.SoftDelete.Column"/>. Because the anchor is the
    ///   soft-delete timestamp, a live row (anchor IS NULL) can never match — <c>retain</c>
    ///   ALONE CAN NEVER PURGE A LIVE ROW. This is structural, not a runtime filter the
    ///   scheduler must remember to apply; the anchor is fixed to the soft-delete column and
    ///   cannot be redirected.</item>
    ///   <item><b>ttl</b> = expiry of LIVE rows, anchored on an EXPLICIT timestamp column named
    ///   with the <c>after &lt;column&gt;</c> clause. Destroying live data is destructive, so it is
    ///   a separate, explicitly-declared key — never an emergent consequence of <c>retain</c>.</item>
    /// </list>
    ///
    /// This slice establishes the metadata contract + fail-fast validation only; the purge
    /// scheduler that acts on this config is a later Retention sub-task.
    /// </summary>
    public sealed class RetentionConfig
    {
        /// <summary>The no-retention sentinel returned for tables that do not opt in.</summary>
        public static readonly RetentionConfig None = new(
            retainWindow: null, retainAnchorColumn: null,
            ttlWindow: null, ttlAnchorColumn: null);

        private RetentionConfig(
            TimeSpan? retainWindow, string? retainAnchorColumn,
            TimeSpan? ttlWindow, string? ttlAnchorColumn)
        {
            RetainWindow = retainWindow;
            RetainAnchorColumn = retainAnchorColumn;
            TtlWindow = ttlWindow;
            TtlAnchorColumn = ttlAnchorColumn;
        }

        /// <summary>
        /// Whether the table hard-purges already-soft-deleted rows (the <c>retain</c> opt-in).
        /// </summary>
        public bool PurgesSoftDeleted => RetainWindow.HasValue;

        /// <summary>
        /// The <c>retain</c> window: how long an already-soft-deleted row is kept before it is
        /// hard-purged, measured from the <see cref="RetainAnchorColumn"/> timestamp. Null when
        /// the table does not opt into <c>retain</c>.
        /// </summary>
        public TimeSpan? RetainWindow { get; }

        /// <summary>
        /// The column the <c>retain</c> window is measured against — ALWAYS the table's
        /// <see cref="MetadataKeys.SoftDelete.Column"/>, canonicalized to database casing. Null
        /// when the table does not opt into <c>retain</c>. This is fixed to the soft-delete
        /// column by construction so <c>retain</c> can never anchor on (and therefore never
        /// purge) a live row.
        /// </summary>
        public string? RetainAnchorColumn { get; }

        /// <summary>
        /// Whether the table expires LIVE rows (the <c>ttl</c> opt-in). This is the destructive,
        /// explicitly-declared live-row purge — never implied by <see cref="PurgesSoftDeleted"/>.
        /// </summary>
        public bool ExpiresLiveRows => TtlWindow.HasValue;

        /// <summary>
        /// The <c>ttl</c> window: how long a live row is kept before it expires, measured from
        /// the <see cref="TtlAnchorColumn"/> timestamp. Null when the table does not opt into
        /// <c>ttl</c>.
        /// </summary>
        public TimeSpan? TtlWindow { get; }

        /// <summary>
        /// The explicit column the <c>ttl</c> window is measured against (the <c>after
        /// &lt;column&gt;</c> clause), canonicalized to database casing. Null when the table does
        /// not opt into <c>ttl</c>.
        /// </summary>
        public string? TtlAnchorColumn { get; }

        /// <summary>Whether the table opts into any retention purge at all.</summary>
        public bool HasRetention => PurgesSoftDeleted || ExpiresLiveRows;

        /// <summary>
        /// The keyword separating a <c>ttl</c> duration from its explicit anchor column.
        /// </summary>
        public const string AfterKeyword = "after";

        /// <summary>
        /// Database types accepted for a retention anchor column across the supported dialects.
        /// The anchor is compared against a cutoff instant, so it must be a timestamp; anchoring
        /// a purge on a non-timestamp column is a configuration error. Mirrors
        /// <c>ChatConfig.DateTimeColumnTypes</c>.
        /// </summary>
        public static readonly IReadOnlySet<string> TimestampColumnTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "datetime", "datetime2", "smalldatetime", "datetimeoffset", "date",
                "timestamp", "timestamptz",
                "timestamp with time zone", "timestamp without time zone",
            };

        // Cached per table instance for the same reason as HistoryConfig/FtsConfig: schema
        // generation and ModelConfigValidator both ask for it, and the model is immutable after
        // load. A parse that throws (invalid duration/anchor) is never cached — each caller sees
        // the failure.
        private static readonly ConditionalWeakTable<IDbTable, RetentionConfig> ConfigByTable = new();

        /// <summary>
        /// Parses the retention config for a single table (cached per table instance). Throws
        /// <see cref="InvalidOperationException"/> when a duration is malformed, when <c>retain</c>
        /// is set on a table with no <see cref="MetadataKeys.SoftDelete.Column"/> (it would have
        /// no soft-deleted rows to ever purge and must not touch live rows), when <c>ttl</c> omits
        /// its required <c>after &lt;column&gt;</c> anchor, or when either anchor column does not exist
        /// or is not a timestamp type — each of which would otherwise silently either purge nothing
        /// (a compliance gap) or, worse, be built to purge the wrong rows.
        /// </summary>
        public static RetentionConfig FromTable(IDbTable table)
        {
            if (table is null)
                throw new ArgumentNullException(nameof(table));

            return ConfigByTable.GetValue(table, Parse);
        }

        private static RetentionConfig Parse(IDbTable table)
        {
            var retainRaw = table.GetMetadataValue(MetadataKeys.Retention.Retain);
            var ttlRaw = table.GetMetadataValue(MetadataKeys.Retention.Ttl);

            var hasRetain = !string.IsNullOrWhiteSpace(retainRaw);
            var hasTtl = !string.IsNullOrWhiteSpace(ttlRaw);
            if (!hasRetain && !hasTtl)
                return None;

            TimeSpan? retainWindow = null;
            string? retainAnchor = null;
            if (hasRetain)
                (retainWindow, retainAnchor) = ParseRetain(table, retainRaw!);

            TimeSpan? ttlWindow = null;
            string? ttlAnchor = null;
            if (hasTtl)
                (ttlWindow, ttlAnchor) = ParseTtl(table, ttlRaw!);

            return new RetentionConfig(retainWindow, retainAnchor, ttlWindow, ttlAnchor);
        }

        // retain: a BARE duration. Its anchor is fixed to the soft-delete column so it can only
        // ever purge already-soft-deleted rows; an 'after' clause is rejected precisely to keep
        // it from being redirected onto a live-row column (that is what ttl is for).
        private static (TimeSpan Window, string Anchor) ParseRetain(IDbTable table, string raw)
        {
            var parts = SplitWords(raw);
            if (parts.Length != 1)
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Retention.Retain}' on '{table.TableSchema}.{table.DbName}' must be a bare " +
                    $"duration (e.g. '90d', '12h'); it anchors on the soft-delete column and takes no " +
                    $"'{AfterKeyword} <column>' clause. Live-row expiry is the separate " +
                    $"'{MetadataKeys.Retention.Ttl}' key.");

            var window = ParseDuration(table, MetadataKeys.Retention.Retain, parts[0]);

            var softDeleteColumn = table.GetMetadataValue(MetadataKeys.SoftDelete.Column);
            if (string.IsNullOrWhiteSpace(softDeleteColumn))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Retention.Retain}' on '{table.TableSchema}.{table.DbName}' requires a " +
                    $"'{MetadataKeys.SoftDelete.Column}' column: retain hard-purges rows that are already " +
                    $"soft-deleted (anchored on that column) and must never purge a live row. Configure " +
                    $"soft-delete, or use '{MetadataKeys.Retention.Ttl}' for live-row expiry.");

            var anchor = ResolveTimestampAnchor(table, MetadataKeys.Retention.Retain, softDeleteColumn.Trim());
            return (window, anchor);
        }

        // ttl: '<duration> after <column>'. The explicit anchor is REQUIRED — expiring live rows
        // is destructive, so the config must name exactly which timestamp column authorizes it.
        private static (TimeSpan Window, string Anchor) ParseTtl(IDbTable table, string raw)
        {
            var parts = SplitWords(raw);
            if (parts.Length != 3
                || !string.Equals(parts[1], AfterKeyword, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Retention.Ttl}' on '{table.TableSchema}.{table.DbName}' must be " +
                    $"'<duration> {AfterKeyword} <column>' (e.g. '30d {AfterKeyword} created_at'); ttl expires " +
                    $"LIVE rows and requires an explicit timestamp anchor column.");

            var window = ParseDuration(table, MetadataKeys.Retention.Ttl, parts[0]);
            var anchor = ResolveTimestampAnchor(table, MetadataKeys.Retention.Ttl, parts[2]);
            return (window, anchor);
        }

        // Supported units: days ('d') and hours ('h'). A positive integer magnitude only — zero
        // and negative windows would purge immediately or never, both configuration errors.
        private static TimeSpan ParseDuration(IDbTable table, string key, string token)
        {
            var reason = new Func<string, InvalidOperationException>(detail =>
                new InvalidOperationException(
                    $"'{key}' on '{table.TableSchema}.{table.DbName}' has an invalid duration '{token}': {detail} " +
                    "Use a positive integer followed by 'd' (days) or 'h' (hours), e.g. '90d' or '12h'."));

            if (token.Length < 2)
                throw reason("too short.");

            var unit = char.ToLowerInvariant(token[^1]);
            var magnitude = token[..^1];
            if (!int.TryParse(magnitude, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
                throw reason("the magnitude must be a positive integer.");

            // A magnitude that parses as an int can still overflow TimeSpan (max ~10.7M
            // days). Rethrow as the same friendly InvalidOperationException so the
            // model-load validator (which filters InvalidOperationException) catches it
            // and the purge sweep's per-table guard skips the table fail-closed, rather
            // than letting a raw OverflowException escape either path.
            try
            {
                return unit switch
                {
                    'd' => TimeSpan.FromDays(value),
                    'h' => TimeSpan.FromHours(value),
                    _ => throw reason($"'{token[^1]}' is not a recognized unit."),
                };
            }
            catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
            {
                throw reason("the window is too large to represent.");
            }
        }

        // Resolves an anchor column, enforcing existence AND timestamp-typedness — a purge
        // measured against a missing or non-timestamp column would compare a cutoff instant to
        // nothing (or to text), silently never matching the rows it was meant to clear.
        private static string ResolveTimestampAnchor(IDbTable table, string key, string columnName)
        {
            if (!table.ColumnLookup.TryGetValue(columnName, out var column))
                throw new InvalidOperationException(
                    $"'{key}' on '{table.TableSchema}.{table.DbName}' anchors on column '{columnName}' which does " +
                    "not exist on the table; a retention purge measured against a missing column would purge nothing.");

            if (!TimestampColumnTypes.Contains(StringNormalizer.NormalizeType(column.DataType)))
                throw new InvalidOperationException(
                    $"'{key}' on '{table.TableSchema}.{table.DbName}' anchors on column '{column.ColumnName}' of " +
                    $"type '{column.DataType}', but a retention anchor must be a timestamp type " +
                    $"({string.Join(", ", TimestampColumnTypes.OrderBy(t => t, StringComparer.Ordinal))}).");

            return column.ColumnName;
        }

        private static string[] SplitWords(string raw) =>
            raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
