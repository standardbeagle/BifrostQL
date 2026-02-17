using BifrostQL.Core.Model;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;

namespace BifrostQL.Core.Model;

/// <summary>
/// Tests for the ITypeMapper implementations across all supported database dialects.
/// </summary>
public class TypeMapperTests
{
    #region SqlServerTypeMapper

    public class SqlServerTypeMapperTests
    {
        private readonly ITypeMapper _mapper = SqlServerTypeMapper.Instance;

        [Theory]
        [InlineData("int", "Int")]
        [InlineData("smallint", "Short")]
        [InlineData("tinyint", "Byte")]
        [InlineData("bigint", "BigInt")]
        [InlineData("decimal", "Decimal")]
        [InlineData("float", "Float")]
        [InlineData("real", "Float")]
        [InlineData("bit", "Boolean")]
        [InlineData("datetime", "DateTime")]
        [InlineData("datetime2", "DateTime")]
        [InlineData("smalldatetime", "DateTime")]
        [InlineData("datetimeoffset", "DateTimeOffset")]
        [InlineData("json", "JSON")]
        [InlineData("varchar", "String")]
        [InlineData("nvarchar", "String")]
        [InlineData("char", "String")]
        [InlineData("nchar", "String")]
        [InlineData("text", "String")]
        [InlineData("ntext", "String")]
        [InlineData("binary", "String")]
        [InlineData("varbinary", "String")]
        [InlineData("uniqueidentifier", "String")]
        [InlineData("xml", "String")]
        [InlineData("date", "String")]
        [InlineData("time", "String")]
        [InlineData("money", "String")]
        [InlineData("numeric", "String")]
        [InlineData("image", "String")]
        [InlineData("geography", "String")]
        [InlineData("geometry", "String")]
        public void GetGraphQlType_MapsCorrectly(string dbType, string expected)
        {
            Assert.Equal(expected, _mapper.GetGraphQlType(dbType));
        }

        [Theory]
        [InlineData("INT", "Int")]
        [InlineData("DateTime2", "DateTime")]
        [InlineData("VARCHAR", "String")]
        [InlineData("Bit", "Boolean")]
        public void GetGraphQlType_IsCaseInsensitive(string dbType, string expected)
        {
            Assert.Equal(expected, _mapper.GetGraphQlType(dbType));
        }

        [Theory]
        [InlineData("int", false, "Int!")]
        [InlineData("int", true, "Int")]
        [InlineData("varchar", false, "String!")]
        [InlineData("varchar", true, "String")]
        [InlineData("datetime2", false, "DateTime!")]
        [InlineData("datetime2", true, "DateTime")]
        public void GetGraphQlTypeName_HandlesNullability(string dbType, bool isNullable, string expected)
        {
            Assert.Equal(expected, _mapper.GetGraphQlTypeName(dbType, isNullable));
        }

        [Theory]
        [InlineData("datetime2", false, "String!")]
        [InlineData("datetime2", true, "String")]
        [InlineData("datetime", false, "String!")]
        [InlineData("datetime", true, "String")]
        [InlineData("datetimeoffset", false, "String!")]
        [InlineData("datetimeoffset", true, "String")]
        [InlineData("int", false, "Int!")]
        [InlineData("int", true, "Int")]
        [InlineData("varchar", false, "String!")]
        public void GetGraphQlInsertTypeName_DateTimesUseString(string dbType, bool isNullable, string expected)
        {
            Assert.Equal(expected, _mapper.GetGraphQlInsertTypeName(dbType, isNullable));
        }

        [Theory]
        [InlineData("int", "FilterTypeIntInput")]
        [InlineData("varchar", "FilterTypeStringInput")]
        [InlineData("datetime2", "FilterTypeDateTimeInput")]
        [InlineData("bit", "FilterTypeBooleanInput")]
        public void GetFilterInputTypeName_ReturnsCorrectFormat(string dbType, string expected)
        {
            Assert.Equal(expected, _mapper.GetFilterInputTypeName(dbType));
        }

