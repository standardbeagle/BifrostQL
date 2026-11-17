using BifrostQL.Core.Schema;
using FluentAssertions;

namespace BifrostQL.Core.Unit.Schema;

/// <summary>
/// <see cref="PathCache{T}.GetFirstValueAsync"/> answers with the ordinal-first path. It used to
/// take the ConcurrentDictionary's first value, whose order is unspecified (string hashing is
/// randomized per process), so two callers taking "the first endpoint" could reach different
/// databases.
/// </summary>
public class PathCacheFirstValueTests
{
    [Fact]
    public async Task GetFirstValueAsync_ReturnsTheOrdinalFirstPath()
    {
        // 200 paths registered in reverse order: the hash-ordered first value matches the
        // ordinal-first path about once in 200 runs.
        var cache = new PathCache<string>();
        var paths = Enumerable.Range(0, 200).Select(i => $"/db{i:D3}").ToArray();
        foreach (var path in paths.Reverse())
            cache.AddLoader(path, () => Task.FromResult(path));

        var first = await cache.GetFirstValueAsync();

        first.Should().Be("/db000");
        first.Should().Be(cache.Paths[0], "the first value and the first of Paths are one definition");
    }
}
