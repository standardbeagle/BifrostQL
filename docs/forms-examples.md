# BifrostQL Forms - Examples

## Basic CRUD Forms

### Insert Form

```csharp
var formBuilder = new BifrostFormBuilder(dbModel);
string html = formBuilder.GenerateForm("Products", FormMode.Insert);
```

Generated HTML:

```html
<form method="POST" action="/bifrost/form/Products/insert" class="bifrost-form" enctype="multipart/form-data">
  <div class="form-group">
    <label for="name">Name</label>
    <input type="text" id="name" name="Name" required aria-required="true">
  </div>
  <div class="form-group">
    <label for="description">Description</label>
    <textarea id="description" name="Description" rows="5"></textarea>
  </div>
  <div class="form-group">
    <label for="price">Price</label>
    <input type="number" id="price" name="Price" step="0.01" required aria-required="true">
  </div>
  <div class="form-group">
    <label for="image">Image</label>
    <input type="file" id="image" name="Image" accept="image/*">
  </div>
  <div class="form-group">
    <label><input type="checkbox" id="instock" name="InStock" value="true"> In Stock</label>
  </div>
  <div class="form-actions">
    <button type="submit" class="btn-primary">Create</button>
    <a href="/bifrost/list/Products" class="btn-secondary">Cancel</a>
  </div>
</form>
```

### Update Form with Pre-populated Values

```csharp
var values = new Dictionary<string, string?>
{
    ["Id"] = "42",
    ["Name"] = "Widget Pro",
    ["Description"] = "A premium widget for all your needs.",
    ["Price"] = "29.99",
    ["InStock"] = "true"
};
string html = formBuilder.GenerateForm("Products", FormMode.Update, values);
```

### Delete Confirmation

```csharp
string html = formBuilder.GenerateForm("Products", FormMode.Delete, values);
```

## Foreign Key Dropdowns

### Loading FK Options from the Database

```csharp
var fkOptions = new Dictionary<string, IReadOnlyList<(string value, string displayText)>>
{
    ["CategoryId"] = categories.Select(c => (c.Id.ToString(), c.Name)).ToList()
};

string html = formBuilder.GenerateForm("Products", FormMode.Insert, foreignKeyOptions: fkOptions);
```

### Detecting FK Display Columns

```csharp
var referencedTable = ForeignKeyHandler.GetReferencedTable(column, table);
if (referencedTable != null)
{
    string displayCol = ForeignKeyHandler.GetDisplayColumn(referencedTable);
    // displayCol will be "Name", "Title", or the first varchar column
}
```

## Enum Configuration

### Small Enum (Radio Buttons)

```csharp
var metadata = new FormsMetadataConfiguration()
    .ConfigureColumn("Orders", "Status", col =>
    {
        col.EnumValues = new[] { "pending", "shipped", "delivered" };
        col.EnumDisplayNames = new Dictionary<string, string>
        {
            ["pending"] = "Pending",
            ["shipped"] = "Shipped",
            ["delivered"] = "Delivered"
        };
    });
```

Generated HTML:

```html
<div class="form-group">
  <fieldset>
    <legend>Status</legend>
    <label><input type="radio" name="Status" value="pending" required> Pending</label>
    <label><input type="radio" name="Status" value="shipped"> Shipped</label>
    <label><input type="radio" name="Status" value="delivered"> Delivered</label>
  </fieldset>
</div>
```

### Large Enum (Select Dropdown)

```csharp
metadata.ConfigureColumn("Users", "Country", col =>
{
    col.EnumValues = new[] { "US", "CA", "GB", "DE", "FR", "JP", "AU" };
    col.EnumDisplayNames = new Dictionary<string, string>
    {
        ["US"] = "United States", ["CA"] = "Canada", ["GB"] = "United Kingdom",
        ["DE"] = "Germany", ["FR"] = "France", ["JP"] = "Japan", ["AU"] = "Australia"
    };
});
```

## Validation Error Handling

### Full Validation Flow

```csharp
// 1. Validate submitted form data
var validator = new BifrostFormValidator();
var result = validator.Validate(formValues, table, FormMode.Insert);

if (!result.IsValid)
{
    // 2. Re-render form with errors and preserved input
    string html = formBuilder.GenerateForm("Users", FormMode.Insert,
        values: formValues,
        errors: result.Errors,
        foreignKeyOptions: fkOptions);
    // 3. Return the HTML to the user
}
```

### Error Display Result

```html
<div class="form-group error">
  <label for="email">Email</label>
  <input type="email" id="email" name="Email" value="not-an-email"
         aria-invalid="true" aria-describedby="email-error"
         required aria-required="true">
  <span id="email-error" class="error-message">Email must be a valid number</span>
</div>
```

## Detail View

```csharp
var detailBuilder = new DetailViewBuilder(dbModel);

var record = new Dictionary<string, object?>
{
    ["Id"] = 42,
    ["Name"] = "Widget Pro",
    ["Price"] = 29.99m,
    ["CategoryId"] = 3,
    ["InStock"] = true,
    ["CreatedAt"] = new DateTime(2024, 6, 15, 10, 30, 0)
};

string html = detailBuilder.GenerateDetailView("Products", record);
```

Generated HTML:

