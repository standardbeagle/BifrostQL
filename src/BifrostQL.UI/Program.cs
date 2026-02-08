using System.CommandLine;
using System.Text;
using BifrostQL.Server;
using BifrostQL.Core.Model;
using Microsoft.Data.SqlClient;
using Photino.NET;

var connectionStringArg = new Argument<string?>("connection")
{
    Description = "SQL Server connection string (e.g., 'Server=localhost;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True'). Optional - can be set via UI.",
    Arity = ArgumentArity.ZeroOrOne
};

var portOption = new Option<int>("--port", "-p")
{
    Description = "Port to run the server on",
    DefaultValueFactory = _ => 5000
};

var exposeOption = new Option<bool>("--expose", "-e")
{
    Description = "Expose the API to the network (binds to 0.0.0.0 instead of localhost)"
};

var headlessOption = new Option<bool>("--headless", "-H")
{
    Description = "Run in headless mode (server only, no UI window)"
};

var rootCommand = new RootCommand("BifrostQL UI - Desktop database explorer")
{
    connectionStringArg,
    portOption,
    exposeOption,
    headlessOption
};

// Shared connection string storage
string? currentConnectionString = null;

rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var connectionString = parseResult.GetValue(connectionStringArg);
    var port = parseResult.GetValue(portOption);
    var expose = parseResult.GetValue(exposeOption);
    var headless = parseResult.GetValue(headlessOption);

    currentConnectionString = connectionString;

    var bindAddress = expose ? "0.0.0.0" : "localhost";
    var serverUrl = $"http://{bindAddress}:{port}";
    var localUrl = $"http://localhost:{port}";

    // Build and start the web server
    var builder = WebApplication.CreateBuilder();

    builder.WebHost.UseUrls(serverUrl);

    // Configure Kestrel for larger headers (needed for some auth tokens)
    builder.WebHost.ConfigureKestrel(serverOptions =>
    {
        serverOptions.Limits.MaxRequestHeadersTotalSize = 131072;
    });

    // Add in-memory configuration for BifrostQL
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["BifrostQL:DisableAuth"] = "true",
        ["BifrostQL:Path"] = "/graphql",
        ["BifrostQL:Playground"] = "/graphiql"
    });

    // Add BifrostQL services if connection string is provided
    if (!string.IsNullOrEmpty(connectionString))
    {
        builder.Services.AddBifrostQL(options =>
        {
            options.BindConnectionString(connectionString)
                   .BindConfiguration(builder.Configuration.GetSection("BifrostQL"));
        });
    }

    builder.Services.AddCors();
    builder.Services.AddEndpointsApiExplorer();

    var app = builder.Build();

    app.UseDeveloperExceptionPage();
    app.UseCors(x => x
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowAnyOrigin());

    // API endpoint to test a connection string
    app.MapPost("/api/connection/test", async (ConnectionTestRequest request, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            return Results.BadRequest(new { error = "Connection string is required" });
        }

        try
        {
            await using var conn = new SqlConnection(request.ConnectionString);
            await conn.OpenAsync(ct);

            // Get database info
            var database = conn.Database;
            var server = conn.DataSource;

            return Results.Ok(new {
                success = true,
                message = $"Successfully connected to {database} on {server}",
                database,
                server
            });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new {
                success = false,
                error = ex.Message
            });
        }
    });

    // API endpoint to set the current connection (requires restart of GraphQL endpoint)
    app.MapPost("/api/connection/set", (ConnectionSetRequest request) =>
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            return Results.BadRequest(new { error = "Connection string is required" });
        }

        currentConnectionString = request.ConnectionString;

        // Note: In a production app, you would reload the GraphQL schema here
        // For now, we return success and the client will need to reconnect/refresh
        return Results.Ok(new {
            success = true,
            message = "Connection updated. Please refresh the application.",
            needsRefresh = true
        });
    });

    // API endpoint to create a test database
    app.MapPost("/api/database/create", async (CreateDatabaseRequest request, CancellationToken ct) =>
    {
        async IAsyncEnumerable<string> StreamProgress()
        {
            yield return $"data: {{\"stage\": \"Parsing connection string\", \"percent\": 5, \"message\": \"Extracting server details\"}}\n\n";

            // Parse the connection string to get server info
            var builder = new SqlConnectionStringBuilder(request.ConnectionString ??
                "Server=localhost;Database=master;User Id=sa;Password=your_password;TrustServerCertificate=True");
            var originalDatabase = builder.InitialCatalog;
            builder.InitialCatalog = "master"; // Connect to master to create database

            yield return $"data: {{\"stage\": \"Connecting to server\", \"percent\": 10, \"message\": \"Establishing connection to master database\"}}\n\n";

            // Try to connect - handle connection errors separately
            SqlConnection? conn = null;
            Exception? connectError = null;

            try
            {
                conn = new SqlConnection(builder.ConnectionString);
                await conn.OpenAsync(ct);
            }
            catch (Exception ex)
            {
                connectError = ex;
            }

            // Handle connection error (yield outside try-catch)
            if (connectError != null)
            {
                yield return $"data: {{\"stage\": \"Error\", \"percent\": 0, \"message\": \"Failed to connect to SQL Server: {connectError.Message}\", \"error\": true}}\n\n";
                yield break;
            }

            // At this point, conn is not null
            await using var _conn = conn!;

            // Generate database name based on template
            var dbName = request.Template switch
            {
                "northwind" => "Northwind_Test",
                "adventureworks-lite" => "AdventureWorksLite_Test",
                "simple-blog" => "SimpleBlog_Test",
                _ => "TestDB_" + Guid.NewGuid().ToString("N").Substring(0, 8)
            };

            yield return $"data: {{\"stage\": \"Creating database\", \"percent\": 20, \"message\": \"Creating database {dbName}\"}}\n\n";

            // Create database
            await using (var cmd = new SqlCommand($"IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = '{dbName}') BEGIN CREATE DATABASE [{dbName}] END", _conn))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }

            yield return $"data: {{\"stage\": \"Creating tables\", \"percent\": 30, \"message\": \"Setting up database schema\"}}\n\n";

            // Switch to the new database
            builder.InitialCatalog = dbName;
            await using var newConn = new SqlConnection(builder.ConnectionString);
            await newConn.OpenAsync(ct);

            // Create tables based on template
            var sql = request.Template switch
            {
                "northwind" => TestDatabaseSchemas.GetNorthwindSchema(),
                "adventureworks-lite" => TestDatabaseSchemas.GetAdventureWorksLiteSchema(),
                "simple-blog" => TestDatabaseSchemas.GetSimpleBlogSchema(),
                _ => TestDatabaseSchemas.GetSimpleBlogSchema()
            };

            var statements = sql.Split(new[] { "GO" }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < statements.Length; i++)
            {
                var percent = 40 + (i * 50 / statements.Length);
                yield return $"data: {{\"stage\": \"Creating schema\", \"percent\": {percent}, \"message\": \"Executing statement {i + 1} of {statements.Length}\"}}\n\n";

                await using var cmd = new SqlCommand(statements[i].Trim(), newConn);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            yield return $"data: {{\"stage\": \"Inserting sample data\", \"percent\": 90, \"message\": \"Adding sample records\"}}\n\n";

            // Insert sample data
            var dataSql = request.Template switch
            {
                "northwind" => TestDatabaseSchemas.GetNorthwindData(),
                "adventureworks-lite" => TestDatabaseSchemas.GetAdventureWorksLiteData(),
                "simple-blog" => TestDatabaseSchemas.GetSimpleBlogData(),
                _ => TestDatabaseSchemas.GetSimpleBlogData()
            };

            var dataStatements = dataSql.Split(new[] { "GO" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < dataStatements.Length; i++)
            {
                await using var cmd = new SqlCommand(dataStatements[i].Trim(), newConn);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var newConnectionString = builder.ConnectionString;

            yield return $"data: {{\"stage\": \"Complete!\", \"percent\": 100, \"message\": \"Database created successfully\", \"connectionString\": \"{newConnectionString}\"}}\n\n";
        }

        return Results.Ok(StreamProgress());
    });

    // Health check endpoint
    app.MapGet("/api/health", () => Results.Ok(new
    {
        status = "ok",
        connected = !string.IsNullOrEmpty(currentConnectionString)
    }));

    // Serve static files from wwwroot
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // BifrostQL GraphQL endpoint (only if connection is set)
    if (!string.IsNullOrEmpty(connectionString))
    {
        app.UseBifrostQL();
    }

    // Fallback to index.html for SPA routing
    app.MapFallbackToFile("index.html");

    // Start the server in the background
    var serverTask = app.RunAsync(cancellationToken);

    Console.WriteLine($"BifrostQL server started at {serverUrl}");
    if (!string.IsNullOrEmpty(connectionString))
    {
        Console.WriteLine($"GraphQL endpoint: {localUrl}/graphql");
    }
    else
    {
        Console.WriteLine("No connection string provided - use the UI to connect to a database");
    }

    if (headless)
    {
        Console.WriteLine("Running in headless mode. Press Ctrl+C to stop.");
        await serverTask;
    }
    else
    {
        // Create the Photino window
        var window = new PhotinoWindow()
            .SetTitle("BifrostQL - Database Explorer")
            .SetSize(1400, 900)
            .Center()
            .SetDevToolsEnabled(true)
            .Load(localUrl);

        window.WaitForClose();

        // Shutdown the server when window closes
        await app.StopAsync();
    }

    return 0;
});

