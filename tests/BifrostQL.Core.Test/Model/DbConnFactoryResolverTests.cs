using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

public class DbConnFactoryResolverTests : IDisposable
{
    public DbConnFactoryResolverTests()
    {
        DbConnFactoryResolver.ClearRegistrations();
    }

    public void Dispose()
    {
        DbConnFactoryResolver.ClearRegistrations();
    }

    #region DetectProvider - SQL Server

    [Theory]
    [InlineData("Server=localhost;Database=mydb;User Id=sa;Password=xxx;")]
    [InlineData("Server=localhost;Database=mydb;Trusted_Connection=True;")]
    [InlineData("Data Source=myserver;Initial Catalog=mydb;Integrated Security=True;")]
    [InlineData("Server=localhost,1433;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True;")]
    [InlineData("Server=tcp:myserver.database.windows.net,1433;Database=mydb;User Id=admin;Password=xxx;")]
    public void DetectProvider_SqlServer_ConnectionStrings_ReturnsSqlServer(string connectionString)
    {
        DbConnFactoryResolver.DetectProvider(connectionString).Should().Be(BifrostDbProvider.SqlServer);
    }

    #endregion

    #region DetectProvider - PostgreSQL

    [Theory]
    [InlineData("Host=localhost;Database=mydb;Username=postgres;Password=xxx;")]
    [InlineData("Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=xxx;")]
    [InlineData("Host=localhost;Database=mydb;Username=postgres;Password=xxx;SSL Mode=Prefer;")]
    public void DetectProvider_PostgreSql_ConnectionStrings_ReturnsPostgreSql(string connectionString)
    {
        DbConnFactoryResolver.DetectProvider(connectionString).Should().Be(BifrostDbProvider.PostgreSql);
    }

    #endregion

    #region DetectProvider - MySQL

    [Theory]
    [InlineData("Server=localhost;Database=mydb;Uid=root;Pwd=xxx;")]
    [InlineData("Server=localhost;Port=3306;Database=mydb;Uid=root;Pwd=xxx;")]
    [InlineData("Server=localhost;Database=mydb;Uid=root;Pwd=xxx;SslMode=None;")]
    public void DetectProvider_MySql_ConnectionStrings_ReturnsMySql(string connectionString)
    {
        DbConnFactoryResolver.DetectProvider(connectionString).Should().Be(BifrostDbProvider.MySql);
    }

    #endregion

    #region DetectProvider - SQLite

    [Theory]
    [InlineData("Data Source=mydb.db")]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=/path/to/database.sqlite")]
    [InlineData("Data Source=C:\\data\\test.sqlite3")]
    [InlineData("Filename=mydb.db")]
    [InlineData("Data Source=test.db;Mode=Memory")]
    public void DetectProvider_Sqlite_ConnectionStrings_ReturnsSqlite(string connectionString)
    {
        DbConnFactoryResolver.DetectProvider(connectionString).Should().Be(BifrostDbProvider.Sqlite);
    }

    #endregion

    #region DetectProvider - Edge Cases

