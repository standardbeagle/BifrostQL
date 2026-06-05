using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Model;
using GraphQL.Types;
using System;
using System.Collections.Generic;
using System.Linq;

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

        private readonly object _lock = new();
        private readonly Dictionary<string, (IDbModel Model, ISchema Schema)> _memo =
            new(StringComparer.OrdinalIgnoreCase);

        public ProfileModelCache(
            DbModelLoader loader,
            SchemaData read,
            IReadOnlyList<string> baseMetadataRules,
            IDictionary<string, IDictionary<string, object?>>? additionalMetadata,
            BifrostProfileRegistry? registry)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _baseMetadataRules = baseMetadataRules ?? Array.Empty<string>();
            _additionalMetadata = additionalMetadata;
            _registry = registry;
        }

        /// <summary>
        /// Returns the <c>(model, schema)</c> built for the given profile, building
        /// and memoizing on first request. A null/empty/unknown name resolves to an
        /// empty default profile (raw base schema, no opt-in modules).
        /// </summary>
        public (IDbModel Model, ISchema Schema) GetFor(string? profileName)
        {
            var profile = ResolveProfile(profileName);

            lock (_lock)
            {
                if (_memo.TryGetValue(profile.Name, out var cached))
                    return cached;

                var built = Build(profile);
                _memo[profile.Name] = built;
                return built;
            }
        }

        /// <summary>Clears the per-profile memo (called on reconnect).</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _memo.Clear();
            }
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
            return new BifrostProfile { Name = "default", Modules = Array.Empty<string>() };
        }

        private (IDbModel Model, ISchema Schema) Build(BifrostProfile profile)
        {
            var rules = profile.Metadata is { Count: > 0 }
                ? _baseMetadataRules.Concat(profile.Metadata).ToList()
                : (IReadOnlyCollection<string>)_baseMetadataRules;

            var model = _loader.BuildModel(_read, new MetadataLoader(rules), _additionalMetadata);
            var schema = DbSchema.FromModel(model, profile);
            return (model, schema);
        }
    }
}