return await rootCommand.Parse(args).InvokeAsync();

// Record types for API requests
record ConnectionTestRequest(string ConnectionString);
record ConnectionSetRequest(string ConnectionString);
record CreateDatabaseRequest(string Template, string? ConnectionString);

// SQL Schema generation methods
public static class TestDatabaseSchemas
{
    public static string GetNorthwindSchema() => @"
CREATE TABLE Categories (
    CategoryID INT IDENTITY(1,1) PRIMARY KEY,
    CategoryName NVARCHAR(100) NOT NULL,
    Description NVARCHAR(500)
);

CREATE TABLE Products (
    ProductID INT IDENTITY(1,1) PRIMARY KEY,
    ProductName NVARCHAR(100) NOT NULL,
    CategoryID INT FOREIGN KEY REFERENCES Categories(CategoryID),
    UnitPrice DECIMAL(10,2) DEFAULT 0,
    UnitsInStock INT DEFAULT 0,
    Discontinued BIT DEFAULT 0
);

CREATE TABLE Customers (
    CustomerID NVARCHAR(10) PRIMARY KEY,
    CompanyName NVARCHAR(100) NOT NULL,
    ContactName NVARCHAR(100),
    Country NVARCHAR(50)
);

CREATE TABLE Orders (
    OrderID INT IDENTITY(1,1) PRIMARY KEY,
    CustomerID NVARCHAR(10) FOREIGN KEY REFERENCES Customers(CustomerID),
    OrderDate DATETIME DEFAULT GETDATE(),
    ShippedDate DATETIME,
    ShipCountry NVARCHAR(50)
);

CREATE TABLE OrderDetails (
    OrderDetailID INT IDENTITY(1,1) PRIMARY KEY,
    OrderID INT FOREIGN KEY REFERENCES Orders(OrderID),
    ProductID INT FOREIGN KEY REFERENCES Products(ProductID),
    UnitPrice DECIMAL(10,2) NOT NULL,
    Quantity INT DEFAULT 1
);
";

