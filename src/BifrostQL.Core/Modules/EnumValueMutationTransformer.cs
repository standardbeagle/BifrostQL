using System;
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules
{
    /// <summary>
    /// Rewrites enum-named input values to their stored DB values for enum columns
    /// on insert/update/upsert and delete-by-key. An unknown enum name produces an
    /// error result.
    /// </summary>
    public sealed class EnumValueMutationTransformer : IMutationTransformer, IModuleNamed
    {
        public string ModuleName => "enum-value";

        // Data-filtering band (100-199): after security checks, rewrites input values.
        public int Priority => 150;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
        {
            if (table is null) throw new ArgumentNullException(nameof(table));
            if (context is null) throw new ArgumentNullException(nameof(context));
            return mutationType is MutationType.Insert or MutationType.Update or MutationType.Delete
                && context.Model.EnumColumns?.HasAnyFor(table.DbName) == true;
        }

        public MutationTransformResult Transform(
            IDbTable table,
            MutationType mutationType,
            Dictionary<string, object?> data,
            MutationTransformContext context)
        {
            if (table is null) throw new ArgumentNullException(nameof(table));
            if (data is null) throw new ArgumentNullException(nameof(data));
            if (context is null) throw new ArgumentNullException(nameof(context));

            var map = context.Model.EnumColumns!;
            var errors = new List<string>();
            var output = new Dictionary<string, object?>(data);

            foreach (var kv in data)
            {
                var column = table.Columns.FirstOrDefault(
                    c => string.Equals(c.GraphQlName, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (column == null)
                    continue;
                if (!map.TryGetEnumType(table.DbName, column.ColumnName, out _))
                    continue;
                if (kv.Value is string name)
                {
                    var dbValue = map.NameToValue(table.DbName, column.ColumnName, name);
                    if (dbValue == null)
                    {
                        errors.Add($"'{name}' is not a valid {column.GraphQlName} value.");
                        continue;
                    }
                    output[kv.Key] = dbValue;
                }
            }

            return new MutationTransformResult
            {
                MutationType = mutationType,
                Data = output,
                Errors = errors.Count > 0 ? errors.ToArray() : Array.Empty<string>(),
            };
        }
    }
}
