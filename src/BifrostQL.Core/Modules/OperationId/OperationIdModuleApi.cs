using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.OperationId
{
    /// <summary>
    /// The operation-id module's client-facing API: <c>_operationId: String</c> on the mutation
    /// fields of a table that opts in with <see cref="MetadataKeys.OperationId.Mode"/>. A table
    /// that does not opt in has no such argument, so its SDL is unchanged.
    /// </summary>
    public sealed class OperationIdModuleApi : IModuleApi
    {
        /// <summary>The context key the captured argument value is stored under.</summary>
        public const string ContextKey = "operation_id";

        public string ModuleName => MetadataKeys.OperationId.Mode;

        public IEnumerable<ModuleArgument> GetQueryArguments(IDbTable table) => Array.Empty<ModuleArgument>();

        public IEnumerable<ModuleArgument> GetMutationArguments(IDbTable table)
        {
            if (OperationIdConfig.ModeOf(table) == OperationIdMode.None)
                yield break;
            yield return new ModuleArgument(OperationIdConfig.Argument, "String", ContextKey,
                "The caller's operation id: the write and its result are recorded under it.");
        }
    }
}