    public static string GetNorthwindData() => @"
INSERT INTO Categories (CategoryName, Description) VALUES
('Beverages', 'Soft drinks, coffees, teas, beers'),
('Condiments', 'Sweet and savory sauces'),
('Confections', 'Desserts and candies');

INSERT INTO Products (ProductName, CategoryID, UnitPrice, UnitsInStock) VALUES
('Chai', 1, 18.00, 39),
('Chang', 1, 19.00, 17),
('Aniseed Syrup', 2, 10.00, 13);

INSERT INTO Customers (CustomerID, CompanyName, ContactName, Country) VALUES
('ALFKI', 'Alfreds Futterkiste', 'Maria Anders', 'Germany'),
('ANATR', 'Ana Trujillo Emparedados', 'Ana Trujillo', 'Mexico'),
('ANTON', 'Antonio Moreno Taqueria', 'Antonio Moreno', 'Mexico');

INSERT INTO Orders (CustomerID, OrderDate, ShipCountry) VALUES
('ALFKI', GETDATE(), 'Germany'),
('ANATR', DATEADD(day, -1, GETDATE()), 'Mexico');

INSERT INTO OrderDetails (OrderID, ProductID, UnitPrice, Quantity) VALUES
(1, 1, 18.00, 10),
(1, 2, 19.00, 5),
(2, 3, 10.00, 20);
";

