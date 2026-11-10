using BifrostQL.Core.Model;
using GraphQL;

namespace BifrostQL.Core.Resolvers
{
    public interface IBifrostContext
    {
        IDbModel Model { get; }
        ISqlExecutionManager Executor { get; }
        IDbConnFactory ConnFactory { get; }
        IDictionary<string, object?> UserContext { get; }
        IServiceProvider? RequestServices { get; }

        /// <summary>
        /// The endpoint path this request was served on, or null when the host's inputs do not
        /// carry one. Hand it to any intent built from this request's model so the executor
        /// resolves the same database on a multi-endpoint host.
        /// </summary>
        string? Endpoint => null;
    }

    public sealed class BifrostContextAdapter : IBifrostContext
    {
        public IDbModel Model { get; }
        public ISqlExecutionManager Executor { get; }
        public IDbConnFactory ConnFactory { get; }
        public IDictionary<string, object?> UserContext { get; }
        public IServiceProvider? RequestServices { get; }
        public string? Endpoint { get; }

        public BifrostContextAdapter(IBifrostFieldContext context)
        {
            ConnFactory = (IDbConnFactory)(context.InputExtensions["connFactory"]
                ?? throw new InvalidDataException("connection factory is not configured"));
            Model = (IDbModel)(context.InputExtensions["model"]
                ?? throw new InvalidDataException("database model is not configured"));
            Executor = (ISqlExecutionManager)(context.InputExtensions["tableReaderFactory"]
                ?? throw new InvalidDataException("tableReaderFactory not configured"));
            UserContext = context.UserContext;
            RequestServices = context.RequestServices;
            Endpoint = context.InputExtensions.TryGetValue(IntentEndpointResolver.EndpointInputKey, out var endpoint)
                ? endpoint as string
                : null;
        }

        /// <summary>
        /// Transition constructor: wraps IResolveFieldContext for resolvers not yet ported to IBifrostFieldContext.
        /// Will be removed once all resolvers use BifrostFieldContextAdapter at their boundary.
        /// </summary>
        public BifrostContextAdapter(IResolveFieldContext context)
            : this(new BifrostFieldContextAdapter(context))
        {
        }
    }

    /// <summary>
    /// Adapts GraphQL.NET's IResolveFieldContext to IBifrostFieldContext.
    /// Used at the resolver boundary to bridge GraphQL.NET types into the Bifrost data layer.
    /// </summary>
    public sealed class BifrostFieldContextAdapter : IBifrostFieldContext
    {
        private readonly IResolveFieldContext _inner;

        public BifrostFieldContextAdapter(IResolveFieldContext context)
        {
            _inner = context ?? throw new ArgumentNullException(nameof(context));
        }

        public string FieldName => _inner.FieldAst.Name.StringValue;
        public string? FieldAlias => _inner.FieldAst.Alias?.Name?.StringValue;
        public object? Source => _inner.Source;
        public IReadOnlyList<object> Path =>
            _inner.Path as IReadOnlyList<object> ?? _inner.Path?.ToList() ?? (IReadOnlyList<object>)Array.Empty<object>();
        public IDictionary<string, object?> UserContext =>
            _inner.UserContext as IDictionary<string, object?> ?? new Dictionary<string, object?>();
        public IServiceProvider? RequestServices => _inner.RequestServices;
        public bool HasSubFields => _inner.SubFields != null;
        public object Document => _inner.Document;
        public object Variables => _inner.Variables;
        public IDictionary<string, object?> InputExtensions =>
            _inner.InputExtensions as IDictionary<string, object?> ?? new Dictionary<string, object?>(_inner.InputExtensions);
        public CancellationToken CancellationToken => _inner.CancellationToken;

        public bool HasArgument(string name) => _inner.HasArgument(name);
        public T? GetArgument<T>(string name) => _inner.GetArgument<T>(name);
    }
}
