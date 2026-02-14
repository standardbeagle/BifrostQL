# BifrostQL Forms - API Reference

## BifrostFormBuilder

**Namespace:** `BifrostQL.Core.Forms`

Generates HTML forms from a database model, producing accessible markup that works without JavaScript.

### Constructor

```csharp
public BifrostFormBuilder(
    IDbModel dbModel,
    string basePath = "/bifrost",
    FormsMetadataConfiguration? metadataConfiguration = null)
```

| Parameter | Type | Description |
|---|---|---|
| `dbModel` | `IDbModel` | Database model to generate forms from. Required. |
| `basePath` | `string` | URL prefix for form actions and links. Default: `/bifrost`. |
| `metadataConfiguration` | `FormsMetadataConfiguration?` | Column-level metadata overrides. Optional. |

### Methods

#### GenerateForm (by table name)

```csharp
public string GenerateForm(
    string tableName,
    FormMode mode,
    IReadOnlyDictionary<string, string?>? values = null,
    IReadOnlyList<ValidationError>? errors = null,
    IReadOnlyDictionary<string, IReadOnlyList<(string value, string displayText)>>? foreignKeyOptions = null)
```

#### GenerateForm (by table object)

```csharp
public string GenerateForm(
    IDbTable table,
    FormMode mode,
    IReadOnlyDictionary<string, string?>? values = null,
    IReadOnlyList<ValidationError>? errors = null,
    IReadOnlyDictionary<string, IReadOnlyList<(string value, string displayText)>>? foreignKeyOptions = null)
```

| Parameter | Type | Description |
|---|---|---|
| `table`/`tableName` | `IDbTable`/`string` | Target table. |
| `mode` | `FormMode` | `Insert`, `Update`, or `Delete`. |
| `values` | `IReadOnlyDictionary<string, string?>?` | Current row values. Required for Update and Delete. |
| `errors` | `IReadOnlyList<ValidationError>?` | Validation errors to display inline. |
| `foreignKeyOptions` | `IReadOnlyDictionary<string, IReadOnlyList<(string, string)>>?` | Pre-fetched options for FK select elements. |

**Returns:** Complete HTML string for the form.

#### GenerateFormControl

```csharp
public string GenerateFormControl(
    ColumnDto column,
    FormMode mode,
    string? value = null,
    IReadOnlyList<ValidationError>? fieldErrors = null)
```

Generates the HTML for a single form control wrapped in a `form-group` div.

---

## BifrostFormValidator

**Namespace:** `BifrostQL.Core.Forms`

Validates form submission data against the database schema.

### Methods

#### Validate

```csharp
public ValidationResult Validate(
    IReadOnlyDictionary<string, string?> formValues,
    IDbTable table,
    FormMode mode)
```

| Parameter | Type | Description |
|---|---|---|
| `formValues` | `IReadOnlyDictionary<string, string?>` | Submitted form values keyed by column name. |
| `table` | `IDbTable` | Table schema to validate against. |
| `mode` | `FormMode` | Form mode (affects which validations run). |

**Returns:** `ValidationResult` with `IsValid` property and `Errors` collection.

---

## ValidationResult

**Namespace:** `BifrostQL.Core.Forms`

| Property | Type | Description |
|---|---|---|
| `IsValid` | `bool` | True when no validation errors exist. |
| `Errors` | `IReadOnlyList<ValidationError>` | List of validation errors. |

---

## ValidationError

**Namespace:** `BifrostQL.Core.Forms`

| Property | Type | Description |
|---|---|---|
| `FieldName` | `string` | Column name of the invalid field. |
| `Message` | `string` | Human-readable error message. |

---

## FormMode

**Namespace:** `BifrostQL.Core.Forms`

| Value | Description |
|---|---|
| `Insert` | New record. IDENTITY columns excluded. |
| `Update` | Edit existing record. Primary key as hidden field. |
| `Delete` | Confirmation. Fields rendered read-only. |

---

## FormsMetadataConfiguration

**Namespace:** `BifrostQL.Core.Forms`

Stores per-column metadata that customizes form control generation.

### Methods

#### ConfigureColumn

```csharp
public FormsMetadataConfiguration ConfigureColumn(
    string tableName,
    string columnName,
    Action<ColumnMetadata> configure)
```

Fluent API. Returns `this` for chaining. Key format is case-insensitive `tableName.columnName`.

#### GetMetadata

```csharp
public ColumnMetadata? GetMetadata(string tableName, string columnName)
```

Returns the configured metadata or null.

---

## ColumnMetadata

**Namespace:** `BifrostQL.Core.Forms`

| Property | Type | Default | Description |
|---|---|---|---|
| `InputType` | `string?` | `null` | Override HTML input type. |
| `Placeholder` | `string?` | `null` | Placeholder text. |
| `Pattern` | `string?` | `null` | HTML5 regex pattern. |
| `Min` | `double?` | `null` | Minimum numeric value. |
| `Max` | `double?` | `null` | Maximum numeric value. |
| `Step` | `double?` | `null` | Numeric step value. |
| `EnumValues` | `string[]?` | `null` | Enum option values. |
| `EnumDisplayNames` | `IReadOnlyDictionary<string, string>?` | `null` | Display labels for enum values. |
| `Accept` | `string?` | `null` | File input accept attribute. |