        [Theory]
        [InlineData("int", true)]
        [InlineData("varchar", true)]
        [InlineData("uniqueidentifier", true)]
        [InlineData("geography", true)]
        [InlineData("geometry", true)]
        [InlineData("hierarchyid", true)]
        [InlineData("sql_variant", true)]
        [InlineData("unknown_type", false)]
        [InlineData("jsonb", false)]
        [InlineData("serial", false)]
        public void IsSupported_ReturnsCorrectly(string dbType, bool expected)
        {
            Assert.Equal(expected, _mapper.IsSupported(dbType));
        }

        [Fact]
        public void Instance_IsSingleton()
        {
            Assert.Same(SqlServerTypeMapper.Instance, SqlServerTypeMapper.Instance);
        }
    }

    #endregion

    #region PostgresTypeMapper

    public class PostgresTypeMapperTests
    {
        private readonly ITypeMapper _mapper = PostgresTypeMapper.Instance;

        [Theory]
        [InlineData("integer", "Int")]
        [InlineData("int", "Int")]
        [InlineData("int4", "Int")]
        [InlineData("serial", "Int")]
        [InlineData("smallint", "Short")]
        [InlineData("int2", "Short")]
        [InlineData("smallserial", "Short")]
        [InlineData("bigint", "BigInt")]
        [InlineData("int8", "BigInt")]
        [InlineData("bigserial", "BigInt")]
        [InlineData("decimal", "Decimal")]
        [InlineData("numeric", "Decimal")]
        [InlineData("real", "Float")]
        [InlineData("float4", "Float")]
        [InlineData("double precision", "Float")]
        [InlineData("float8", "Float")]
        [InlineData("boolean", "Boolean")]
        [InlineData("bool", "Boolean")]
        [InlineData("timestamp without time zone", "DateTime")]
        [InlineData("timestamp", "DateTime")]
        [InlineData("timestamp with time zone", "DateTimeOffset")]
        [InlineData("timestamptz", "DateTimeOffset")]
        [InlineData("json", "JSON")]
        [InlineData("jsonb", "JSON")]
        [InlineData("character varying", "String")]
        [InlineData("varchar", "String")]
        [InlineData("text", "String")]
        [InlineData("uuid", "String")]
        [InlineData("bytea", "String")]
        [InlineData("date", "String")]
        [InlineData("time", "String")]
        [InlineData("interval", "String")]
        [InlineData("inet", "String")]
        [InlineData("cidr", "String")]
        [InlineData("point", "String")]
        public void GetGraphQlType_MapsCorrectly(string dbType, string expected)
        {
            Assert.Equal(expected, _mapper.GetGraphQlType(dbType));
        }

        [Theory]
        [InlineData("timestamp", false, "String!")]
        [InlineData("timestamp", true, "String")]
        [InlineData("timestamp without time zone", false, "String!")]
        [InlineData("timestamp with time zone", false, "String!")]
        [InlineData("timestamptz", true, "String")]
        [InlineData("integer", false, "Int!")]
        public void GetGraphQlInsertTypeName_TimestampsUseString(string dbType, bool isNullable, string expected)
        {
            Assert.Equal(expected, _mapper.GetGraphQlInsertTypeName(dbType, isNullable));
        }

        [Theory]
        [InlineData("integer", true)]
        [InlineData("jsonb", true)]
        [InlineData("uuid", true)]
        [InlineData("boolean", true)]
        [InlineData("timestamptz", true)]
        [InlineData("bytea", true)]
        [InlineData("tsvector", true)]
        [InlineData("inet", true)]
        [InlineData("uniqueidentifier", false)]
        [InlineData("nvarchar", false)]
        public void IsSupported_ReturnsCorrectly(string dbType, bool expected)
        {
            Assert.Equal(expected, _mapper.IsSupported(dbType));
        }

        [Fact]
        public void Instance_IsSingleton()
        {
            Assert.Same(PostgresTypeMapper.Instance, PostgresTypeMapper.Instance);
        }
    }

    #endregion

    #region MySqlTypeMapper

    public class MySqlTypeMapperTests
    {
        private readonly ITypeMapper _mapper = MySqlTypeMapper.Instance;

