using GraphQL.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BifrostQL.Core.Schema
{
    public sealed class PathCache<T>
    {
        private Dictionary<string, Cache<T>> _schemas = new Dictionary<string, Cache<T>>();
        public PathCache() { }

        public void AddLoader(string path, Func<T> loader)
        {
            _schemas.Add(path, new Cache<T>(loader));
        }

        public T GetValue(string path)
        {
            return _schemas.TryGetValue(path, out var cache) ? cache.Value : throw new ArgumentOutOfRangeException(nameof(path), "Path cache not configured for path:" + path);
        }
    }

    sealed class Cache<T>
    {
        private T? _schema;
        private Func<T> _loader;
        public Cache(Func<T> loader) {
            _loader= loader;
        }

        public T Value => _schema ??= _loader();
    }
}
