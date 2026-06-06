using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using BifrostQL.Core.Workflows;
using BifrostQL.Server;
using GraphQL;

namespace BifrostQL.Samples.HostedSpa;

/// <summary>
/// Sidecar workflow endpoints for the Membership Manager domain — the
/// "endpoints half" of the workflow-mutation convention described in
/// <c>docs/src/content/docs/guides/workflow-mutations.md</c>.
///
/// Each endpoint is a single named operation that orchestrates several Bifrost
/// mutations server-side through <see cref="IBifrostWorkflowExecutor"/>. Every
/// internal write traverses the SAME GraphQL mutation pipeline as a direct
/// <c>/graphql</c> request — <c>tenant-filter</c> and the policy engine still
/// apply — and each endpoint also runs a pre-flight gate via the shared
/// <see cref="PolicyEvaluator"/> so the whole operation is rejected up front
/// rather than failing partway through. Every operation writes exactly one
/// <c>audit_log</c> row naming the action (<c>payment.recorded</c>,
/// <c>membership.renewed</c>).
///
/// Payment-provider integration (charging a card, reconciling an external
/// processor) is intentionally out of scope: these endpoints record a payment
/// or renewal that has already been collected. A server-side adapter that
/// orchestrates a real provider is the documented future adapter point — it
/// would run before the <c>dues_payments</c> insert below.
///
/// <c>POST /workflows/membership/check-in</c> records event attendance the same
/// way: it inserts an <c>event_attendance</c> row through the executor and
/// writes an <c>event.checkin</c> audit entry. The
/// <c>UNIQUE(event_id, member_id)</c> constraint on <c>event_attendance</c>
/// means a member can only be checked in once per event; a repeat check-in is
/// reported as <c>409 Conflict</c> rather than writing a duplicate row.
/// </summary>
public static class MembershipWorkflowEndpoints
{
    /// <summary>
    /// Request body for <c>POST /workflows/membership/record-payment</c>.
    /// <paramref name="Notes"/> has no column on <c>dues_payments</c>, so it is
    /// recorded in <c>audit_log.summary</c>.
    /// </summary>
    public sealed record RecordPaymentRequest(
        long InvoiceId,
        long AmountCents,
        string? Method,
        string? PaidOn,
        string? Notes);

    /// <summary>
    /// Request body for <c>POST /workflows/membership/renew</c>. The membership's
    /// term is advanced to <paramref name="NewEndDate"/>; when
    /// <paramref name="InvoiceAmountCents"/> is supplied a fresh <c>dues_invoices</c>
    /// row is opened for the renewed term.
    /// </summary>
    public sealed record RenewMembershipRequest(
        long MemberMembershipId,
        string NewEndDate,
        long? InvoiceAmountCents,
        string? InvoiceDueOn);

    /// <summary>
    /// Request body for <c>POST /workflows/membership/check-in</c>. Records that
    /// <paramref name="MemberId"/> attended <paramref name="EventId"/>. When
    /// <paramref name="CheckedInAt"/> is omitted the current UTC time is used.
    /// </summary>
    public sealed record CheckInRequest(
        long EventId,
        long MemberId,
        string? CheckedInAt);

    /// <summary>
    /// Request body for <c>POST /workflows/membership/link-identity</c>. Associates
    /// the <c>app_users</c> login <paramref name="UserId"/> with the
    /// <c>members</c> profile <paramref name="MemberId"/> by setting
    /// <c>members.user_id</c>.
    /// </summary>
    public sealed record LinkIdentityRequest(
        long MemberId,
        long UserId);

