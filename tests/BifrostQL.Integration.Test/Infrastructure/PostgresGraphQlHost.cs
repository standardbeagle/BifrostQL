using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Ngsql;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace BifrostQL.Integration.Test.Infrastructure;

/// <summary>
/// A scratch PostgreSQL database behind the production registrar (<c>AddBifrostQL</c>) on a
/// test server, for facts that must cross the whole HTTP request path. The caller's identity
/// arrives the way an authentication handler would leave it: a <see cref="ClaimsPrincipal"/>
/// carrying a subject and <see cref="ClaimTypes.Role"/> claims, set by middleware ahead of
/// <c>UseBifrostQL</c>.
/// </summary>
public sealed class PostgresGraphQlHost : IAsyncDisposable
{
    public const string SkipReason = "BIFROST_TEST_POSTGRES environment variable not set";

    public static string? Master => Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");

    private readonly string _dbName;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public string ConnectionString { get; }

    private PostgresGraphQlHost(string dbName, string connectionString, IHost host)
    {
        _dbName = dbName;
        ConnectionString = connectionString;
        _host = host;
        _client = host.GetTestClient();
    }

    /// <summary>
    /// Creates a database named for <paramref name="prefix"/>, runs <paramref name="ddl"/> in it,
    /// and starts a server over it with <paramref name="metadata"/> rules and any extra
    /// <paramref name="services"/> (hooks, observers) registered beside BifrostQL's own.
    /// </summary>
    public static async Task<PostgresGraphQlHost> StartAsync(
        string prefix, string ddl, IReadOnlyList<string> metadata, Action<IServiceCollection>? services = null)
    {
        var master = Master ?? throw new InvalidOperationException(SkipReason);
        var dbName = $"{prefix}_{Guid.NewGuid():N}";
        await ExecAsync(master, $"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(master) { Database = dbName }.ConnectionString;
        await ExecAsync(connectionString, ddl);

        var settings = new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = "/graphql",
        };
        for (var i = 0; i < metadata.Count; i++)
            settings[$"Bifrost:Metadata:{i}"] = metadata[i];
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        DbConnFactoryResolver.Register(BifrostDbProvider.PostgreSql, cs => new PostgresDbConnFactory(cs));
        var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(collection =>
            {
                collection.AddRouting();
                services?.Invoke(collection);
                collection.AddBifrostQL(options => options
                    .BindConfiguration(config.GetSection("Bifrost"))
                    .BindConnectionString(connectionString)
                    .BindProvider("postgres"));
            });
            web.Configure(app =>
            {
                app.Use((context, next) =>
                {
                    if (context.Request.Headers.TryGetValue("X-Test-User", out var user))
                    {
                        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.ToString()) };
                        foreach (var role in context.Request.Headers["X-Test-Role"])
                            claims.Add(new Claim(ClaimTypes.Role, role!));
                        if (context.Request.Headers.TryGetValue("X-Test-Tenant", out var tenant))
                            claims.Add(new Claim(LocalAuthClaims.Tenant, tenant.ToString()));
                        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
                    }
                    return next(context);
                });
                app.UseBifrostQL();
            });
        }).StartAsync();

        return new PostgresGraphQlHost(dbName, connectionString, host);
    }

    /// <summary>Posts <paramref name="query"/> as <paramref name="user"/> holding <paramref name="roles"/>.</summary>
    public async Task<JsonDocument> PostAsync(string query, string? user = null, params string[] roles)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql");
        request.Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (user is not null)
        {
            request.Headers.Add("X-Test-User", user);
            foreach (var role in roles)
                request.Headers.Add("X-Test-Role", role);
        }
        using var response = await _client.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Posts <paramref name="query"/> with raw request <paramref name="headers"/> (for example
    /// <c>X-Test-User</c>, <c>X-Test-Tenant</c>, <c>Idempotency-Key</c>; a repeated name sends
    /// the header once per value), returning the status and the parsed body.
    /// </summary>
    public async Task<(System.Net.HttpStatusCode Status, JsonDocument Body)> SendAsync(
        string query, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql");
        request.Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        using var response = await _client.SendAsync(request);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()));
    }

    /// <summary>Every row <paramref name="sql"/> returns, each column read as text (null stays null).</summary>
    public async Task<List<Dictionary<string, string?>>> RowsAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, string?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
            rows.Add(row);
        }
        return rows;
    }

    public async Task<long> CountAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public static void AssertNoErrors(JsonDocument reply)
    {
        if (reply.RootElement.TryGetProperty("errors", out _))
            throw new Xunit.Sdk.XunitException("Expected no errors: " + reply.RootElement.GetRawText());
    }

    /// <summary>The first error of <paramref name="reply"/>, which must carry one.</summary>
    public static JsonElement FirstError(JsonDocument reply)
    {
        if (!reply.RootElement.TryGetProperty("errors", out var errors) || errors.GetArrayLength() == 0)
            throw new Xunit.Sdk.XunitException("Expected an error: " + reply.RootElement.GetRawText());
        return errors[0];
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        NpgsqlConnection.ClearAllPools();
        await ExecAsync(Master!, $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
