using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.OperationId;
using GraphQL;
using GraphQL.Resolvers;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// GraphQL entry point for the batch mutation field. Argument parsing and
    /// per-profile transformer filtering live here; execution — one transaction for
    /// the whole batch, transformer chain and hooks per action — is delegated to
    /// <see cref="BatchMutationPipeline"/>, the seam shared with the batch
    /// mutation-intent path, so no entry point can reach SQL without it.
    /// </summary>
    public sealed class DbTableBatchResolver : IBifrostResolver, IFieldResolver
    {
        private readonly IDbTable _table;

        public DbTableBatchResolver(IDbTable table)
        {
            _table = table;
        }

        public async ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            var bifrost = new BifrostContextAdapter(context);
            // Unreachable through the generated schema (no batch field is emitted for a
            // history target), but guarded at the execution seam too — before argument
            // parsing, so even an empty forged batch is refused. The pipeline re-guards.
            TableMutationPipeline.GuardNotHistoryTarget(_table, bifrost.Model);
            // An operation id covers the whole batch: one record, written in the batch's own
            // transaction, so the batch and its record commit together or not at all.
            var operationId = OperationIdExecution.Resolve(context, _table);
            var actions = context.GetArgument<List<Dictionary<string, object?>>>("actions");
            if (actions == null || actions.Count == 0)
                return 0;

            var parsed = new List<BatchMutationPipeline.BatchAction>(actions.Count);
            foreach (var action in actions)
            {
                if (MutationActionSelector.TryFromAction(action, out var which, out var data))
                    parsed.Add(new BatchMutationPipeline.BatchAction(which, data));
            }

            // Module mutation arguments (e.g. _hardDelete) are declared on the batch field and
            // apply to every delete action in the batch, mirroring the single-row resolver.
            var moduleArguments = ModuleApiRegistry.CaptureMutationArguments(context, _table);
            // Filter by the request's active profile so a batch write applies the same
            // per-profile module set a read (and a single-row write) does. Fail-closed:
            // security/data-integrity transformers below the application floor are retained.
            var transformers = BifrostProfileRegistry.FilterBy(
                context.RequestServices!.GetRequiredService<IMutationTransformers>(), context.UserContext);

            MutationPipelineContext PipelineContext(OperationWriteScope? writeScope) => new()
            {
                WriteScope = writeScope,
                // This resolver opens a write scope only for a batch carrying an operation id,
                // so the scope is the id's evidence.
                RecordsOperationId = writeScope is not null,
                Model = bifrost.Model,
                ConnFactory = bifrost.ConnFactory,
                Transformers = transformers,
                UserContext = context.UserContext,
                Services = context.RequestServices,
                ModuleArguments = moduleArguments,
                CancellationToken = context.CancellationToken,
            };

            if (operationId is null)
                return await BatchMutationPipeline.ExecuteBatchAsync(_table, parsed, PipelineContext(writeScope: null));

            return await OperationIdExecution.ExecuteAsync(
                bifrost.Model, bifrost.ConnFactory, context.UserContext, operationId,
                Fingerprint(actions, moduleArguments),
                () =>
                {
                    foreach (var action in parsed)
                        OperationIdExecution.AuthorizeReplay(_table, action.Action, action.Data, null, context.UserContext);
                },
                async scope => await BatchMutationPipeline.ExecuteBatchAsync(_table, parsed, PipelineContext(scope)),
                context.CancellationToken);
        }

        /// <summary>
        /// The fingerprint a batch with an operation id is recorded under: the <c>actions</c>
        /// list as the client sent it, in order, and every module argument the field carries
        /// (the operation id itself excepted). A batch addresses no single key, so its target
        /// key is empty.
        /// </summary>
        private OperationFingerprint Fingerprint(
            List<Dictionary<string, object?>> actions, IReadOnlyDictionary<string, object?> moduleArguments)
        {
            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal) { ["actions"] = actions };
            OperationIdExecution.AddModuleArguments(arguments, moduleArguments);
            return OperationFingerprint.Of(_table, "batch", new Dictionary<string, object?>(), arguments);
        }

        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            return ResolveAsync(new BifrostFieldContextAdapter(context));
        }
    }
}
