using BifrostQL.Core.Schema;
using FluentAssertions;

namespace BifrostQL.Core.Unit.Schema;

/// <summary>
/// Concurrency smoke tests for PathCache — exercises concurrent reads and resets
/// against the ConcurrentDictionary-backed implementation to guard against data races.
/// </summary>
public class PathCacheConcurrencyTests
{
    [Fact]
    public async Task GetFirstValueAsync_ConcurrentWithReset_DoesNotThrow()
    {
        // Arrange
        var cache = new PathCache<int>();
        var callCount = 0;
        cache.AddLoader("/test", () =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(42);
        });

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Act — hammer GetFirstValueAsync and ResetAll from multiple threads simultaneously
        await Task.WhenAll(
            Task.Run(() =>
            {
                Parallel.For(0, 200, _ =>
                {
                    try
                    {
                        cache.GetFirstValueAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
            }),
            Task.Run(() =>
            {
                for (var i = 0; i < 50; i++)
                {
                    try
                    {
                        cache.ResetAll();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            })
        );

        // Assert — no race-induced exceptions
        exceptions.Should().BeEmpty("concurrent reads and resets must not throw");
        callCount.Should().BeGreaterThan(0, "loader must have been invoked at least once");
    }

    [Fact]
    public async Task GetFirstValueAsync_ConcurrentResetForPath_DoesNotThrow()
    {
        // Arrange
        var cache = new PathCache<string>();
        cache.AddLoader("/a", () => Task.FromResult("a"));
        cache.AddLoader("/b", () => Task.FromResult("b"));

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Act — concurrent GetFirstValueAsync + per-path Reset
        await Task.WhenAll(
            Task.Run(() =>
            {
                Parallel.For(0, 100, _ =>
                {
                    try
                    {
                        var value = cache.GetFirstValueAsync().GetAwaiter().GetResult();
                        // value should be either "a" or "b" (never null for loaded entries)
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
            }),
            Task.Run(() =>
            {
                for (var i = 0; i < 30; i++)
                {
                    try
                    {
                        cache.Reset("/a");
                        cache.Reset("/b");
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            })
        );

        // Assert
        exceptions.Should().BeEmpty("per-path Reset must be safe under concurrent reads");
    }

    [Fact]
    public void HasPath_AfterAddLoader_ReturnsTrue()
    {
        var cache = new PathCache<int>();
        cache.AddLoader("/x", () => Task.FromResult(1));
        cache.HasPath("/x").Should().BeTrue();
        cache.HasPath("/y").Should().BeFalse();
    }

    [Fact]
    public void AddLoader_DuplicatePath_ThrowsArgumentException()
    {
        var cache = new PathCache<int>();
        cache.AddLoader("/dup", () => Task.FromResult(0));

        var act = () => cache.AddLoader("/dup", () => Task.FromResult(1));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task GetFirstValueAsync_EmptyCache_ReturnsDefault()
    {
        var cache = new PathCache<int>();
        var result = await cache.GetFirstValueAsync();
        result.Should().Be(default(int));
    }
}
