using System;
using System.Linq;

namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Resolves a metadata table reference (<c>outbox-table</c>, <c>history-table</c>) to a
    /// table in the model. One definition, shared by model-load validation and the runtime
    /// writers, so a reference that validated at load resolves to the SAME table at write
    /// time — a validator and a writer that disagreed would report a healthy config and
    /// then write into the wrong table.
    /// </summary>
    public static class ModelTableReference
    {
        /// <summary>
        /// Finds the table named by a <c>schema.name</c> (or bare <c>name</c>) reference,
        /// case-insensitively; null when no table matches.
        ///
        /// A schema-qualified reference matches on schema AND name with NO name-only
        /// fallback: falling back would silently bind to a same-named table in a different
        /// schema and write there while reporting success — precisely the misconfiguration
        /// the existence check exists to catch. A bare reference matches by name alone
        /// (there is no schema to honor).
        /// </summary>
        public static IDbTable? Find(IDbModel model, string qualified)
        {
            if (model is null)
                throw new ArgumentNullException(nameof(model));
            if (string.IsNullOrWhiteSpace(qualified))
                return null;

            var trimmed = qualified.Trim();
            var dot = trimmed.LastIndexOf('.');
            if (dot > 0)
            {
                var schema = trimmed[..dot];
                var name = trimmed[(dot + 1)..];
                return model.Tables.FirstOrDefault(t =>
                    string.Equals(t.TableSchema, schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.DbName, name, StringComparison.OrdinalIgnoreCase));
            }

            return model.Tables.FirstOrDefault(t =>
                string.Equals(t.DbName, trimmed, StringComparison.OrdinalIgnoreCase));
        }
    }
}
