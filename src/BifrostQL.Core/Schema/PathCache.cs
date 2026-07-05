using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BifrostQL.Core.Schema
{
    public sealed class PathCache<T>
    {
        private readonly ConcurrentDictionary<string, Cache<T>> _schemas = new();
        public PathCache() { }

        public void AddLoader(string path, Func<Task<T>> loader)
        {
            if (!_schemas.TryAdd(path, new Cache<T>(loader)))
                throw new ArgumentException("A loader is already registered for path: " + path, nameof(path));
        }

        /// <summary>
        /// True if a loader is registered for the path. Does not trigger loading.
        /// </summary>
        public bool HasPath(string path) => _schemas.ContainsKey(path);

        /// <summary>
        /// Number of registered endpoint loaders. Used to decide whether an unmatched
        /// request path may safely fall back to the single registered endpoint (1) or
        /// must be rejected because it could resolve to the wrong database (2+).
        /// </summary>
        public int Count => _schemas.Count;

        /// <summary>
        /// Returns the cached value for a path, loading it on first access.
        /// </summary>
        public Task<T> GetValueAsync(string path)
        {
            return _schemas.TryGetValue(path, out var cache)
                ? cache.GetValueAsync()
                : throw new ArgumentOutOfRangeException(nameof(path), "Path cache not configured for path:" + path);
        }

        /// <summary>
        /// Returns the first cached value, or default if no loaders are registered.
        /// Triggers lazy loading of the first entry if not yet loaded.
        /// </summary>
        public async Task<T?> GetFirstValueAsync()
        {
            var first = _schemas.Values.FirstOrDefault();
            if (first is null)
                return default;
            return await first.GetValueAsync();
        }

        /// <summary>
        /// Clears the cached value for a path so the loader re-executes on next access.
        /// </summary>
        public void Reset(string path)
        {
            if (_schemas.TryGetValue(path, out var cache))
                cache.Reset();
        }

        /// <summary>
        /// Clears all cached values so loaders re-execute on next access.
        /// </summary>
        public void ResetAll()
        {
            foreach (var cache in _schemas.Values)
                cache.Reset();
        }
    }

    internal sealed class Cache<T>
    {
        private readonly Func<Task<T>> _loader;
        private readonly object _gate = new();
        private Task<T>? _task;

        public Cache(Func<Task<T>> loader)
        {
            _loader = loader;
        }

        /// <summary>
        /// Memoizes the loader's task. The fast path is lock-free once a successful
        /// task is published; a faulted or canceled task is discarded so the next
        /// caller retries (preserving the original retry-on-failure behavior).
        /// </summary>
        public Task<T> GetValueAsync()
        {
            var current = _task;
            if (current is { IsFaulted: false, IsCanceled: false })
                return current;

            lock (_gate)
            {
                if (_task is null || _task.IsFaulted || _task.IsCanceled)
                    _task = _loader();
                return _task;
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _task = null;
            }
        }
    }
}
