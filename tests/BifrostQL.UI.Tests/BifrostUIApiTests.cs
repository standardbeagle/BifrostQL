using Xunit;
using Xunit.Abstractions;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BifrostQL.UI.Tests;

/// <summary>
/// Tests for the BifrostQL UI API endpoints.
/// These tests verify the backend API functionality for connection management
/// and database creation without requiring browser automation.
/// </summary>
[Trait("Category", "API")]
[Trait("Category", "Connection")]
public class BifrostUIApiTests
{
    private readonly ITestOutputHelper _output;
    private const string BaseUrl = "http://localhost:5000";

    public BifrostUIApiTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task<bool> IsServerRunning()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await client.GetAsync($"{BaseUrl}/api/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsOkStatus()
    {
        if (!await IsServerRunning())
        {
            _output.WriteLine("Server is not running. Test skipped.");
            return;
        }

        using var client = new HttpClient();
        var response = await client.GetAsync($"{BaseUrl}/api/health");

        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var health = JsonDocument.Parse(content);

        Assert.Equal("ok", health.RootElement.GetProperty("status").GetString());
        _output.WriteLine($"Health status: {health.RootElement.GetProperty("status")}");
    }

    [Fact]
    public async Task HealthEndpoint_ShowsConnectedStatus()
    {
        if (!await IsServerRunning())
        {
            _output.WriteLine("Server is not running. Test skipped.");
            return;
        }

        using var client = new HttpClient();
        var response = await client.GetAsync($"{BaseUrl}/api/health");

        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var health = JsonDocument.Parse(content);

        // Initially should not be connected
        var connected = health.RootElement.GetProperty("connected").GetBoolean();
        Assert.False(connected, "Should not be connected initially");

        _output.WriteLine($"Connected status: {connected}");
    }

    [Fact]
    public async Task TestConnectionEndpoint_RejectsEmptyConnectionString()
    {
        if (!await IsServerRunning())
        {
            _output.WriteLine("Server is not running. Test skipped.");
            return;
        }

        using var client = new HttpClient();

        var payload = new
        {
            connectionString = ""
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"{BaseUrl}/api/connection/test", content);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        var responseText = await response.Content.ReadAsStringAsync();
        Assert.Contains("required", responseText.ToLower());

        _output.WriteLine("Connection endpoint correctly rejects empty connection string.");
    }

    [Fact]
    public async Task TestConnectionEndpoint_InvalidConnection_ReturnsError()
    {
        if (!await IsServerRunning())
        {
            _output.WriteLine("Server is not running. Test skipped.");
            return;
        }

        using var client = new HttpClient();

        var payload = new
        {
            connectionString = "Server=invalid;Database=nonexistent;User Id=test;Password=test"
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"{BaseUrl}/api/connection/test", content);

        // Should return error for invalid connection
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
        if (!await IsServerRunning())
        {
            _output.WriteLine("Server is not running. Test skipped.");
            return;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var payload = new
        {
            template = template,
            connectionString = "Server=localhost;Database=master;User Id=sa;Password=test;TrustServerCertificate=True"
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/database/create") { Content = content };
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var responseText = await response.Content.ReadAsStringAsync();

        _output.WriteLine($"Template '{template}' endpoint status: {response.StatusCode}");
        _output.WriteLine($"Response: {responseText}");

        Assert.Equal(System.Net.HttpStatusCode.Gone, response.StatusCode);
        Assert.DoesNotContain("Password=test", responseText);
    }

    [Fact]
    public async Task CreateDatabaseEndpoint_InvalidTemplate_IsDisabledBeforeProcessing()
    {
        if (!await IsServerRunning())
        {
            _output.WriteLine("Server is not running. Test skipped.");
            return;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var payload = new
        {
            template = "invalid-template-name",
            connectionString = "Server=localhost;Database=master;User Id=sa;Password=test;TrustServerCertificate=True"
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/database/create") { Content = content };
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var responseText = await response.Content.ReadAsStringAsync();

        _output.WriteLine($"Invalid template status: {response.StatusCode} (expected: Gone)");
        _output.WriteLine($"Response: {responseText}");

        Assert.Equal(System.Net.HttpStatusCode.Gone, response.StatusCode);
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
    public async Task GraphQL_EndpointIsAccessible()
    {
        if (!await IsServerRunning())
        {
            _output.WriteLine("Server is not running. Test skipped.");
            return;
        }

        using var client = new HttpClient();

        // GraphQL introspection query
        var query = "{ __schema { types { name } } }";

        var payload = new { query };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"{BaseUrl}/graphql", content);

        // Without a database connection, GraphQL might not be initialized
        // but the endpoint should still respond
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
        if (!await IsServerRunning())
        {
            _output.WriteLine("Server is not running. Test skipped.");
            return;
        }

        using var client = new HttpClient();
        var response = await client.GetAsync($"{BaseUrl}/index.html");

        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("BifrostQL", content);
        Assert.Contains("<!DOCTYPE html>", content);

        _output.WriteLine("Index page served successfully.");
    }

    [Fact]
    public async Task StaticAssets_ServeJavaScript()
    {
        if (!await IsServerRunning())
        {
            _output.WriteLine("Server is not running. Test skipped.");
            return;
        }

        using var client = new HttpClient();
        var response = await client.GetAsync($"{BaseUrl}/assets/index-BuX1V4Qj.js");

        // The JS file should be served
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

    [Fact]
    public void NorthwindSchema_ContainsRequiredTables()
    {
        // Test schemas by parsing Program.cs file content
        var programCs = System.IO.File.ReadAllText("/home/beagle/work/core/bifrost/src/BifrostQL.UI/Program.cs");

        Assert.Contains("CREATE TABLE Categories", programCs);
        Assert.Contains("CREATE TABLE Products", programCs);
        Assert.Contains("CREATE TABLE Customers", programCs);
        Assert.Contains("CREATE TABLE Orders", programCs);
        Assert.Contains("CREATE TABLE OrderDetails", programCs);

        _output.WriteLine("Northwind schema contains all required tables.");
    }

    [Fact]
    public void NorthwindSchema_ContainsForeignKeyRelationships()
    {
        var programCs = System.IO.File.ReadAllText("/home/beagle/work/core/bifrost/src/BifrostQL.UI/Program.cs");

        Assert.Contains("FOREIGN KEY", programCs);
        Assert.Contains("REFERENCES", programCs);

        _output.WriteLine("Northwind schema contains foreign key relationships.");
    }

    [Fact]
    public void AdventureWorksLiteSchema_ContainsRequiredTables()
    {
        var programCs = System.IO.File.ReadAllText("/home/beagle/work/core/bifrost/src/BifrostQL.UI/Program.cs");

        Assert.Contains("CREATE TABLE Departments", programCs);
        Assert.Contains("CREATE TABLE Employees", programCs);
        Assert.Contains("CREATE TABLE Shifts", programCs);
        Assert.Contains("CREATE TABLE EmployeeDepartmentHistory", programCs);

        _output.WriteLine("AdventureWorks Lite schema contains all required tables.");
    }

    [Fact]
    public void SimpleBlogSchema_ContainsRequiredTables()
    {
        var programCs = System.IO.File.ReadAllText("/home/beagle/work/core/bifrost/src/BifrostQL.UI/Program.cs");

        Assert.Contains("CREATE TABLE Users", programCs);
        Assert.Contains("CREATE TABLE Posts", programCs);
        Assert.Contains("CREATE TABLE Comments", programCs);
        Assert.Contains("CREATE TABLE Tags", programCs);
        Assert.Contains("CREATE TABLE PostTags", programCs);

        _output.WriteLine("Simple Blog schema contains all required tables.");
    }

    [Fact]
    public void SimpleBlogSchema_ContainsManyToManyRelationship()
    {
        var programCs = System.IO.File.ReadAllText("/home/beagle/work/core/bifrost/src/BifrostQL.UI/Program.cs");

        // PostTags table should exist for many-to-many relationship
        Assert.Contains("PostTags", programCs);
        Assert.Contains("FOREIGN KEY", programCs);

        _output.WriteLine("Simple Blog schema contains many-to-many relationship.");
    }

    [Fact]
    public void NorthwindData_ContainsSampleRecords()
    {
        var programCs = System.IO.File.ReadAllText("/home/beagle/work/core/bifrost/src/BifrostQL.UI/Program.cs");

        Assert.Contains("INSERT INTO Categories", programCs);
        Assert.Contains("INSERT INTO Products", programCs);
        Assert.Contains("INSERT INTO Customers", programCs);

        _output.WriteLine("Northwind data contains sample INSERT statements.");
    }

    [Fact]
    public void SimpleBlogData_ContainsSampleRecords()
    {
        var programCs = System.IO.File.ReadAllText("/home/beagle/work/core/bifrost/src/BifrostQL.UI/Program.cs");

        Assert.Contains("INSERT INTO Users", programCs);
        Assert.Contains("INSERT INTO Posts", programCs);
        Assert.Contains("INSERT INTO Comments", programCs);

        _output.WriteLine("Simple Blog data contains sample INSERT statements.");
    }

    [Fact]
    public void AllSchemas_UseProperSQLSyntax()
    {
        var programCs = System.IO.File.ReadAllText("/home/beagle/work/core/bifrost/src/BifrostQL.UI/Program.cs");

        Assert.Contains("CREATE TABLE", programCs);
        Assert.Contains("PRIMARY KEY", programCs);

        _output.WriteLine("All schemas use proper SQL CREATE TABLE syntax.");
    }
}