```html
<div class="bifrost-detail">
  <h1>Product Details</h1>
  <dl>
    <dt>Id</dt><dd>42</dd>
    <dt>Name</dt><dd>Widget Pro</dd>
    <dt>Price</dt><dd>29.99</dd>
    <dt>Category Id</dt><dd><a href="/bifrost/view/Categories/3">3</a></dd>
    <dt>In Stock</dt><dd>Yes</dd>
    <dt>Created At</dt><dd><time datetime="2024-06-15T10:30:00.0000000">June 15, 2024</time></dd>
  </dl>
  <div class="actions">
    <a href="/bifrost/edit/Products/42" class="btn-primary">Edit</a>
    <a href="/bifrost/delete/Products/42" class="btn-danger">Delete</a>
    <a href="/bifrost/list/Products" class="btn-secondary">Back to List</a>
  </div>
</div>
```

## List View with Sorting and Pagination

```csharp
var listBuilder = new ListViewBuilder(dbModel);
var pagination = new PaginationInfo(currentPage: 2, pageSize: 10, totalRecords: 47);

var records = new List<IReadOnlyDictionary<string, object?>>
{
    new Dictionary<string, object?> { ["Id"] = 11, ["Name"] = "Widget A", ["Price"] = 9.99m },
    new Dictionary<string, object?> { ["Id"] = 12, ["Name"] = "Widget B", ["Price"] = 14.99m }
};

string html = listBuilder.GenerateListView("Products", records, pagination,
    currentSort: "Name", currentDir: "asc");
```

## Metadata Configuration Patterns

### Email and Phone Fields

```csharp
var metadata = new FormsMetadataConfiguration()
    .ConfigureColumn("Contacts", "Email", col =>
    {
        col.InputType = "email";
        col.Placeholder = "name@company.com";
    })
    .ConfigureColumn("Contacts", "Phone", col =>
    {
        col.InputType = "tel";
        col.Pattern = "[0-9]{3}-[0-9]{3}-[0-9]{4}";
        col.Placeholder = "555-123-4567";
    });
```

### Currency with Range

```csharp
metadata.ConfigureColumn("Products", "Price", col =>
{
    col.Min = 0;
    col.Max = 999999.99;
    col.Step = 0.01;
    col.Placeholder = "0.00";
});
```

### Document Upload

```csharp
metadata.ConfigureColumn("Invoices", "Attachment", col =>
{
    col.Accept = "application/pdf,.doc,.docx";
});
```

## Progressive Enhancement

### Basic Setup

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <link rel="stylesheet" href="/wwwroot/bifrost-forms.css">
</head>
<body>
  <!-- Generated form HTML -->

  <script src="/wwwroot/bifrost-forms-enhance.js" defer></script>
</body>
</html>
```

### AJAX Form Submission

Add `data-ajax` to opt in:

```html
<form class="bifrost-form" data-ajax method="POST" action="/bifrost/form/Users/insert">
  <!-- fields -->
</form>
```

Server-side AJAX detection:

```csharp
if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
{
    return Json(new
    {
        success = result.IsValid,
        errors = result.Errors.Select(e => new { e.FieldName, e.Message }),
        redirectUrl = result.IsValid ? $"/bifrost/view/{table}/{id}" : null
    });
}
```

## CSS Theming

### Dark Theme

```css
:root {
  --bf-text: #e0e0e0;
  --bf-muted: #9e9e9e;
  --bf-border: #424242;
  --bf-focus: #64b5f6;
  --bf-primary: #1976d2;
  --bf-danger: #e53935;
  --bf-stripe: #1e1e1e;
}
.bifrost-form, .bifrost-detail, .bifrost-list { background: #121212; }
.form-group input, .form-group textarea, .form-group select { background: #1e1e1e; color: #e0e0e0; }
```

### Compact Style

```css
:root { --bf-radius: 2px; }
.form-group { margin-bottom: 0.5rem; }
.form-group input, .form-group textarea, .form-group select { padding: 0.25rem 0.5rem; }
.btn-primary, .btn-secondary, .btn-danger { padding: 0.25rem 0.75rem; font-size: 0.8125rem; }
```

## Integration with ASP.NET Core

### Middleware Approach

```csharp
app.MapPost("/bifrost/form/{table}/insert", async (string table, HttpContext ctx) =>
{
    var dbModel = ctx.RequestServices.GetRequiredService<IDbModel>();
    var formBuilder = new BifrostFormBuilder(dbModel);
    var validator = new BifrostFormValidator();
    var dbTable = dbModel.GetTableFromDbName(table);

    var form = await ctx.Request.ReadFormAsync();
    var values = form.ToDictionary(f => f.Key, f => (string?)f.Value.ToString());
    var result = validator.Validate(values, dbTable, FormMode.Insert);

    if (!result.IsValid)
    {
        var html = formBuilder.GenerateForm(table, FormMode.Insert, values, result.Errors);
        ctx.Response.ContentType = "text/html";
        await ctx.Response.WriteAsync(html);
        return;
    }

    // Execute GraphQL mutation...
    ctx.Response.Redirect($"/bifrost/view/{table}/{newId}", permanent: false);
});
```

### Razor View Approach

```cshtml
@{
    var formBuilder = new BifrostFormBuilder(Model.DbModel);
    var html = formBuilder.GenerateForm(Model.TableName, FormMode.Insert,
        foreignKeyOptions: Model.FkOptions);
}

<link rel="stylesheet" href="/wwwroot/bifrost-forms.css">

@Html.Raw(html)

<script src="/wwwroot/bifrost-forms-enhance.js" defer></script>
```
