using BifrostQL.Integration.Test.Infrastructure;

namespace BifrostQL.Integration.Test.Navigation;

// MySQL concrete test classes - only run when BIFROST_TEST_MYSQL env var is set

public sealed class MySqlPaginationTests : PaginationTestBase<MySqlTestDatabase>
{
    public MySqlPaginationTests(DatabaseFixture<MySqlTestDatabase> fixture) : base(fixture) { }
}

public sealed class MySqlSortingTests : SortingTestBase<MySqlTestDatabase>
{
    public MySqlSortingTests(DatabaseFixture<MySqlTestDatabase> fixture) : base(fixture) { }
}

public sealed class MySqlFilteringTests : FilteringTestBase<MySqlTestDatabase>
{
    public MySqlFilteringTests(DatabaseFixture<MySqlTestDatabase> fixture) : base(fixture) { }
}

public sealed class MySqlJoinTests : JoinTestBase<MySqlTestDatabase>
{
    public MySqlJoinTests(DatabaseFixture<MySqlTestDatabase> fixture) : base(fixture) { }
}

public sealed class MySqlCombinedTests : CombinedTestBase<MySqlTestDatabase>
{
    public MySqlCombinedTests(DatabaseFixture<MySqlTestDatabase> fixture) : base(fixture) { }
}
