using BifrostQL.Core.Model;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using BifrostQL.Tool.Commands;

// Register every dialect so the database-generic CLI resolves any provider the
// user's connection string points at. Core carries no built-in provider fallback;
// registering only one dialect would turn a Postgres/MySQL/SQLite connection
// string into a runtime InvalidOperationException.
DbConnFactoryResolver.Register(BifrostDbProvider.SqlServer, cs => new SqlServerDbConnFactory(cs));
DbConnFactoryResolver.Register(BifrostDbProvider.PostgreSql, cs => new PostgresDbConnFactory(cs));
DbConnFactoryResolver.Register(BifrostDbProvider.MySql, cs => new MySqlDbConnFactory(cs));
DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));

var config = ToolConfig.Parse(args);
var output = new OutputFormatter(Console.Out, config.JsonOutput);

var router = new CommandRouter()
    .Register(new InitCommand())
    .Register(new TestCommand())
    .Register(new SchemaCommand())
    .Register(new ConfigValidateCommand())
    .Register(new ConfigGenerateCommand())
    .Register(new DoctorCommand())
    .Register(new WatchCommand())
    .Register(new ExportCommand())
    .Register(new ServeCommand());

return await router.ExecuteAsync(config, output);
