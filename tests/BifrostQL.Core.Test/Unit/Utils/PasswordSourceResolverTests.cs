using BifrostQL.Core.Utils;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Utils
{
    /// <summary>
    /// Tests for <see cref="PasswordSourceResolver"/>. The resolver encodes the
    /// precedence rules for <c>bifrostui vault add</c>'s password inputs
    /// (<c>--password</c>, <c>--password-stdin</c>, interactive prompt, legacy
    /// stdin fallback). Each test pins one branch of the decision tree.
    /// </summary>
    public class PasswordSourceResolverTests
    {
        private static System.Func<string?> NoStdin()
            => () => throw new System.InvalidOperationException("stdin should not be read in this case");

        private static System.Func<string?> StdinReturns(string? line)
            => () => line;

        // ---------- Mutual exclusion ----------

        [Fact]
        public void Resolve_BothPasswordAndPasswordStdin_ReturnsMutexError()
        {
            // Arrange
            // Act
            var result = PasswordSourceResolver.Resolve(
                hasPasswordFlag: true,
                passwordValue: "foo",
                passwordStdin: true,
                stdinIsRedirected: true,
                readStdinLine: NoStdin());

            // Assert
            result.Kind.Should().Be(PasswordSourceKind.Error);
            result.ErrorMessage.Should().Be("Error: --password and --password-stdin are mutually exclusive");
        }

        [Fact]
        public void Resolve_BothFlagsSet_DoesNotReadStdin()
        {
            // Arrange: mutex check must short-circuit before any stdin access
            var stdinCalled = false;
            System.Func<string?> reader = () => { stdinCalled = true; return "anything"; };

            // Act
            PasswordSourceResolver.Resolve(
                hasPasswordFlag: true,
                passwordValue: "foo",
                passwordStdin: true,
                stdinIsRedirected: true,
                readStdinLine: reader);

            // Assert
            stdinCalled.Should().BeFalse();
        }

        // ---------- --password-stdin with tty ----------

        [Fact]
        public void Resolve_PasswordStdin_WithTty_ReturnsRedirectedError()
        {
            // Arrange
            // Act
            var result = PasswordSourceResolver.Resolve(
                hasPasswordFlag: false,
                passwordValue: null,
                passwordStdin: true,
                stdinIsRedirected: false,
                readStdinLine: NoStdin());

            // Assert
            result.Kind.Should().Be(PasswordSourceKind.Error);
            result.ErrorMessage.Should().Be("Error: --password-stdin requires stdin to be redirected");
        }

        // ---------- --password-stdin with empty piped input ----------

        [Theory]
        [InlineData("")]
        [InlineData("\n")]
        [InlineData("\r\n")]
        [InlineData("   ")]
        [InlineData("  \t  ")]
        [InlineData(null)]
        public void Resolve_PasswordStdin_EmptyOrWhitespace_ReturnsEmptyError(string? piped)
        {
            // Arrange
            // Act
            var result = PasswordSourceResolver.Resolve(
                hasPasswordFlag: false,
                passwordValue: null,
                passwordStdin: true,
                stdinIsRedirected: true,
                readStdinLine: StdinReturns(piped));

            // Assert
            result.Kind.Should().Be(PasswordSourceKind.Error);
            result.ErrorMessage.Should().Be("Error: --password-stdin received empty input");
        }

        // ---------- --password-stdin happy path ----------

        [Theory]
        [InlineData("secret", "secret")]
        [InlineData("secret\n", "secret")]
        [InlineData("secret\r\n", "secret")]
        [InlineData("  pass with space ", "  pass with space")] // leading spaces preserved
        [InlineData("p@$$w0rd!", "p@$$w0rd!")]
        public void Resolve_PasswordStdin_ValidInput_ReturnsTrimmedValue(string piped, string expected)
        {
            // Arrange
            // Act
            var result = PasswordSourceResolver.Resolve(
                hasPasswordFlag: false,
                passwordValue: null,
                passwordStdin: true,
                stdinIsRedirected: true,
                readStdinLine: StdinReturns(piped));

            // Assert
            result.Kind.Should().Be(PasswordSourceKind.Value);
            result.Value.Should().Be(expected);
            result.LegacyStdinFallback.Should().BeFalse();
        }

        // ---------- --password explicit value ----------

        [Fact]
        public void Resolve_PasswordFlag_ReturnsValueVerbatim()
        {
            // Arrange
            // Act
            var result = PasswordSourceResolver.Resolve(
                hasPasswordFlag: true,
                passwordValue: "hunter2",
                passwordStdin: false,
                stdinIsRedirected: false,
                readStdinLine: NoStdin());

            // Assert
            result.Kind.Should().Be(PasswordSourceKind.Value);
            result.Value.Should().Be("hunter2");
            result.LegacyStdinFallback.Should().BeFalse();
        }

        [Fact]
        public void Resolve_PasswordFlag_EmptyStringIsAllowed()
        {
            // Arrange: --password "" is unusual but explicit; we pass it through.
            // Act
            var result = PasswordSourceResolver.Resolve(
                hasPasswordFlag: true,
                passwordValue: "",
                passwordStdin: false,
                stdinIsRedirected: false,
                readStdinLine: NoStdin());

            // Assert
            result.Kind.Should().Be(PasswordSourceKind.Value);
            result.Value.Should().Be("");
        }

        [Fact]
        public void Resolve_PasswordFlag_DoesNotReadStdin_EvenWhenRedirected()
        {
            // Arrange
            var stdinCalled = false;
            System.Func<string?> reader = () => { stdinCalled = true; return "noise"; };

            // Act
            var result = PasswordSourceResolver.Resolve(
                hasPasswordFlag: true,
                passwordValue: "cli-pw",
                passwordStdin: false,
                stdinIsRedirected: true,
                readStdinLine: reader);

            // Assert
            stdinCalled.Should().BeFalse();
            result.Value.Should().Be("cli-pw");
        }

        // ---------- Interactive prompt ----------

        [Fact]
        public void Resolve_NoFlags_TtyStdin_ReturnsInteractive()
        {
            // Arrange
            // Act
            var result = PasswordSourceResolver.Resolve(
                hasPasswordFlag: false,
                passwordValue: null,
                passwordStdin: false,
                stdinIsRedirected: false,
                readStdinLine: NoStdin());

            // Assert
            result.Kind.Should().Be(PasswordSourceKind.Interactive);
            result.Value.Should().BeNull();
            result.ErrorMessage.Should().BeNull();
            result.LegacyStdinFallback.Should().BeFalse();
        }

        // ---------- Legacy stdin back-compat ----------

        [Fact]
        public void Resolve_NoFlags_RedirectedStdin_ReadsAsLegacyFallback()
        {
            // Arrange
            // Act
            var result = PasswordSourceResolver.Resolve(
                hasPasswordFlag: false,
                passwordValue: null,
                passwordStdin: false,
                stdinIsRedirected: true,
                readStdinLine: StdinReturns("legacy-pw\n"));

            // Assert
            result.Kind.Should().Be(PasswordSourceKind.Value);
            result.Value.Should().Be("legacy-pw");
            result.LegacyStdinFallback.Should().BeTrue();
        }

        [Fact]
        public void Resolve_LegacyFallback_EmptyInput_StillReturnsValue()
        {
            // Arrange: legacy path does NOT validate non-empty; only --password-stdin does.
            // This preserves existing script behavior.
            // Act
            var result = PasswordSourceResolver.Resolve(
                hasPasswordFlag: false,
                passwordValue: null,
                passwordStdin: false,
                stdinIsRedirected: true,
                readStdinLine: StdinReturns(""));

            // Assert
            result.Kind.Should().Be(PasswordSourceKind.Value);
            result.Value.Should().Be("");
            result.LegacyStdinFallback.Should().BeTrue();
        }

        // ---------- Null reader guard ----------

        [Fact]
        public void Resolve_NullReader_Throws()
        {
            // Arrange
            // Act
            System.Action act = () => PasswordSourceResolver.Resolve(
                hasPasswordFlag: false,
                passwordValue: null,
                passwordStdin: true,
                stdinIsRedirected: true,
                readStdinLine: null!);

            // Assert
            act.Should().Throw<System.ArgumentNullException>();
        }
    }
}
