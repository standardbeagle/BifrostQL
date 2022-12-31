using HandyQL.Core;

var builder = WebApplication.CreateBuilder(args);
builder.AddHandyQL();
builder.Services.AddCors();
var app = builder.Build();

app.UseDeveloperExceptionPage();
app.UseCors(x => x
    .AllowAnyMethod()
    .AllowAnyHeader()
    .AllowAnyOrigin());
app.UseHandyQL();


await app.RunAsync();
