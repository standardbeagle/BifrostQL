using BifrostQL.Core.Auth;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.OperationId;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Core.Operations;

/// <summary>
/// The resolver of one exposed server operation. It enforces the operation's declarative
/// policy BEFORE the operation's code runs — the grant list from <c>operations</c> and the
/// <c>operations-tenant</c> rule — so no operation can skip them, then runs the operation:
/// a query directly, a mutation inside one <see cref="OperationWriteScope"/>, recorded under the
/// request's operation id when <c>:root</c> sets <c>operation-id</c>.
/// Wired through <see cref="BifrostDispatcher"/>, so its errors take the same path to the
/// wire as every generated field's.
/// </summary>
internal sealed class OperationResolver : IBifrostResolver
{
    private static readonly IMutationTransformers NoTransformers = new MutationTransformersWrap();
    private readonly ExposedOperation _exposed;
    private static readonly PolicyEvaluator Policy = new();

    private readonly IReadOnlyList<string> _argumentNames;

    public OperationResolver(ExposedOperation exposed)
    {
        _exposed = exposed;
        _argumentNames = ArgumentNamesOf(exposed.Operation);
    }

    public async ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
    {
        var request = new BifrostContextAdapter(context);
        var operation = _exposed.Operation;
        Authorize(request);

        if (operation.Kind == BifrostOperationKind.Query)
            return await operation.ExecuteAsync(new BifrostOperationContext(
                operation, context, request.Model, request.ConnFactory, request.Executor, NoTransformers, scope: null));

        // The same per-profile transformer set a generated mutation on this request gets.
        var transformers = BifrostProfileRegistry.FilterBy(
            context.RequestServices?.GetService<IMutationTransformers>()
                ?? throw new BifrostExecutionError("Mutation operations require the mutation transformer chain."),
            context.UserContext);

        var operationId = OperationIdExecution.ResolveForOperation(context, request.Model);
        if (operationId is null)
            return await OperationWriteScope.RunAsync(request.ConnFactory, scope => Run(scope, recordsOperationId: false),
                context.CancellationToken);

        // Authorize already ran above, on this request, so a replay passes the live grant check.
        return await OperationIdExecution.ExecuteAsync(
            request.Model, request.ConnFactory, context.UserContext, operationId, OperationIdExecution.FieldOf(context),
            OperationFingerprint.OfOperation(operation.Name, ArgumentsOf(context)),
            authorizeReplay: () => { },
            scope => Run(scope, recordsOperationId: true),
            context.CancellationToken);

        Task<object?> Run(OperationWriteScope scope, bool recordsOperationId) => operation.ExecuteAsync(
                new BifrostOperationContext(
                    operation, context, request.Model, request.ConnFactory, request.Executor, transformers, scope,
                    recordsOperationId))
            .AsTask();
    }

    /// <summary>Every argument the field carries, by name, for the run's fingerprint.</summary>
    private Dictionary<string, object?> ArgumentsOf(IBifrostFieldContext context)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in _argumentNames)
        {
            if (context.HasArgument(name))
                arguments[name] = context.GetArgument<object?>(name);
        }
        return arguments;
    }

    /// <summary>The argument names <see cref="IBifrostOperation.Signature"/> declares.</summary>
    private static IReadOnlyList<string> ArgumentNamesOf(IBifrostOperation operation)
    {
        var document = GraphQLParser.Parser.Parse($"type Operation {{ {operation.Name}{operation.Signature} }}");
        var field = document.Definitions.OfType<GraphQLParser.AST.GraphQLObjectTypeDefinition>().Single().Fields!.Items.Single();
        return field.Arguments?.Items.Select(a => a.Name.StringValue).ToArray() ?? Array.Empty<string>();
    }

    private void Authorize(BifrostContextAdapter request)
    {
        var identity = PolicyIdentity.FromUserContext(
            request.UserContext, TenantFilterTransformer.ResolveTenantContextKey(request.Model));
        if (!Policy.HoldsAnyGrant(identity, _exposed.Grants, PolicyEvaluator.AdminRoleOf(request.Model)))
            throw new BifrostExecutionError(
                $"Access denied: the caller may not run operation '{_exposed.Operation.Name}'.")
            { ErrorCode = BifrostExecutionError.AccessDeniedCode };
        if (_exposed.TenantRequired && string.IsNullOrWhiteSpace(identity.TenantId))
            throw new BifrostExecutionError(
                $"Access denied: operation '{_exposed.Operation.Name}' requires a tenant context.")
            { ErrorCode = BifrostExecutionError.AccessDeniedCode };
    }
}