    /// <summary>
    /// Maps the Membership Manager sidecar workflow endpoints onto
    /// <paramref name="app"/>. Call this alongside <c>UseBifrostQL</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapMembershipWorkflows(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/workflows/membership/record-payment", RecordPaymentAsync);
        app.MapPost("/workflows/membership/renew", RenewMembershipAsync);
        app.MapPost("/workflows/membership/check-in", CheckInAsync);
        app.MapPost("/workflows/membership/link-identity", LinkIdentityAsync);
        return app;
    }

    /// <summary>
    /// Records a collected dues payment: inserts a <c>dues_payments</c> row,
    /// marks the funding invoice paid, advances the linked <c>member_memberships</c>
    /// row to <c>active</c>, and writes a <c>payment.recorded</c> audit entry.
    /// </summary>
    private static async Task<IResult> RecordPaymentAsync(
        RecordPaymentRequest request,
        HttpContext http,
        IWorkflowRunner workflows,
        PathCache<Inputs> schemaCache)
    {
        if (request.AmountCents <= 0)
            return Results.BadRequest("Payment amount must be positive.");

        var userContext = http.GetBifrostUserContext();

        // Pre-flight gate: reject the whole workflow before any write, using the
        // SAME evaluator and TablePolicy the mutation pipeline uses. dues_payments
        // is created, so the gating action is Create.
        if (!await CanActAsync(schemaCache, "dues_payments", PolicyAction.Create, userContext))
            return Results.Forbid();

        var summary = $"Payment of {request.AmountCents} cents recorded against invoice {request.InvoiceId}";
        if (!string.IsNullOrWhiteSpace(request.Notes))
            summary = $"{summary} - {request.Notes}";

        var result = await workflows.RunAsync("record-payment", new Dictionary<string, object?>
        {
            ["invoiceId"] = request.InvoiceId,
            ["amountCents"] = request.AmountCents,
            ["method"] = string.IsNullOrWhiteSpace(request.Method) ? "card" : request.Method,
            ["paidOn"] = string.IsNullOrWhiteSpace(request.PaidOn)
                ? DateTime.UtcNow.ToString("yyyy-MM-dd")
                : request.PaidOn,
            ["summary"] = summary,
            ["actorUserId"] = ActorUserId(userContext),
            ["createdAt"] = UtcTimestamp(),
        }, userContext);

        return ToWorkflowResult(result);
    }

    /// <summary>
    /// Renews a membership: advances its term end-date and status, optionally
    /// opens a fresh <c>dues_invoices</c> row for the renewed term, and writes a
    /// <c>membership.renewed</c> audit entry.
    /// </summary>
    private static async Task<IResult> RenewMembershipAsync(
        RenewMembershipRequest request,
        HttpContext http,
        IWorkflowRunner workflows,
        PathCache<Inputs> schemaCache)
    {
        if (string.IsNullOrWhiteSpace(request.NewEndDate))
            return Results.BadRequest("A new end date is required to renew a membership.");

        var userContext = http.GetBifrostUserContext();

        // Pre-flight gate: the workflow updates member_memberships, so the
        // gating action is Update.
        if (!await CanActAsync(schemaCache, "member_memberships", PolicyAction.Update, userContext))
            return Results.Forbid();

        var result = await workflows.RunAsync("renew-membership", new Dictionary<string, object?>
        {
            ["memberMembershipId"] = request.MemberMembershipId,
            ["newEndDate"] = request.NewEndDate,
            ["createInvoice"] = request.InvoiceAmountCents is { } amount && amount > 0,
            ["invoiceAmountCents"] = request.InvoiceAmountCents,
            ["issuedOn"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            ["invoiceDueOn"] = string.IsNullOrWhiteSpace(request.InvoiceDueOn)
                ? request.NewEndDate
                : request.InvoiceDueOn,
            ["summary"] = $"Membership {request.MemberMembershipId} renewed through {request.NewEndDate}",
            ["actorUserId"] = ActorUserId(userContext),
            ["createdAt"] = UtcTimestamp(),
        }, userContext);

        return ToWorkflowResult(result);
    }

    /// <summary>
    /// Records a member's attendance at an event: inserts an
    /// <c>event_attendance</c> row and writes an <c>event.checkin</c> audit
    /// entry. The <c>UNIQUE(event_id, member_id)</c> constraint means a member
    /// can only be checked in once per event — a repeat check-in is reported as
    /// <c>409 Conflict</c> and writes neither an attendance row nor an audit row.
    /// </summary>
    private static async Task<IResult> CheckInAsync(
        CheckInRequest request,
        HttpContext http,
        IWorkflowRunner workflows,
        PathCache<Inputs> schemaCache)
    {
        var userContext = http.GetBifrostUserContext();

        // Pre-flight gate: reject the whole workflow before any write, using the
        // SAME evaluator and TablePolicy the mutation pipeline uses.
        // event_attendance is created, so the gating action is Create.
        if (!await CanActAsync(schemaCache, "event_attendance", PolicyAction.Create, userContext))
            return Results.Forbid();

        var checkedInAt = string.IsNullOrWhiteSpace(request.CheckedInAt)
            ? UtcTimestamp()
            : request.CheckedInAt;
        var result = await workflows.RunAsync("check-in", new Dictionary<string, object?>
        {
            ["eventId"] = request.EventId,
            ["memberId"] = request.MemberId,
            ["checkedInAt"] = checkedInAt,
            ["summary"] = $"Member {request.MemberId} checked in to event {request.EventId}",
            ["actorUserId"] = ActorUserId(userContext),
            ["createdAt"] = UtcTimestamp(),
        }, userContext);

        return ToWorkflowResult(result);
    }

    /// <summary>
    /// Links an <c>app_users</c> login to a <c>members</c> profile: sets
    /// <c>members.user_id</c> and writes one <c>member.identity-linked</c> audit
    /// entry naming the actor and the member entity. This is the explicit,
    /// auditable identity→member association the policy epic's
    /// <c>members policy-row-scope: user_id = {user_id}</c> depends on. Both the
    /// member and the app_user are read first (and so tenant-scoped) before any
    /// write, so a link is only recorded when both rows are visible to the caller.
    /// </summary>
    private static async Task<IResult> LinkIdentityAsync(
        LinkIdentityRequest request,
        HttpContext http,
        IWorkflowRunner workflows,
        PathCache<Inputs> schemaCache)
    {
        var userContext = http.GetBifrostUserContext();

        // Pre-flight gate: the workflow updates members, so the gating action is
        // Update. Linking an identity is a privileged operation — it must be
        // policy-gated, not open to any caller.
        if (!await CanActAsync(schemaCache, "members", PolicyAction.Update, userContext))
            return Results.Forbid();

        var result = await workflows.RunAsync("link-identity", new Dictionary<string, object?>
        {
            ["memberId"] = request.MemberId,
            ["userId"] = request.UserId,
            ["summary"] = $"App user {request.UserId} linked to member {request.MemberId}",
            ["actorUserId"] = ActorUserId(userContext),
            ["createdAt"] = UtcTimestamp(),
        }, userContext);

        return ToWorkflowResult(result);
    }

    /// <summary>
    /// Pre-flight policy check using the shared <see cref="PolicyEvaluator"/> and
    /// the same <see cref="TablePolicy"/> the mutation pipeline reads. Returns
    /// <c>true</c> when the table carries no schema yet (the gate cannot be
    /// evaluated, so the pipeline's per-mutation policy check remains the
    /// backstop).
    /// </summary>
    private static async Task<bool> CanActAsync(
        PathCache<Inputs> schemaCache, string table, PolicyAction action,
        IDictionary<string, object?> userContext)
    {
        var extensions = await schemaCache.GetFirstValueAsync();
        if (extensions?["model"] is not IDbModel model)
            return true;

        var policy = PolicyConfigCollector.FromTable(model.GetTableFromDbName(table));
        var identity = BuildIdentity(userContext);
        return new PolicyEvaluator().CanAct(policy, action, identity).Allowed;
    }

    /// <summary>
    /// Projects the request's Bifrost user context into the <see cref="AppIdentity"/>
    /// the <see cref="PolicyEvaluator"/> expects — the same shape
    /// <c>PolicyMutationTransformer</c> builds for a direct GraphQL mutation.
    /// </summary>
    private static AppIdentity BuildIdentity(IDictionary<string, object?> userContext)
    {
        var userId = userContext.TryGetValue(MetadataKeys.Auth.DefaultUserIdContextKey, out var id)
                     && id is not null
            ? id.ToString()!
            : "anonymous";
        if (string.IsNullOrWhiteSpace(userId))
            userId = "anonymous";

        var roles = userContext.TryGetValue(MetadataKeys.Auth.DefaultRolesContextKey, out var r)
            ? ExtractStrings(r)
            : Array.Empty<string>();

        return new AppIdentity(userId, "workflow-endpoint", roles: roles);
    }

    private static string[] ExtractStrings(object? value) => value switch
    {
        null => Array.Empty<string>(),
        string s => new[] { s },
        IEnumerable<string> typed => typed.ToArray(),
        System.Collections.IEnumerable seq => seq.Cast<object?>()
            .Select(o => o?.ToString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToArray(),
        _ => Array.Empty<string>(),
    };

    private static IResult ToWorkflowResult(WorkflowRunResult result)
    {
        if (result.Succeeded)
            return Results.Ok();

        return result.Error?.Code switch
        {
            "not_found" => Results.NotFound(),
            "conflict" => Results.Conflict("This member is already checked in for this event."),
            _ => Results.Problem(result.Error?.Message ?? "Workflow failed."),
        };
    }

    /// <summary>
    /// A UTC timestamp in the <c>datetime('now')</c> format the Membership
    /// Manager schema uses. Bifrost's generated <c>insert</c> input type marks
    /// every NOT NULL column required even when the column has a SQL default,
    /// so workflow inserts supply <c>created_at</c> explicitly.
    /// </summary>
    private static string UtcTimestamp()
        => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// Reads the actor's user id from the user context for the <c>audit_log</c>
    /// row, or <c>null</c> when the request is unauthenticated. The user-id claim
    /// arrives as a string; <c>audit_log.actor_user_id</c> is an integer FK, so a
    /// numeric claim is coerced to a number for the generated <c>insert</c> input
    /// type. A non-numeric id is passed through unchanged.
    /// </summary>
    private static object? ActorUserId(IDictionary<string, object?> userContext)
    {
        if (!userContext.TryGetValue(MetadataKeys.Auth.DefaultUserIdContextKey, out var id) || id is null)
            return null;

        return id switch
        {
            string s when long.TryParse(s, out var parsed) => parsed,
            _ => id,
        };
    }
}
