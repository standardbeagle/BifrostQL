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

        public void ApplyDatabaseMetadata(IDictionary<string, object?> metadata, string rootName = ":root")
        {
            foreach (var rule in _metaRules)
            {
                rule.ApplyToSchema(rootName, metadata);
            }
        }

        public void ApplySchemaMetadata(IDbSchema schema, IDictionary<string, object?> metadata)
        {
            foreach (var rule in _metaRules)
            {
                rule.ApplyToSchema(schema.DbName, metadata);
            }
        }

        public void ApplyTableMetadata(IDbTable table, IDictionary<string, object?> metadata)
        {
            foreach (var rule in _metaRules)
            {
                rule.ApplyToTable(table.TableSchema, table.DbName, table.Columns.Select(c => c.DbName).ToArray(), metadata);
            }
        }

        public void ApplyColumnMetadata(IDbTable table, ColumnDto column, IDictionary<string, object?> metadata)
        {
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

        private static readonly Regex RuleRegex = new(@"(?<selectors>.+)\s*{\s*(?<properties>.*)}", RegexOptions.IgnoreCase);

        public MetadataLoaderRule(string rule)
        {
            var split = RuleRegex.Match(rule);
            var selectors = split.Groups["selectors"].Value;
            var selectorList = selectors.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray();
            _rules = selectorList.Select(s => s.Split(new[] { '.' }).Select(m => new MetadataMatcher(m)).ToArray()).ToArray();
            var properties = split.Groups["properties"].Value;
            var propertyList = properties.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            _metadata = propertyList.Select(p => p.Split(new[] { ':' })).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
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
                _nameRule = new Regex(split.Groups["nameRule"].Value.Replace("*", ".*"), RegexOptions.IgnoreCase);
                _attributeRule = new Regex(split.Groups["attributeRule"].Value.Replace("*", ".*"), RegexOptions.IgnoreCase);
            }
            else
            {
                _nameRule = new Regex(rule.Replace("*", ".*"), RegexOptions.IgnoreCase);
                _attributeRule = null;
            }
        }

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
