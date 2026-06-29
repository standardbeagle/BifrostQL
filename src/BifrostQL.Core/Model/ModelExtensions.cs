using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BifrostQL.Core.Model
{
    public static class ModelExtensions
    {
        public static string ToGraphQl(this string input, string prefix = "")
        {
            var translations = input.Select(c => c switch
            {
                ' ' => "_",
                '-' => "_",
                '_' => "_",
                >= 'a' and <= 'z' => c.ToString(),
                >= 'A' and <= 'Z' => c.ToString(),
                >= '0' and <= '9' => c.ToString(),
                _ => $"_{((byte)c):x}"
            });
            var result = string.Concat(translations);
            // An empty input yields an empty translation; fall back to the prefix
            // (or a bare underscore) so a valid GraphQL name is always produced and
            // the result[0] check below can't throw.
            if (result.Length == 0)
                return (string.IsNullOrEmpty(prefix) ? "_" : prefix).ToLowerFirstChar();
            if (result[0] >= '0' && result[0] <= '9')
                result = "_" + result;
            if (result.StartsWith("_"))
                result = prefix + result;
            return result.ToLowerFirstChar();
        }

        private static string ToLowerFirstChar(this string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            return char.ToLower(input[0]) + input.Substring(1);
        }
    }
}
