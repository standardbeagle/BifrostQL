using BifrostQL.Integration.Test.Infrastructure;

namespace BifrostQL.Integration.Test.Navigation;

// PostgreSQL concrete test classes - only run when BIFROST_TEST_POSTGRES env var is set

public sealed class PostgresPaginationTests : PaginationTestBase<PostgresTestDatabase>
{
    public PostgresPaginationTests(DatabaseFixture<PostgresTestDatabase> fixture) : base(fixture) { }
}

public sealed class PostgresSortingTests : SortingTestBase<PostgresTestDatabase>
{
    public PostgresSortingTests(DatabaseFixture<PostgresTestDatabase> fixture) : base(fixture) { }
}

public sealed class PostgresFilteringTests : FilteringTestBase<PostgresTestDatabase>
{
    public PostgresFilteringTests(DatabaseFixture<PostgresTestDatabase> fixture) : base(fixture) { }
}

public sealed class PostgresJoinTests : JoinTestBase<PostgresTestDatabase>
{
    public PostgresJoinTests(DatabaseFixture<PostgresTestDatabase> fixture) : base(fixture) { }
}

public sealed class PostgresCombinedTests : CombinedTestBase<PostgresTestDatabase>
{
    public PostgresCombinedTests(DatabaseFixture<PostgresTestDatabase> fixture) : base(fixture) { }
}
