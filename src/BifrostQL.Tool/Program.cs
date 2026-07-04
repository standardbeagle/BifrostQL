using BifrostQL.Core.Model;
using BifrostQL.SqlServer;
using BifrostQL.Tool.Commands;

// Register the SQL Server dialect so connection-string based loaders can resolve it.
// Core carries no built-in provider fallback; dialect packages register themselves.
DbConnFactoryResolver.Register(BifrostDbProvider.SqlServer, cs => new SqlServerDbConnFactory(cs));

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
