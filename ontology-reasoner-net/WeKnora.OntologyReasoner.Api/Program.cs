using WeKnora.OntologyReasoner.Api.Endpoints;
using WeKnora.OntologyReasoner.Core.Assembly;
using WeKnora.OntologyReasoner.Core.Storage;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["DB_CONNECTION_STRING"]
    ?? "Host=localhost;Database=weknora;Username=weknora;Password=weknora";

builder.Services.AddSingleton<IOntologyRepo>(new PostgresOntologyRepo(connectionString));
builder.Services.AddSingleton<SliceAssembler>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapPost("/reason", ReasonEndpoint.Handle);

app.Run();
