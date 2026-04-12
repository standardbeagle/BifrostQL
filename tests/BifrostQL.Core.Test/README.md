# BifrostQL.Core.Test Organization

This test project is organized into Unit and Integration folders for clarity and maintainability.

## Folder Structure

```
BifrostQL.Core.Test/
├── Unit/                   # Fast, isolated unit tests (no external dependencies)
│   ├── Forms/             # Form builder and validation tests
│   ├── Model/             # Database model, metadata, and configuration tests
│   │   └── AppSchema/     # App schema detection (WordPress, Drupal, etc.)
│   ├── Modules/           # Module system tests (filters, mutations, audit)
│   │   └── Eav/           # Entity-Attribute-Value module tests
│   ├── QueryModel/        # SQL generation and query model tests
│   │   └── TestFixtures/  # Shared test fixtures
│   ├── Resolvers/         # GraphQL resolver tests
│   ├── Schema/            # GraphQL schema generation tests
│   ├── Serialization/     # PHP serialization tests
│   ├── Storage/           # File storage tests
│   ├── StoredProcedure/   # Stored procedure tests
│   ├── Utils/             # Utility class tests
│   └── Views/             # View builder tests
│
├── Integration/            # Tests requiring external resources (databases)
│   ├── Model/             # Model tests with real database connections
│   ├── Modules/           # Module integration tests (multi-database)
│   ├── Schema/            # Schema generation with real databases
│   └── Sqlite/            # SQLite-specific integration tests
│
├── Benchmarks/            # Performance benchmarks
└── Usings.cs              # Global using statements
```

## Running Tests

### Run all tests
```bash
dotnet test
```

### Run only unit tests
```bash
dotnet test --filter "FullyQualifiedName~Unit"
```

### Run only integration tests
```bash
dotnet test --filter "FullyQualifiedName~Integration"
```

### Run tests for a specific area
```bash
dotnet test --filter "FullyQualifiedName~Modules"
dotnet test --filter "FullyQualifiedName~QueryModel"
```

## Test Categories

### Unit Tests
- Fast execution (< 100ms each typically)
- No external dependencies (databases, file system, network)
- Use mocking (NSubstitute) for dependencies
- Test business logic in isolation

### Integration Tests
- Require database connections (SQLite, PostgreSQL, MySQL, SQL Server)
- Test real query execution and schema loading
- Validate cross-database compatibility
- May be slower due to database setup/teardown

## Adding New Tests

1. **Unit tests** go in the `Unit/` folder under the appropriate category
2. **Integration tests** go in the `Integration/` folder
3. Follow existing naming convention: `{ClassName}Tests.cs`
4. Use descriptive test method names: `MethodName_Scenario_ExpectedResult`
