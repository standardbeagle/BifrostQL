using GraphQL;
using GraphQLProxy;
using GraphQLProxy.Model;

var builder = WebApplication.CreateBuilder(args);
var loader = new DbModelLoader(builder.Configuration);
var model = await loader.LoadAsync();
var connFactory = new DbConnFactory(builder.Configuration.GetConnectionString("ConnStr"));
var dbSchema = new DbSchema(model, connFactory);

//builder.Services.AddSingleton<IDbModel>(model);
//builder.Services.AddScoped<IDbConnFactory, DbConnFactory>();

builder.Services.AddGraphQL(b => b
    .AddSchema(dbSchema)
    .AddSystemTextJson());

var app = builder.Build();

app.UseDeveloperExceptionPage();
app.UseWebSockets();
app.UseGraphQL("/graphql");
app.UseGraphQLPlayground("/",
    new GraphQL.Server.Ui.Playground.PlaygroundOptions
    {
        GraphQLEndPoint = "/graphql",
        SubscriptionsEndPoint = "/graphql",
    });


await app.RunAsync();
