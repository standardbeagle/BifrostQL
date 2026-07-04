using System.Collections.Concurrent;

namespace BifrostQL.Core.Storage
{
    /// <summary>
    /// Global registry of storage-provider factories keyed by provider type
    /// (e.g. "s3"). Provider packages (such as BifrostQL.Aws) register their
    /// implementations at startup so <see cref="StorageProviderFactory"/> can
    /// hand them out without Core taking a compile-time dependency on any
    /// cloud SDK. The built-in "local" provider is always available and does
    /// not need registration.
    /// </summary>
    public static class StorageProviderRegistry
    {
        private static readonly ConcurrentDictionary<string, Func<IStorageProvider>> Factories =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Registers a factory for a storage provider type. Called by provider
        /// packages at application startup.
        /// </summary>
        public static void Register(string providerType, Func<IStorageProvider> factory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(providerType);
            ArgumentNullException.ThrowIfNull(factory);
            Factories[providerType] = factory;
        }

        /// <summary>Provider types that have been registered via <see cref="Register"/>.</summary>
        public static IReadOnlyCollection<string> RegisteredTypes => Factories.Keys.ToArray();

        /// <summary>Creates a fresh instance of every registered provider.</summary>
        internal static IEnumerable<IStorageProvider> CreateRegistered()
            => Factories.Values.Select(create => create());

        /// <summary>Clears all registrations. Intended for testing only.</summary>
        internal static void Clear() => Factories.Clear();
    }
}
