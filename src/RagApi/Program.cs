using RagApi.Data;
using RagApi.Options;
using RagApi.Services;
using RagApi.Hubs;
using Microsoft.SemanticKernel;
using RagApi.Services.Ingestion;
using RagApi.Services.Ingestion.Parsers;

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

var lmStudioUrl = builder.Configuration["UpRAG:LMStudioEndpoint"] ?? "http://<LM_STUDIO_IP>:1224/v1";
var modelId = builder.Configuration["UpRAG:ModelId"] ?? "qwen2.5-vl-7b-instruct";
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
builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection("Ingestion"));

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

// Register Ingestion Services
builder.Services.AddTransient<IDocumentParser, PdfParser>();
builder.Services.AddTransient<IDocumentParser, WordParser>();
builder.Services.AddTransient<IDocumentParser, PowerPointParser>();
builder.Services.AddTransient<IDocumentParser, TabularParser>();
builder.Services.AddTransient<IDocumentParser, TextParser>();
builder.Services.AddSingleton<IOcrService, TesseractOcrService>();
builder.Services.AddHttpClient<IVisionCaptionService, VisionCaptionService>();
builder.Services.AddTransient<IDocumentParser, ImageParser>();
builder.Services.AddTransient<DocumentParser>();
builder.Services.AddTransient<DocumentIngestionService>();
builder.Services.AddHostedService<DocumentIngestionWorker>();

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