        [Theory]
        [InlineData("int", "Int")]
        [InlineData("integer", "Int")]
        [InlineData("mediumint", "Int")]
        [InlineData("smallint", "Short")]
        [InlineData("tinyint", "Byte")]
        [InlineData("bigint", "BigInt")]
        [InlineData("decimal", "Decimal")]
        [InlineData("numeric", "Decimal")]
        [InlineData("float", "Float")]
        [InlineData("double", "Float")]
        [InlineData("real", "Float")]
        [InlineData("bit", "Boolean")]
        [InlineData("boolean", "Boolean")]
        [InlineData("bool", "Boolean")]
        [InlineData("datetime", "DateTime")]
        [InlineData("timestamp", "DateTime")]
        [InlineData("json", "JSON")]
        [InlineData("varchar", "String")]
        [InlineData("char", "String")]
        [InlineData("text", "String")]
        [InlineData("tinytext", "String")]
        [InlineData("mediumtext", "String")]
        [InlineData("longtext", "String")]
        [InlineData("enum", "String")]
        [InlineData("set", "String")]
        [InlineData("date", "String")]
        [InlineData("time", "String")]
        [InlineData("year", "String")]
        [InlineData("blob", "String")]
        [InlineData("binary", "String")]
        public void GetGraphQlType_MapsCorrectly(string dbType, string expected)
        {
            Assert.Equal(expected, _mapper.GetGraphQlType(dbType));
        }

        [Theory]
        [InlineData("datetime", false, "String!")]
        [InlineData("datetime", true, "String")]
        [InlineData("timestamp", false, "String!")]
        [InlineData("timestamp", true, "String")]
        [InlineData("int", false, "Int!")]
        public void GetGraphQlInsertTypeName_DateTimesUseString(string dbType, bool isNullable, string expected)
        {
            Assert.Equal(expected, _mapper.GetGraphQlInsertTypeName(dbType, isNullable));
        }

        [Theory]
        [InlineData("int", true)]
        [InlineData("enum", true)]
        [InlineData("set", true)]
        [InlineData("json", true)]
        [InlineData("mediumint", true)]
        [InlineData("longtext", true)]
        [InlineData("geometry", true)]
        [InlineData("uniqueidentifier", false)]
        [InlineData("nvarchar", false)]
        [InlineData("jsonb", false)]
        public void IsSupported_ReturnsCorrectly(string dbType, bool expected)
        {
            Assert.Equal(expected, _mapper.IsSupported(dbType));
        }

        [Fact]
        public void Instance_IsSingleton()
        {
            Assert.Same(MySqlTypeMapper.Instance, MySqlTypeMapper.Instance);
        }
    }

    #endregion

    #region SqliteTypeMapper

    public class SqliteTypeMapperTests
    {
        private readonly ITypeMapper _mapper = SqliteTypeMapper.Instance;

        [Theory]
        [InlineData("INTEGER", "Int")]
        [InlineData("int", "Int")]
        [InlineData("mediumint", "Int")]
        [InlineData("smallint", "Short")]
        [InlineData("tinyint", "Byte")]
        [InlineData("bigint", "BigInt")]
        [InlineData("real", "Float")]
        [InlineData("double", "Float")]
        [InlineData("float", "Float")]
        [InlineData("numeric", "Decimal")]
        [InlineData("decimal", "Decimal")]
        [InlineData("boolean", "Boolean")]
        [InlineData("bit", "Boolean")]
        [InlineData("datetime", "DateTime")]
        [InlineData("timestamp", "DateTime")]
        [InlineData("json", "JSON")]
        [InlineData("text", "String")]
        [InlineData("varchar", "String")]
        [InlineData("char", "String")]
        [InlineData("clob", "String")]
        [InlineData("nvarchar", "String")]
        [InlineData("blob", "String")]
        [InlineData("date", "String")]
        [InlineData("none", "String")]
        public void GetGraphQlType_MapsCorrectly(string dbType, string expected)
        {
            Assert.Equal(expected, _mapper.GetGraphQlType(dbType));
        }

