using System.Collections.Generic;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Tests the consumer module-API registration seam (AddModuleApi&lt;T&gt; →
/// <see cref="ModuleApiRegistry.Register"/>): a registered <see cref="IModuleApi"/> must be
/// honored by ALL FOUR registry paths — query SDL, mutation SDL, query capture, mutation
/// capture — not just emission, and captured values must land under the same table-scoped
/// key convention the built-in <see cref="SoftDeleteModuleApi"/> uses. The probe module gates
/// on a unique metadata key so it only affects this file's fixture table, never the shared
/// tables other tests assert exact SDL strings against.
/// </summary>
public sealed class ModuleApiRegistrationTests
{
    private const string ProbeMetadataKey = "test-module-api";
    private const string QueryArgName = "_probeQuery";
    private const string QueryContextKey = "probe_query";
    private const string MutationArgName = "_probeMutation";
    private const string MutationContextKey = "probe_mutation";

    private sealed class ProbeModuleApi : IModuleApi
    {
        public string ModuleName => "probe";

        private static bool Active(IDbTable table) => table.Metadata.ContainsKey(ProbeMetadataKey);

        public IEnumerable<ModuleArgument> GetQueryArguments(IDbTable table)
        {
            if (Active(table))
                yield return new ModuleArgument(QueryArgName, "Boolean", QueryContextKey, "probe query flag");
        }

        public IEnumerable<ModuleArgument> GetMutationArguments(IDbTable table)
        {
            if (Active(table))
                yield return new ModuleArgument(MutationArgName, "Boolean", MutationContextKey, "probe mutation flag");
        }
    }

    private static IDbTable ProbeTable()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Widgets", t => t
                .WithSchema("probe")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithMetadata(ProbeMetadataKey, "on"))
            .Build();
        return model.GetTableFromDbName("Widgets");
    }

    [Fact]
    public void Register_ModuleHonoredByAllFourPaths_IncludingCapture()
    {
        var table = ProbeTable();

        // Before registration none of the paths know the probe args.
        ModuleApiRegistry.QueryArgumentsSdl(table).Should().NotContain(QueryArgName);

        using (ModuleApiRegistry.Register(new ProbeModuleApi()))
        {
            // 1. Query SDL emission.
            ModuleApiRegistry.QueryArgumentsSdl(table).Should().Contain($" {QueryArgName}: Boolean");
            // 2. Mutation SDL emission.
            ModuleApiRegistry.MutationArgumentsSdl(table).Should().Contain($", {MutationArgName}: Boolean");

            // 3. Query CAPTURE — prove the supplied value is written into the user context,
            //    not merely emitted on the schema (an emitted-but-uncaptured arg is a no-op).
            var queryCtx = Substitute.For<IBifrostFieldContext>();
            queryCtx.HasArgument(QueryArgName).Returns(true);
            queryCtx.GetArgument<bool>(QueryArgName).Returns(true);
            var userContext = new Dictionary<string, object?>();
            ModuleApiRegistry.CaptureQueryArguments(queryCtx, table, userContext);
            userContext.Should().ContainKey(ModuleApiRegistry.ScopedKey(QueryContextKey, table));
            userContext[ModuleApiRegistry.ScopedKey(QueryContextKey, table)].Should().Be(true);

            // 4. Mutation CAPTURE.
            var mutationCtx = Substitute.For<IBifrostFieldContext>();
            mutationCtx.HasArgument(MutationArgName).Returns(true);
            mutationCtx.GetArgument<bool>(MutationArgName).Returns(true);
            var captured = ModuleApiRegistry.CaptureMutationArguments(mutationCtx, table);
            captured.Should().ContainKey(MutationContextKey);
            captured[MutationContextKey].Should().Be(true);
        }

        // Disposal unregisters — the registry returns to its built-in-only state.
        ModuleApiRegistry.QueryArgumentsSdl(table).Should().NotContain(QueryArgName);
    }

    [Fact]
    public void Register_CapturedValue_LandsUnderTableScopedKey_LikeSoftDelete()
    {
        var table = ProbeTable();

        using (ModuleApiRegistry.Register(new ProbeModuleApi()))
        {
            var queryCtx = Substitute.For<IBifrostFieldContext>();
            queryCtx.HasArgument(QueryArgName).Returns(true);
            queryCtx.GetArgument<bool>(QueryArgName).Returns(true);
            var userContext = new Dictionary<string, object?>();

            ModuleApiRegistry.CaptureQueryArguments(queryCtx, table, userContext);

            // Identical `{contextKey}:{schema}.{table}` convention as SoftDeleteModuleApi.
            userContext.Should().ContainKey($"{QueryContextKey}:probe.Widgets");
        }
    }

    [Fact]
    public void Register_IsAdditive_BuiltInSoftDeleteStillPresent()
    {
        using (ModuleApiRegistry.Register(new ProbeModuleApi()))
        {
            ModuleApiRegistry.Modules.Should().Contain(m => m is SoftDeleteModuleApi);
            ModuleApiRegistry.Modules.Should().Contain(m => m is ProbeModuleApi);
        }
    }

    [Fact]
    public void Register_DeduplicatesByType_NoDoubleEmission()
    {
        var table = ProbeTable();

        using (ModuleApiRegistry.Register(new ProbeModuleApi()))
        using (ModuleApiRegistry.Register(new ProbeModuleApi()))
        {
            // A repeated container build must not accumulate duplicates (which would
            // double-emit the GraphQL argument and break schema generation).
            var sdl = ModuleApiRegistry.QueryArgumentsSdl(table);
            sdl.IndexOf(QueryArgName, System.StringComparison.Ordinal)
                .Should().Be(sdl.LastIndexOf(QueryArgName, System.StringComparison.Ordinal));
        }
    }
}
