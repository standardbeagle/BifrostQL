using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BifrostQL.Core.Model
{
    internal class SqlNameComparer : IEqualityComparer<string>
    {
        public static readonly SqlNameComparer Instance = new SqlNameComparer();

        private SqlNameComparer() { }
        public bool Equals(string? x, string? y)
        {
            return string.Equals(x, y, StringComparison.InvariantCultureIgnoreCase);
        }

        public int GetHashCode([DisallowNull] string obj)
        {
            return string.GetHashCode(obj, StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
