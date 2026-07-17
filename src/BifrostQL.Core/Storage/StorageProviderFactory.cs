namespace BifrostQL.Core.Storage
{
    /// <summary>
    /// Factory for creating storage provider instances based on provider type.
    /// </summary>
    public sealed class StorageProviderFactory
    {
        private readonly Dictionary<string, IStorageProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

        public StorageProviderFactory()
        {
            // The local filesystem provider is always built in. Cloud providers
            // (e.g. S3 from BifrostQL.Aws) opt in via StorageProviderRegistry so
            // Core carries no compile-time dependency on any cloud SDK.
            RegisterProvider(new LocalStorageProvider());
            foreach (var provider in StorageProviderRegistry.CreateRegistered())
                RegisterProvider(provider);
        }

        /// <summary>
        /// Registers a storage provider
        /// </summary>
        public void RegisterProvider(IStorageProvider provider)
        {
            _providers[provider.ProviderType] = provider;
        }

        /// <summary>
        /// Gets a storage provider by type.
        /// </summary>
        /// <remarks>
        /// Deliberately <c>internal</c>, not public: a raw <see cref="IStorageProvider"/>
        /// exposes <c>UploadAsync(config, anyKey, ...)</c>, which writes bytes at a
        /// caller-chosen storage key and bypasses <see cref="FileStorageService"/> — the
        /// only sanctioned upload path, and the one that guarantees a fresh random
        /// storage key decoupled from the caller-derived address (invariant 8a,
        /// .claude/rules/protocol-adapter-security.md). Keeping provider resolution
        /// assembly-scoped means no code outside Core and its friend assemblies can
        /// discover a provider through this factory and write around that guarantee.
        /// Every production caller lives in Core (<see cref="FileStorageService"/>,
        /// the file-folder computed columns), so no public surface is lost.
        /// </remarks>
        internal IStorageProvider GetProvider(string providerType)
        {
            if (_providers.TryGetValue(providerType, out var provider))
            {
                return provider;
            }

            throw new NotSupportedException($"Storage provider '{providerType}' is not supported. " +
                $"Supported providers: {string.Join(", ", _providers.Keys)}");
        }

        /// <summary>
        /// Gets a storage provider for the specified bucket configuration.
        /// Internal for the same reason as <see cref="GetProvider(string)"/>.
        /// </summary>
        internal IStorageProvider GetProvider(StorageBucketConfig config)
        {
            return GetProvider(config.ProviderType);
        }

        /// <summary>
        /// Checks if a provider type is supported
        /// </summary>
        public bool IsSupported(string providerType)
        {
            return _providers.ContainsKey(providerType);
        }
    }
}
