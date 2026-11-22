using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GraphQL.Types;
using Microsoft.Extensions.Configuration;

namespace BifrostQL.Core.Model
{
    public class MetadataLoader : IMetadataLoader
    {
        private readonly MetadataLoaderRule[] _metaRules;

        /// <summary>
        /// Reads the metadata rule strings from a configuration section that holds an array of
        /// rule strings. Every reader of the <c>Metadata</c> config (the Host registration and
        /// <c>bifrost config-validate</c>) goes through here, so they agree on what a rule is.
        /// A scalar section, or an entry that is an object or array, is refused rather than
        /// skipped: skipping it would drop the rule, and a dropped tenant-filter rule means no
        /// tenant scoping. The message names the configuration path, never the value.
        /// An absent, empty-array or empty-string section yields no rules.
        /// </summary>
        public static IReadOnlyList<string> ReadMetadataRules(IConfigurationSection section)
        {
            ArgumentNullException.ThrowIfNull(section, nameof(section));
            // Non-empty, not non-null: a layered host configuration can carry an empty Value
            // on a section that also has the array's children (seen under WebApplicationFactory).
            if (!string.IsNullOrEmpty(section.Value))
                throw new InvalidOperationException(
                    $"Configuration '{section.Path}' must be an array of rule strings, found a single value.");

            var rules = new List<string>();
            foreach (var entry in section.GetChildren())
            {
                // An empty object or array entry binds as a valueless key with no children.
                if (entry.Value == null || entry.GetChildren().Any())
                    throw new InvalidOperationException(
                        $"Configuration '{entry.Path}' must be a rule string, found an object or array.");
                rules.Add(entry.Value);
            }
            return rules;
        }

        public MetadataLoader(IReadOnlyCollection<string> rules)
        {
            ArgumentNullException.ThrowIfNull(rules, nameof(rules));
            _metaRules = rules.Select(r => new MetadataLoaderRule(r)).ToArray();
        }

        public void ApplyDatabaseMetadata(IDictionary<string, object?> metadata, string rootName = ":root")
        {
            ArgumentNullException.ThrowIfNull(metadata, nameof(metadata));
            foreach (var rule in _metaRules)
            {
                rule.ApplyToSchema(rootName, metadata);
            }
        }

        public void ApplySchemaMetadata(IDbSchema schema, IDictionary<string, object?> metadata)
        {
            ArgumentNullException.ThrowIfNull(schema, nameof(schema));
            foreach (var rule in _metaRules)
            {
                rule.ApplyToSchema(schema.DbName, metadata);
            }
        }

        public void ApplyTableMetadata(IDbTable table, IDictionary<string, object?> metadata)
        {
            ArgumentNullException.ThrowIfNull(table, nameof(table));
            var columnNames = table.Columns.Select(c => c.DbName).ToArray();
            foreach (var rule in _metaRules)
            {
                rule.ApplyToTable(table.TableSchema, table.DbName, columnNames, metadata);
            }
        }

        public void ApplyColumnMetadata(IDbTable table, ColumnDto column, IDictionary<string, object?> metadata)
        {
            ArgumentNullException.ThrowIfNull(table, nameof(table));
            var columnNames = table.Columns.Select(c => c.DbName).ToArray();
            foreach (var rule in _metaRules)
            {
                rule.ApplyToColumn(table.TableSchema, table.DbName, columnNames, column.DbName, metadata);
            }
        }
    }

    class MetadataLoaderRule
    {
        private const string RootSelector = ":root";

        private readonly MetadataMatcher[][] _rules;
        private readonly Dictionary<string, string> _metadata;

        // Properties whose values accumulate across every rule that targets the
        // same schema element, comma-joined, instead of the last rule winning.
        // Both carry comma-separated lists (join declarations, "Target:Junction"
        // many-to-many pairs) that multiple rules are expected to contribute to.
        private static readonly HashSet<string> _appendProperties =
            new(StringComparer.OrdinalIgnoreCase)
            {
                MetadataKeys.Relationships.Join,
                MetadataKeys.Relationships.ManyToMany,
            };

