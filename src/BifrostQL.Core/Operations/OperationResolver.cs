using BifrostQL.Core.Auth;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Core.Operations;

/// <summary>
/// The resolver of one exposed server operation. It enforces the operation's declarative
/// policy BEFORE the operation's code runs — the grant list from <c>operations</c> and the
/// <c>operations-tenant</c> rule — so no operation can skip them, then runs the operation:
/// a query directly, a mutation inside one <see cref="OperationWriteScope"/>.
/// Wired through <see cref="BifrostDispatcher"/>, so its errors take the same path to the
/// wire as every generated field's.
/// </summary>
internal sealed class OperationResolver : IBifrostResolver
{
    private static readonly IMutationTransformers NoTransformers = new MutationTransformersWrap();
    private readonly ExposedOperation _exposed;
    private readonly PolicyEvaluator _policy = new();

    public OperationResolver(ExposedOperation exposed) => _exposed = exposed;

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
        return await OperationWriteScope.RunAsync(request.ConnFactory, scope => operation.ExecuteAsync(
                new BifrostOperationContext(
                    operation, context, request.Model, request.ConnFactory, request.Executor, transformers, scope))
            .AsTask(), context.CancellationToken);
    }

    private void Authorize(BifrostContextAdapter request)
    {
        var identity = PolicyIdentity.FromUserContext(
            request.UserContext, TenantFilterTransformer.ResolveTenantContextKey(request.Model));
        if (!_policy.HoldsAnyGrant(identity, _exposed.Grants))
            throw new BifrostExecutionError(
                $"Access denied: the caller may not run operation '{_exposed.Operation.Name}'.")
            { ErrorCode = BifrostExecutionError.AccessDeniedCode };
        if (_exposed.TenantRequired && string.IsNullOrWhiteSpace(identity.TenantId))
            throw new BifrostExecutionError(
                $"Access denied: operation '{_exposed.Operation.Name}' requires a tenant context.")
            { ErrorCode = BifrostExecutionError.AccessDeniedCode };
    }
}
