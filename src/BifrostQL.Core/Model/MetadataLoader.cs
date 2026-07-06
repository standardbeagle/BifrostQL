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

        public MetadataLoader(IConfiguration configuration, string key)
        {
            ArgumentNullException.ThrowIfNull(configuration, nameof(configuration));
            ArgumentNullException.ThrowIfNull(key, nameof(key));

            var section = configuration.GetSection(key);
            var rules = section.GetChildren().Where(c => c.Value != null).Select(c => c.Value!).ToArray();
            _metaRules = rules.Select(r => new MetadataLoaderRule(r)).ToArray();
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
            foreach (var rule in _metaRules)
            {
                rule.ApplyToTable(table.TableSchema, table.DbName, table.Columns.Select(c => c.DbName).ToArray(), metadata);
            }
        }

        public void ApplyColumnMetadata(IDbTable table, ColumnDto column, IDictionary<string, object?> metadata)
        {
            ArgumentNullException.ThrowIfNull(table, nameof(table));
            foreach (var rule in _metaRules)
            {
                rule.ApplyToColumn(table.TableSchema, table.DbName, table.Columns.Select(c => c.DbName).ToArray(), column.DbName, metadata);
            }
        }
    }

    class MetadataLoaderRule
    {
        private readonly MetadataMatcher[][] _rules;
        private readonly Dictionary<string, string> _metadata;
        private static readonly HashSet<string> _appendProperties = new() { "join" };

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
            _rules = selectorList.Select(s => s.Split(new[] { '.' }).Select(m => new MetadataMatcher(m)).ToArray()).ToArray();

            var properties = split.Groups["properties"].Value;
            var propertyList = properties.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            _metadata = new Dictionary<string, string>();
            foreach (var raw in propertyList)
            {
                // Split on the FIRST ':' only so values may themselves contain ':'
                // (e.g. many-to-many "Target:Junction", computed-sql "name:Type:expr").
                var kv = raw.Split(new[] { ':' }, 2);
                if (kv.Length != 2)
                    throw new ArgumentException($"Invalid metadata property '{raw.Trim()}' in rule '{rule}': expected 'key: value'.", nameof(rule));
                var key = kv[0].Trim();
                var value = kv[1].Trim();
                if (key.Length == 0)
                    throw new ArgumentException($"Empty metadata key in rule '{rule}'.", nameof(rule));
                if (!_metadata.TryAdd(key, value))
                    throw new ArgumentException($"Duplicate metadata key '{key}' in rule '{rule}'.", nameof(rule));
            }
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
