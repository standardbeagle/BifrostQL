using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BifrostQL.Core.Modules.Chat;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Connector slice 5 at the HTTP seam: the plan confirmation round-trip. The
    /// middleware relays a parked proposal as the SSE <c>confirmation</c> event,
    /// <c>POST {Path}/conversations/{id}/confirmations/{confirmationId}</c> resolves
    /// it fail-closed (identity- and conversation-bound, single-use — every mismatch
    /// is the same 404), the confirmed write lands through the batch intent seam with
    /// tenant stamp + history trail, and BOTH outcomes are recorded in the
    /// conversation as a system-role transcript row. The completion service is the
    /// scripted fake driving the real default-registered <see cref="PlanChatConnector"/>
    /// through the same contract the real loop runs (the loop itself is pinned in
    /// PlanChatConnectorTests).
    /// </summary>
    public class ChatEndpointPlanTests : IAsyncLifetime
    {
        private const string PlanTool = "plan_insert_posts";
        private const string ProposalInput =
            """{"rows":[{"title":"Launch note","publish_at":"2026-08-01 10:00:00","status":"scheduled"}]}""";

        private readonly ChatEndpointHost _h = new();

        public async Task InitializeAsync()
        {
            await _h.InitializeAsync();
            await _h.ExecAsync(
                """
                CREATE TABLE posts (
                    id         INTEGER PRIMARY KEY,
                    tenant_id  TEXT NOT NULL,
                    title      TEXT NULL,
                    publish_at DATETIME NULL,
                    status     TEXT NULL
                )
                """);
        }

        public async Task DisposeAsync() => await _h.DisposeAsync();

        private static readonly string[] PlanMetadata =
        {
            "*.posts { chat-connector: plan; chat-plan-operations: insert,update; " +
                "tenant-filter: tenant_id; history: enabled; history-columns: title,publish_at,status }",
        };

        /// <summary>
        /// Drives the plan connector through the confirmation contract exactly as the
        /// real tool loop does: execute (proposal only), relay the confirmation
        /// activity, PARK on ResolveAsync, relay the decision, feed the outcome back.
        /// </summary>
        private void ScriptPlanCall(string answerWhenApproved = "Scheduled.")
        {
            _h.Fake.OptionsScript = (_, options, ct) => PlanCall(options!, ct);

            async IAsyncEnumerable<ChatCompletionEvent> PlanCall(
                ChatCompletionRequestOptions options, [EnumeratorCancellation] CancellationToken ct)
            {
                var tools = options.Tools!;
                tools.Tools.Single(t => t.Name == PlanTool).RequiresConfirmation.Should().BeTrue();
                yield return new ChatToolActivity(PlanTool, ChatToolPhase.Call, "proposing");

                var result = await tools.Executor.ExecuteAsync(PlanTool, ProposalInput, ct);
                var request = result.ConfirmationRequest!;
                yield return new ChatToolConfirmationActivity(PlanTool, request);

                var outcome = await request.ResolveAsync(ct);
                yield return new ChatToolConfirmationDecisionActivity(
                    PlanTool, request.ConfirmationId, request.Table, request.Operation,
                    outcome.Approved, outcome.Reason);
                yield return new ChatToolActivity(PlanTool, ChatToolPhase.Result, outcome.Result.TextPayload);

                var answer = outcome.Approved ? answerWhenApproved : "Okay, I won't schedule it.";
                yield return new ChatCompletionDelta(answer);
                yield return new ChatCompletionResult(answer, ChatCompletionStopReason.Complete, null, 1, 1);
            }
        }

        private static Task<HttpResponseMessage> ResolveAsync(
            HttpClient client, long conversationId, string confirmationId, object body, string tenant = "tenant-a") =>
            client.SendAsync(ChatEndpointHost.Post(
                $"/_chat/conversations/{conversationId}/confirmations/{confirmationId}",
                body, $"user-of-{tenant}", tenant));

        private static async Task<(StreamReader Reader, SseEvent Confirmation)> ReadUntilConfirmationAsync(
            HttpResponseMessage response)
        {
            var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            while (true)
            {
                var evt = await SseReader.ReadNextAsync(reader);
                if (evt.Name == "confirmation")
                    return (reader, evt);
            }
        }

        [Fact]
        public async Task Confirm_RoundTrip_RelaysTheProposal_PersistsOnApprove_AndRecordsTheTranscript()
        {
            // Arrange
            var client = await _h.StartAsync(extraMetadata: PlanMetadata);
            ScriptPlanCall();
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");

            // Act: stream until the proposal parks.
            using var response = await _h.PostMessageAsync(
                client, conversationId, "Schedule the launch note",
                completion: HttpCompletionOption.ResponseHeadersRead);
            var (reader, confirmation) = await ReadUntilConfirmationAsync(response);

            // The proposal is data the client can render — and NOTHING is written yet.
            confirmation.Data.GetProperty("toolName").GetString().Should().Be(PlanTool);
            confirmation.Data.GetProperty("table").GetString().Should().Be("posts");
            confirmation.Data.GetProperty("operation").GetString().Should().Be("insert");
            confirmation.Data.GetProperty("summary").GetString().Should().Be("insert 1 row into main.posts");
            confirmation.Data.GetProperty("rows")[0].GetProperty("title").GetString().Should().Be("Launch note");
            var confirmationId = confirmation.Data.GetProperty("confirmationId").GetString()!;
            (await _h.ScalarAsync("SELECT COUNT(*) FROM posts")).Should().Be(0L,
                "a plan tool call never writes — it proposes");

            // Approve from a second request (the streaming one is parked).
            using var approve = await ResolveAsync(client, conversationId, confirmationId, new { approve = true });
            approve.StatusCode.Should().Be(HttpStatusCode.OK);

            // The stream resumes: decision relayed, tool result, final answer.
            var resolved = await SseReader.ReadNextAsync(reader);
            resolved.Name.Should().Be("confirmation-resolved");
            resolved.Data.GetProperty("approved").GetBoolean().Should().BeTrue();
            (await SseReader.ReadNextAsync(reader)).Name.Should().Be("tool");
            (await SseReader.ReadNextAsync(reader)).Name.Should().Be("delta");
            var done = await SseReader.ReadNextAsync(reader);
            done.Name.Should().Be("done");

            // The confirmed write landed through the full pipeline: tenant stamp and
            // the change-history trail, in one transaction.
            (await _h.ScalarAsync(
                "SELECT COUNT(*) FROM posts WHERE title = 'Launch note' AND tenant_id = 'tenant-a'"))
                .Should().Be(1L);
            (await _h.ScalarAsync(
                "SELECT COUNT(*) FROM __history WHERE entity LIKE '%posts%' AND op = 'insert'"))
                .Should().Be(1L);

            // Transcript fidelity: the outcome is a stored system-role row.
            (await _h.ScalarAsync("SELECT content FROM messages WHERE role = 'system'"))
                .Should().Be($"[plan proposal {confirmationId} (insert on posts): approved]");
            (await _h.ScalarAsync("SELECT content FROM messages WHERE role = 'assistant'"))
                .Should().Be("Scheduled.");
        }

        [Fact]
        public async Task Deny_PersistsNothing_ModelContinues_AndTheTranscriptRecordsTheReason()
        {
            var client = await _h.StartAsync(extraMetadata: PlanMetadata);
            ScriptPlanCall();
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");

            using var response = await _h.PostMessageAsync(
                client, conversationId, "Schedule the launch note",
                completion: HttpCompletionOption.ResponseHeadersRead);
            var (reader, confirmation) = await ReadUntilConfirmationAsync(response);
            var confirmationId = confirmation.Data.GetProperty("confirmationId").GetString()!;

            using var deny = await ResolveAsync(client, conversationId, confirmationId,
                new { approve = false, reason = "wrong date" });
            deny.StatusCode.Should().Be(HttpStatusCode.OK);

            var resolved = await SseReader.ReadNextAsync(reader);
            resolved.Name.Should().Be("confirmation-resolved");
            resolved.Data.GetProperty("approved").GetBoolean().Should().BeFalse();
            resolved.Data.GetProperty("reason").GetString().Should().Be("wrong date");
            // The model receives the declined result and CONTINUES to a normal answer.
            (await SseReader.ReadNextAsync(reader)).Name.Should().Be("tool");
            (await SseReader.ReadNextAsync(reader)).Name.Should().Be("delta");
            (await SseReader.ReadNextAsync(reader)).Name.Should().Be("done");

            (await _h.ScalarAsync("SELECT COUNT(*) FROM posts")).Should().Be(0L, "denied means nothing is written");
            // The chat pair's own history rows exist (messages are history-enabled);
            // the DENIED proposal recorded no posts trail.
            (await _h.ScalarAsync("SELECT COUNT(*) FROM __history WHERE entity LIKE '%posts%'")).Should().Be(0L);
            (await _h.ScalarAsync("SELECT content FROM messages WHERE role = 'system'"))
                .Should().Be($"[plan proposal {confirmationId} (insert on posts): denied, reason: wrong date]");
            (await _h.ScalarAsync("SELECT content FROM messages WHERE role = 'assistant'"))
                .Should().Be("Okay, I won't schedule it.");
        }

        [Fact]
        public async Task Confirmation_WrongIdentity_WrongConversation_AndReuse_AreTheSame404()
        {
            var client = await _h.StartAsync(extraMetadata: PlanMetadata);
            ScriptPlanCall();
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");
            var otherConversation = await _h.CreateConversationAsync(client, "tenant-a");

            using var response = await _h.PostMessageAsync(
                client, conversationId, "Schedule it", completion: HttpCompletionOption.ResponseHeadersRead);
            var (reader, confirmation) = await ReadUntilConfirmationAsync(response);
            var confirmationId = confirmation.Data.GetProperty("confirmationId").GetString()!;

            // Another tenant's caller: 404, and the proposal survives.
            using (var crossTenant = await ResolveAsync(
                client, conversationId, confirmationId, new { approve = true }, tenant: "tenant-b"))
                crossTenant.StatusCode.Should().Be(HttpStatusCode.NotFound);

            // The right caller against the WRONG conversation: the same 404.
            using (var crossConversation = await ResolveAsync(
                client, otherConversation, confirmationId, new { approve = true }))
                crossConversation.StatusCode.Should().Be(HttpStatusCode.NotFound);

            // An unknown id: the same 404.
            using (var unknown = await ResolveAsync(
                client, conversationId, "0123456789abcdef0123456789abcdef", new { approve = true }))
                unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

            (await _h.ScalarAsync("SELECT COUNT(*) FROM posts")).Should().Be(0L,
                "no probe above may resolve — or deny — the real caller's proposal");

            // The real caller still owns it — exactly once; reuse is the same 404.
            using (var approve = await ResolveAsync(client, conversationId, confirmationId, new { approve = true }))
                approve.StatusCode.Should().Be(HttpStatusCode.OK);
            using (var reuse = await ResolveAsync(client, conversationId, confirmationId, new { approve = false }))
                reuse.StatusCode.Should().Be(HttpStatusCode.NotFound);

            while ((await SseReader.ReadNextAsync(reader)).Name != "done") { }
            (await _h.ScalarAsync("SELECT COUNT(*) FROM posts")).Should().Be(1L);
        }

        [Fact]
        public async Task Confirmation_Endpoint_ValidatesTheRequestShape_AndIsAuthGated()
        {
            var client = await _h.StartAsync(extraMetadata: PlanMetadata);
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");

            // Missing 'approve' is a caller error, not a silent deny.
            using (var missing = await ResolveAsync(
                client, conversationId, "0123456789abcdef0123456789abcdef", new { reason = "?" }))
            {
                missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await missing.Content.ReadAsStringAsync()).Should().Contain("approve");
            }

            // Anonymous: 401 before anything is touched.
            using var anonymous = await client.SendAsync(ChatEndpointHost.Post(
                $"/_chat/conversations/{conversationId}/confirmations/0123456789abcdef0123456789abcdef",
                new { approve = true }, user: null, tenant: null));
            anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // GET is not a resolution.
            using var get = await client.GetAsync(
                $"/_chat/conversations/{conversationId}/confirmations/0123456789abcdef0123456789abcdef");
            get.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        }

        [Fact]
        public async Task Timeout_DeniesTheProposal_AndTheStreamFinishesWithoutARow()
        {
            // A 300 ms confirmation window, overridden the same way a host would.
            var client = await _h.StartAsync(
                extraMetadata: PlanMetadata,
                configureServices: services =>
                    Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
                        .AddSingleton(services, new ChatConnectorOptions
                        {
                            PlanConfirmationTimeout = TimeSpan.FromMilliseconds(300),
                        }));
            ScriptPlanCall();
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");

            using var response = await _h.PostMessageAsync(client, conversationId, "Schedule it");
            var events = SseReader.Parse(await response.Content.ReadAsStringAsync());

            events.Select(e => e.Name).Should().Equal(
                "message-accepted", "tool", "confirmation", "confirmation-resolved", "tool", "delta", "done");
            var resolved = events.Single(e => e.Name == "confirmation-resolved");
            resolved.Data.GetProperty("approved").GetBoolean().Should().BeFalse();
            (await _h.ScalarAsync("SELECT COUNT(*) FROM posts")).Should().Be(0L);
            (await _h.ScalarAsync("SELECT content FROM messages WHERE role = 'system'"))
                .Should().NotBeNull("the timeout deny is recorded in the transcript too");
        }
    }
}