    public static string GetAdventureWorksLiteSchema() => @"
CREATE TABLE Departments (
    DepartmentID INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    GroupName NVARCHAR(100)
);

CREATE TABLE Employees (
    EmployeeID INT IDENTITY(1,1) PRIMARY KEY,
    FirstName NVARCHAR(50) NOT NULL,
    LastName NVARCHAR(50) NOT NULL,
    DepartmentID INT FOREIGN KEY REFERENCES Departments(DepartmentID),
    HireDate DATETIME DEFAULT GETDATE()
);

CREATE TABLE Shifts (
    ShiftID INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(50) NOT NULL,
    StartTime TIME NOT NULL,
    EndTime TIME NOT NULL
);

CREATE TABLE EmployeeDepartmentHistory (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeID INT FOREIGN KEY REFERENCES Employees(EmployeeID),
    DepartmentID INT FOREIGN KEY REFERENCES Departments(DepartmentID),
    ShiftID INT FOREIGN KEY REFERENCES Shifts(ShiftID),
    StartDate DATETIME NOT NULL,
    EndDate DATETIME NULL
);
";

    public static string GetAdventureWorksLiteData() => @"
INSERT INTO Departments (Name, GroupName) VALUES
('Engineering', 'Research and Development'),
('Sales', 'Sales and Marketing'),
('Finance', 'Executive General and Administration');

INSERT INTO Shifts (Name, StartTime, EndTime) VALUES
('Day', '06:00:00', '14:00:00'),
('Evening', '14:00:00', '22:00:00'),
('Night', '22:00:00', '06:00:00');

INSERT INTO Employees (FirstName, LastName, DepartmentID, HireDate) VALUES
('John', 'Smith', 1, '2020-01-15'),
('Jane', 'Doe', 2, '2021-03-20'),
('Bob', 'Johnson', 1, '2019-11-05');

INSERT INTO EmployeeDepartmentHistory (EmployeeID, DepartmentID, ShiftID, StartDate) VALUES
(1, 1, 1, '2020-01-15'),
(2, 2, 1, '2021-03-20'),
(3, 1, 2, '2019-11-05');
";

    public static string GetSimpleBlogSchema() => @"
CREATE TABLE Users (
    UserID INT IDENTITY(1,1) PRIMARY KEY,
    Username NVARCHAR(50) UNIQUE NOT NULL,
    Email NVARCHAR(100) UNIQUE NOT NULL,
    CreatedAt DATETIME DEFAULT GETDATE()
);

CREATE TABLE Posts (
    PostID INT IDENTITY(1,1) PRIMARY KEY,
    Title NVARCHAR(200) NOT NULL,
    Content NVARCHAR(MAX) NOT NULL,
    AuthorID INT FOREIGN KEY REFERENCES Users(UserID),
    PublishedAt DATETIME DEFAULT GETDATE(),
    UpdatedAt DATETIME DEFAULT GETDATE()
);

CREATE TABLE Comments (
    CommentID INT IDENTITY(1,1) PRIMARY KEY,
    PostID INT FOREIGN KEY REFERENCES Posts(PostID),
    AuthorID INT FOREIGN KEY REFERENCES Users(UserID),
    Content NVARCHAR(1000) NOT NULL,
    CreatedAt DATETIME DEFAULT GETDATE()
);

CREATE TABLE Tags (
    TagID INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(50) UNIQUE NOT NULL
);

CREATE TABLE PostTags (
    PostTagID INT IDENTITY(1,1) PRIMARY KEY,
    PostID INT FOREIGN KEY REFERENCES Posts(PostID),
    TagID INT FOREIGN KEY REFERENCES Tags(TagID)
);
";

    public static string GetSimpleBlogData() => @"
INSERT INTO Users (Username, Email) VALUES
('admin', 'admin@blog.com'),
('johndoe', 'john@example.com'),
('janedoe', 'jane@example.com');

INSERT INTO Posts (Title, Content, AuthorID) VALUES
('Welcome to the Blog', 'This is our first post!', 1),
('GraphQL Basics', 'Learn about GraphQL queries and mutations.', 1),
('Building APIs', 'Tips for building modern APIs.', 2);

INSERT INTO Comments (PostID, AuthorID, Content) VALUES
(1, 2, 'Great first post!'),
(1, 3, 'Looking forward to more content.'),
(2, 3, 'Very helpful explanation!');

INSERT INTO Tags (Name) VALUES
('GraphQL'),
('Tutorial'),
('API');

INSERT INTO PostTags (PostID, TagID) VALUES
(2, 1),
(2, 2),
(3, 3);
";
}
