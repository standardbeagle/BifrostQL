namespace BifrostQL.Core.AppMetadata
{
    /// <summary>
    /// Builds the <see cref="AppMetadataModel"/> aggregate from one or more
    /// <see cref="IAppMetadataSource"/> instances.
    ///
    /// This loader is the app-metadata-overlay counterpart of
    /// <c>BifrostQL.Core.Model.MetadataLoader</c>, but it is a deliberately
    /// separate, coexisting pipeline: it does not touch <c>DbModel</c>,
    /// <c>MetadataKeys</c>, or the schema-metadata <c>MetadataLoader</c>. The
    /// overlay it produces is keyed by qualified table name so it aligns with
    /// <c>DbModel</c> tables without modifying them.
    /// </summary>
    public sealed class AppMetadataLoader
    {
        private readonly IAppMetadataSource _source;

        /// <summary>
        /// Creates a loader over a single overlay source. To load from several
        /// sources, pass a <see cref="CompositeAppMetadataSource"/>.
        /// </summary>
        public AppMetadataLoader(IAppMetadataSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            _source = source;
        }

        /// <summary>
        /// Loads the overlay from the configured source and assembles it into
        /// an <see cref="AppMetadataModel"/> aggregate. Returns an aggregate
        /// with no entities when the source supplies none.
        /// </summary>
        public async Task<AppMetadataModel> LoadAsync()
        {
            var entities = await _source.LoadEntityMetadataAsync();
            return new AppMetadataModel
            {
                Entities = new Dictionary<string, EntityMetadata>(
                    entities, StringComparer.OrdinalIgnoreCase),
            };
        }
    }
}
