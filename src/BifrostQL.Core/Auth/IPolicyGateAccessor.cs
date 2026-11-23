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
    /// request cannot be tied to one database model, and when the accessor was resolved from the
    /// root service provider (a singleton captured it), whose gate every caller would share.
    /// </summary>
    ValueTask<IPolicyGate> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The gate for <paramref name="userContext"/>, a context the endpoint already projected
    /// (<c>GetBifrostUserContextAsync</c>), built without projecting the caller again: the gate and
    /// the write the endpoint runs under that context answer for one identity, and the grant
    /// resolver runs once. The gate answers for the identity (user id, roles, permissions) the
    /// context holds at this call: a later change to the dictionary does not change that gate, and
    /// a call after such a change gets a gate for the new contents, never the earlier one. Calls
    /// with the same identity share one build for the request.
    /// An empty context (the projection's refusal) gets a gate that refuses everything. Throws as
    /// <see cref="GetAsync(CancellationToken)"/> does.
    /// </summary>
    ValueTask<IPolicyGate> GetAsync(IDictionary<string, object?> userContext, CancellationToken cancellationToken = default);
}
