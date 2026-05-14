using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
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
    /// Maps the Membership Manager sidecar workflow endpoints onto
    /// <paramref name="app"/>. Call this alongside <c>UseBifrostQL</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapMembershipWorkflows(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/workflows/membership/record-payment", RecordPaymentAsync);
        app.MapPost("/workflows/membership/renew", RenewMembershipAsync);
        app.MapPost("/workflows/membership/check-in", CheckInAsync);
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
        IBifrostWorkflowExecutor bifrost,
        PathCache<Inputs> schemaCache)
    {
        if (request.AmountCents <= 0)
            return Results.BadRequest("Payment amount must be positive.");

        var userContext = http.GetBifrostUserContext();

        // Pre-flight gate: reject the whole workflow before any write, using the
        // SAME evaluator and TablePolicy the mutation pipeline uses. dues_payments
        // is created, so the gating action is Create.
        if (!CanAct(schemaCache, "dues_payments", PolicyAction.Create, userContext))
            return Results.Forbid();

        var invoice = await bifrost.QuerySingleAsync("dues_invoices", request.InvoiceId, userContext);
        if (invoice is null)
            return Results.NotFound();           // tenant-filter already scoped the read

        // 1. Record the payment. dues_payments has no notes column — the note is
        //    carried into audit_log.summary below.
        await bifrost.InsertAsync("dues_payments", new
        {
            tenant_id = invoice["tenant_id"],
            invoice_id = request.InvoiceId,
            amount_cents = request.AmountCents,
            method = string.IsNullOrWhiteSpace(request.Method) ? "card" : request.Method,
            paid_on = string.IsNullOrWhiteSpace(request.PaidOn)
                ? DateTime.UtcNow.ToString("yyyy-MM-dd")
                : request.PaidOn,
        }, userContext);

        // 2. Mark the funding invoice paid. Bifrost's generated `update` requires
        //    every non-nullable column, so the unchanged columns are carried
        //    forward from the row just read; only `status` is advanced.
        await bifrost.UpdateAsync("dues_invoices", new
        {
            invoice_id = request.InvoiceId,
            tenant_id = invoice["tenant_id"],
            member_id = invoice["member_id"],
            member_membership_id = invoice["member_membership_id"],
            amount_cents = invoice["amount_cents"],
            issued_on = invoice["issued_on"],
            due_on = invoice["due_on"],
            status = "paid",
        }, userContext);

        // 3. Advance the invoice's membership to active — a recorded payment
        //    makes the member current. Skipped when the invoice has no linked
        //    membership. The membership row is read first so the `update` can
        //    carry its required columns forward unchanged.
        var membershipId = invoice.TryGetValue("member_membership_id", out var mm) ? mm : null;
        if (membershipId is not null)
        {
            var membership = await bifrost.QuerySingleAsync(
                "member_memberships", membershipId, userContext);
            if (membership is not null)
            {
                await bifrost.UpdateAsync("member_memberships", new
                {
                    member_membership_id = membership["member_membership_id"],
                    tenant_id = membership["tenant_id"],
                    member_id = membership["member_id"],
                    plan_id = membership["plan_id"],
                    start_date = membership["start_date"],
                    end_date = membership["end_date"],
                    status = "active",
                }, userContext);
            }
        }

        // 4. Audit the named operation. The payment note lands here because
        //    dues_payments carries no notes column.
        var summary = $"Payment of {request.AmountCents} cents recorded against invoice {request.InvoiceId}";
        if (!string.IsNullOrWhiteSpace(request.Notes))
            summary = $"{summary} — {request.Notes}";
        await bifrost.InsertAsync("audit_log", new
        {
            tenant_id = invoice["tenant_id"],
            actor_user_id = ActorUserId(userContext),
            action = "payment.recorded",
            entity_type = "dues_payments",
            entity_id = request.InvoiceId.ToString(),
            summary,
            created_at = UtcTimestamp(),
        }, userContext);

        return Results.Ok();
    }

    /// <summary>
    /// Renews a membership: advances its term end-date and status, optionally
    /// opens a fresh <c>dues_invoices</c> row for the renewed term, and writes a
    /// <c>membership.renewed</c> audit entry.
    /// </summary>
    private static async Task<IResult> RenewMembershipAsync(
        RenewMembershipRequest request,
        HttpContext http,
        IBifrostWorkflowExecutor bifrost,
        PathCache<Inputs> schemaCache)
    {
        if (string.IsNullOrWhiteSpace(request.NewEndDate))
            return Results.BadRequest("A new end date is required to renew a membership.");

        var userContext = http.GetBifrostUserContext();

        // Pre-flight gate: the workflow updates member_memberships, so the
        // gating action is Update.
        if (!CanAct(schemaCache, "member_memberships", PolicyAction.Update, userContext))
            return Results.Forbid();

        var membership = await bifrost.QuerySingleAsync(
            "member_memberships", request.MemberMembershipId, userContext);
        if (membership is null)
            return Results.NotFound();           // tenant-filter already scoped the read

        // 1. Advance the membership term and status. Bifrost's generated
        //    `update` requires every non-nullable column, so the unchanged
        //    columns are carried forward from the row just read.
        await bifrost.UpdateAsync("member_memberships", new
        {
            member_membership_id = request.MemberMembershipId,
            tenant_id = membership["tenant_id"],
            member_id = membership["member_id"],
            plan_id = membership["plan_id"],
            start_date = membership["start_date"],
            end_date = request.NewEndDate,
            status = "active",
        }, userContext);

        // 2. Optionally open a dues invoice for the renewed term.
        if (request.InvoiceAmountCents is { } amount && amount > 0)
        {
            await bifrost.InsertAsync("dues_invoices", new
            {
                tenant_id = membership["tenant_id"],
                member_id = membership["member_id"],
                member_membership_id = request.MemberMembershipId,
                amount_cents = amount,
                issued_on = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                due_on = string.IsNullOrWhiteSpace(request.InvoiceDueOn)
                    ? request.NewEndDate
                    : request.InvoiceDueOn,
                status = "open",
            }, userContext);
        }

        // 3. Audit the named operation.
        await bifrost.InsertAsync("audit_log", new
        {
            tenant_id = membership["tenant_id"],
            actor_user_id = ActorUserId(userContext),
            action = "membership.renewed",
            entity_type = "member_memberships",
            entity_id = request.MemberMembershipId.ToString(),
            summary = $"Membership {request.MemberMembershipId} renewed through {request.NewEndDate}",
            created_at = UtcTimestamp(),
        }, userContext);

        return Results.Ok();
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
        IBifrostWorkflowExecutor bifrost,
        PathCache<Inputs> schemaCache)
    {
        var userContext = http.GetBifrostUserContext();

        // Pre-flight gate: reject the whole workflow before any write, using the
        // SAME evaluator and TablePolicy the mutation pipeline uses.
        // event_attendance is created, so the gating action is Create.
        if (!CanAct(schemaCache, "event_attendance", PolicyAction.Create, userContext))
            return Results.Forbid();

        var @event = await bifrost.QuerySingleAsync("events", request.EventId, userContext);
        if (@event is null)
            return Results.NotFound();           // tenant-filter already scoped the read

        var member = await bifrost.QuerySingleAsync("members", request.MemberId, userContext);
        if (member is null)
            return Results.NotFound();           // tenant-filter already scoped the read

        // 1. Record the check-in. The UNIQUE(event_id, member_id) constraint
        //    rejects a second check-in for the same member at the same event;
        //    that surfaces as a pipeline error, reported here as 409 Conflict so
        //    neither an attendance row nor an audit row is written twice.
        try
        {
            await bifrost.InsertAsync("event_attendance", new
            {
                tenant_id = @event["tenant_id"],
                event_id = request.EventId,
                member_id = request.MemberId,
                checked_in_at = string.IsNullOrWhiteSpace(request.CheckedInAt)
                    ? UtcTimestamp()
                    : request.CheckedInAt,
            }, userContext);
        }
        catch (InvalidOperationException ex)
            when (ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict("This member is already checked in for this event.");
        }

        // 2. Audit the named operation.
        await bifrost.InsertAsync("audit_log", new
        {
            tenant_id = @event["tenant_id"],
            actor_user_id = ActorUserId(userContext),
            action = "event.checkin",
            entity_type = "event_attendance",
            entity_id = request.EventId.ToString(),
            summary = $"Member {request.MemberId} checked in to event {request.EventId}",
            created_at = UtcTimestamp(),
        }, userContext);

        return Results.Ok();
    }

    /// <summary>
    /// Pre-flight policy check using the shared <see cref="PolicyEvaluator"/> and
    /// the same <see cref="TablePolicy"/> the mutation pipeline reads. Returns
    /// <c>true</c> when the table carries no schema yet (the gate cannot be
    /// evaluated, so the pipeline's per-mutation policy check remains the
    /// backstop).
    /// </summary>
    private static bool CanAct(
        PathCache<Inputs> schemaCache, string table, PolicyAction action,
        IDictionary<string, object?> userContext)
    {
        var extensions = schemaCache.GetFirstValue();
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
    /// row, or <c>null</c> when the request is unauthenticated.
    /// </summary>
    private static object? ActorUserId(IDictionary<string, object?> userContext)
        => userContext.TryGetValue(MetadataKeys.Auth.DefaultUserIdContextKey, out var id)
            ? id
            : null;
}
