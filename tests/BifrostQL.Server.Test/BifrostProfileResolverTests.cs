using BifrostQL.Core.Modules;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Finding 12: profile selection is limited to the explicit header and query channels;
    /// the "any trailing path segment is a profile name" fallback is removed, and unknown-
    /// profile errors never echo raw attacker-controlled input verbatim.
    /// </summary>
    public class BifrostProfileResolverTests
    {
        [Fact]
        public void PathSegment_IsNoLongerTreatedAsProfileName()
        {
            var context = new DefaultHttpContext();
            // After app.Map("/graphql"), Path holds the remainder, e.g. "/v1".
            context.Request.Path = "/v1";

            BifrostProfileResolver.ResolveProfileName(context).Should().BeNull(
                "a trailing path segment must not be interpreted as a profile selector");
        }

        [Fact]
        public void HeaderChannel_StillResolvesProfileName()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["X-BifrostQL-Profile"] = "reporting";

            BifrostProfileResolver.ResolveProfileName(context).Should().Be("reporting");
        }

        [Fact]
        public void QueryChannel_StillResolvesProfileName()
        {
            var context = new DefaultHttpContext();
            context.Request.QueryString = new QueryString("?profile=reporting");

            BifrostProfileResolver.ResolveProfileName(context).Should().Be("reporting");
        }

        [Fact]
        public void UnknownProfile_WithUnsafeName_DoesNotEchoRawInput()
        {
            var registry = new BifrostProfileRegistry();
            var context = new DefaultHttpContext();
            var attack = "<script>alert(1)</script>";
            context.Request.Headers["X-BifrostQL-Profile"] = attack;

            var result = BifrostProfileResolver.Resolve(registry, context);

            result.HasError.Should().BeTrue();
            result.ErrorMessage.Should().NotContain(attack,
                "attacker-controlled profile text must not be reflected into the error verbatim");
            result.ErrorMessage.Should().Be("Unknown profile.");
        }

        [Fact]
        public void UnknownProfile_WithSafeName_MayEchoTheName()
        {
            var registry = new BifrostProfileRegistry();
            var context = new DefaultHttpContext();
            context.Request.Headers["X-BifrostQL-Profile"] = "reporting";

            var result = BifrostProfileResolver.Resolve(registry, context);

            result.HasError.Should().BeTrue();
            result.ErrorMessage.Should().Be("Unknown profile 'reporting'.");
        }
    }
}
