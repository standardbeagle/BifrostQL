using GraphQL;
using GraphQL.DataLoader;
using GraphQL.Types;
using GraphQLProxy;
using GraphQLProxy.Model;

var builder = WebApplication.CreateBuilder(args);
var loader = new DbModelLoader(builder.Configuration);
var model = await loader.LoadAsync();
var connFactory = new DbConnFactory(builder.Configuration.GetConnectionString("ConnStr"));

builder.Services.AddScoped<ITableReaderFactory, TableReaderFactory>();
builder.Services.AddSingleton<IDbModel>(model);
builder.Services.AddSingleton<IDbConnFactory>(connFactory);
builder.Services.AddSingleton<DbDatabaseQuery>();
builder.Services.AddSingleton<DbDatabaseMutation>();
builder.Services.AddSingleton<ISchema, DbSchema>();
builder.Services.AddCors();

builder.Services.AddGraphQL(b => b
    .AddSchema<DbSchema>()
    .AddSystemTextJson()
    .AddDataLoader());

var app = builder.Build();

app.UseDeveloperExceptionPage();
app.UseCors(x => x
    .AllowAnyMethod()
    .AllowAnyHeader()
    .AllowAnyOrigin());
app.UseWebSockets();
app.UseGraphQL("/graphql");
app.UseGraphQLPlayground("/",
    new GraphQL.Server.Ui.Playground.PlaygroundOptions
    {
        GraphQLEndPoint = "/graphql",
        SubscriptionsEndPoint = "/graphql",
    });


await app.RunAsync();
