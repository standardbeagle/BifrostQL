using BifrostQL.Server.Resp;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// Proves the RESP front door answers an unknown/unsupported command with a clean, Redis-compatible
    /// <c>-ERR unknown command</c> (naming the command and echoing its args as guidance) and — critically —
    /// stays fully responsive afterwards: NO hang, NO deadlock, NO protocol desync. A client sending a
    /// command BifrostQL does not implement (LPUSH/SUBSCRIBE/MULTI/a random token) must be able to issue a
    /// valid command next and get its reply, and the reader must never block waiting for more input on a
    /// well-framed unsupported command — even one carrying arguments.
    /// </summary>
    public sealed class RespUnsupportedCommandTests
    {
        private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

        // Auth is off so a valid PING probes responsiveness without a credential round-trip; the
        // unknown-command reply is produced BEFORE the auth gate anyway, so this isolates the desync check.
        private static RespWireOptions Options() => new() { RequireAuthentication = false };

        [Theory]
        [InlineData("LPUSH", "mylist", "value")]     // an unsupported write with args
        [InlineData("SUBSCRIBE", "channel")]          // pub/sub — unsupported, must not block the reader
        [InlineData("MULTI")]                         // transactions — unsupported, no args
        [InlineData("ZORKMID", "alpha", "beta")]      // a wholly unknown token with args
        public async Task UnsupportedCommand_ReturnsCleanErr_WithGuidance_AndConnectionStaysResponsive(
            params string[] command)
        {
            await using var fixture = await RespFixture.StartAsync(
                new FakeRespCredentialStore(), RespFixture.EmptyServices(), Options(), RespDataHandlers.All());

            await fixture.Client.SendCommandAsync(command);
            var reply = await fixture.Client.ReadReplyAsync().WaitAsync(ReplyTimeout);

            // Clean, honest -ERR that names the command (no hang: the reply arrived within the timeout).
            var error = reply.Should().BeOfType<RespError>().Which.Message;
            error.Should().StartWith($"ERR unknown command '{command[0]}'");
            if (command.Length > 1)
                error.Should().Contain($"'{command[1]}'", "the error echoes the args as Redis-style guidance");

            // The connection is not desynced or closed: a subsequent valid command still answers.
            await fixture.Client.SendCommandAsync("PING");
            var pong = await fixture.Client.ReadReplyAsync().WaitAsync(ReplyTimeout);
            pong.Should().BeOfType<RespSimpleString>().Which.Value.Should().Be(RespProtocol.Pong);
        }

        [Fact]
        public async Task ManyUnsupportedCommandsInSequence_NeverDesync_EachGetsItsOwnErr()
        {
            await using var fixture = await RespFixture.StartAsync(
                new FakeRespCredentialStore(), RespFixture.EmptyServices(), Options(), RespDataHandlers.All());

            // A stream of unsupported commands with mixed arities must produce exactly one -ERR each, in
            // order — proving the reader consumed each full frame and never fell out of frame alignment.
            var commands = new[]
            {
                new[] { "LPUSH", "k", "v" },
                new[] { "MULTI" },
                new[] { "SUBSCRIBE", "a", "b", "c" },
                new[] { "WATCH", "key" },
            };

            foreach (var command in commands)
            {
                await fixture.Client.SendCommandAsync(command);
                var reply = await fixture.Client.ReadReplyAsync().WaitAsync(ReplyTimeout);
                reply.Should().BeOfType<RespError>()
                    .Which.Message.Should().StartWith($"ERR unknown command '{command[0]}'");
            }

            // Still in frame: a valid PING answers after the whole run.
            await fixture.Client.SendCommandAsync("PING", "still-alive");
            var echoed = await fixture.Client.ReadReplyAsync().WaitAsync(ReplyTimeout);
            System.Text.Encoding.UTF8.GetString(
                echoed.Should().BeOfType<RespBulkString>().Which.Value!).Should().Be("still-alive");
        }
    }
}
