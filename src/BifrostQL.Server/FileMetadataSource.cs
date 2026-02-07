using BifrostQL.Core.Model;
using Microsoft.Extensions.Configuration;

namespace BifrostQL.Server
{
    /// <summary>
    /// Loads table metadata from appsettings.json configuration.
    /// Wraps the existing MetadataLoader parsing logic and converts its rule-based
    /// format into the IMetadataSource dictionary format.
    /// </summary>
    public sealed class FileMetadataSource : IMetadataSource
    {
        private readonly IReadOnlyCollection<string> _rules;

        public int Priority => 0;

        public FileMetadataSource(IConfigurationSection configSection, string key)
        {
            ArgumentNullException.ThrowIfNull(configSection, nameof(configSection));
            ArgumentNullException.ThrowIfNull(key, nameof(key));
            var section = configSection.GetSection(key);
            _rules = section.GetChildren()
                .Where(c => c.Value != null)
                .Select(c => c.Value!)
                .ToArray();
        }

        public FileMetadataSource(IReadOnlyCollection<string> rules)
        {
            ArgumentNullException.ThrowIfNull(rules, nameof(rules));
            _rules = rules;
        }

        public Task<IDictionary<string, IDictionary<string, object?>>> LoadTableMetadataAsync()
        {
            var result = new Dictionary<string, IDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

            foreach (var rule in _rules)
            {
                ParseRule(rule, result);
            }

            return Task.FromResult<IDictionary<string, IDictionary<string, object?>>>(result);
        }

        /// <summary>
        /// Parses a metadata rule string like "dbo.users { tenant-filter: tenant_id; soft-delete: deleted_at }"
        /// into table-name keyed dictionary entries.
        /// </summary>
        internal static void ParseRule(string rule, IDictionary<string, IDictionary<string, object?>> result)
        {
            var braceStart = rule.IndexOf('{');
            var braceEnd = rule.LastIndexOf('}');
            if (braceStart < 0 || braceEnd < 0 || braceEnd <= braceStart)
                return;

            var selector = rule.Substring(0, braceStart).Trim();
            var propertiesStr = rule.Substring(braceStart + 1, braceEnd - braceStart - 1).Trim();

            if (string.IsNullOrWhiteSpace(selector) || string.IsNullOrWhiteSpace(propertiesStr))
                return;

            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var propertyParts = propertiesStr.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in propertyParts)
            {
                var colonIdx = part.IndexOf(':');
                if (colonIdx <= 0)
                    continue;
                var key = part.Substring(0, colonIdx).Trim();
                var value = part.Substring(colonIdx + 1).Trim();
                properties[key] = value;
            }

            if (properties.Count == 0)
                return;

            if (!result.TryGetValue(selector, out var existing))
            {
                result[selector] = properties;
            }
            else
            {
                foreach (var (key, value) in properties)
                {
                    existing[key] = value;
                }
            }
        }
    }
}
