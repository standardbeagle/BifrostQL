using GraphQL;
using BifrostQL.Model;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Schema;

namespace BifrostQL.Server
{
    /// <summary>
    /// Builds the shared <see cref="Inputs"/> a <c>PathCache&lt;Inputs&gt;</c> loader returns for
    /// a single BifrostQL endpoint: reads the DB schema once, builds the base <see cref="DbModel"/>,
    /// pre-loads enum lookup values, and constructs the per-profile <see cref="ProfileModelCache"/>.
    /// Both <see cref="BifrostSetupOptions"/> (single endpoint) and <see cref="BifrostMultiDbOptions"/>
    /// (multiple endpoints) drive their PathCache loader through this single bootstrap sequence so the
    /// two configuration paths cannot drift out of sync.
    /// </summary>
    internal static class ProfileCacheBootstrapper
    {
        public static async Task<Inputs> BuildInputsAsync(
            string connectionString,
            string? providerName,
            IReadOnlyList<string> metadataRules,
            IReadOnlyList<IMetadataSource> metadataSources,
            BifrostProfileRegistry registry)
        {
            IDictionary<string, IDictionary<string, object?>>? additionalMetadata = null;
            if (metadataSources.Count > 0)
            {
                var composite = new CompositeMetadataSource(metadataSources);
                additionalMetadata = await composite.LoadTableMetadataAsync();
            }
            var provider = string.IsNullOrWhiteSpace(providerName)
                ? (BifrostDbProvider?)null
                : DbConnFactoryResolver.ParseProviderName(providerName);
            var connFactory = DbConnFactoryResolver.Create(connectionString, provider);
            // Read once, then build a model+schema per profile from the shared read.
            var loader = new DbModelLoader(connFactory, new MetadataLoader(metadataRules));
            var read = await loader.ReadAsync();
            // Pre-load enum lookup values once (async DB read) so the
            // synchronous per-profile cache can attach the enum map.
            var baseModel = loader.BuildModel(read, new MetadataLoader(metadataRules), additionalMetadata);
            var enumValues = await loader.LoadEnumValuesAsync(baseModel);
            var profileCache = new ProfileModelCache(loader, read, metadataRules, additionalMetadata, registry, enumValues);
            // Default/base build (null → empty default profile) for back-compat extensions.
            var (model, schema) = profileCache.GetFor(null);
            return new Inputs(new Dictionary<string, object?>
            {
                { "model", model },
                { "connFactory", connFactory },
                { "dbSchema", schema },
                { "profileModelCache", profileCache },
            });
        }
    }
}
