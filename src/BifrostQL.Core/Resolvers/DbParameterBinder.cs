using System;
using System.Collections.Generic;
using System.Data.Common;
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
        /// Binds <c>@columnName</c> parameters from a column → value map.
        /// </summary>
        public static void AddParameters(DbCommand cmd, IReadOnlyDictionary<string, object?> data)
        {
            foreach (var kv in data)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = $"@{kv.Key}";
                p.Value = kv.Value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
        }

        /// <summary>
        /// Binds the <c>@p0/@p1/…</c> parameters carried by a rendered
        /// AdditionalFilter. Their names come from SqlParameterCollection and
        /// cannot collide with the <c>@columnName</c> parameters above.
        /// </summary>
        public static void AddExtraParameters(DbCommand cmd, IReadOnlyList<SqlParameterInfo>? parameters)
        {
            if (parameters == null) return;
            foreach (var info in parameters)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = info.Name;
                p.Value = info.Value ?? DBNull.Value;
                if (info.DbType != null)
                    p.DbType = Enum.Parse<System.Data.DbType>(info.DbType);
                cmd.Parameters.Add(p);
            }
        }

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
