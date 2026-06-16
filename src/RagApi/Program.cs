using RagApi.Data;
using RagApi.Options;
using RagApi.Services;

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
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

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

app.Run();
