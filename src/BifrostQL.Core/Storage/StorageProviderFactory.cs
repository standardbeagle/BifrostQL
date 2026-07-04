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
        /// Gets a storage provider by type
        /// </summary>
        public IStorageProvider GetProvider(string providerType)
        {
            if (_providers.TryGetValue(providerType, out var provider))
            {
                return provider;
            }

            throw new NotSupportedException($"Storage provider '{providerType}' is not supported. " +
                $"Supported providers: {string.Join(", ", _providers.Keys)}");
        }

        /// <summary>
        /// Gets a storage provider for the specified bucket configuration
        /// </summary>
        public IStorageProvider GetProvider(StorageBucketConfig config)
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
