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
            // Register default providers
            RegisterProvider(new LocalStorageProvider());
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
