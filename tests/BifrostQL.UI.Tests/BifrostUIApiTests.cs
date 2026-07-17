using Xunit;
using Xunit.Abstractions;
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BifrostQL.UI.Tests;

/// <summary>
/// Exercises the BifrostQL UI HTTP API + static-file endpoints against a real
/// host booted in headless mode by <see cref="HeadlessUiServer"/>. Runs fully
/// in-process — no manually started server and no browser automation.
/// </summary>
[Trait("Category", "API")]
[Trait("Category", "Connection")]
[Collection(HeadlessUiServerCollection.Name)]
public class BifrostUIApiTests
{
    private readonly ITestOutputHelper _output;
    private readonly HeadlessUiServer _server;
    private HttpClient Client => _server.Client;

    public BifrostUIApiTests(HeadlessUiServer server, ITestOutputHelper output)
    {
        _server = server;
        _output = output;
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsOkStatus()
    {
        var response = await Client.GetAsync("/api/health");
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var health = JsonDocument.Parse(content);

        Assert.Equal("ok", health.RootElement.GetProperty("status").GetString());
        _output.WriteLine($"Health status: {health.RootElement.GetProperty("status")}");
    }

    [Fact]
    public async Task HealthEndpoint_ShowsConnectedStatus()
    {
        var response = await Client.GetAsync("/api/health");
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var health = JsonDocument.Parse(content);

        // The host starts with no connection string, so it reports disconnected.
        var connected = health.RootElement.GetProperty("connected").GetBoolean();
        Assert.False(connected, "Should not be connected initially");

        _output.WriteLine($"Connected status: {connected}");
    }

    [Fact]
    public async Task TestConnectionEndpoint_RejectsEmptyConnectionString()
    {
        var payload = new { connectionString = "" };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await Client.PostAsync("/api/connection/test", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var responseText = await response.Content.ReadAsStringAsync();
        Assert.Contains("required", responseText.ToLower());

        _output.WriteLine("Connection endpoint correctly rejects empty connection string.");
    }

    [Fact]
    public async Task TestConnectionEndpoint_InvalidConnection_ReturnsError()
    {
        var payload = new { connectionString = "Server=invalid;Database=nonexistent;User Id=test;Password=test;Connect Timeout=1" };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await Client.PostAsync("/api/connection/test", content);

        // An unreachable server surfaces as a failed connection attempt.
        Assert.False(response.IsSuccessStatusCode);

        var responseText = await response.Content.ReadAsStringAsync();
        _output.WriteLine($"Expected validation error: {response.StatusCode}");
        _output.WriteLine($"Response: {responseText}");
    }

    [Theory]
    [InlineData("northwind")]
    [InlineData("adventureworks-lite")]
    [InlineData("simple-blog")]
    public async Task CreateDatabaseEndpoint_IsDisabled(string template)
    {
        var payload = new
        {
            template,
            connectionString = "Server=localhost;Database=master;User Id=sa;Password=test;TrustServerCertificate=True"
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await Client.PostAsync("/api/database/create", content);
        var responseText = await response.Content.ReadAsStringAsync();

        _output.WriteLine($"Template '{template}' endpoint status: {response.StatusCode}");
        _output.WriteLine($"Response: {responseText}");

        // The legacy password-bearing endpoint is permanently disabled (410 Gone)
        // and must never echo the submitted password back.
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.DoesNotContain("Password=test", responseText);
    }

    [Fact]
    public async Task CreateDatabaseEndpoint_InvalidTemplate_IsDisabledBeforeProcessing()
    {
        var payload = new
        {
            template = "invalid-template-name",
            connectionString = "Server=localhost;Database=master;User Id=sa;Password=test;TrustServerCertificate=True"
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await Client.PostAsync("/api/database/create", content);
        var responseText = await response.Content.ReadAsStringAsync();

        _output.WriteLine($"Invalid template status: {response.StatusCode} (expected: Gone)");
        _output.WriteLine($"Response: {responseText}");

        // The endpoint is disabled before any template lookup, so even a bogus
        // template returns Gone rather than a 404/400.
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.DoesNotContain("Password=test", responseText);
    }

    // SetConnectionEndpoint_RejectsEmptyConnectionString was removed in
    // task XGSUbdBiIzla along with POST /api/connection/set — that
    // endpoint used to accept a raw connection string (password and all)
    // over plain HTTP. The replacement flows are:
    //   - POST /api/vault/connect to activate a saved vault entry
    //   - the Photino native-bridge "request-credential" handler, which
    //     opens an isolated child window, collects the password there,
    //     and writes the encrypted vault entry in-process
    // Both replacement paths keep passwords off the HTTP boundary, so
    // there is no /api/connection/set to test any more.

    [Fact]
    public async Task Cors_ForeignOrigin_IsNotAllowed()
    {
        // A malicious page the user visits must not be able to read loopback API
        // responses cross-origin. The CORS policy is same-origin only
        // (SetIsOriginAllowed(_ => false)), so the middleware must NOT emit an
        // Access-Control-Allow-Origin header for a foreign Origin — without it the
        // browser blocks the reading page from seeing the response body.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await Client.SendAsync(request);

        Assert.False(
            response.Headers.Contains("Access-Control-Allow-Origin"),
            "Loopback API must not grant CORS access to a foreign origin");
    }

    [Fact]
    public async Task Cors_ForeignOriginPreflight_IsNotAllowed()
    {
        // A CORS preflight for a state-changing request from a foreign origin must
        // also be refused — no Access-Control-Allow-Origin means the real request
        // is never sent by the browser.
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/vault/servers");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        using var response = await Client.SendAsync(request);

        Assert.False(
            response.Headers.Contains("Access-Control-Allow-Origin"),
            "Loopback API must not approve a cross-origin preflight from a foreign origin");
    }

    [Fact]
    public async Task GraphQL_EndpointIsAccessible()
    {
        var payload = new { query = "{ __schema { types { name } } }" };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await Client.PostAsync("/graphql", content);

        // Without a database connection GraphQL may not be fully initialized, but
        // the endpoint must still respond rather than refuse the connection.
        _output.WriteLine($"GraphQL endpoint status: {response.StatusCode}");
        if (response.IsSuccessStatusCode)
        {
            var responseText = await response.Content.ReadAsStringAsync();
            _output.WriteLine($"GraphQL response length: {responseText.Length}");
        }
    }

    [Fact]
    public async Task StaticFiles_ServeIndexHtml()
    {
        var response = await Client.GetAsync("/index.html");
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("BifrostQL", content);
        Assert.Contains("<!DOCTYPE html>", content);

        _output.WriteLine("Index page served successfully.");
    }

    [Fact]
    public async Task StaticAssets_ServeJavaScript()
    {
        // The bundle filename is content-hashed and changes per SPA build, so a
        // miss here is informational rather than a failure.
        var response = await Client.GetAsync("/assets/index-BuX1V4Qj.js");

        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsByteArrayAsync();
            Assert.True(content.Length > 10000, "JavaScript bundle should be substantial");
            _output.WriteLine($"JavaScript bundle served: {content.Length} bytes");
        }
        else
        {
            _output.WriteLine($"JavaScript bundle status: {response.StatusCode} (may have different hash)");
        }
    }
}

/// <summary>
/// Tests for template schema generation.
/// Verifies that the SQL schemas for test databases are valid.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Templates")]
public class BifrostUITemplateTests
{
    private readonly ITestOutputHelper _output;

    public BifrostUITemplateTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Resolves src/BifrostQL.UI/TestDatabaseSchemas.cs relative to the test source
    // file so the suite works regardless of where the repo is cloned. The test
    // database schemas live in TestDatabaseSchemas.cs (split out of Program.cs).
    private static string SchemaSourcePath([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return System.IO.Path.Combine(repoRoot, "src", "BifrostQL.UI", "TestDatabaseSchemas.cs");
    }

    [Fact]
    public void NorthwindSchema_ContainsRequiredTables()
    {
        // Test schemas by parsing Program.cs file content
        var schemaSource = System.IO.File.ReadAllText(SchemaSourcePath());

        Assert.Contains("CREATE TABLE Categories", schemaSource);
        Assert.Contains("CREATE TABLE Products", schemaSource);
        Assert.Contains("CREATE TABLE Customers", schemaSource);
        Assert.Contains("CREATE TABLE Orders", schemaSource);
        Assert.Contains("CREATE TABLE OrderDetails", schemaSource);

        _output.WriteLine("Northwind schema contains all required tables.");
    }

    [Fact]
    public void NorthwindSchema_ContainsForeignKeyRelationships()
    {
        var schemaSource = System.IO.File.ReadAllText(SchemaSourcePath());

        Assert.Contains("FOREIGN KEY", schemaSource);
        Assert.Contains("REFERENCES", schemaSource);

        _output.WriteLine("Northwind schema contains foreign key relationships.");
    }

    [Fact]
    public void AdventureWorksLiteSchema_ContainsRequiredTables()
    {
        var schemaSource = System.IO.File.ReadAllText(SchemaSourcePath());

        Assert.Contains("CREATE TABLE Departments", schemaSource);
        Assert.Contains("CREATE TABLE Employees", schemaSource);
        Assert.Contains("CREATE TABLE Shifts", schemaSource);
        Assert.Contains("CREATE TABLE EmployeeDepartmentHistory", schemaSource);

        _output.WriteLine("AdventureWorks Lite schema contains all required tables.");
    }

    [Fact]
    public void SimpleBlogSchema_ContainsRequiredTables()
    {
        var schemaSource = System.IO.File.ReadAllText(SchemaSourcePath());

        Assert.Contains("CREATE TABLE Users", schemaSource);
        Assert.Contains("CREATE TABLE Posts", schemaSource);
        Assert.Contains("CREATE TABLE Comments", schemaSource);
        Assert.Contains("CREATE TABLE Tags", schemaSource);
        Assert.Contains("CREATE TABLE PostTags", schemaSource);

        _output.WriteLine("Simple Blog schema contains all required tables.");
    }

    [Fact]
    public void SimpleBlogSchema_ContainsManyToManyRelationship()
    {
        var schemaSource = System.IO.File.ReadAllText(SchemaSourcePath());

        // PostTags table should exist for many-to-many relationship
        Assert.Contains("PostTags", schemaSource);
        Assert.Contains("FOREIGN KEY", schemaSource);

        _output.WriteLine("Simple Blog schema contains many-to-many relationship.");
    }

    [Fact]
    public void NorthwindData_ContainsSampleRecords()
    {
        var schemaSource = System.IO.File.ReadAllText(SchemaSourcePath());

        Assert.Contains("INSERT INTO Categories", schemaSource);
        Assert.Contains("INSERT INTO Products", schemaSource);
        Assert.Contains("INSERT INTO Customers", schemaSource);

        _output.WriteLine("Northwind data contains sample INSERT statements.");
    }

    [Fact]
    public void SimpleBlogData_ContainsSampleRecords()
    {
        var schemaSource = System.IO.File.ReadAllText(SchemaSourcePath());

        Assert.Contains("INSERT INTO Users", schemaSource);
        Assert.Contains("INSERT INTO Posts", schemaSource);
        Assert.Contains("INSERT INTO Comments", schemaSource);

        _output.WriteLine("Simple Blog data contains sample INSERT statements.");
    }

    [Fact]
    public void AllSchemas_UseProperSQLSyntax()
    {
        var schemaSource = System.IO.File.ReadAllText(SchemaSourcePath());

        Assert.Contains("CREATE TABLE", schemaSource);
        Assert.Contains("PRIMARY KEY", schemaSource);

        _output.WriteLine("All schemas use proper SQL CREATE TABLE syntax.");
    }
}
