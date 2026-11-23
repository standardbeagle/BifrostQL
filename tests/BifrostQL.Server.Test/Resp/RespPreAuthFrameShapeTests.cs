using System.Security.Claims;
using System.Text;
using BifrostQL.Server.Auth;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// Before AUTH a RESP session may only send what AUTH, HELLO, PING, QUIT and RESET need: a
    /// flat array of at most seven bulk strings within <see cref="RespWireOptions.MaxPreAuthFrameLength"/>
    /// bytes. Without these limits any peer that reached the port could make the server decode
    /// and buffer a <see cref="RespWireOptions.MaxFrameLength"/> (1 MiB) frame per connection,
    /// nested aggregates included, before proving who it is.
    ///
    /// <para>Each axis (shape, element count, bytes) has its own refusal fact and its own
    /// post-auth control, so each limit binds alone. Every refusal answers the same fixed text
    /// and closes the connection.</para>
    /// </summary>
    public sealed class RespPreAuthFrameShapeTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
        private const string User = "alice";
        private const string Secret = "s3cret";
        private const string Refused = RespProtocol.ProtocolErrorPrefix + RespProtocol.PreAuthFrameRefusedDetail;

        // ---- shape ----------------------------------------------------------

        [Theory]
        [InlineData("*2\r\n$4\r\nPING\r\n*1\r\n$1\r\nx\r\n")]    // nested array
        [InlineData("*2\r\n$4\r\nPING\r\n:1\r\n")]               // integer element
        [InlineData("*2\r\n$4\r\nPING\r\n+hi\r\n")]              // simple-string element
        [InlineData("*2\r\n$4\r\nPING\r\n%1\r\n$1\r\nk\r\n$1\r\nv\r\n")] // map element
        [InlineData("*2\r\n$4\r\nPING\r\n$-1\r\n")]              // null bulk element
        [InlineData("*-1\r\n")]                                   // null array
        public async Task A_non_flat_bulk_string_frame_is_refused_before_auth(string raw)
        {
            await using var fixture = await StartAsync();

            await fixture.Client.SendRawAsync(Encoding.ASCII.GetBytes(raw));

            await ExpectRefusedAndClosedAsync(fixture);
        }

        [Fact]
        public async Task A_simple_string_argument_is_accepted_after_auth()
        {
            // Control for the shape axis: the general decoder still takes a simple-string
            // argument once the session has an identity.
            await using var fixture = await StartAsync();
            await AuthenticateAsync(fixture);

            await fixture.Client.SendRawAsync(Encoding.ASCII.GetBytes("*2\r\n$4\r\nPING\r\n+hi\r\n"));

            Bulk(await ReadAsync(fixture)).Should().Be("hi");
        }

        [Theory]
        [InlineData("PING\r\n")]
        [InlineData("AUTH alice s3cret\r\n")]
        public async Task An_inline_command_is_refused_before_auth(string inline)
        {
            await using var fixture = await StartAsync();

            await fixture.Client.SendRawAsync(Encoding.ASCII.GetBytes(inline));

            await ExpectRefusedAndClosedAsync(fixture);
        }

        [Fact]
        public async Task A_long_inline_command_is_refused_at_its_first_byte()
        {
            // 64 KiB with no CR: the decoder would otherwise read a line up to MaxBulkLength.
            // The refusal comes at the first byte, so the reply arrives without the line ending.
            await using var fixture = await StartAsync();

            await fixture.Client.SendRawAsync(Encoding.ASCII.GetBytes("AUTH " + new string('x', 64 * 1024)));

            await ExpectRefusedAndClosedAsync(fixture);
        }

        // ---- element count --------------------------------------------------

        [Fact]
        public async Task Eight_elements_are_refused_before_auth()
        {
            await using var fixture = await StartAsync();

            await fixture.Client.SendCommandAsync("HELLO", "3", "AUTH", User, Secret, "SETNAME", "n", "extra");

            await ExpectRefusedAndClosedAsync(fixture);
        }

        [Fact]
        public async Task Eight_elements_reach_the_command_after_auth()
        {
            // Control for the count axis: the same frame is dispatched once authenticated, and
            // HELLO's own parser answers it while the connection stays open.
            await using var fixture = await StartAsync();
            await AuthenticateAsync(fixture);

            await fixture.Client.SendCommandAsync("HELLO", "3", "AUTH", User, Secret, "SETNAME", "n", "extra");
            Error(await ReadAsync(fixture)).Should().Be("ERR Syntax error in HELLO");

            await fixture.Client.SendCommandAsync("PING");
            (await ReadAsync(fixture)).Should().BeOfType<RespSimpleString>().Which.Value.Should().Be("PONG");
        }

        // ---- bytes ------------------------------------------------------------

        [Fact]
        public async Task The_longest_legal_hello_at_the_byte_cap_authenticates()
        {
            // Seven elements and exactly MaxPreAuthFrameLength bytes: the largest frame an
            // unauthenticated session may send must still log in.
            var options = Options();
            await using var fixture = await StartAsync(options);

            var frame = HelloFrameOfLength(options.MaxPreAuthFrameLength);
            frame.Length.Should().Be(options.MaxPreAuthFrameLength);
            await fixture.Client.SendRawAsync(frame);

            var reply = await ReadAsync(fixture);
            reply.Should().BeOfType<RespMap>("HELLO 3 with valid AUTH answers the RESP3 server map");
        }

        [Fact]
        public async Task One_byte_over_the_cap_is_refused_before_auth()
        {
            var options = Options();
            await using var fixture = await StartAsync(options);

            await fixture.Client.SendRawAsync(HelloFrameOfLength(options.MaxPreAuthFrameLength + 1));

            await ExpectRefusedAndClosedAsync(fixture);
        }

        [Fact]
        public async Task A_frame_over_the_byte_cap_sent_in_chunks_is_refused_before_auth()
        {
            // Every element is small enough for MaxBulkLength and the count is legal; only the
            // total breaks the cap. RESP declares no total, so the cap must count received bytes.
            await using var fixture = await StartAsync();

            await SendInChunksAsync(fixture, OversizedAuthFrame());

            await ExpectRefusedAndClosedAsync(fixture);
        }

        [Fact]
        public async Task A_frame_over_the_byte_cap_is_dispatched_after_auth()
        {
            // Control for the byte axis: the same frame is an ordinary failed AUTH once the
            // session is authenticated, and the connection stays open.
            await using var fixture = await StartAsync();
            await AuthenticateAsync(fixture);

            await SendInChunksAsync(fixture, OversizedAuthFrame());
            Error(await ReadAsync(fixture)).Should().StartWith("WRONGPASS");

            await fixture.Client.SendCommandAsync("PING");
            (await ReadAsync(fixture)).Should().BeOfType<RespSimpleString>().Which.Value.Should().Be("PONG");
        }

        [Fact]
        public async Task A_declared_bulk_over_the_byte_cap_is_refused_without_waiting_for_its_payload()
        {
            // The payload is never sent. A decoder that trusted the declared length would sit
            // waiting for 100,000 bytes; the budget check refuses at the length line.
            await using var fixture = await StartAsync();

            await fixture.Client.SendRawAsync(Encoding.ASCII.GetBytes("*3\r\n$4\r\nAUTH\r\n$5\r\nalice\r\n$100000\r\n"));

            await ExpectRefusedAndClosedAsync(fixture, TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task The_reader_refuses_a_declared_bulk_over_the_budget_before_reading_its_payload()
        {
            // The payload IS on the wire here: the refusal must come before it is pulled or allocated.
            const int payloadLength = 64 * 1024;
            var wire = new MemoryStream();
            wire.Write(Encoding.ASCII.GetBytes($"*2\r\n$4\r\nAUTH\r\n${payloadLength}\r\n"));
            wire.Write(new byte[payloadLength]);
            wire.Write(Encoding.ASCII.GetBytes("\r\n"));
            wire.Position = 0;
            var counting = new CountingStream(wire);
            var reader = new RespReader(counting, 1 << 20, 1 << 20, 32, maxFrameLength: 1 << 20);

            var act = async () => await reader.ReadUnauthenticatedCommandAsync(4096, default);

            await act.Should().ThrowAsync<RespPreAuthFrameException>();
            // One 8 KiB buffer fill is the floor; nothing proportional to 64 KiB was read.
            counting.BytesRead.Should().BeLessThanOrEqualTo(8192);
        }

        // ---- phases ----------------------------------------------------------

        [Fact]
        public async Task Reset_to_anonymous_brings_the_pre_auth_limits_back()
        {
            await using var fixture = await StartAsync();
            await AuthenticateAsync(fixture);
            await fixture.Client.SendCommandAsync("RESET");
            (await ReadAsync(fixture)).Should().BeOfType<RespSimpleString>().Which.Value.Should().Be("RESET");

            await fixture.Client.SendCommandAsync("HELLO", "3", "AUTH", User, Secret, "SETNAME", "n", "extra");

            await ExpectRefusedAndClosedAsync(fixture);
        }

        [Fact]
        public async Task A_front_door_without_authentication_keeps_the_regular_caps()
        {
            // No pre-auth phase exists when authentication is not required, so the tighter caps
            // must not apply there.
            await using var fixture = await StartAsync(new RespWireOptions { RequireAuthentication = false });

            await fixture.Client.SendCommandAsync("PING", new string('x', 16 * 1024));

            Bulk(await ReadAsync(fixture)).Should().HaveLength(16 * 1024);
        }

        // ---- configuration ------------------------------------------------------

        [Fact]
        public void The_listener_declares_a_concrete_pre_auth_frame_cap()
        {
            // Load-bearing: AGENTS.md's posture table records this number for the RESP row.
            new RespWireOptions().MaxPreAuthFrameLength.Should().Be(4096);
        }

        [Theory]
        [InlineData(RespProtocol.MinPreAuthFrameLength - 1, 1 << 20)]
        [InlineData(8192, 4096)]
        public async Task The_listener_refuses_to_start_with_an_out_of_range_pre_auth_cap(int preAuth, int frame)
        {
            var adapter = new RespWireAdapter(
                new RespWireOptions { AllowCleartextAuth = true, MaxPreAuthFrameLength = preAuth, MaxFrameLength = frame },
                new FakeRespCredentialStore());

            var act = () => adapter.StartAsync(default);

            (await act.Should().ThrowAsync<ArgumentOutOfRangeException>())
                .Which.ParamName.Should().Be(nameof(RespWireOptions.MaxPreAuthFrameLength));
        }

        [Theory]
        [InlineData(RespProtocol.MinPreAuthFrameLength, 1 << 20)]
        [InlineData(4096, 4096)]
        public async Task The_listener_starts_with_a_pre_auth_cap_in_range(int preAuth, int frame)
        {
            var adapter = new RespWireAdapter(
                new RespWireOptions { AllowCleartextAuth = true, MaxPreAuthFrameLength = preAuth, MaxFrameLength = frame },
                new FakeRespCredentialStore());

            await adapter.StartAsync(default);
        }

        // ---- helpers ------------------------------------------------------------

        private static RespWireOptions Options() => new() { AllowCleartextAuth = true };

        private static Task<RespFixture> StartAsync(RespWireOptions? options = null)
            => RespFixture.StartAsync(
                new FakeRespCredentialStore().Add(User, Secret, Principal()),
                RespFixture.EmptyServices(),
                options ?? Options());

        private static ClaimsPrincipal Principal() =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "user-alice"),
                new Claim(LocalAuthClaims.Tenant, "tenant-a"),
            }, authenticationType: "resp"));

        private static async Task AuthenticateAsync(RespFixture fixture)
        {
            await fixture.Client.SendCommandAsync("AUTH", User, Secret);
            (await ReadAsync(fixture)).Should().BeOfType<RespSimpleString>("the arrange step must authenticate");
        }

        private static async Task ExpectRefusedAndClosedAsync(RespFixture fixture, TimeSpan? within = null)
        {
            var wait = within ?? Timeout;
            var reply = await fixture.Client.ReadReplyAsync().WaitAsync(wait);
            reply.Should().NotBeNull("the server must answer the refused frame before closing");
            Error(reply!).Should().Be(Refused);
            (await fixture.Client.ReadReplyAsync().WaitAsync(wait))
                .Should().BeNull("the connection closes after a refused pre-auth frame");
        }

        private static async Task<RespValue> ReadAsync(RespFixture fixture)
        {
            var reply = await fixture.Client.ReadReplyAsync().WaitAsync(Timeout);
            reply.Should().NotBeNull("the server must answer every command");
            return reply!;
        }

        private static string Error(RespValue value)
            => value.Should().BeOfType<RespError>().Subject.Message;

        private static string? Bulk(RespValue value)
        {
            var bytes = value.Should().BeOfType<RespBulkString>().Subject.Value;
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }

        private static byte[] Frame(params string[] arguments)
        {
            var sb = new StringBuilder();
            sb.Append('*').Append(arguments.Length).Append("\r\n");
            foreach (var argument in arguments)
                sb.Append('$').Append(Encoding.UTF8.GetByteCount(argument)).Append("\r\n").Append(argument).Append("\r\n");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary><c>HELLO 3 AUTH alice s3cret SETNAME &lt;name&gt;</c>, the name padded so the frame is exactly <paramref name="length"/> bytes.</summary>
        private static byte[] HelloFrameOfLength(int length)
        {
            var nameLength = length - Frame("HELLO", "3", "AUTH", User, Secret, "SETNAME", "").Length;
            // Padding the name also widens its length prefix; shrink until the total lands exactly.
            while (Frame("HELLO", "3", "AUTH", User, Secret, "SETNAME", new string('n', nameLength)).Length > length)
                nameLength--;
            return Frame("HELLO", "3", "AUTH", User, Secret, "SETNAME", new string('n', nameLength));
        }

        /// <summary>AUTH with a 5,000-byte password: three legal elements, about 5 KiB in total.</summary>
        private static byte[] OversizedAuthFrame() => Frame("AUTH", User, new string('p', 5000));

        private static async Task SendInChunksAsync(RespFixture fixture, byte[] frame)
        {
            const int chunk = 512;
            for (var offset = 0; offset < frame.Length; offset += chunk)
            {
                try
                {
                    await fixture.Client.SendRawAsync(frame.AsSpan(offset, Math.Min(chunk, frame.Length - offset)).ToArray());
                }
                catch (IOException)
                {
                    return; // the server refused mid-frame and closed; the reply is already queued
                }
            }
        }
    }
}
