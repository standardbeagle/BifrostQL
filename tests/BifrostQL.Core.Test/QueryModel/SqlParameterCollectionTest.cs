using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel;

public sealed class SqlParameterCollectionTest
{
    #region Initial State Tests

    [Fact]
    public void NewInstance_HasEmptyParameters()
    {
        // Arrange & Act
        var sut = new SqlParameterCollection();

        // Assert
        sut.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void Parameters_ReturnsReadOnlyList()
    {
        // Arrange
        var sut = new SqlParameterCollection();

        // Act
        var parameters = sut.Parameters;

        // Assert
        parameters.Should().BeAssignableTo<IReadOnlyList<SqlParameterInfo>>();
    }

    #endregion

    #region AddParameter Tests

    [Fact]
    public void AddParameter_WithValue_ReturnsParameterNameStartingAt0()
    {
        // Arrange
        var sut = new SqlParameterCollection();

        // Act
        var name = sut.AddParameter(42);

        // Assert
        name.Should().Be("@p0");
    }

    [Fact]
    public void AddParameter_MultipleTimes_IncrementsIndex()
    {
        // Arrange
        var sut = new SqlParameterCollection();

        // Act
        var name0 = sut.AddParameter("first");
        var name1 = sut.AddParameter("second");
        var name2 = sut.AddParameter("third");

        // Assert
        name0.Should().Be("@p0");
        name1.Should().Be("@p1");
        name2.Should().Be("@p2");
    }

    [Fact]
    public void AddParameter_StoresValueInParameters()
    {
        // Arrange
        var sut = new SqlParameterCollection();
        const int expectedValue = 42;

        // Act
        sut.AddParameter(expectedValue);

        // Assert
        sut.Parameters.Should().HaveCount(1);
        sut.Parameters[0].Value.Should().Be(expectedValue);
    }

    [Fact]
    public void AddParameter_StoresNameInParameters()
    {
        // Arrange
        var sut = new SqlParameterCollection();

        // Act
        var returnedName = sut.AddParameter("test");

        // Assert
        sut.Parameters[0].Name.Should().Be(returnedName);
    }

    [Fact]
    public void AddParameter_WithDbType_StoresDbType()
    {
        // Arrange
        var sut = new SqlParameterCollection();
        const string expectedDbType = "nvarchar(100)";

        // Act
        sut.AddParameter("test value", expectedDbType);

        // Assert
        sut.Parameters[0].DbType.Should().Be(expectedDbType);
    }

    [Fact]
    public void AddParameter_WithoutDbType_HasNullDbType()
    {
        // Arrange
        var sut = new SqlParameterCollection();

        // Act
        sut.AddParameter("test value");

        // Assert
        sut.Parameters[0].DbType.Should().BeNull();
    }

    [Fact]
    public void AddParameter_WithNullValue_StoresNull()
    {
        // Arrange
        var sut = new SqlParameterCollection();

        // Act
        var name = sut.AddParameter(null);

        // Assert
        name.Should().Be("@p0");
        sut.Parameters[0].Value.Should().BeNull();
    }

    [Fact]
    public void AddParameter_WithVariousTypes_StoresAllCorrectly()
    {
        // Arrange
        var sut = new SqlParameterCollection();
        var dateTime = DateTime.Now;
        var guid = Guid.NewGuid();

        // Act
        sut.AddParameter(42, "int");
        sut.AddParameter("hello", "nvarchar");
        sut.AddParameter(3.14, "float");
        sut.AddParameter(true, "bit");
        sut.AddParameter(dateTime, "datetime");
        sut.AddParameter(guid, "uniqueidentifier");
        sut.AddParameter(null, "nvarchar");

        // Assert
        sut.Parameters.Should().HaveCount(7);
        sut.Parameters[0].Value.Should().Be(42);
        sut.Parameters[1].Value.Should().Be("hello");
        sut.Parameters[2].Value.Should().Be(3.14);
        sut.Parameters[3].Value.Should().Be(true);
        sut.Parameters[4].Value.Should().Be(dateTime);
        sut.Parameters[5].Value.Should().Be(guid);
        sut.Parameters[6].Value.Should().BeNull();
    }

    #endregion

    #region AddParameters (Multiple) Tests

    [Fact]
    public void AddParameters_WithEmptyEnumerable_ReturnsEmptyString()
    {
        // Arrange
        var sut = new SqlParameterCollection();

        // Act
        var result = sut.AddParameters(Array.Empty<object?>());

        // Assert
        result.Should().BeEmpty();
        sut.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void AddParameters_WithSingleValue_ReturnsSingleParameter()
    {
        // Arrange
        var sut = new SqlParameterCollection();

        // Act
        var result = sut.AddParameters(new object?[] { 42 });

        // Assert
        result.Should().Be("@p0");
        sut.Parameters.Should().HaveCount(1);
    }

    [Fact]
    public void AddParameters_WithMultipleValues_ReturnsCommaSeparatedNames()
    {
        // Arrange
        var sut = new SqlParameterCollection();

        // Act
        var result = sut.AddParameters(new object?[] { 1, 2, 3 });

        // Assert
        result.Should().Be("@p0, @p1, @p2");
    }

    [Fact]
    public void AddParameters_StoresAllValues()
    {
        // Arrange
        var sut = new SqlParameterCollection();
        var values = new object?[] { "a", "b", "c", "d", "e" };

        // Act
        sut.AddParameters(values);

        // Assert
        sut.Parameters.Should().HaveCount(5);
        for (var i = 0; i < values.Length; i++)
        {
            sut.Parameters[i].Value.Should().Be(values[i]);
            sut.Parameters[i].Name.Should().Be($"@p{i}");
        }
    }

    [Fact]
    public void AddParameters_WithDbType_AppliesDbTypeToAll()
    {
        // Arrange
        var sut = new SqlParameterCollection();
        const string dbType = "int";

        // Act
        sut.AddParameters(new object?[] { 1, 2, 3 }, dbType);

        // Assert
        sut.Parameters.Should().OnlyContain(p => p.DbType == dbType);
    }

    [Fact]
    public void AddParameters_ContinuesIndexFromPreviousAdds()
    {
        // Arrange
        var sut = new SqlParameterCollection();
        sut.AddParameter("first");
        sut.AddParameter("second");

        // Act
        var result = sut.AddParameters(new object?[] { "a", "b" });

        // Assert
        result.Should().Be("@p2, @p3");
        sut.Parameters.Should().HaveCount(4);
    }

    [Fact]
    public void AddParameters_WithNullValues_StoresNulls()
    {
        // Arrange
        var sut = new SqlParameterCollection();

        // Act
        var result = sut.AddParameters(new object?[] { null, "value", null });

        // Assert
        result.Should().Be("@p0, @p1, @p2");
        sut.Parameters[0].Value.Should().BeNull();
        sut.Parameters[1].Value.Should().Be("value");
        sut.Parameters[2].Value.Should().BeNull();
    }

    [Fact]
    public void AddParameters_ForInClause_GeneratesValidSql()
    {
        // Arrange
        var sut = new SqlParameterCollection();
        var ids = new object?[] { 1, 2, 3, 4, 5 };

        // Act
        var paramNames = sut.AddParameters(ids, "int");

        // Assert
        var expectedSql = $"SELECT * FROM Users WHERE Id IN ({paramNames})";
        expectedSql.Should().Be("SELECT * FROM Users WHERE Id IN (@p0, @p1, @p2, @p3, @p4)");
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public void AddParameter_ConcurrentCalls_ProducesUniqueNames()
    {
        // Arrange
        var sut = new SqlParameterCollection();
        const int threadCount = 100;
        var names = new string[threadCount];

        // Act
        Parallel.For(0, threadCount, i =>
        {
            names[i] = sut.AddParameter(i);
        });

        // Assert
        names.Distinct().Should().HaveCount(threadCount, "all parameter names should be unique");
    }

    [Fact]
    public void AddParameter_ConcurrentCalls_AllParametersStored()
    {
        // Arrange
        var sut = new SqlParameterCollection();
        const int threadCount = 100;

        // Act
        Parallel.For(0, threadCount, i =>
        {
            sut.AddParameter(i);
        });

        // Assert
        sut.Parameters.Should().HaveCount(threadCount);
    }

    [Fact]
    public void AddParameter_ConcurrentCalls_IndexNeverRepeats()
    {
        // Arrange
        var sut = new SqlParameterCollection();
        const int threadCount = 1000;
        var indices = new System.Collections.Concurrent.ConcurrentBag<int>();

        // Act
        Parallel.For(0, threadCount, _ =>
        {
            var name = sut.AddParameter("value");
            var index = int.Parse(name.Replace("@p", ""));
            indices.Add(index);
        });

        // Assert
        indices.Distinct().Should().HaveCount(threadCount, "all indices should be unique");
        indices.Min().Should().Be(0);
        indices.Max().Should().Be(threadCount - 1);
    }

    [Fact]
    public void AddParameters_SequentialCalls_AllValuesStored()
    {
        // Arrange
        var sut = new SqlParameterCollection();
        const int batchCount = 50;
        const int valuesPerBatch = 10;

        // Act
        for (var i = 0; i < batchCount; i++)
        {
            var values = Enumerable.Range(0, valuesPerBatch).Cast<object?>().ToArray();
            sut.AddParameters(values);
        }

        // Assert
        sut.Parameters.Should().HaveCount(batchCount * valuesPerBatch);
    }

    [Fact]
    public void AddParameters_ConcurrentCalls_AllValuesStored()
    {
        // Arrange
        var sut = new SqlParameterCollection();
        const int threadCount = 50;
        const int valuesPerThread = 10;

        // Act - Now fully thread-safe with ConcurrentDictionary
        Parallel.For(0, threadCount, _ =>
        {
            var values = Enumerable.Range(0, valuesPerThread).Cast<object?>().ToArray();
            sut.AddParameters(values);
        });

        // Assert
        sut.Parameters.Should().HaveCount(threadCount * valuesPerThread);
    }

    #endregion

    #region SqlParameterInfo Record Tests

    [Fact]
    public void SqlParameterInfo_WithAllProperties_StoresCorrectly()
    {
        // Arrange & Act
        var info = new SqlParameterInfo("@param", "value", "nvarchar(50)");

        // Assert
        info.Name.Should().Be("@param");
        info.Value.Should().Be("value");
        info.DbType.Should().Be("nvarchar(50)");
    }

    [Fact]
    public void SqlParameterInfo_WithNullDbType_UsesDefault()
    {
        // Arrange & Act
        var info = new SqlParameterInfo("@param", "value");

        // Assert
        info.DbType.Should().BeNull();
    }

    [Fact]
    public void SqlParameterInfo_Equality_WorksCorrectly()
    {
        // Arrange
        var info1 = new SqlParameterInfo("@p0", 42, "int");
        var info2 = new SqlParameterInfo("@p0", 42, "int");
        var info3 = new SqlParameterInfo("@p1", 42, "int");

        // Assert
        info1.Should().Be(info2);
        info1.Should().NotBe(info3);
    }

    [Fact]
    public void SqlParameterInfo_WithDeconstruction_WorksCorrectly()
    {
        // Arrange
        var info = new SqlParameterInfo("@p0", 42, "int");

        // Act
        var (name, value, dbType) = info;

        // Assert
        name.Should().Be("@p0");
        value.Should().Be(42);
        dbType.Should().Be("int");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void AddParameter_WithLargeValue_StoresCorrectly()
    {
        // Arrange
        var sut = new SqlParameterCollection();
        var largeString = new string('x', 100000);

        // Act
        sut.AddParameter(largeString, "nvarchar(max)");

        // Assert
        sut.Parameters[0].Value.Should().Be(largeString);
    }

    [Fact]
    public void AddParameters_WithLargeCollection_HandlesCorrectly()
    {
        // Arrange
        var sut = new SqlParameterCollection();
        var values = Enumerable.Range(0, 10000).Cast<object?>().ToArray();

        // Act
        var result = sut.AddParameters(values);

        // Assert
        sut.Parameters.Should().HaveCount(10000);
        result.Split(", ").Should().HaveCount(10000);
    }

    [Fact]
    public void AddParameter_WithSpecialCharacterValue_StoresCorrectly()
    {
        // Arrange
        var sut = new SqlParameterCollection();
        const string specialValue = "O'Brien; DROP TABLE Users;--";

        // Act
        sut.AddParameter(specialValue, "nvarchar");

        // Assert
        sut.Parameters[0].Value.Should().Be(specialValue);
    }

    [Fact]
    public void AddParameter_IndexStartsAtZero_FirstCall()
    {
        // Arrange
        var sut = new SqlParameterCollection();

        // Act
        var name = sut.AddParameter("test");

        // Assert
        name.Should().Be("@p0");
    }

    [Fact]
    public void MixedOperations_AddParameter_AndAddParameters_WorkCorrectly()
    {
        // Arrange
        var sut = new SqlParameterCollection();

        // Act
        var p0 = sut.AddParameter("first");
        var batch1 = sut.AddParameters(new object?[] { "a", "b" });
        var p3 = sut.AddParameter("middle");
        var batch2 = sut.AddParameters(new object?[] { "x", "y", "z" });
        var p7 = sut.AddParameter("last");

        // Assert
        p0.Should().Be("@p0");
        batch1.Should().Be("@p1, @p2");
        p3.Should().Be("@p3");
        batch2.Should().Be("@p4, @p5, @p6");
        p7.Should().Be("@p7");
        sut.Parameters.Should().HaveCount(8);
    }

    #endregion
}
