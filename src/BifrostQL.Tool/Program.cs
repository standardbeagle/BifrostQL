using BifrostQL.Tool.Commands;

var config = ToolConfig.Parse(args);
var output = new OutputFormatter(Console.Out, config.JsonOutput);

var router = new CommandRouter()
    .Register(new InitCommand())
    .Register(new TestCommand())
    .Register(new SchemaCommand())
    .Register(new ConfigValidateCommand())
    .Register(new ConfigGenerateCommand())
    .Register(new ServeCommand());

return await router.ExecuteAsync(config, output);
