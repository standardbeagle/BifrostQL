using BifrostQL.Model;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BifrostQL.Core.Model
{
    public sealed class ColumnMatcher
    {
        private readonly (Regex schema, Regex table, Regex column)[] _matchers;
        private readonly bool _default;

        public ColumnMatcher(bool defaultResult)
        {
            _matchers = Array.Empty<(Regex schema, Regex table, Regex column)>();
            _default = defaultResult;
        }

        public ColumnMatcher((string schema, string table, string column)[] matches, bool defaultResult)
        {
            _matchers = matches.Select(m => (
            new Regex(m.schema, RegexOptions.IgnoreCase), 
            new Regex(m.table, RegexOptions.IgnoreCase),
            new Regex(m.column, RegexOptions.IgnoreCase)
            )).ToArray();
            _default = defaultResult;

        }

        public bool Match(ColumnDto column)
        {
            if (_matchers.Length == 0)
                return _default;
            return _matchers.Any(m => 
                m.schema.IsMatch(column.TableSchema) && 
                m.table.IsMatch(column.TableName) &&
                m.column.IsMatch(column.ColumnName)
                );
        }
        public static ColumnMatcher FromSection(IConfigurationSection section, bool defaultResult)
        {
            if (section == null)
                return new ColumnMatcher(defaultResult);
            var matches = section.GetChildren().SelectMany(c => c.GetChildren().SelectMany(cc => cc.GetChildren().Select(ccc => (cc.Key, ccc.Key, ccc.Value)))).ToArray();
            return new ColumnMatcher(matches, defaultResult);
        }
    }
}
