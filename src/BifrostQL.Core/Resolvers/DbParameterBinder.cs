using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Numerics;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Shared parameter-binding helpers for the mutation/insert/batch/tree-sync
    /// executors, so the bind loops and identity coercion live in one place
    /// instead of being copied per resolver.
    /// </summary>
    internal static class DbParameterBinder
    {
        /// <summary>
        /// Renders a <c>"col"=&lt;placeholder&gt;</c> assignment for an UPDATE SET list,
        /// letting the dialect cast the bound parameter to the column's real type when its
        /// driver would otherwise bind it as text (PostgreSQL). See
        /// <see cref="ISqlDialect.AssignmentPlaceholder"/>.
        /// </summary>
        public static string SetAssignment(ISqlDialect dialect, IDbTable table, string column)
            => $"{dialect.EscapeIdentifier(column)}={dialect.AssignmentPlaceholder(column, ColumnType(table, column))}";

        /// <summary>
        /// Renders the value placeholder for an INSERT VALUES list, with the same
        /// dialect-aware cast as <see cref="SetAssignment"/>.
        /// </summary>
        public static string ValuePlaceholder(ISqlDialect dialect, IDbTable table, string column)
            => dialect.AssignmentPlaceholder(column, ColumnType(table, column));

        private static string? ColumnType(IDbTable table, string column)
            => table.ColumnLookup.TryGetValue(column, out var c) ? c.DataType : null;

        /// <summary>
        /// Resolves a mutation-input key to its database column name. Client input
        /// is keyed by GraphQL field name (which can be sanitized/prefixed and so
        /// differ from the real column), while transformer-added keys (tenant,
        /// soft-delete, audit) are already database names. Map GraphQL → DB and
        /// pass through anything already a database column, so downstream SQL and
        /// parameters use one consistent name space.
        /// </summary>
        public static string ToDbColumnName(IDbTable table, string key)
            => table.GraphQlLookup.TryGetValue(key, out var col) ? col.DbName : key;

        /// <summary>
        /// Rekeys a mutation data map from GraphQL field names to database column
        /// names via <see cref="ToDbColumnName"/>.
        /// </summary>
        public static Dictionary<string, object?> ToDbColumnKeys(IDbTable table, IReadOnlyDictionary<string, object?> data)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in data)
                result[ToDbColumnName(table, kv.Key)] = kv.Value;
            return result;
        }

        /// <summary>
        /// True when the given mutation-input key maps to a primary-key column,
        /// tolerant of both GraphQL field names and raw database column names.
        /// </summary>
        public static bool IsPrimaryKeyColumn(IDbTable table, string key)
        {
            if (table.GraphQlLookup.TryGetValue(key, out var byGraphQl))
                return byGraphQl.IsPrimaryKey;
            return table.ColumnLookup.TryGetValue(key, out var byDb) && byDb.IsPrimaryKey;
        }

        /// <summary>
        /// Binds <c>@columnName</c> parameters from a column → value map. Column
        /// names that are not valid parameter identifiers (spaces, punctuation)
        /// are sanitized via <see cref="SqlParameterNames.Sanitize"/>, matching
        /// the placeholders <see cref="ISqlDialect.AssignmentPlaceholder"/>
        /// renders into the SQL text.
        /// </summary>
        public static void AddParameters(DbCommand cmd, IReadOnlyDictionary<string, object?> data)
        {
            foreach (var kv in data)
                Bind(cmd, $"@{SqlParameterNames.Sanitize(kv.Key)}", kv.Value, dbType: null);
        }

        /// <summary>
        /// Binds the <c>@p0/@p1/…</c> parameters carried by a rendered
        /// AdditionalFilter. Their names come from SqlParameterCollection, whose
        /// generated shape <see cref="SqlParameterNames"/> reserves against
        /// column-derived names, so they cannot collide with the
        /// <c>@columnName</c> parameters above.
        /// </summary>
        public static void AddExtraParameters(DbCommand cmd, IReadOnlyList<SqlParameterInfo>? parameters)
        {
            if (parameters == null) return;
            foreach (var info in parameters)
                Bind(cmd, info.Name, info.Value, info.DbType);
        }

        /// <summary>
        /// Binds one parameter, refusing to bind a name already on the command.
        /// Last-write-wins (or provider-defined first-wins) on a duplicate name is
        /// how a client-supplied column value silently replaced a
        /// transformer-injected predicate's value — a cross-tenant read/write with
        /// no error anywhere. The namespaces are now structurally disjoint, so a
        /// duplicate means an invariant broke: fail the request rather than emit
        /// SQL whose predicate is not the one the transformer built.
        /// </summary>
        private static void Bind(DbCommand cmd, string name, object? value, string? dbType)
        {
            if (cmd.Parameters.Contains(name))
                throw new BifrostExecutionError(
                    $"Parameter '{name}' is already bound on this command. Refusing to rebind it, " +
                    "because a duplicate parameter name silently substitutes one bound value for " +
                    "another and can defeat a transformer-injected security predicate.");

            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = ToProviderValue(value) ?? DBNull.Value;
            if (dbType != null)
                p.DbType = Enum.Parse<System.Data.DbType>(dbType);
            cmd.Parameters.Add(p);
        }

        /// <summary>
        /// Converts a value to something an ADO.NET provider can bind.
        ///
        /// GraphQL's <c>BigInt</c> scalar hands us a <see cref="BigInteger"/>, which no
        /// provider maps — binding one throws "No mapping exists from object type
        /// System.Numerics.BigInteger to a known managed provider native type" and fails
        /// the request AFTER validation passed. That made every filter on a bigint
        /// column fail at the database, whatever value was supplied.
        ///
        /// A value outside <see cref="long"/> range cannot be stored in a bigint column,
        /// so it is bound as text: it matches no row (the truthful answer) instead of
        /// being truncated into a different, valid-looking key.
        /// </summary>
        private static object? ToProviderValue(object? value) => value switch
        {
            BigInteger big when big >= long.MinValue && big <= long.MaxValue => (long)big,
            BigInteger big => big.ToString(CultureInfo.InvariantCulture),
            _ => value,
        };

        /// <summary>
        /// Identity values come back as <see cref="decimal"/> from some providers;
        /// coerce to <see cref="long"/> so downstream consumers see a stable type.
        /// </summary>
        public static object? HandleDecimals(object? value) => value switch
        {
            null => null,
            decimal d => Convert.ToInt64(d),
            _ => value,
        };
    }
}
