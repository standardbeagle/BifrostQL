using BifrostQL.Server;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    public class SpaHostingExtensionsTests
    {
        [Theory]
        [InlineData("/graphql")]
        [InlineData("/graphql/sales")]
        [InlineData("/api")]
        [InlineData("/api/users")]
        [InlineData("/health")]
        [InlineData("/")]
        public void ExcludedPrefixes_BypassSpaFallback(string path)
        {
            var options = new BifrostSpaOptions
            {
                ExcludedPathPrefixes = new[] { "/graphql", "/api", "/health", "/" },
            };

            SpaHostingExtensions.IsExcludedFromSpaFallback(path, options)
                .Should().BeTrue($"'{path}' matches an excluded prefix");
        }

        [Theory]
        [InlineData("/app")]
        [InlineData("/dashboard/orders")]
        [InlineData("/users/42")]
        public void NonExcludedPaths_AllowSpaFallback(string path)
        {
            var options = new BifrostSpaOptions
            {
                ExcludedPathPrefixes = new[] { "/graphql", "/api", "/health" },
            };

            SpaHostingExtensions.IsExcludedFromSpaFallback(path, options)
                .Should().BeFalse($"'{path}' does not match any excluded prefix");
        }

        [Fact]
        public void PrefixMatch_IsCaseInsensitive()
        {
            var options = new BifrostSpaOptions
            {
                ExcludedPathPrefixes = new[] { "/GraphQL" },
            };

            SpaHostingExtensions.IsExcludedFromSpaFallback("/graphql/sales", options)
                .Should().BeTrue();
        }

        [Fact]
        public void PrefixMatch_RequiresSegmentBoundary()
        {
            // "/apixyz" must NOT be treated as under the "/api" prefix.
            var options = new BifrostSpaOptions
            {
                ExcludedPathPrefixes = new[] { "/api" },
            };

            SpaHostingExtensions.IsExcludedFromSpaFallback("/apixyz", options)
                .Should().BeFalse();
        }

        [Fact]
        public void RootPrefix_ExcludesOnlyExactRoot()
        {
            // A "/" prefix is the playground path default; it should match the root
            // request exactly but not steal every SPA route.
            var options = new BifrostSpaOptions
            {
                ExcludedPathPrefixes = new[] { "/" },
            };

            SpaHostingExtensions.IsExcludedFromSpaFallback("/", options).Should().BeTrue();
            SpaHostingExtensions.IsExcludedFromSpaFallback("/app", options).Should().BeFalse();
        }

        [Fact]
        public void DefaultOptions_ExcludeGraphQLAndApiAndHealth()
        {
            var options = new BifrostSpaOptions();

            SpaHostingExtensions.IsExcludedFromSpaFallback("/graphql", options).Should().BeTrue();
            SpaHostingExtensions.IsExcludedFromSpaFallback("/api/anything", options).Should().BeTrue();
            SpaHostingExtensions.IsExcludedFromSpaFallback("/health", options).Should().BeTrue();
            SpaHostingExtensions.IsExcludedFromSpaFallback("/dashboard", options).Should().BeFalse();
        }

        [Fact]
        public void AddExcludedPathPrefix_AppendsToExclusions()
        {
            var options = new BifrostSpaOptions();
            options.AddExcludedPathPrefix("/metrics");

            SpaHostingExtensions.IsExcludedFromSpaFallback("/metrics", options).Should().BeTrue();
        }
    }
}
