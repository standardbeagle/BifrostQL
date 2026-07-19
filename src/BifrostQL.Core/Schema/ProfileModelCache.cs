using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Model;
using GraphQL.Types;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BifrostQL.Core.Schema
{
    /// <summary>
    /// Per-connection cache that turns a single shared DB read into a
    /// <c>(model, schema)</c> pair per profile, memoized by profile name.
    /// </summary>
    /// <remarks>
    /// The expensive DB <see cref="SchemaData">read</see> is shared across all
    /// profiles; only the metadata-driven <c>BuildModel</c> + schema generation
    /// re-run per profile (CPU-only, then memoized). Each cache instance is bound
    /// to one read — reconnect creates a new cache (or calls <see cref="Reset"/>).
    /// </remarks>
    public sealed class ProfileModelCache
    {
        private readonly DbModelLoader _loader;
        private readonly SchemaData _read;
        private readonly IReadOnlyList<string> _baseMetadataRules;
        private readonly IDictionary<string, IDictionary<string, object?>>? _additionalMetadata;
        private readonly BifrostProfileRegistry? _registry;
        private readonly EnumValueLoader.LoadResult? _enumValues;

        // Per-profile memo: builds are seconds-long (model clone + metadata +
        // full schema generation), so a single cache-wide lock would serialize
        // every profile behind whichever build is in flight — hits included.
        // Lazy<T> with ExecutionAndPublication makes each profile build exactly
        // once while hits and builds for other profiles proceed concurrently.
        private readonly ConcurrentDictionary<string, Lazy<(IDbModel Model, ISchema Schema)>> _memo =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Test hook: invoked with the profile name at the start of each build.</summary>
        internal Action<string>? OnBuilding { get; set; }

        public ProfileModelCache(
            DbModelLoader loader,
            SchemaData read,
            IReadOnlyList<string> baseMetadataRules,
            IDictionary<string, IDictionary<string, object?>>? additionalMetadata,
            BifrostProfileRegistry? registry,
            EnumValueLoader.LoadResult? enumValues = null)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _baseMetadataRules = baseMetadataRules ?? Array.Empty<string>();
            _additionalMetadata = additionalMetadata;
            _registry = registry;
            _enumValues = enumValues;
        }

        /// <summary>
        /// Returns the <c>(model, schema)</c> built for the given profile, building
        /// and memoizing on first request. A null/empty/unknown name resolves to an
        /// empty default profile (raw base schema, no opt-in modules).
        /// </summary>
        public (IDbModel Model, ISchema Schema) GetFor(string? profileName)
        {
            var profile = ResolveProfile(profileName);

            var lazy = _memo.GetOrAdd(
                profile.Name,
                _ => new Lazy<(IDbModel Model, ISchema Schema)>(
                    () => Build(profile),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                return lazy.Value;
            }
            catch
            {
                // Lazy caches exceptions under ExecutionAndPublication; evict the
                // failed entry (only if it is still ours) so the next request
                // retries the build instead of replaying the cached failure.
                _memo.TryRemove(KeyValuePair.Create(profile.Name, lazy));
                throw;
            }
        }

        /// <summary>Clears the per-profile memo (called on reconnect).</summary>
        public void Reset()
        {
            _memo.Clear();
        }

        private BifrostProfile ResolveProfile(string? profileName)
        {
            if (!string.IsNullOrEmpty(profileName))
            {
                var found = _registry?.Get(profileName);
                if (found != null)
                    return found;
            }

            // Empty modules = nothing opt-in active (no "no-profile = all-on" path).
            return new BifrostProfile { Name = ProfileNames.System.Default, Modules = Array.Empty<string>() };
        }

        private (IDbModel Model, ISchema Schema) Build(BifrostProfile profile)
        {
            OnBuilding?.Invoke(profile.Name);

            var rules = profile.Metadata is { Count: > 0 }
                ? _baseMetadataRules.Concat(profile.Metadata).ToList()
                : (IReadOnlyCollection<string>)_baseMetadataRules;

            var model = _loader.BuildModel(_read, new MetadataLoader(rules), _additionalMetadata);

            // Attach the pre-loaded enum map BEFORE schema emission so enum-aware
            // schema generation and request-time code can read it off the model.
            if (_enumValues != null && model is DbModel dbm)
                dbm.EnumColumns = EnumColumnMap.Build(model, _enumValues.Values, _enumValues.ValueColumns);

            var schema = DbSchema.FromModel(model, profile);
            return (model, schema);
        }
    }
}
