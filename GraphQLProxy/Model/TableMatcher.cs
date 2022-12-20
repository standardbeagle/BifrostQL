using System.Text.RegularExpressions;

namespace GraphQLProxy.Model
{
    public class TableMatcher
    {
        private readonly (Regex schema, Regex[] tables)[] _matchers;
        private readonly bool _default;

        public TableMatcher((string schema, string[] tables)[] matches, bool defaultResult)
        {
            _matchers = matches.Select(m => (new Regex(m.schema, RegexOptions.IgnoreCase), m.tables.Select(t => new Regex(t, RegexOptions.IgnoreCase)).ToArray())).ToArray();
            _default = defaultResult;
        }

        public bool Match(TableDto table)
        {
            if (_matchers.Length == 0)
                return _default;
            return _matchers.Any(m => m.schema.IsMatch(table.TableSchema) && m.tables.Any(t => t.IsMatch(table.TableName)));
        }
    }
}
