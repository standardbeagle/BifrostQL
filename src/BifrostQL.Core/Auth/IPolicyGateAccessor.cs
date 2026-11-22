namespace BifrostQL.Core.Auth;

/// <summary>
/// Hands an application endpoint the <see cref="IPolicyGate"/> for the current request.
/// Registered scoped by <c>AddBifrostQL</c>; inject it and await <see cref="GetAsync"/>.
/// Building the gate projects the caller (awaiting the host's grant resolver) and loads the
/// endpoint's database model, both asynchronously, and only when an endpoint asks: a route
/// that never awaits the accessor never triggers either.
/// </summary>
public interface IPolicyGateAccessor
{
    /// <summary>
    /// The gate for the current request, built on the first call and reused for the rest of
    /// the request. A caller with no projectable identity gets a gate that refuses everything.
    /// Throws <see cref="System.InvalidOperationException"/> when no request is active or the
    /// request cannot be tied to one database model.
    /// </summary>
    ValueTask<IPolicyGate> GetAsync(CancellationToken cancellationToken = default);
}
