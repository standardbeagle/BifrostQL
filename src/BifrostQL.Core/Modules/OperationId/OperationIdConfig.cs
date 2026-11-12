using System.Runtime.CompilerServices;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.OperationId
{
    /// <summary>How a table treats a caller operation id (<see cref="MetadataKeys.OperationId.Mode"/>).</summary>
    public enum OperationIdMode
    {
        /// <summary>The table does not opt in: an id is not read and nothing is recorded.</summary>
        None,

        /// <summary>A write that carries an id records it; a write without one runs as before.</summary>
        Optional,

        /// <summary>A write that carries an id records it; a write without one is refused.</summary>
        Required,
    }

    /// <summary>
    /// The parsed operation-id configuration: the per-table <see cref="OperationIdMode"/> and the
    /// model's store table. The runtime and <see cref="ModelConfigValidator"/> both read it here,
    /// so validation cannot drift from what a write does.
    /// </summary>
    public static class OperationIdConfig
    {
        /// <summary>The mutation-field argument that carries the id.</summary>
        public const string Argument = "_operationId";

        /// <summary>The HTTP header that carries the id for every opted-in field of a GraphQL request.</summary>
        public const string Header = "Idempotency-Key";

        /// <summary>
        /// The user-context key the GraphQL HTTP mount writes the <see cref="Header"/> value to.
        /// The mount sets or removes it on every request, so no identity claim can supply it.
        /// </summary>
        public const string UserContextKey = "bifrost:operation-id";

        /// <summary>The longest id accepted, in characters.</summary>
        public const int MaxLength = 200;

        private static readonly ConditionalWeakTable<IDbTable, StrongBox<OperationIdMode>> ModeByTable = new();

        /// <summary>
        /// The table's mode. Throws <see cref="InvalidOperationException"/> for a value other
        /// than <see cref="MetadataKeys.OperationId.Optional"/> or
        /// <see cref="MetadataKeys.OperationId.Required"/>: a typo must not leave a table that
        /// reads as opted in recording nothing.
        /// </summary>
        public static OperationIdMode ModeOf(IDbTable table)
        {
            ArgumentNullException.ThrowIfNull(table);
            return ModeByTable.GetValue(table, t => new StrongBox<OperationIdMode>(Parse(t))).Value;
        }

        private static OperationIdMode Parse(IDbTable table)
        {
            var raw = table.GetMetadataValue(MetadataKeys.OperationId.Mode);
            if (raw is null)
                return OperationIdMode.None;
            var value = raw.Trim();
            if (string.Equals(value, MetadataKeys.OperationId.Optional, StringComparison.OrdinalIgnoreCase))
                return OperationIdMode.Optional;
            if (string.Equals(value, MetadataKeys.OperationId.Required, StringComparison.OrdinalIgnoreCase))
                return OperationIdMode.Required;
            throw new InvalidOperationException(
                $"'{MetadataKeys.OperationId.Mode}' must be '{MetadataKeys.OperationId.Optional}' or " +
                $"'{MetadataKeys.OperationId.Required}'.");
        }

        /// <summary>
        /// The configured store table, searched among the model's hidden tables (the only place
        /// a valid store lives) and its exposed ones (so the validator can name the mistake).
        /// Null when the key is absent or names no table.
        /// </summary>
        public static IDbTable? FindStore(IDbModel model)
        {
            var name = model.GetMetadataValue(MetadataKeys.OperationId.Store);
            if (string.IsNullOrWhiteSpace(name))
                return null;
            return ModelTableReference.Find(model.HiddenTables, name) ?? ModelTableReference.Find(model.Tables, name);
        }

        /// <summary>
        /// Refuses a write to a <see cref="OperationIdMode.Required"/> table that does not carry
        /// an operation id. Called from <see cref="MutationTransformersWrap.TransformAsync"/>, the
        /// one seam every write path runs, so no door (GraphQL, batch, save, adapter intent) can
        /// write such a table unrecorded. The text names no table.
        /// </summary>
        internal static void EnsureCarriesIdWhenRequired(IDbTable table, bool recordsOperationId)
        {
            if (!recordsOperationId && ModeOf(table) == OperationIdMode.Required)
                throw new Resolvers.BifrostExecutionError("This write requires an operation id.")
                {
                    ErrorCode = OperationIdExecution.RequiredCode,
                };
        }

        /// <summary>Whether <paramref name="table"/> is one of the model's hidden tables.</summary>
        public static bool IsHidden(IDbModel model, IDbTable table)
            => model.HiddenTables.Contains(table);
    }
}
