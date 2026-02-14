# BifrostQL Forms Sample

Demonstrates all features of BifrostQL's schema-driven HTML forms using in-memory demo data. No database connection required.

## Run

```bash
dotnet run --project examples/forms-sample/forms-sample.csproj
```

Open http://localhost:5000 in your browser.

## Features Demonstrated

- **Insert forms** with text, number, date, checkbox, file upload, and textarea controls
- **Update forms** with pre-populated values
- **Delete forms** with confirmation prompt
- **Foreign key dropdowns** (Products -> Categories, Orders -> Customers/Products)
- **Enum controls** (radio buttons for small sets, select for large sets)
- **Metadata configuration** (email/tel input types, patterns, numeric ranges, custom file accept)
- **Validation error display** with ARIA attributes
- **Detail views** with definition list layout and formatted values
- **List views** with sortable headers, pagination, and search
- **Progressive enhancement** with client-side validation, delete confirmation, and file preview
- **Default CSS stylesheet** with custom properties for theming

## Database Schema

The `schema.sql` file provides a SQL Server schema you can use with a live BifrostQL connection. The sample app uses in-memory equivalents of these tables:

- **Categories** (Id, Name, Description)
- **Products** (Id, Name, Description, Price, CategoryId FK, Image, InStock, Status)
- **Customers** (Id, Name, Email, Phone, Country)
- **Orders** (Id, CustomerId FK, ProductId FK, Quantity, TotalPrice, Status)

## File Structure

```
forms-sample/
  Program.cs           - ASP.NET Core host with routes and demo data
  forms-sample.csproj  - Project file
  appsettings.json     - Configuration (connection string for live database)
  schema.sql           - SQL Server schema for live use
  wwwroot/
    bifrost-forms.css          - Default stylesheet
    bifrost-forms-enhance.js   - Progressive enhancement JavaScript
```