        // Selectors are the brace-free prefix; the property block spans the FIRST
        // '{' to the LAST '}'. Greedy properties let a value carry balanced braces
        // (e.g. "policy-row-scope: user_id = {user_id}") without the '{' being
        // mistaken for the block opener.
        private static readonly Regex RuleRegex = new(@"^\s*(?<selectors>[^{}]+?)\s*\{(?<properties>.*)\}\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        public MetadataLoaderRule(string rule)
        {
            ArgumentNullException.ThrowIfNull(rule, nameof(rule));

            var split = RuleRegex.Match(rule);
            if (!split.Success)
                throw new ArgumentException(
                    $"Invalid metadata rule '{rule}': expected 'selector[, selector...] {{ key: value[; key: value...] }}'.",
                    nameof(rule));

            var selectors = split.Groups["selectors"].Value;
            var selectorList = selectors.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray();
            if (selectorList.Length == 0)
                throw new ArgumentException($"Invalid metadata rule '{rule}': no selector was specified before '{{'.", nameof(rule));
            // ':root' is the only pseudo-selector the runtime applies (ApplyDatabaseMetadata).
            // Any other ':'-prefixed selector (':auth', a ':roots' typo) would match nothing
            // and drop its keys silently, so it is refused here.
            var pseudoSelector = selectorList.FirstOrDefault(s => s.StartsWith(':') && !string.Equals(s, RootSelector, StringComparison.OrdinalIgnoreCase));
            if (pseudoSelector != null)
                throw new ArgumentException(
                    $"Invalid metadata rule '{rule}': unknown selector '{pseudoSelector}'; '{RootSelector}' is the only ':'-prefixed selector.",
                    nameof(rule));
            _rules = selectorList.Select(s => s.Split(new[] { '.' }).Select(m => new MetadataMatcher(m)).ToArray()).ToArray();

            var properties = split.Groups["properties"].Value;
            _metadata = new Dictionary<string, string>();
            foreach (var segment in SplitProperties(properties, rule))
            {
                // Split on the FIRST ':' only so values may themselves contain ':'
                // (e.g. many-to-many "Target:Junction", computed-sql "name:Type:expr").
                var kv = segment.Split(new[] { ':' }, 2);
                if (kv.Length != 2)
                    throw new ArgumentException($"Invalid metadata property '{segment.Trim()}' in rule '{rule}': expected 'key: value'.", nameof(rule));
                var key = MetadataKeys.NormalizeKey(kv[0].Trim());
                var value = UnwrapValue(kv[1].Trim());
                if (key.Length == 0)
                    throw new ArgumentException($"Empty metadata key in rule '{rule}'.", nameof(rule));
                if (!_metadata.TryAdd(key, value))
                    throw new ArgumentException($"Duplicate metadata key '{key}' in rule '{rule}'.", nameof(rule));
            }
        }

        // Splits the property block on ';', but a ';' inside a backtick-quoted
        // verbatim value does not split. This lets a complex value keep its own
        // ';' — e.g. multi-column computed-sql
        //   computed-sql: `a:String:{x}; b:String:{y}`
        // — without the block delimiter tearing it apart.
        private static IEnumerable<string> SplitProperties(string block, string rule)
        {
            var segments = new List<string>();
            var sb = new StringBuilder();
            var inVerbatim = false;
            foreach (var ch in block)
            {
                if (ch == '`')
                {
                    inVerbatim = !inVerbatim;
                    sb.Append(ch);
                }
                else if (ch == ';' && !inVerbatim)
                {
                    segments.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(ch);
                }
            }
            if (inVerbatim)
                throw new ArgumentException($"Unbalanced backtick in metadata rule '{rule}': every verbatim value must open and close with '`'.", nameof(rule));
            segments.Add(sb.ToString());
            return segments.Where(s => !string.IsNullOrWhiteSpace(s));
        }

        // A value that is a SINGLE complete verbatim span — it opens with '`' and
        // its only other '`' is the final character — is unwrapped: the interior is
        // preserved exactly (no trimming, no ';'/':' interpretation). Any other
        // backtick placement (multiple spans like "`a` and `b`", or a span embedded
        // mid-string like "prefix `x;y` suffix") is kept as-is, backticks included:
        // SplitProperties has already honored those spans for ';' protection, and
        // stripping only the outer pair would corrupt the value. Backticks surviving
        // in multi-span / embedded-span values is the documented behavior.
        private static string UnwrapValue(string value)
        {
            if (value.Length >= 2 && value[0] == '`' && value.IndexOf('`', 1) == value.Length - 1)
                return value.Substring(1, value.Length - 2);
            return value;
        }

        public bool ApplyToSchema(string schema, IDictionary<string, object?> properties)
        {
            if (!_rules.Any(rule => rule.Length == 1 && rule[0].MatchName(schema))) return false;

            ApplyProperties(properties);
            return true;
        }

        public bool ApplyToTable(string schema, string table, IReadOnlyCollection<string> columns, IDictionary<string, object?> properties)
        {
            if (!_rules.Any(rule =>
                    rule.Length == 2 && rule[0].MatchName(schema) && rule[1].MatchTable(table, columns)))
                return false;
            ApplyProperties(properties);
            return true;
        }

        public bool ApplyToColumn(string schema, string table, IReadOnlyCollection<string> columns, string column, IDictionary<string, object?> properties)
        {
            if (!_rules.Any(rule =>
                    rule.Length == 3 && rule[0].MatchName(schema) && rule[1].MatchTable(table, columns) && rule[2].MatchName(column)))
                return false;
            ApplyProperties(properties);
            return true;
        }

        private void ApplyProperties(IDictionary<string, object?> properties)
        {
            foreach (var property in _metadata)
            {
                if (!_appendProperties.Contains(property.Key))
                    properties[property.Key] = property.Value;
                else if (properties.TryGetValue(property.Key, out var v) && !string.IsNullOrWhiteSpace(v as string))
                {
                    properties[property.Key] = $"{v}, {property.Value}";
                }
                else
                {
                    properties[property.Key] = property.Value;
                }
            }
        }
    }
    public class MetadataMatcher
    {
        private static readonly Regex RuleRegex = new(@"(?<nameRule>.*)\s*\|\s*has\s*\((?<attributeRule>.*)\)", RegexOptions.IgnoreCase);
        private readonly Regex _nameRule;
        private readonly Regex? _attributeRule;
        public MetadataMatcher(string rule)
        {
            var split = RuleRegex.Match(rule);
            if (split.Success && split.Groups["attributeRule"].Value != "")
            {
                _nameRule = BuildGlobRegex(split.Groups["nameRule"].Value.Trim());
                _attributeRule = BuildGlobRegex(split.Groups["attributeRule"].Value.Trim());
            }
            else
            {
                _nameRule = BuildGlobRegex(rule.Trim());
                _attributeRule = null;
            }
        }

        // Treats the pattern as a literal glob: every character is escaped so
        // regex metacharacters in identifiers (e.g. "order+items", "data(2024)")
        // match literally, then '*' alone is restored as the ".*" wildcard.
        private static Regex BuildGlobRegex(string glob) =>
            new($"^{Regex.Escape(glob).Replace("\\*", ".*")}$", RegexOptions.IgnoreCase);

        public bool MatchName(string name)
        {
            return _attributeRule == null && _nameRule.IsMatch(name);
        }

        public bool MatchTable(string name, IReadOnlyCollection<string> attributes)
        {
            if (_attributeRule == null)
                return _nameRule.IsMatch(name);

            return _nameRule.IsMatch(name) && attributes.Any(a => _attributeRule.IsMatch(a));
        }

        public override string ToString()
        {
            if (_attributeRule == null)
                return _nameRule.ToString();
            return $"{_nameRule}|has({_attributeRule})";
        }
    }
}
