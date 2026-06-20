using RagApi.Data;
using RagApi.Options;
using RagApi.Services;
using RagApi.Hubs;
using Microsoft.SemanticKernel;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        var frontendUrl = builder.Configuration["UpRAG:FrontendUrl"] ?? "http://localhost:4200";
        policy.WithOrigins(frontendUrl)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

builder.Services.AddSignalR();

var lmStudioUrl = builder.Configuration["UpRAG:LMStudioEndpoint"] ?? "http://127.0.0.1:1234/v1";
var modelId = builder.Configuration["UpRAG:ModelId"] ?? "meta-llama-3.1-8b-instruct";
var apiKey = builder.Configuration["UpRAG:ApiKey"] ?? "EMPTY";

var kernelBuilder = Kernel.CreateBuilder();
kernelBuilder.AddOpenAIChatCompletion(
    modelId: modelId,
    apiKey: apiKey,
    endpoint: new Uri(lmStudioUrl));
builder.Services.AddSingleton(kernelBuilder.Build());

builder.Services.Configure<TeiOptions>(builder.Configuration.GetSection("Tei"));
builder.Services.Configure<SqlServerOptions>(builder.Configuration.GetSection("SqlServer"));
builder.Services.Configure<SearchOptions>(builder.Configuration.GetSection("Search"));

var queryCacheSize = builder.Configuration.GetValue<int?>("Search:QueryEmbeddingCacheSize") ?? 1024;
builder.Services.AddMemoryCache(options =>
{
    if (queryCacheSize > 0)
    {
        options.SizeLimit = queryCacheSize;
    }
});

builder.Services.AddHttpClient<ITeiEmbeddingService, TeiEmbeddingService>();
builder.Services.AddSingleton<ISqlDocumentRepository, SqlDocumentRepository>();
builder.Services.AddHostedService<DatabaseInitializerHostedService>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "RagApi v1");
});

app.UseCors("AllowAll");

app.MapControllers();
app.MapHub<ChatHub>("/chathub");

app.Run();
