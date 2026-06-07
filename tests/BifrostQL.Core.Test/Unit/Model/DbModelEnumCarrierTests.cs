using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Model;

public class DbModelEnumCarrierTests
{
    [Fact]
    public void EnumColumns_DefaultsNull_OnInterface()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();

        ((IDbModel)model).EnumColumns.Should().BeNull();
    }

    [Fact]
    public void EnumColumns_IsSettableOnDbModel()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();

        // Cast to DbModel to access the settable property
        if (model is DbModel dbModel)
        {
            // Property should be settable to null
            dbModel.EnumColumns = null;
            dbModel.EnumColumns.Should().BeNull();
        }
    }
}