    [Fact]
    public void DetectProvider_EmptyString_ThrowsArgumentException()
    {
        var act = () => DbConnFactoryResolver.DetectProvider("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DetectProvider_NullString_ThrowsArgumentException()
    {
        var act = () => DbConnFactoryResolver.DetectProvider(null!);
        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region ParseProviderName

    [Theory]
    [InlineData("sqlserver", BifrostDbProvider.SqlServer)]
    [InlineData("SqlServer", BifrostDbProvider.SqlServer)]
    [InlineData("SQLSERVER", BifrostDbProvider.SqlServer)]
    [InlineData("mssql", BifrostDbProvider.SqlServer)]
    [InlineData("postgresql", BifrostDbProvider.PostgreSql)]
    [InlineData("postgres", BifrostDbProvider.PostgreSql)]
    [InlineData("npgsql", BifrostDbProvider.PostgreSql)]
    [InlineData("pgsql", BifrostDbProvider.PostgreSql)]
    [InlineData("mysql", BifrostDbProvider.MySql)]
    [InlineData("mariadb", BifrostDbProvider.MySql)]
    [InlineData("sqlite", BifrostDbProvider.Sqlite)]
    public void ParseProviderName_ValidNames_ReturnsCorrectProvider(string name, BifrostDbProvider expected)
    {
        DbConnFactoryResolver.ParseProviderName(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseProviderName_EmptyOrWhitespace_ThrowsArgumentException(string name)
    {
        var act = () => DbConnFactoryResolver.ParseProviderName(name);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParseProviderName_UnknownProvider_ThrowsArgumentException()
    {
        var act = () => DbConnFactoryResolver.ParseProviderName("oracle");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown provider*oracle*");
    }

    #endregion

    #region Create - Default SQL Server

    [Fact]
    public void Create_NoProvider_SqlServerConnectionString_ReturnsDbConnFactory()
    {
        var factory = DbConnFactoryResolver.Create("Server=localhost;Database=mydb;User Id=sa;Password=xxx;");

        factory.Should().BeOfType<DbConnFactory>();
        factory.Dialect.Should().BeAssignableTo<ISqlDialect>();
    }

    [Fact]
    public void Create_ExplicitSqlServer_ReturnsDbConnFactory()
    {
        var factory = DbConnFactoryResolver.Create("Server=localhost;Database=mydb;", BifrostDbProvider.SqlServer);

        factory.Should().BeOfType<DbConnFactory>();
    }

    #endregion

    #region Create - Registered Provider

    [Fact]
    public void Create_RegisteredProvider_UsesRegisteredFactory()
    {
        var mockFactory = NSubstitute.Substitute.For<IDbConnFactory>();
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => mockFactory);

        var result = DbConnFactoryResolver.Create("Data Source=test.db", BifrostDbProvider.Sqlite);

        result.Should().BeSameAs(mockFactory);
    }

    [Fact]
    public void Create_RegisteredProvider_AutoDetected_UsesRegisteredFactory()
    {
        var mockFactory = NSubstitute.Substitute.For<IDbConnFactory>();
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => mockFactory);

        var result = DbConnFactoryResolver.Create("Data Source=test.db");

        result.Should().BeSameAs(mockFactory);
    }

    [Fact]
    public void Create_UnregisteredNonSqlServer_ThrowsInvalidOperation()
    {
        var act = () => DbConnFactoryResolver.Create(
            "Host=localhost;Database=mydb;Username=postgres;Password=xxx;",
            BifrostDbProvider.PostgreSql);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No factory registered*PostgreSql*");
    }

    #endregion

    #region Create - Validation

    [Fact]
    public void Create_EmptyConnectionString_ThrowsArgumentException()
    {
        var act = () => DbConnFactoryResolver.Create("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_NullConnectionString_ThrowsArgumentException()
    {
        var act = () => DbConnFactoryResolver.Create(null!);
        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region Register

    [Fact]
    public void Register_OverwritesPrevious()
    {
        var factory1 = NSubstitute.Substitute.For<IDbConnFactory>();
        var factory2 = NSubstitute.Substitute.For<IDbConnFactory>();

        DbConnFactoryResolver.Register(BifrostDbProvider.MySql, cs => factory1);
        DbConnFactoryResolver.Register(BifrostDbProvider.MySql, cs => factory2);

        var result = DbConnFactoryResolver.Create("Server=localhost;Uid=root;Pwd=xxx;", BifrostDbProvider.MySql);
        result.Should().BeSameAs(factory2);
    }

    [Fact]
    public void Register_NullCreator_ThrowsArgumentNullException()
    {
        var act = () => DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region ParseConnectionStringParts

    [Fact]
    public void ParseConnectionStringParts_BasicParsing_ReturnsKeyValuePairs()
    {
        var parts = DbConnFactoryResolver.ParseConnectionStringParts("Server=localhost;Database=mydb;User Id=sa;");

        parts.Should().ContainKey("Server");
        parts.Should().ContainKey("Database");
        parts.Should().ContainKey("User Id");
        parts["Server"].Should().Be("localhost");
        parts["Database"].Should().Be("mydb");
    }

    [Fact]
    public void ParseConnectionStringParts_CaseInsensitiveLookup()
    {
        var parts = DbConnFactoryResolver.ParseConnectionStringParts("Server=localhost;Database=mydb;");

        parts.Should().ContainKey("server");
        parts.Should().ContainKey("DATABASE");
    }

    [Fact]
    public void ParseConnectionStringParts_TrimsWhitespace()
    {
        var parts = DbConnFactoryResolver.ParseConnectionStringParts(" Server = localhost ; Database = mydb ;");

        parts["Server"].Should().Be("localhost");
        parts["Database"].Should().Be("mydb");
    }

    [Fact]
    public void ParseConnectionStringParts_EmptySemicolons_Ignored()
    {
        var parts = DbConnFactoryResolver.ParseConnectionStringParts("Server=localhost;;Database=mydb;");

        parts.Should().HaveCount(2);
    }

    [Fact]
    public void ParseConnectionStringParts_NoEquals_SkipsSegment()
    {
        var parts = DbConnFactoryResolver.ParseConnectionStringParts("Server=localhost;garbage;Database=mydb;");

        parts.Should().HaveCount(2);
    }

    #endregion
}