        [Theory]
        [InlineData("datetime", false, "String!")]
        [InlineData("datetime", true, "String")]
        [InlineData("timestamp", false, "String!")]
        [InlineData("timestamp", true, "String")]
        [InlineData("INTEGER", false, "Int!")]
        public void GetGraphQlInsertTypeName_DateTimesUseString(string dbType, bool isNullable, string expected)
        {
            Assert.Equal(expected, _mapper.GetGraphQlInsertTypeName(dbType, isNullable));
        }

        [Theory]
        [InlineData("integer", true)]
        [InlineData("text", true)]
        [InlineData("blob", true)]
        [InlineData("boolean", true)]
        [InlineData("json", true)]
        [InlineData("real", true)]
        [InlineData("uniqueidentifier", false)]
        [InlineData("jsonb", false)]
        [InlineData("serial", false)]
        public void IsSupported_ReturnsCorrectly(string dbType, bool expected)
        {
            Assert.Equal(expected, _mapper.IsSupported(dbType));
        }

        [Fact]
        public void Instance_IsSingleton()
        {
            Assert.Same(SqliteTypeMapper.Instance, SqliteTypeMapper.Instance);
        }
    }

    #endregion

    #region Cross-Dialect Consistency

    public class CrossDialectTests
    {
        /// <summary>
        /// Verifies that common types map to the same GraphQL types across all dialects.
        /// </summary>
        [Theory]
        [InlineData("int", "Int")]
        [InlineData("smallint", "Short")]
        [InlineData("bigint", "BigInt")]
        [InlineData("float", "Float")]
        [InlineData("json", "JSON")]
        public void CommonTypes_MapConsistently(string dbType, string expectedGqlType)
        {
            Assert.Equal(expectedGqlType, SqlServerTypeMapper.Instance.GetGraphQlType(dbType));
            Assert.Equal(expectedGqlType, MySqlTypeMapper.Instance.GetGraphQlType(dbType));
            Assert.Equal(expectedGqlType, SqliteTypeMapper.Instance.GetGraphQlType(dbType));
        }

        /// <summary>
        /// Verifies that unknown types always fall back to String across all dialects.
        /// </summary>
        [Theory]
        [InlineData("completely_unknown_type")]
        [InlineData("")]
        [InlineData("  ")]
        public void UnknownTypes_FallBackToString(string dbType)
        {
            Assert.Equal("String", SqlServerTypeMapper.Instance.GetGraphQlType(dbType));
            Assert.Equal("String", PostgresTypeMapper.Instance.GetGraphQlType(dbType));
            Assert.Equal("String", MySqlTypeMapper.Instance.GetGraphQlType(dbType));
            Assert.Equal("String", SqliteTypeMapper.Instance.GetGraphQlType(dbType));
        }

        /// <summary>
        /// Verifies that all mappers implement the ITypeMapper interface correctly.
        /// </summary>
        [Fact]
        public void AllMappers_ImplementInterface()
        {
            ITypeMapper[] mappers =
            {
                SqlServerTypeMapper.Instance,
                PostgresTypeMapper.Instance,
                MySqlTypeMapper.Instance,
                SqliteTypeMapper.Instance,
            };

            foreach (var mapper in mappers)
            {
                // GetGraphQlType should never return null or empty
                var result = mapper.GetGraphQlType("varchar");
                Assert.NotNull(result);
                Assert.NotEmpty(result);

                // GetGraphQlTypeName should include "!" for non-nullable
                var nonNullable = mapper.GetGraphQlTypeName("varchar", false);
                Assert.EndsWith("!", nonNullable);

                // GetGraphQlTypeName should not include "!" for nullable
                var nullable = mapper.GetGraphQlTypeName("varchar", true);
                Assert.DoesNotContain("!", nullable);

                // GetFilterInputTypeName should follow naming convention
                var filterType = mapper.GetFilterInputTypeName("varchar");
                Assert.StartsWith("FilterType", filterType);
                Assert.EndsWith("Input", filterType);
            }
        }
    }

    #endregion

    #region Backward Compatibility

