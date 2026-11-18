using System.Text;
using BifrostQL.Server.Auth;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>The TOTP primitive against RFC 6238 Appendix B and its drift and parsing rules.</summary>
    public sealed class TotpTests
    {
        // RFC 6238 Appendix B: the seed is the ASCII string repeated to the hash's block-key size.
        private static byte[] Seed(LocalTotpAlgorithm algorithm) => Encoding.ASCII.GetBytes(algorithm switch
        {
            LocalTotpAlgorithm.Sha1 => "12345678901234567890",
            LocalTotpAlgorithm.Sha256 => "12345678901234567890123456789012",
            _ => "1234567890123456789012345678901234567890123456789012345678901234",
        });

        [Theory]
        [InlineData(59L, LocalTotpAlgorithm.Sha1, "94287082")]
        [InlineData(59L, LocalTotpAlgorithm.Sha256, "46119246")]
        [InlineData(59L, LocalTotpAlgorithm.Sha512, "90693936")]
        [InlineData(1111111109L, LocalTotpAlgorithm.Sha1, "07081804")]
        [InlineData(1111111109L, LocalTotpAlgorithm.Sha256, "68084774")]
        [InlineData(1111111109L, LocalTotpAlgorithm.Sha512, "25091201")]
        [InlineData(1111111111L, LocalTotpAlgorithm.Sha1, "14050471")]
        [InlineData(1111111111L, LocalTotpAlgorithm.Sha256, "67062674")]
        [InlineData(1111111111L, LocalTotpAlgorithm.Sha512, "99943326")]
        [InlineData(1234567890L, LocalTotpAlgorithm.Sha1, "89005924")]
        [InlineData(1234567890L, LocalTotpAlgorithm.Sha256, "91819424")]
        [InlineData(1234567890L, LocalTotpAlgorithm.Sha512, "93441116")]
        [InlineData(2000000000L, LocalTotpAlgorithm.Sha1, "69279037")]
        [InlineData(2000000000L, LocalTotpAlgorithm.Sha256, "90698825")]
        [InlineData(2000000000L, LocalTotpAlgorithm.Sha512, "38618901")]
        [InlineData(20000000000L, LocalTotpAlgorithm.Sha1, "65353130")]
        [InlineData(20000000000L, LocalTotpAlgorithm.Sha256, "77737706")]
        [InlineData(20000000000L, LocalTotpAlgorithm.Sha512, "47863826")]
        public void Code_MatchesTheRfc6238AppendixBVectors(long unixSeconds, LocalTotpAlgorithm algorithm, string expected)
        {
            var step = Totp.TimeStep(DateTimeOffset.FromUnixTimeSeconds(unixSeconds), 30);

            Totp.Code(Seed(algorithm), step, algorithm, 8).Should().Be(expected);
        }

        [Fact]
        public void SixDigitCode_IsTheLowSixDigitsOfTheEightDigitOne()
        {
            var step = Totp.TimeStep(DateTimeOffset.FromUnixTimeSeconds(59), 30);

            Totp.Code(Seed(LocalTotpAlgorithm.Sha1), step, LocalTotpAlgorithm.Sha1, 6).Should().Be("287082");
        }

        [Fact]
        public void Match_AcceptsOneStepEitherSide_AndNothingFurther()
        {
            var key = Seed(LocalTotpAlgorithm.Sha1);
            const long now = 1_000;
            string At(long step) => Totp.Code(key, step, LocalTotpAlgorithm.Sha1, 6);

            Totp.Match(key, At(now), now, LocalTotpAlgorithm.Sha1, 6).Should().Be(now);
            Totp.Match(key, At(now - 1), now, LocalTotpAlgorithm.Sha1, 6).Should().Be(now - 1);
            Totp.Match(key, At(now + 1), now, LocalTotpAlgorithm.Sha1, 6).Should().Be(now + 1);
            Totp.Match(key, At(now - 2), now, LocalTotpAlgorithm.Sha1, 6).Should().BeNull("two steps back is past the drift window");
            Totp.Match(key, At(now + 2), now, LocalTotpAlgorithm.Sha1, 6).Should().BeNull("two steps ahead is past the drift window");
        }

        [Theory]
        [InlineData("")]
        [InlineData("12345")]
        [InlineData("1234567")]
        [InlineData("12345a")]
        [InlineData(" 12345")]
        [InlineData("١٢٣٤٥٦")] // Arabic-Indic digits: char.IsDigit says yes, ASCII says no
        public void Match_RefusesMalformedCodes(string code)
        {
            Totp.Match(Seed(LocalTotpAlgorithm.Sha1), code, 1_000, LocalTotpAlgorithm.Sha1, 6).Should().BeNull();
        }

        [Fact]
        public void TimeStep_IsTheThirtySecondCounter()
        {
            Totp.TimeStep(DateTimeOffset.FromUnixTimeSeconds(59), 30).Should().Be(1);
            Totp.TimeStep(DateTimeOffset.FromUnixTimeSeconds(60), 30).Should().Be(2);
            Totp.TimeStep(DateTimeOffset.FromUnixTimeSeconds(1111111109), 30).Should().Be(0x23523EC);
        }

        [Fact]
        public void Base32_RoundTrips_AndMatchesRfc4648()
        {
            Base32.Encode(Encoding.ASCII.GetBytes("foobar")).Should().Be("MZXW6YTBOI");
            Base32.Encode(Encoding.ASCII.GetBytes("f")).Should().Be("MY");
            var secret = Enumerable.Range(0, 32).Select(i => (byte)(i * 37)).ToArray();
            Base32.Decode(Base32.Encode(secret)).Should().Equal(secret);
            Base32.Decode("mzxw6ytboi").Should().Equal(Encoding.ASCII.GetBytes("foobar"));
        }
    }
}
