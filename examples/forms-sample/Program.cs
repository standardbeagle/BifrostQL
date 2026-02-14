using BifrostQL.Core.Forms;
using BifrostQL.Core.Model;
using BifrostQL.Core.Views;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseStaticFiles();

// Metadata configuration for form controls
var metadata = new FormsMetadataConfiguration()
    .ConfigureColumn("Products", "Status", col =>
    {
        col.EnumValues = ["active", "draft", "discontinued"];
        col.EnumDisplayNames = new Dictionary<string, string>
        {
            ["active"] = "Active",
            ["draft"] = "Draft",
            ["discontinued"] = "Discontinued"
        };
    })
    .ConfigureColumn("Products", "Price", col =>
    {
        col.Min = 0;
        col.Max = 999999.99;
        col.Step = 0.01;
        col.Placeholder = "0.00";
    })
    .ConfigureColumn("Products", "Image", col =>
    {
        col.Accept = "image/png,image/jpeg,image/webp";
    })
    .ConfigureColumn("Customers", "Email", col =>
    {
        col.InputType = "email";
        col.Placeholder = "name@company.com";
    })
    .ConfigureColumn("Customers", "Phone", col =>
    {
        col.InputType = "tel";
        col.Pattern = "[0-9]{3}-[0-9]{3}-[0-9]{4}";
        col.Placeholder = "555-123-4567";
    })
    .ConfigureColumn("Customers", "Country", col =>
    {
        col.EnumValues = ["US", "CA", "GB", "DE", "FR", "JP", "AU"];
        col.EnumDisplayNames = new Dictionary<string, string>
        {
            ["US"] = "United States", ["CA"] = "Canada", ["GB"] = "United Kingdom",
            ["DE"] = "Germany", ["FR"] = "France", ["JP"] = "Japan", ["AU"] = "Australia"
        };
    })
    .ConfigureColumn("Orders", "Status", col =>
    {
        col.EnumValues = ["pending", "shipped", "delivered", "cancelled"];
        col.EnumDisplayNames = new Dictionary<string, string>
        {
            ["pending"] = "Pending",
            ["shipped"] = "Shipped",
            ["delivered"] = "Delivered",
            ["cancelled"] = "Cancelled"
        };
    });

// Routes
app.MapGet("/", () => Results.Content(Pages.Index(), "text/html"));

app.MapGet("/demo/insert/{table}", (string table) =>
{
    var model = DemoModel.Create();
    var form = new BifrostFormBuilder(model, "/demo", metadata);
    var fkOptions = DemoModel.ForeignKeyOptions(table);
    var html = form.GenerateForm(table, FormMode.Insert, foreignKeyOptions: fkOptions);
    return Results.Content(Pages.Wrap($"New {table}", html), "text/html");
});

app.MapGet("/demo/update/{table}/{id}", (string table, string id) =>
{
    var model = DemoModel.Create();
    var form = new BifrostFormBuilder(model, "/demo", metadata);
    var fkOptions = DemoModel.ForeignKeyOptions(table);
    var values = DemoModel.Record(table, id);
    var html = form.GenerateForm(table, FormMode.Update, values, foreignKeyOptions: fkOptions);
    return Results.Content(Pages.Wrap($"Edit {table}", html), "text/html");
});

app.MapGet("/demo/delete/{table}/{id}", (string table, string id) =>
{
    var model = DemoModel.Create();
    var form = new BifrostFormBuilder(model, "/demo", metadata);
    var values = DemoModel.Record(table, id);
    var html = form.GenerateForm(table, FormMode.Delete, values);
    return Results.Content(Pages.Wrap($"Delete {table}", html), "text/html");
});

app.MapGet("/demo/view/{table}/{id}", (string table, string id) =>
{
    var model = DemoModel.Create();
    var detail = new DetailViewBuilder(model, "/demo");
    var record = DemoModel.RecordObjects(table, id);
    var html = detail.GenerateDetailView(table, record);
    return Results.Content(Pages.Wrap($"{table} Detail", html), "text/html");
});

app.MapGet("/demo/list/{table}", (string table, int? page, int? size) =>
{
    var model = DemoModel.Create();
    var list = new ListViewBuilder(model, "/demo");
    var records = DemoModel.Records(table);
    var pagination = new PaginationInfo(page ?? 1, size ?? 10, records.Count);
    var html = list.GenerateListView(table, records, pagination);
    return Results.Content(Pages.Wrap(table, html), "text/html");
});

app.MapGet("/demo/validation/{table}", (string table) =>
{
    var model = DemoModel.Create();
    var form = new BifrostFormBuilder(model, "/demo", metadata);
    var errors = new List<ValidationError>
    {
        new("Name", "Name is required"),
        new("Price", "Price must be a valid number")
    };
    var values = new Dictionary<string, string?> { ["Name"] = "", ["Price"] = "abc" };
    var html = form.GenerateForm(table, FormMode.Insert, values, errors);
    return Results.Content(Pages.Wrap("Validation Demo", html), "text/html");
});

