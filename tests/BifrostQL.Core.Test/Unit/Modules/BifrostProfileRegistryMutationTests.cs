using System.Collections.Concurrent;
using BifrostQL.Core.Modules;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

public class BifrostProfileRegistryMutationTests
{
    [Fact]
    public void ReplaceAll_SwapsTheSet()
    {
        // Arrange: a registry seeded with an "old" profile.
        var registry = new BifrostProfileRegistry();
        registry.Add(new BifrostProfile { Name = "old" });

        // Act: replace with an entirely different set.
        registry.ReplaceAll(new[]
        {
            new BifrostProfile { Name = "sales", Label = "Sales" },
            new BifrostProfile { Name = "admin", Label = "Admin" },
        });

        // Assert: old gone, new present, All reflects the swap.
        registry.Get("old").Should().BeNull();
        registry.Get("sales").Should().NotBeNull();
        registry.Get("admin").Should().NotBeNull();
        registry.All.Select(p => p.Name).Should().BeEquivalentTo("sales", "admin");
        registry.HasProfiles.Should().BeTrue();
    }

    [Fact]
    public void Clear_EmptiesTheRegistry()
    {
        // Arrange
        var registry = new BifrostProfileRegistry();
        registry.Add(new BifrostProfile { Name = "sales" });

        // Act
        registry.Clear();

        // Assert
        registry.All.Should().BeEmpty();
        registry.HasProfiles.Should().BeFalse();
        registry.Get("sales").Should().BeNull();
    }

    [Fact]
    public async Task ReplaceAll_IsConsistentUnderConcurrentReads()
    {
        // Arrange: a registry that we will repeatedly replace while readers
        // observe it. Each replacement is an all-or-nothing snapshot, so any
        // Get/All a reader sees must be internally consistent — never a mix of
        // an old and a new generation.
        var registry = new BifrostProfileRegistry();
        registry.ReplaceAll(new[] { new BifrostProfile { Name = "gen0-a" }, new BifrostProfile { Name = "gen0-b" } });

        var observed = new ConcurrentBag<int>();
        using var cts = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                // Snapshot count: a torn write would surface a count != 2.
                var names = registry.All.Select(p => p.Name).ToArray();
                observed.Add(names.Length);
            }
        });

        // Act: hammer ReplaceAll with same-size generations.
        for (var gen = 1; gen <= 2000; gen++)
        {
            registry.ReplaceAll(new[]
            {
                new BifrostProfile { Name = $"gen{gen}-a" },
                new BifrostProfile { Name = $"gen{gen}-b" },
            });
        }
        cts.Cancel();
        await reader;

        // Assert: every observed snapshot held exactly two profiles, and the
        // final state is the last generation we wrote.
        observed.Should().OnlyContain(count => count == 2);
        registry.All.Select(p => p.Name).Should().BeEquivalentTo("gen2000-a", "gen2000-b");
    }
}
