using BifrostQL.Server;
using Xunit;

namespace BifrostQL.Server.Test
{
    public class BifrostMultiDbOptionsTests
    {
        [Fact]
        public void AddEndpoint_WithValidConfig_AddsEndpoint()
        {
            var options = new BifrostMultiDbOptions();

            options.AddEndpoint(e =>
            {
                e.ConnectionString = "Server=localhost;Database=db1;";
                e.Path = "/graphql/db1";
                e.PlaygroundPath = "/graphiql/db1";
            });

            Assert.Single(options.Endpoints);
            Assert.Equal("Server=localhost;Database=db1;", options.Endpoints[0].ConnectionString);
            Assert.Equal("/graphql/db1", options.Endpoints[0].Path);
            Assert.Equal("/graphiql/db1", options.Endpoints[0].PlaygroundPath);
        }

        [Fact]
        public void AddEndpoint_MultipleEndpoints_AddsAll()
        {
            var options = new BifrostMultiDbOptions();

            options.AddEndpoint(e =>
            {
                e.ConnectionString = "Server=localhost;Database=db1;";
                e.Path = "/graphql/db1";
            });
            options.AddEndpoint(e =>
            {
                e.ConnectionString = "Server=localhost;Database=db2;";
                e.Path = "/graphql/db2";
            });

            Assert.Equal(2, options.Endpoints.Count);
            Assert.Equal("/graphql/db1", options.Endpoints[0].Path);
            Assert.Equal("/graphql/db2", options.Endpoints[1].Path);
        }

        [Fact]
        public void AddEndpoint_DuplicatePath_ThrowsArgumentException()
        {
            var options = new BifrostMultiDbOptions();

            options.AddEndpoint(e =>
            {
                e.ConnectionString = "Server=localhost;Database=db1;";
                e.Path = "/graphql/db1";
            });

            var ex = Assert.Throws<ArgumentException>(() =>
            {
                options.AddEndpoint(e =>
                {
                    e.ConnectionString = "Server=localhost;Database=db2;";
                    e.Path = "/graphql/db1";
                });
            });
            Assert.Contains("/graphql/db1", ex.Message);
        }

        [Fact]
        public void AddEndpoint_DuplicatePathCaseInsensitive_ThrowsArgumentException()
        {
            var options = new BifrostMultiDbOptions();

            options.AddEndpoint(e =>
            {
                e.ConnectionString = "Server=localhost;Database=db1;";
                e.Path = "/graphql/DB1";
            });

            Assert.Throws<ArgumentException>(() =>
            {
                options.AddEndpoint(e =>
                {
                    e.ConnectionString = "Server=localhost;Database=db2;";
                    e.Path = "/graphql/db1";
                });
            });
        }

        [Fact]
        public void AddEndpoint_MissingConnectionString_ThrowsArgumentException()
        {
            var options = new BifrostMultiDbOptions();

            var ex = Assert.Throws<ArgumentException>(() =>
            {
                options.AddEndpoint(e =>
                {
                    e.Path = "/graphql/db1";
                });
            });
            Assert.Contains("ConnectionString", ex.Message);
        }

        [Fact]
        public void AddEndpoint_EmptyPath_ThrowsArgumentException()
        {
            var options = new BifrostMultiDbOptions();

            var ex = Assert.Throws<ArgumentException>(() =>
            {
                options.AddEndpoint(e =>
                {
                    e.ConnectionString = "Server=localhost;Database=db1;";
                    e.Path = "";
                });
            });
            Assert.Contains("Path", ex.Message);
        }

        [Fact]
        public void IsUsingAuth_AllDisabled_ReturnsFalse()
        {
            var options = new BifrostMultiDbOptions();

            options.AddEndpoint(e =>
            {
                e.ConnectionString = "Server=localhost;Database=db1;";
                e.Path = "/graphql/db1";
                e.DisableAuth = true;
            });

            Assert.False(options.IsUsingAuth);
        }

        [Fact]
        public void IsUsingAuth_OneEnabled_ReturnsTrue()
        {
            var options = new BifrostMultiDbOptions();

            options.AddEndpoint(e =>
            {
                e.ConnectionString = "Server=localhost;Database=db1;";
                e.Path = "/graphql/db1";
                e.DisableAuth = true;
            });
            options.AddEndpoint(e =>
            {
                e.ConnectionString = "Server=localhost;Database=db2;";
                e.Path = "/graphql/db2";
                e.DisableAuth = false;
            });

            Assert.True(options.IsUsingAuth);
        }

        [Fact]
        public void IsUsingAuth_NoEndpoints_ReturnsFalse()
        {
            var options = new BifrostMultiDbOptions();
            Assert.False(options.IsUsingAuth);
        }

        [Fact]
        public void AddEndpoint_DefaultValues_HasCorrectDefaults()
        {
            var config = new BifrostEndpointConfig();

            Assert.Equal("/graphql", config.Path);
            Assert.Equal("/", config.PlaygroundPath);
            Assert.True(config.DisableAuth);
            Assert.Empty(config.Metadata);
            Assert.Null(config.ConnectionString);
        }

        [Fact]
        public void AddEndpoint_WithMetadata_StoresMetadata()
        {
            var options = new BifrostMultiDbOptions();

            options.AddEndpoint(e =>
            {
                e.ConnectionString = "Server=localhost;Database=db1;";
                e.Path = "/graphql/db1";
                e.Metadata = new[] { "dbo.users { tenant-filter: tenant_id }" };
            });

            Assert.Single(options.Endpoints[0].Metadata);
            Assert.Equal("dbo.users { tenant-filter: tenant_id }", options.Endpoints[0].Metadata.First());
        }

        [Fact]
        public void AddEndpoint_FluentChaining_ReturnsSameInstance()
        {
            var options = new BifrostMultiDbOptions();

            var result = options
                .AddEndpoint(e =>
                {
                    e.ConnectionString = "Server=localhost;Database=db1;";
                    e.Path = "/graphql/db1";
                })
                .AddEndpoint(e =>
                {
                    e.ConnectionString = "Server=localhost;Database=db2;";
                    e.Path = "/graphql/db2";
                });

            Assert.Same(options, result);
            Assert.Equal(2, options.Endpoints.Count);
        }
    }
}
