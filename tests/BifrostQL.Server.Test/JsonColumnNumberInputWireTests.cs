using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// A number inside a <c>JSON</c> column value keeps its exact text from the HTTP
/// request body to the stored document. GraphQL.SystemTextJson's variable reader
/// parsed every fraction to a decimal, so a 40-digit number was stored rounded to
/// 28–29 digits before the <c>JSON</c> scalar saw it. Driven through the production
/// registrar (<c>AddBifrostQL</c>) and a raw request body, so the variables pass the
/// host's own serializer.
/// </summary>
public sealed class JsonColumnNumberInputWireTests : IAsyncLifetime
{
    private const string FortyDigitFraction = "0.1234567890123456789012345678901234567891";
    private const string FortyDigitExponent = "1.234567890123456789012345678901234567891e-40";

    private string _dbPath = null!;
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"json-number-input-{Guid.NewGuid():N}.db");
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE docs (id INTEGER PRIMARY KEY, payload JSON NULL, ratio REAL NULL, amount DECIMAL NULL);" +
                "INSERT INTO docs (id) VALUES (1);";
            await cmd.ExecuteNonQueryAsync();
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = "/graphql",
        }).Build();

        _host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddBifrostQL(o => o
                    .BindConfiguration(config.GetSection("Bifrost"))
                    .BindConnectionString($"Data Source={_dbPath}")
                    .BindProvider("sqlite"));
            });
            web.Configure(app => app.UseBifrostQL());
        }).StartAsync();
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _host.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        await Task.CompletedTask;
    }

    /// <summary>Posts a request body verbatim, so no client serializer touches the numbers.</summary>
    private async Task<JsonDocument> PostRawAsync(string body)
    {
        var response = await _client.PostAsync("/graphql", new StringContent(body, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(text);
        doc.RootElement.TryGetProperty("errors", out _).Should().BeFalse(text);
        return doc;
    }

    private async Task<string> StoredPayloadAsync()
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM docs WHERE id = 1";
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task FortyDigitNumbers_InAJsonColumn_RoundTripUnchanged()
    {
        using (await PostRawAsync(
                   "{\"query\":\"mutation W($v: JSON!) { docs(update: {id: 1, payload: $v}) }\"," +
                   $"\"variables\":{{\"v\":{{\"rate\":{FortyDigitFraction},\"tiny\":[{FortyDigitExponent}],\"half\":0.5,\"n\":7}}}}}}"))
        {
        }

        var stored = await StoredPayloadAsync();
        using (var storedDoc = JsonDocument.Parse(stored))
        {
            storedDoc.RootElement.GetProperty("rate").GetRawText().Should().Be(FortyDigitFraction);
            storedDoc.RootElement.GetProperty("tiny")[0].GetRawText().Should().Be(FortyDigitExponent);
            // Controls: numbers a double or decimal holds exactly keep their stock reading.
            storedDoc.RootElement.GetProperty("half").GetRawText().Should().Be("0.5");
            storedDoc.RootElement.GetProperty("n").GetRawText().Should().Be("7");
        }

        using var read = await PostRawAsync("{\"query\":\"{ docs { data { payload } } }\"}");
        var payload = read.RootElement.GetProperty("data").GetProperty("docs").GetProperty("data")[0].GetProperty("payload");
        payload.GetProperty("rate").GetRawText().Should().Be(FortyDigitFraction);
    }

    /// <summary>
    /// A number token longer than <c>ExactNumericScalars.MaxNumberTextLength</c> is refused as
    /// a malformed body before the reader parses it: the integer form went straight into
    /// <c>BigInteger.Parse</c>, whose cost grows faster than the token, and the fraction form
    /// was copied and normalized several times. Nothing is written.
    /// </summary>
    [Theory]
    [InlineData("integer")]
    [InlineData("fraction")]
    public async Task NumberTokenOverTheLengthCap_IsRefusedAndNothingIsStored(string form)
    {
        var digits = new string('7', BifrostQL.Core.Schema.ExactNumericScalars.MaxNumberTextLength + 1);
        var token = form == "integer" ? digits : "0." + digits;
        var body = "{\"query\":\"mutation W($v: JSON!) { docs(update: {id: 1, payload: $v}) }\"," +
                   $"\"variables\":{{\"v\":{{\"big\":{token}}}}}}}";

        var response = await _client.PostAsync("/graphql", new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM docs WHERE id = 1";
        (await cmd.ExecuteScalarAsync()).Should().Be(DBNull.Value);
    }

    /// <summary>Control for the cap: a 40-digit integer, past long but far inside the cap, is stored exactly.</summary>
    [Fact]
    public async Task FortyDigitInteger_InAJsonColumn_IsStoredExactly()
    {
        const string fortyDigitInteger = "1234567890123456789012345678901234567891";
        using (await PostRawAsync(
                   "{\"query\":\"mutation W($v: JSON!) { docs(update: {id: 1, payload: $v}) }\"," +
                   $"\"variables\":{{\"v\":{{\"big\":{fortyDigitInteger}}}}}}}"))
        {
        }

        using var storedDoc = JsonDocument.Parse(await StoredPayloadAsync());
        storedDoc.RootElement.GetProperty("big").GetRawText().Should().Be(fortyDigitInteger);
    }

    /// <summary>
    /// The same 40-digit token bound to a <c>Float</c> or <c>Decimal</c> field still
    /// parses (to the nearest double / decimal), as it did when the stock reader handed
    /// it over as a rounded decimal.
    /// </summary>
    [Fact]
    public async Task FortyDigitNumber_BoundToFloatAndDecimal_StillParses()
    {
        using (await PostRawAsync(
                   "{\"query\":\"mutation W($r: Float!, $a: Decimal!) { docs(update: {id: 1, ratio: $r, amount: $a}) }\"," +
                   $"\"variables\":{{\"r\":{FortyDigitFraction},\"a\":{FortyDigitFraction}}}}}"))
        {
        }

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ratio FROM docs WHERE id = 1";
        Convert.ToDouble(await cmd.ExecuteScalarAsync()).Should().BeApproximately(0.123456789012345678, 1e-15);
    }
}