    public class BackwardCompatibilityTests
    {
        /// <summary>
        /// Verifies that the SQL Server type mapper produces identical results
        /// to the original SchemaGenerator.GetSimpleGraphQlTypeName for all
        /// types that were previously handled.
        /// </summary>
        [Theory]
        [InlineData("int", "Int")]
        [InlineData("smallint", "Short")]
        [InlineData("tinyint", "Byte")]
        [InlineData("decimal", "Decimal")]
        [InlineData("bigint", "BigInt")]
        [InlineData("float", "Float")]
        [InlineData("real", "Float")]
        [InlineData("datetime", "DateTime")]
        [InlineData("datetime2", "DateTime")]
        [InlineData("datetimeoffset", "DateTimeOffset")]
        [InlineData("bit", "Boolean")]
        [InlineData("json", "JSON")]
        [InlineData("varchar", "String")]
        [InlineData("nvarchar", "String")]
        [InlineData("char", "String")]
        [InlineData("nchar", "String")]
        [InlineData("binary", "String")]
        [InlineData("varbinary", "String")]
        [InlineData("text", "String")]
        [InlineData("ntext", "String")]
        public void SqlServerMapper_MatchesOriginalSchemaGenerator(string dbType, string expected)
        {
            // Verify the type mapper matches the original behavior
            Assert.Equal(expected, SqlServerTypeMapper.Instance.GetGraphQlType(dbType));

            // Also verify through SchemaGenerator static methods
            Assert.Equal(
                Schema.SchemaGenerator.GetGraphQlTypeName(dbType, true),
                SqlServerTypeMapper.Instance.GetGraphQlTypeName(dbType, true));

            Assert.Equal(
                Schema.SchemaGenerator.GetGraphQlTypeName(dbType, false),
                SqlServerTypeMapper.Instance.GetGraphQlTypeName(dbType, false));
        }

        /// <summary>
        /// Verifies that the insert type name special-casing for datetime types
        /// is preserved in the SQL Server type mapper.
        /// </summary>
        [Theory]
        [InlineData("datetime2", false, "String!")]
        [InlineData("datetime2", true, "String")]
        [InlineData("datetime", false, "String!")]
        [InlineData("datetime", true, "String")]
        [InlineData("datetimeoffset", false, "String!")]
        [InlineData("datetimeoffset", true, "String")]
        [InlineData("int", false, "Int!")]
        [InlineData("varchar", true, "String")]
        public void SqlServerMapper_InsertTypeNames_MatchOriginal(string dbType, bool isNullable, string expected)
        {
            Assert.Equal(expected, SqlServerTypeMapper.Instance.GetGraphQlInsertTypeName(dbType, isNullable));
            Assert.Equal(
                Schema.SchemaGenerator.GetGraphQlInsertTypeName(dbType, isNullable),
                SqlServerTypeMapper.Instance.GetGraphQlInsertTypeName(dbType, isNullable));
        }

        /// <summary>
        /// Verifies that the filter input type name format is preserved.
        /// </summary>
        [Theory]
        [InlineData("int", "FilterTypeIntInput")]
        [InlineData("varchar", "FilterTypeStringInput")]
        [InlineData("bit", "FilterTypeBooleanInput")]
        [InlineData("datetime2", "FilterTypeDateTimeInput")]
        public void SqlServerMapper_FilterTypeNames_MatchOriginal(string dbType, string expected)
        {
            Assert.Equal(expected, SqlServerTypeMapper.Instance.GetFilterInputTypeName(dbType));
            Assert.Equal(
                Schema.SchemaGenerator.GetFilterInputTypeName(dbType),
                SqlServerTypeMapper.Instance.GetFilterInputTypeName(dbType));
        }
    }

    #endregion

    #region IDbConnFactory Integration

    public class DbConnFactoryTypeMapperTests
    {
        [Fact]
        public void DefaultDbConnFactory_UsesSqlServerTypeMapper()
        {
            var factory = new DbConnFactory("Server=localhost;Database=test;User Id=sa;Password=test;TrustServerCertificate=True");
            Assert.IsType<SqlServerTypeMapper>(factory.TypeMapper);
            Assert.Same(SqlServerTypeMapper.Instance, factory.TypeMapper);
        }
    }

    #endregion
}
