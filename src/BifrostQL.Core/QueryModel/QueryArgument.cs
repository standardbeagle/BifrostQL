using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BifrostQL.Core.QueryModel
{
    public sealed class QueryArgument
    {
        public string Name { get; init; } = null!;
        public object? Value { get; set; }
        public override string ToString() => $"{Name}={Value}";

        public string GetFullText()
        {
            var fullValue = Value switch
            {
                null => "null",
                string s => s,
                IEnumerable<string> e => $"[{string.Join(",", e)}]",
                IEnumerable<QueryArgument> e => $"[{string.Join(",", e.Select(qa => qa.GetFullText()))}]",
                IEnumerable<object?> e => $"[{string.Join(",", e.Select(o => JsonSerializer.Serialize(o)))}]",
                QueryArgument a => a.GetFullText(),
                _ => JsonSerializer.Serialize(Value)
            };
            return $"{Name}={Value}";
        }
    }
}
