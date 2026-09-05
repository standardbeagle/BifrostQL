using BifrostQL.UI.Web;
using FluentAssertions;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// M26b: /api/databases peerAuth shells out to <c>sudo -u &lt;psqlUser&gt; psql</c>
/// with a caller-chosen OS user — anyone who can reach the desktop API picks the
/// account psql runs as. The OS user must be restricted to the current user or
/// an explicit allow-list (<c>BIFROST_UI_PSQL_PEER_USERS</c>).
/// </summary>
public sealed class PsqlDatabaseListerTests
{
    [Fact]
    public async Task ListDatabasesAsync_RejectsArbitraryOsUser()
    {
        // "root" is never the current user under test (tests do not run as root in
        // CI), and is not on the default allow-list, so the refusal must fire
        // BEFORE any sudo/psql process is started.
        var act = async () => await PsqlDatabaseLister.ListDatabasesAsync(
            "Host=/tmp;Username=peer", psqlUser: "root", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not*permitted*",
                "a caller-chosen OS user must never reach sudo -u");
    }

    [Fact]
    public async Task ListDatabasesAsync_CurrentUser_IsNotRefused()
    {
        // The current OS user is always allowed. The refusal is the only outcome
        // this fact pins; whether psql itself exists on the box is environmental.
        try
        {
            await PsqlDatabaseLister.ListDatabasesAsync(
                "Host=/tmp;Username=peer", psqlUser: Environment.UserName, CancellationToken.None);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not") && ex.Message.Contains("permitted"))
        {
            Assert.Fail($"current user must not hit the allow-list refusal: {ex.Message}");
        }
        catch
        {
            // psql missing / connection refused are fine — the gate let it through.
        }
    }
}
