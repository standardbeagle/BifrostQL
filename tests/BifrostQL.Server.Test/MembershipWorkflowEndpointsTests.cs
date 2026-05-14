using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// WebApplicationFactory integration tests for the Membership Manager sidecar
/// workflow endpoints hosted by the HostedSpa sample
/// (<c>POST /workflows/membership/record-payment</c> and
/// <c>POST /workflows/membership/renew</c>).
///
/// Each test drives the endpoint over HTTP and then reads back through the
/// <c>/graphql</c> endpoint to prove the orchestration landed: the workflow
/// row was written, the dependent rows advanced, and exactly one
/// <c>audit_log</c> entry naming the action was appended. Because the endpoints
/// orchestrate through <see cref="IBifrostWorkflowExecutor"/>, those reads also
/// prove the writes traversed the same GraphQL pipeline as a direct mutation.
///
/// Each run points the sample at a fresh, uniquely named SQLite file so the
/// seed always runs and runs never collide — the same pattern as
/// <see cref="HostedSpaSmokeTests"/>.
/// </summary>
public class MembershipWorkflowEndpointsTests
    : IClassFixture<MembershipWorkflowEndpointsTests.WorkflowFactory>
{
    private readonly WorkflowFactory _factory;

    public MembershipWorkflowEndpointsTests(WorkflowFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RecordPayment_WritesPayment_MarksInvoicePaid_AndAuditsAction()
    {
        // Arrange: a fresh host so the seeded invoice 1 is open and unpaid.
        await using var factory = new WorkflowFactory();
        var client = factory.CreateClient();

        // Act: record a collected payment against the seeded open invoice.
        var response = await client.PostAsJsonAsync(
            "/workflows/membership/record-payment",
            new
            {
                invoiceId = 1,
                amountCents = 12000,
                method = "check",
                paidOn = "2025-01-05",
                notes = "Paid in person at the front desk.",
            });

        // Assert: the endpoint succeeded.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert: a dues_payments row was inserted through the pipeline.
        var payments = await QueryRows(
            client, "{ dues_payments { data { invoice_id amount_cents method } } }", "dues_payments");
        payments.Should().ContainSingle();
        payments[0].GetProperty("invoice_id").GetInt32().Should().Be(1);
        payments[0].GetProperty("amount_cents").GetInt32().Should().Be(12000);
        payments[0].GetProperty("method").GetString().Should().Be("check");

        // Assert: the funding invoice was marked paid.
        var invoices = await QueryRows(
            client, "{ dues_invoices { data { invoice_id status } } }", "dues_invoices");
        invoices.Single(i => i.GetProperty("invoice_id").GetInt32() == 1)
            .GetProperty("status").GetString().Should().Be("paid");

        // Assert: the linked membership advanced to active.
        var memberships = await QueryRows(
            client, "{ member_memberships { data { member_membership_id status } } }", "member_memberships");
        memberships.Single(m => m.GetProperty("member_membership_id").GetInt32() == 1)
            .GetProperty("status").GetString().Should().Be("active");

        // Assert: exactly one audit_log row naming the action, with the note in summary.
        var audits = await QueryRows(
            client, "{ audit_log { data { action entity_type summary } } }", "audit_log");
        var audit = audits.Should().ContainSingle().Subject;
        audit.GetProperty("action").GetString().Should().Be("payment.recorded");
        audit.GetProperty("entity_type").GetString().Should().Be("dues_payments");
        audit.GetProperty("summary").GetString()
            .Should().Contain("Paid in person at the front desk.");
    }

    [Fact]
    public async Task RenewMembership_AdvancesTerm_OpensInvoice_AndAuditsAction()
    {
        // Arrange: a fresh host so the seeded membership 1 is expired.
        await using var factory = new WorkflowFactory();
        var client = factory.CreateClient();

        // Act: renew the membership for a fresh term and open a renewal invoice.
        var response = await client.PostAsJsonAsync(
            "/workflows/membership/renew",
            new
            {
                memberMembershipId = 1,
                newEndDate = "2026-01-10",
                invoiceAmountCents = 12000,
                invoiceDueOn = "2025-02-10",
            });

        // Assert: the endpoint succeeded.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert: the membership term advanced and the status is active.
        var memberships = await QueryRows(
            client, "{ member_memberships { data { member_membership_id end_date status } } }", "member_memberships");
        var membership = memberships.Single(m => m.GetProperty("member_membership_id").GetInt32() == 1);
        membership.GetProperty("end_date").GetString().Should().Be("2026-01-10");
        membership.GetProperty("status").GetString().Should().Be("active");

        // Assert: a renewal invoice was opened for the membership.
        var invoices = await QueryRows(
            client, "{ dues_invoices { data { member_membership_id amount_cents status } } }", "dues_invoices");
        invoices.Should().Contain(i =>
            i.GetProperty("member_membership_id").GetInt32() == 1
            && i.GetProperty("amount_cents").GetInt32() == 12000
            && i.GetProperty("status").GetString() == "open");

        // Assert: exactly one audit_log row naming the renewal.
        var audits = await QueryRows(
            client, "{ audit_log { data { action entity_type } } }", "audit_log");
        var audit = audits.Should().ContainSingle().Subject;
        audit.GetProperty("action").GetString().Should().Be("membership.renewed");
        audit.GetProperty("entity_type").GetString().Should().Be("member_memberships");
    }

    [Fact]
    public async Task RecordPayment_UnknownInvoice_ReturnsNotFound_AndWritesNothing()
    {
        // Arrange
        await using var factory = new WorkflowFactory();
        var client = factory.CreateClient();

        // Act: an invoice id that does not exist.
        var response = await client.PostAsJsonAsync(
            "/workflows/membership/record-payment",
            new { invoiceId = 999, amountCents = 5000 });

        // Assert: the workflow reports not-found and does not partially apply.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var payments = await QueryRows(
            client, "{ dues_payments { data { invoice_id } } }", "dues_payments");
        payments.Should().BeEmpty("a not-found invoice must not record a payment");

        var audits = await QueryRows(
            client, "{ audit_log { data { action } } }", "audit_log");
        audits.Should().BeEmpty("a rejected workflow must not write an audit row");
    }

    [Fact]
    public async Task RecordPayment_NonPositiveAmount_ReturnsBadRequest()
    {
        // Arrange
        await using var factory = new WorkflowFactory();
        var client = factory.CreateClient();

        // Act: a zero amount is not a valid payment.
        var response = await client.PostAsJsonAsync(
            "/workflows/membership/record-payment",
            new { invoiceId = 1, amountCents = 0 });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Posts a GraphQL query to the same host and returns the named table's
    /// <c>data</c> array, asserting the query resolved without GraphQL errors.
    /// </summary>
    private static async Task<List<JsonElement>> QueryRows(
        HttpClient client, string query, string table)
    {
        var response = await client.PostAsJsonAsync("/graphql", new { query });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("errors", out _)
            .Should().BeFalse($"the {table} read-back query should resolve without GraphQL errors");

        return doc.RootElement
            .GetProperty("data")
            .GetProperty(table)
            .GetProperty("data")
            .EnumerateArray()
            .Select(e => e.Clone())
            .ToList();
    }

    /// <summary>
    /// Hosts the HostedSpa sample for the workflow tests, pointing it at a fresh
    /// uniquely named SQLite database so the membership seed always runs.
    /// </summary>
    public sealed class WorkflowFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"hostedspa-workflow-{Guid.NewGuid():N}.db");

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:bifrost"] = $"Data Source={_dbPath}",
                }));

            return base.CreateHost(builder);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }
}