---

## TypeMapper

**Namespace:** `BifrostQL.Core.Forms`

Static utility for mapping database types to HTML input types.

| Method | Returns | Description |
|---|---|---|
| `GetInputType(string dataType)` | `string` | HTML5 input type for the database type. |
| `IsTextArea(string dataType)` | `bool` | True for `text`/`ntext`. |
| `IsNumericType(string dataType)` | `bool` | True for integer/decimal/float types. |
| `IsDateTimeType(string dataType)` | `bool` | True for date/time types. |
| `IsBooleanType(string dataType)` | `bool` | True for `bit`/`boolean`. |
| `IsBinaryType(string dataType)` | `bool` | True for `varbinary`/`binary`/`image`/`blob`. |
| `AppendTypeAttributes(StringBuilder, string)` | `void` | Appends step/pattern attributes. |

---

## ForeignKeyHandler

**Namespace:** `BifrostQL.Core.Forms`

Detects foreign key relationships and generates select elements.

| Method | Returns | Description |
|---|---|---|
| `IsForeignKey(ColumnDto, IDbTable)` | `bool` | True if column is a FK child. |
| `GetReferencedTable(ColumnDto, IDbTable)` | `IDbTable?` | Parent table for the FK. |
| `GetReferencedKeyColumn(ColumnDto, IDbTable)` | `string?` | Parent PK column name. |
| `GetDisplayColumn(IDbTable)` | `string` | Best column for display labels. |
| `GenerateSelect(ColumnDto, IReadOnlyList<(string, string)>, string?)` | `string` | HTML select element. |

---

## EnumHandler

**Namespace:** `BifrostQL.Core.Forms`

Generates radio buttons or select dropdowns for enum columns.

| Method | Returns | Description |
|---|---|---|
| `ShouldUseRadio(int enumCount)` | `bool` | True for 4 or fewer options. |
| `GenerateRadioGroup(...)` | `string` | Fieldset with radio buttons. |
| `GenerateEnumSelect(...)` | `string` | Select dropdown. |

---

## FileUploadHandler

**Namespace:** `BifrostQL.Core.Forms`

Generates file upload inputs for binary columns.

| Method | Returns | Description |
|---|---|---|
| `GenerateFileInput(ColumnDto, ColumnMetadata?, bool)` | `string` | File input element with optional help text. |

---

## DetailViewBuilder

**Namespace:** `BifrostQL.Core.Views`

Generates read-only detail view HTML for a single database record.

### Constructor

```csharp
public DetailViewBuilder(IDbModel dbModel, string basePath = "/bifrost")
```

### Methods

#### GenerateDetailView

```csharp
public string GenerateDetailView(
    string tableName,
    IReadOnlyDictionary<string, object?> recordData)
```

```csharp
public string GenerateDetailView(
    IDbTable table,
    IReadOnlyDictionary<string, object?> recordData)
```

**Returns:** Complete HTML detail view with definition list, formatted values, and action links.

#### GenerateFieldRow

```csharp
public string GenerateFieldRow(ColumnDto column, object? value, IDbTable table)
```

**Returns:** A single `<dt>`/`<dd>` pair.

#### FormatValue

```csharp
public string FormatValue(ColumnDto column, object? value, IDbTable table)
```

**Returns:** Formatted HTML for a column value (links for FK/email/URL, `<time>` for dates, Yes/No for booleans).

---

## ListViewBuilder

**Namespace:** `BifrostQL.Core.Views`

Generates list view HTML with sortable headers, pagination, and search.

### Constructor

```csharp
public ListViewBuilder(IDbModel dbModel, string basePath = "/bifrost", int maxColumns = 7)
```

### Methods

#### GenerateListView

```csharp
public string GenerateListView(
    string tableName,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> records,
    PaginationInfo pagination,
    string? currentSort = null,
    string? currentDir = null,
    string? currentSearch = null)
```

**Returns:** Complete HTML list view with table, sorting, pagination, and search.

#### GenerateTableHeader / GenerateTableRow / GeneratePagination / GenerateSearchForm

Component-level methods for generating individual parts of the list view.

---

## PaginationInfo

**Namespace:** `BifrostQL.Core.Views`

### Constructor

```csharp
public PaginationInfo(int currentPage, int pageSize, int totalRecords)
```

| Property | Type | Description |
|---|---|---|
| `CurrentPage` | `int` | Current page number (1-based). |
| `PageSize` | `int` | Records per page. |
| `TotalRecords` | `int` | Total record count. |
| `TotalPages` | `int` | Computed total pages. |
| `HasPrevious` | `bool` | True if not on first page. |
| `HasNext` | `bool` | True if not on last page. |

---

## ValueFormatter

**Namespace:** `BifrostQL.Core.Views`

Static utility for formatting database values as HTML.

| Method | Returns | Description |
|---|---|---|
| `FormatDateTime(object)` | `string` | `<time>` element with ISO datetime. |
| `FormatBoolean(object)` | `string` | "Yes" or "No". |
| `FormatNull()` | `string` | `<span class="null-value">(null)</span>`. |
| `TruncateText(string, int)` | `string` | Truncated, HTML-encoded text with ellipsis. |
| `FormatFileSize(long)` | `string` | Human-readable file size (e.g., "1.5 MB"). |
