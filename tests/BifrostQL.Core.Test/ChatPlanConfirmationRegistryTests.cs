using BifrostQL.Core.Modules.Chat;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Unit coverage for the plan confirmation registry: single-use resolution bound
/// to identity + conversation (mismatches fail indistinguishably and do NOT burn
/// the entry), timeout resolving as a DENY, request-teardown cancellation, and the
/// binding-key derivations the connector and the chat middleware share.
/// </summary>
public class ChatPlanConfirmationRegistryTests
{
    private static readonly TimeSpan LongTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Resolve_DeliversTheDecision_ExactlyOnce()
    {
        var registry = new ChatPlanConfirmationRegistry();
        var pending = registry.Register("t1|u1", "42", LongTimeout, CancellationToken.None);
        pending.ConfirmationId.Should().MatchRegex("^[0-9a-f]{32}$", "ids are crypto-random hex");

        registry.TryResolve(pending.ConfirmationId, "t1|u1", "42", new ChatPlanDecision(true, "go"))
            .Should().BeTrue();
        registry.TryResolve(pending.ConfirmationId, "t1|u1", "42", new ChatPlanDecision(false, null))
            .Should().BeFalse("single-use: a resolved id reads as unknown");

        var decision = await pending.Decision;
        decision.Approved.Should().BeTrue();
        decision.Reason.Should().Be("go");
        registry.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Mismatches_FailIdentically_AndKeepTheEntryAlive()
    {
        var registry = new ChatPlanConfirmationRegistry();
        var pending = registry.Register("t1|u1", "42", LongTimeout, CancellationToken.None);
        var approve = new ChatPlanDecision(true, null);

        registry.TryResolve(pending.ConfirmationId, "t2|u2", "42", approve).Should().BeFalse();
        registry.TryResolve(pending.ConfirmationId, "t1|u1", "77", approve).Should().BeFalse();
        registry.TryResolve("unknown", "t1|u1", "42", approve).Should().BeFalse();

        registry.PendingCount.Should().Be(1, "a rejected probe must not deny the real caller's proposal");
        pending.Decision.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Timeout_ResolvesAsADeny_AndRemovesTheEntry()
    {
        // Injected clock: the timeout fires when the test advances time, not when a loaded
        // machine gets round to a 50 ms wall-clock timer. The real wait budget is far beyond
        // anything the advanced clock needs.
        var clock = new FakeTimeProvider();
        var registry = new ChatPlanConfirmationRegistry(clock);
        var pending = registry.Register("t1|u1", "42", TimeSpan.FromMinutes(1), CancellationToken.None);
        pending.Decision.IsCompleted.Should().BeFalse("the timeout has not elapsed on the injected clock");

        clock.Advance(TimeSpan.FromMinutes(1));

        var decision = await pending.Decision.WaitAsync(TimeSpan.FromMinutes(1));

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Contain("timed out");
        registry.PendingCount.Should().Be(0);
        registry.TryResolve(pending.ConfirmationId, "t1|u1", "42", new ChatPlanDecision(true, null))
            .Should().BeFalse("an expired id reads as unknown");
    }

    [Fact]
    public async Task RequestTeardown_CancelsTheDecision_AndRemovesTheEntry()
    {
        var registry = new ChatPlanConfirmationRegistry();
        using var cts = new CancellationTokenSource();
        var pending = registry.Register("t1|u1", "42", LongTimeout, cts.Token);

        cts.Cancel();

        await ((Func<Task>)(() => pending.Decision.WaitAsync(TimeSpan.FromSeconds(5))))
            .Should().ThrowAsync<OperationCanceledException>();
        registry.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Register_RejectsBlankBindingsAndNonPositiveTimeouts()
    {
        var registry = new ChatPlanConfirmationRegistry();

        ((Action)(() => registry.Register("", "42", LongTimeout, CancellationToken.None)))
            .Should().Throw<ArgumentException>();
        ((Action)(() => registry.Register("t|u", " ", LongTimeout, CancellationToken.None)))
            .Should().Throw<ArgumentException>();
        ((Action)(() => registry.Register("t|u", "42", TimeSpan.Zero, CancellationToken.None)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IdentityKey_ComposesTenantAndUser_AndRefusesAnonymous()
    {
        ChatPlanConfirmationRegistry.RequireIdentityKey(new Dictionary<string, object?>
        {
            ["tenant_id"] = "t-9",
            ["user_id"] = "u-3",
        }, "tenant_id").Should().Be("t-9|u-3");

        // Either half suffices (single-tenant deployments have no tenant claim) …
        ChatPlanConfirmationRegistry.RequireIdentityKey(new Dictionary<string, object?>
        {
            ["user_id"] = "u-3",
        }, "tenant_id").Should().Be("|u-3");

        // … but an anonymous context cannot gate a write.
        ((Action)(() => ChatPlanConfirmationRegistry.RequireIdentityKey(
                new Dictionary<string, object?>(), "tenant_id")))
            .Should().Throw<InvalidOperationException>().WithMessage("*anonymous*");
    }

    [Fact]
    public void IdentityKey_SeparatesTenants_UnderTheModelsRenamedTenantKey()
    {
        // tenant-context-key: org_id, the tenant only in org_id (a raw principal claim lands as a
        // one-element string[]). Two orgs whose IdPs issue the same subject must not share one
        // binding, or one org's user confirms the other org's parked write.
        IDictionary<string, object?> Caller(string org) => new Dictionary<string, object?>
        {
            ["user_id"] = "same-subject",
            ["org_id"] = new[] { org },
        };

        var acme = ChatPlanConfirmationRegistry.RequireIdentityKey(Caller("acme"), "org_id");
        var globex = ChatPlanConfirmationRegistry.RequireIdentityKey(Caller("globex"), "org_id");

        acme.Should().NotBe(globex, "the binding is keyed by the model's tenant claim");
        acme.Should().Be("acme|same-subject");

        // The registry refuses the other org's resolve end to end.
        var registry = new ChatPlanConfirmationRegistry();
        var pending = registry.Register(acme, "42", LongTimeout, CancellationToken.None);
        registry.TryResolve(pending.ConfirmationId, globex, "42", new ChatPlanDecision(true, null))
            .Should().BeFalse("another org's identity cannot resolve the proposal");
        registry.TryResolve(pending.ConfirmationId, acme, "42", new ChatPlanDecision(true, null))
            .Should().BeTrue("control: the proposing identity resolves it");
    }

    [Fact]
    public void ConversationKey_ReadsTheTransportBinding_AndRefusesUnboundContexts()
    {
        ChatPlanConfirmationRegistry.RequireConversationKey(new Dictionary<string, object?>
        {
            [ChatPlanConfirmationRegistry.ConversationContextKey] = "42",
        }).Should().Be("42");

        ((Action)(() => ChatPlanConfirmationRegistry.RequireConversationKey(
                new Dictionary<string, object?>())))
            .Should().Throw<InvalidOperationException>()
            .WithMessage($"*{ChatPlanConfirmationRegistry.ConversationContextKey}*");

        ChatPlanConfirmationRegistry.CanonicalConversationKey(42L).Should().Be("42");
    }
}
