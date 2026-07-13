using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
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
            var actions = context.GetArgument<List<Dictionary<string, object?>>>("actions");
            if (actions == null || actions.Count == 0)
                return 0;

            var parsed = new List<BatchMutationPipeline.BatchAction>(actions.Count);
            foreach (var action in actions)
            {
                if (MutationActionSelector.TryFromAction(action, out var which, out var data))
                    parsed.Add(new BatchMutationPipeline.BatchAction(which, data));
            }

            var ctx = new MutationPipelineContext
            {
                Model = bifrost.Model,
                ConnFactory = bifrost.ConnFactory,
                // Filter by the request's active profile so a batch write applies the same
                // per-profile module set a read (and a single-row write) does. Fail-closed:
                // security/data-integrity transformers below the application floor are retained.
                Transformers = BifrostProfileRegistry.FilterBy(
                    context.RequestServices!.GetRequiredService<IMutationTransformers>(), context.UserContext),
                UserContext = context.UserContext,
                Services = context.RequestServices,
                // Module mutation arguments (e.g. _hardDelete) are declared on the
                // batch field and apply to every delete action in the batch, mirroring
                // the single-row resolver.
                ModuleArguments = ModuleApiRegistry.CaptureMutationArguments(context, _table),
                CancellationToken = context.CancellationToken,
            };

            return await BatchMutationPipeline.ExecuteBatchAsync(_table, parsed, ctx);
        }

        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            return ResolveAsync(new BifrostFieldContextAdapter(context));
        }
    }
}