app.Run();

// HTML page wrapper
static class Pages
{
    public static string Wrap(string title, string body) => $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>{title} - BifrostQL Forms</title>
          <link rel="stylesheet" href="/bifrost-forms.css">
        </head>
        <body>
          <nav style="max-width:960px;margin:0 auto 2rem;padding:1rem 0;border-bottom:1px solid #ccc">
            <strong>BifrostQL Forms</strong> |
            <a href="/">Home</a> |
            <a href="/demo/list/Products">Products</a> |
            <a href="/demo/list/Customers">Customers</a> |
            <a href="/demo/list/Orders">Orders</a>
          </nav>
          <main>{body}</main>
          <script src="/bifrost-forms-enhance.js" defer></script>
        </body>
        </html>
        """;

    public static string Index() => Wrap("Home", """
        <div style="max-width:600px;margin:0 auto">
          <h1>BifrostQL Forms Sample</h1>
          <p>Demonstrates schema-driven HTML forms with in-memory data.</p>
          <h2>Forms</h2>
          <ul>
            <li><a href="/demo/insert/Products">New Product</a> - file upload, enum, FK dropdown</li>
            <li><a href="/demo/insert/Customers">New Customer</a> - email, phone, country enum</li>
            <li><a href="/demo/insert/Orders">New Order</a> - FK dropdowns, status enum</li>
            <li><a href="/demo/update/Products/1">Edit Product #1</a></li>
            <li><a href="/demo/delete/Products/1">Delete Product #1</a></li>
            <li><a href="/demo/validation/Products">Validation Errors</a></li>
          </ul>
          <h2>Views</h2>
          <ul>
            <li><a href="/demo/list/Products">Products</a></li>
            <li><a href="/demo/list/Customers">Customers</a></li>
            <li><a href="/demo/detail/Products/1">Product Detail</a></li>
            <li><a href="/demo/detail/Customers/1">Customer Detail</a></li>
          </ul>
        </div>
        """);
}

// In-memory demo model (avoids test-project dependency)
static class DemoModel
{
    public static IDbModel Create()
    {
        var categories = MakeTable("Categories",
            Col("Id", "int", pk: true, identity: true),
            Col("Name", "nvarchar"),
            Col("Description", "nvarchar", nullable: true));

        var products = MakeTable("Products",
            Col("Id", "int", pk: true, identity: true),
            Col("Name", "nvarchar"),
            Col("Description", "ntext", nullable: true),
            Col("Price", "decimal"),
            Col("CategoryId", "int"),
            Col("Image", "varbinary", nullable: true),
            Col("InStock", "bit"),
            Col("Status", "nvarchar"));

        var customers = MakeTable("Customers",
            Col("Id", "int", pk: true, identity: true),
            Col("Name", "nvarchar"),
            Col("Email", "nvarchar"),
            Col("Phone", "nvarchar", nullable: true),
            Col("Country", "nvarchar"));

        var orders = MakeTable("Orders",
            Col("Id", "int", pk: true, identity: true),
            Col("CustomerId", "int"),
            Col("ProductId", "int"),
            Col("Quantity", "int"),
            Col("TotalPrice", "decimal"),
            Col("Status", "nvarchar"));

        // Single links (FK relationships)
        AddSingleLink(products, "CategoryId", categories, "Id", "Categories");
        AddSingleLink(orders, "CustomerId", customers, "Id", "Customers");
        AddSingleLink(orders, "ProductId", products, "Id", "Products");

        return new SimpleDbModel(categories, products, customers, orders);
    }

    public static Dictionary<string, IReadOnlyList<(string, string)>> ForeignKeyOptions(string table) =>
        table switch
        {
            "Products" => new() { ["CategoryId"] = new List<(string, string)> { ("1", "Electronics"), ("2", "Books"), ("3", "Clothing") } },
            "Orders" => new()
            {
                ["CustomerId"] = new List<(string, string)> { ("1", "Alice Johnson"), ("2", "Bob Smith"), ("3", "Carol White") },
                ["ProductId"] = new List<(string, string)> { ("1", "Wireless Mouse"), ("2", "USB-C Hub"), ("3", "Design Patterns"), ("4", "Cotton T-Shirt") }
            },
            _ => new()
        };

    public static Dictionary<string, string?> Record(string table, string id) =>
        table switch
        {
            "Products" when id == "1" => new() { ["Id"] = "1", ["Name"] = "Wireless Mouse", ["Description"] = "Ergonomic wireless mouse", ["Price"] = "29.99", ["CategoryId"] = "1", ["InStock"] = "true", ["Status"] = "active" },
            "Customers" when id == "1" => new() { ["Id"] = "1", ["Name"] = "Alice Johnson", ["Email"] = "alice@example.com", ["Phone"] = "555-100-2000", ["Country"] = "US" },
            "Orders" when id == "1" => new() { ["Id"] = "1", ["CustomerId"] = "1", ["ProductId"] = "1", ["Quantity"] = "2", ["TotalPrice"] = "59.98", ["Status"] = "shipped" },
            _ => new() { ["Id"] = id }
        };

    public static Dictionary<string, object?> RecordObjects(string table, string id) =>
        Record(table, id).ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

    public static List<IReadOnlyDictionary<string, object?>> Records(string table) =>
        table switch
        {
            "Products" => [
                D("Id", 1, "Name", "Wireless Mouse", "Price", 29.99m, "CategoryId", 1, "InStock", true, "Status", "active"),
                D("Id", 2, "Name", "USB-C Hub", "Price", 49.99m, "CategoryId", 1, "InStock", true, "Status", "active"),
                D("Id", 3, "Name", "Design Patterns", "Price", 39.95m, "CategoryId", 2, "InStock", true, "Status", "active"),
                D("Id", 4, "Name", "Cotton T-Shirt", "Price", 24.99m, "CategoryId", 3, "InStock", true, "Status", "active"),
            ],
            "Customers" => [
                D("Id", 1, "Name", "Alice Johnson", "Email", "alice@example.com", "Phone", "555-100-2000", "Country", "US"),
                D("Id", 2, "Name", "Bob Smith", "Email", "bob@example.com", "Phone", "555-200-3000", "Country", "CA"),
                D("Id", 3, "Name", "Carol White", "Email", "carol@example.com", "Phone", (object?)null, "Country", "GB"),
            ],
            "Orders" => [
                D("Id", 1, "CustomerId", 1, "ProductId", 1, "Quantity", 2, "TotalPrice", 59.98m, "Status", "shipped"),
                D("Id", 2, "CustomerId", 1, "ProductId", 3, "Quantity", 1, "TotalPrice", 39.95m, "Status", "delivered"),
                D("Id", 3, "CustomerId", 2, "ProductId", 2, "Quantity", 1, "TotalPrice", 49.99m, "Status", "pending"),
            ],
            _ => []
        };

    private static ColumnDto Col(string name, string type, bool pk = false, bool identity = false, bool nullable = false) =>
        new() { ColumnName = name, GraphQlName = name, NormalizedName = name, DataType = type, IsPrimaryKey = pk, IsIdentity = identity, IsNullable = nullable };

    private static DbTable MakeTable(string name, params ColumnDto[] columns)
    {
        var lookup = columns.ToDictionary(c => c.ColumnName, c => c, StringComparer.OrdinalIgnoreCase);
        return new DbTable
        {
            DbName = name,
            GraphQlName = name,
            NormalizedName = name,
            TableSchema = "dbo",
            ColumnLookup = lookup,
            GraphQlLookup = lookup,
        };
    }

    private static void AddSingleLink(DbTable child, string childCol, DbTable parent, string parentCol, string linkName)
    {
        child.SingleLinks[linkName] = new TableLinkDto
        {
            Name = $"{child.DbName}->{parent.DbName}",
            ChildTable = child,
            ChildId = child.ColumnLookup[childCol],
            ParentTable = parent,
            ParentId = parent.ColumnLookup[parentCol],
        };
    }

    private static Dictionary<string, object?> D(params object?[] pairs)
    {
        var dict = new Dictionary<string, object?>();
        for (var i = 0; i < pairs.Length; i += 2) dict[(string)pairs[i]!] = pairs[i + 1];
        return dict;
    }
}

sealed class SimpleDbModel : IDbModel
{
    private readonly Dictionary<string, IDbTable> _tables;

    public SimpleDbModel(params IDbTable[] tables)
    {
        _tables = tables.ToDictionary(t => t.DbName, t => t, StringComparer.OrdinalIgnoreCase);
        Tables = tables;
    }

    public IReadOnlyCollection<IDbTable> Tables { get; }
    public IReadOnlyCollection<DbStoredProcedure> StoredProcedures => Array.Empty<DbStoredProcedure>();
    public IDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();
    public string? GetMetadataValue(string property) => Metadata.TryGetValue(property, out var v) ? v?.ToString() : null;
    public bool GetMetadataBool(string property, bool defaultValue) => Metadata.TryGetValue(property, out var v) && v?.ToString() == "true";
    public IDbTable GetTableByFullGraphQlName(string name) => _tables.Values.First(t => t.GraphQlName == name);
    public IDbTable GetTableFromDbName(string name) => _tables[name];
}
