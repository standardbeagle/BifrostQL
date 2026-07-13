using BifrostQL.Core.Modules.Chat;
using FluentAssertions;
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
        var registry = new ChatPlanConfirmationRegistry();
        var pending = registry.Register("t1|u1", "42", TimeSpan.FromMilliseconds(50), CancellationToken.None);

        var decision = await pending.Decision.WaitAsync(TimeSpan.FromSeconds(5));

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
        }).Should().Be("t-9|u-3");

        // Either half suffices (single-tenant deployments have no tenant claim) …
        ChatPlanConfirmationRegistry.RequireIdentityKey(new Dictionary<string, object?>
        {
            ["user_id"] = "u-3",
        }).Should().Be("|u-3");

        // … but an anonymous context cannot gate a write.
        ((Action)(() => ChatPlanConfirmationRegistry.RequireIdentityKey(
                new Dictionary<string, object?>())))
            .Should().Throw<InvalidOperationException>().WithMessage("*anonymous*");
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
