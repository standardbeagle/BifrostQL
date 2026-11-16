using BifrostQL.Core.Model;

namespace BifrostQL.Server.Feeds
{
    /// <summary>
    /// A successful feed authentication: the projected Bifrost user context and, for a scoped feed
    /// token, the table names the token may read. A Bearer principal is unscoped — the pipeline
    /// scopes it like any other identity.
    /// </summary>
    public sealed class FeedAuthentication
    {
        private readonly IReadOnlyCollection<string>? _allowedTables;

        private FeedAuthentication(IDictionary<string, object?> userContext, IReadOnlyCollection<string>? allowedTables)
        {
            UserContext = userContext;
            _allowedTables = allowedTables;
        }

        /// <summary>The projected user context every read runs under.</summary>
        public IDictionary<string, object?> UserContext { get; }

        internal static FeedAuthentication Unscoped(IDictionary<string, object?> userContext) => new(userContext, null);

        internal static FeedAuthentication Scoped(IDictionary<string, object?> userContext, IReadOnlyCollection<string> allowedTables)
            => new(userContext, allowedTables ?? throw new ArgumentNullException(nameof(allowedTables)));

        /// <summary>
        /// Whether this credential may read <paramref name="table"/>, the table the route RESOLVED
        /// to. Each allow-list entry is resolved through <see cref="FeedTableResolver"/> against the
        /// same model and compared by identity, so an entry naming a bare name two schemas share
        /// covers neither table, and a route spelling that resolves elsewhere is never let through
        /// by matching the entry's text.
        /// </summary>
        public bool Permits(IDbModel model, IDbTable table)
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(table);

            if (_allowedTables is null)
                return true;
            foreach (var entry in _allowedTables)
            {
                if (ReferenceEquals(FeedTableResolver.Resolve(model.Tables, entry), table))
                    return true;
            }
            return false;
        }

        /// <summary>True for a scoped feed token, whose miss on any route must be its uniform 401.</summary>
        internal bool IsScoped => _allowedTables is not null;
    }
}
