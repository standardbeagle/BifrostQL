namespace BifrostQL.Core.AppMetadata
{
    /// <summary>
    /// Root aggregate of the app-metadata overlay.
    /// <see cref="AppMetadataModel"/> is a pure data type — it has no database
    /// or GraphQL dependency — that describes how an application client (SPA
    /// or React Native) should present the entities exposed by a BifrostQL
    /// instance.
    ///
    /// Named <c>AppMetadataModel</c> rather than <c>AppMetadata</c> because the
    /// enclosing namespace is <c>BifrostQL.Core.AppMetadata</c>; C# does not
    /// permit a type whose name collides with a namespace in scope.
    ///
    /// This overlay is a NEW layer that sits on top of, and does not replace,
    /// the existing BifrostQL schema-metadata system (<c>DbModel</c>,
    /// <c>MetadataKeys</c>, the <c>dbo.table { key: value }</c> rule grammar).
    /// The two coexist: the overlay is standalone JSON and deliberately does
    /// not reuse the <c>{ }</c> rule-delimiter grammar.
    /// </summary>
    public sealed record AppMetadataModel
    {
        /// <summary>
        /// Entity-level metadata keyed by qualified table name (e.g.
        /// <c>dbo.users</c>). Empty when no entities have overlay metadata.
        /// </summary>
        public IReadOnlyDictionary<string, EntityMetadata> Entities { get; init; }
            = new Dictionary<string, EntityMetadata>();
    }
}
