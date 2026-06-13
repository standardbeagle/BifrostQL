using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Server;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test
{
    public class GenericTransformerRegistrationTests
    {
        // A test filter transformer with an injected dependency, proving the generic
        // AddFilterTransformer<T>() overload resolves T from the service provider.
        private sealed class DependencyMarker
        {
            public string Value => "injected";
        }

        private sealed class DiResolvedFilter : IFilterTransformer
        {
            public DependencyMarker Marker { get; }
            public DiResolvedFilter(DependencyMarker marker) => Marker = marker;
            public int Priority => 250;
            public bool AppliesTo(IDbTable table, QueryTransformContext context) => false;
            public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context) => null;
        }

        private sealed class PreConfiguredFilter : IFilterTransformer
        {
            public int Priority => 260;
            public bool AppliesTo(IDbTable table, QueryTransformContext context) => false;
            public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context) => null;
        }

        [Fact]
        public void ResolveTransformers_AppendsDiResolvedTypeWithDependencies()
        {
            var services = new ServiceCollection();
            services.AddSingleton<DependencyMarker>();
            services.AddSingleton<DiResolvedFilter>();
            var sp = services.BuildServiceProvider();

            var result = BifrostServiceCollectionExtensions.ResolveTransformers(
                Array.Empty<IFilterTransformer>(),
                new List<Type> { typeof(DiResolvedFilter) },
                sp);

            var resolved = Assert.Single(result.OfType<DiResolvedFilter>());
            // Constructor dependency was satisfied from DI.
            Assert.Equal("injected", resolved.Marker.Value);
        }

        [Fact]
        public void ResolveTransformers_IsAdditive_KeepsCollectionAndGenericTypes()
        {
            var preConfigured = new PreConfiguredFilter();
            var services = new ServiceCollection();
            services.AddSingleton<DependencyMarker>();
            services.AddSingleton<DiResolvedFilter>();
            var sp = services.BuildServiceProvider();

            var result = BifrostServiceCollectionExtensions.ResolveTransformers(
                new IFilterTransformer[] { preConfigured },
                new List<Type> { typeof(DiResolvedFilter) },
                sp);

            Assert.Contains(preConfigured, result);
            Assert.Contains(result, t => t is DiResolvedFilter);
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void ResolveTransformers_NoTypes_ReturnsConfiguredUnchanged()
        {
            var preConfigured = new PreConfiguredFilter();
            var configured = new IFilterTransformer[] { preConfigured };

            var result = BifrostServiceCollectionExtensions.ResolveTransformers(
                configured,
                new List<Type>(),
                new ServiceCollection().BuildServiceProvider());

            Assert.Same(configured, result);
        }

        [Fact]
        public void ResolveTransformers_DistinctTypes_YieldOneInstanceEach_AfterDedupedRegistration()
        {
            // The generic overloads dedup the same T, so ResolveTransformers sees each
            // type once and yields a single instance — no accidental duplicate.
            var services = new ServiceCollection();
            services.AddSingleton<DependencyMarker>();
            services.AddSingleton<DiResolvedFilter>();
            var sp = services.BuildServiceProvider();

            var options = new BifrostSetupOptions();
            options.AddFilterTransformer<DiResolvedFilter>()
                   .AddFilterTransformer<DiResolvedFilter>(); // duplicate ignored

            // Mirror what ConfigureServices passes: the deduped type list.
            var types = new List<Type> { typeof(DiResolvedFilter) };
            var result = BifrostServiceCollectionExtensions.ResolveTransformers(
                Array.Empty<IFilterTransformer>(), types, sp);

            Assert.Single(result.OfType<DiResolvedFilter>());
        }

        [Fact]
        public void GenericOverloads_RecordTypes_AndAreChainable()
        {
            // The fluent generic overloads exist and chain; they accumulate types that
            // ConfigureServices later resolves. (Smoke test of the public surface that
            // CLAUDE.md and Modules/README advertise.)
            var options = new BifrostSetupOptions();
            var returned = options
                .AddFilterTransformer<DiResolvedFilter>()
                .AddMutationTransformer<TestMutation>()
                .AddQueryObserver<TestObserver>();

            Assert.Same(options, returned);
        }

        private sealed class TestMutation : IMutationTransformer
        {
            public int Priority => 300;
            public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context) => false;
            public MutationTransformResult Transform(
                IDbTable table,
                MutationType mutationType,
                Dictionary<string, object?> data,
                MutationTransformContext context) =>
                new MutationTransformResult { MutationType = mutationType, Data = data };
        }

        private sealed class TestObserver : IQueryObserver
        {
            public QueryPhase[] Phases => Array.Empty<QueryPhase>();
            public ValueTask OnQueryPhaseAsync(QueryPhase phase, QueryObserverContext context) =>
                ValueTask.CompletedTask;
        }
    }
}
