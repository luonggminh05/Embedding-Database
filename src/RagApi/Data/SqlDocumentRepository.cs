using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using RagApi.Models;
using RagApi.Options;

namespace RagApi.Data;

public interface ISqlDocumentRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task UpsertDocumentsAsync(AddDocumentsRequest request, IReadOnlyList<IReadOnlyList<float>> embeddings, CancellationToken cancellationToken);
    Task DropVectorIndexAsync(CancellationToken cancellationToken);
    Task RecreateVectorIndexAsync(CancellationToken cancellationToken);
    Task<SearchResponse> SearchAsync(string query, IReadOnlyList<float> queryEmbedding, int requestedTopK, CancellationToken cancellationToken);
}

public sealed class SqlDocumentRepository : ISqlDocumentRepository
{
    private readonly string _connectionString;
    private readonly SearchOptions _searchOptions;
    private readonly ILogger<SqlDocumentRepository> _logger;

    public SqlDocumentRepository(
        IOptions<SqlServerOptions> sqlOptions,
        IOptions<SearchOptions> searchOptions,
        ILogger<SqlDocumentRepository> logger)
    {
        _connectionString = sqlOptions.Value.ConnectionString;
        _searchOptions = searchOptions.Value;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        const int maxRetries = 10;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await EnsureDatabaseAsync(cancellationToken);
                await TryRunDdlAsync("ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;", cancellationToken);

                if (!await TableExistsAsync(cancellationToken))
                {
                    await RunDdlAsync("""
                        CREATE TABLE Documents (
                            id_int   INT IDENTITY(1,1) CONSTRAINT PK_Documents PRIMARY KEY CLUSTERED,
                            id       VARCHAR(255) NOT NULL CONSTRAINT UQ_Documents_id UNIQUE,
                            document NVARCHAR(MAX),
                            metadata NVARCHAR(MAX),
                            embedding VECTOR(1024)
                        );
                        """, cancellationToken);
                    _logger.LogInformation("Created Documents table.");
                }

                await EnsureFullTextAsync(cancellationToken);
                await EnsureVectorIndexAsync(cancellationToken);

                _logger.LogInformation("DB initialized successfully.");
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex, "Waiting for SQL Server ({Attempt}/{MaxRetries})...", attempt, maxRetries);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        throw new InvalidOperationException("Could not initialize DB after retries.");
    }

    public async Task UpsertDocumentsAsync(AddDocumentsRequest request, IReadOnlyList<IReadOnlyList<float>> embeddings, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        for (var i = 0; i < request.Documents.Count; i++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                MERGE Documents AS target
                USING (
                    SELECT
                        @id AS id,
                        @document AS document,
                        @metadata AS metadata,
                        CAST(CAST(@embeddingJson AS VARCHAR(MAX)) AS VECTOR(1024)) AS embedding
                ) AS source
                ON (target.id = source.id)
                WHEN MATCHED THEN
                    UPDATE SET document = source.document, metadata = source.metadata, embedding = source.embedding
                WHEN NOT MATCHED THEN
                    INSERT (id, document, metadata, embedding)
                    VALUES (source.id, source.document, source.metadata, source.embedding);
                """;

            AddParameter(command, "@id", SqlDbType.VarChar, 255, request.Ids[i]);
            AddParameter(command, "@document", SqlDbType.NVarChar, -1, request.Documents[i]);
            AddParameter(command, "@metadata", SqlDbType.NVarChar, -1, GetMetadataJson(request, i));
            AddParameter(command, "@embeddingJson", SqlDbType.NVarChar, -1, JsonSerializer.Serialize(embeddings[i]));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        _logger.LogInformation("Upserted {Count} chunks to SQL Server.", request.Documents.Count);
    }

    public async Task DropVectorIndexAsync(CancellationToken cancellationToken)
    {
        await TryRunDdlAsync("DROP INDEX IF EXISTS idx_documents_embedding ON Documents;", cancellationToken);
        _logger.LogInformation("Dropped DiskANN Vector Index for DML operations.");
    }

    public async Task RecreateVectorIndexAsync(CancellationToken cancellationToken)
    {
        await EnsureVectorIndexAsync(cancellationToken);
    }

    public async Task<SearchResponse> SearchAsync(string query, IReadOnlyList<float> queryEmbedding, int requestedTopK, CancellationToken cancellationToken)
    {
        var topK = Math.Clamp(requestedTopK, 1, Math.Max(1, _searchOptions.TopKMax));
        var vectorCandidateCount = Math.Max(topK, _searchOptions.VectorCandidateCount);
        var rrfConstant = _searchOptions.RrfConstant <= 0 ? 60 : _searchOptions.RrfConstant;
        var queryEmbeddingJson = JsonSerializer.Serialize(queryEmbedding);
        var fullTextReady = await HasActiveFullTextIndexAsync(cancellationToken);

        var exactKeyword = System.Text.RegularExpressions.Regex.Match(query, @"\b\d{6,}\b").Success 
            ? System.Text.RegularExpressions.Regex.Match(query, @"\b\d{6,}\b").Value 
            : "";

        var sql = fullTextReady
            ? """
                WITH VectorBase AS (
                    SELECT
                        id,
                        document,
                        metadata,
                        VECTOR_DISTANCE('cosine', embedding, CAST(CAST(@queryEmbeddingJson AS VARCHAR(MAX)) AS VECTOR(1024))) AS distance
                    FROM Documents
                ),
                VectorSearch AS (
                    SELECT TOP (@vectorCandidateCount)
                        id,
                        document,
                        metadata,
                        distance,
                        ROW_NUMBER() OVER (ORDER BY distance ASC) AS vector_rank
                    FROM VectorBase
                    ORDER BY distance ASC
                ),
                KeywordSearch AS (
                    SELECT TOP (@vectorCandidateCount)
                           [KEY] AS id,
                           ROW_NUMBER() OVER (ORDER BY [RANK] DESC) AS keyword_rank
                    FROM FREETEXTTABLE(Documents, document, @query)
                ),
                ExactMatchSearch AS (
                    SELECT id, 10000 AS exact_score
                    FROM Documents
                    WHERE @exactKeyword <> '' AND document LIKE '%' + @exactKeyword + '%'
                ),
                CandidateIds AS (
                    SELECT id FROM VectorSearch
                    UNION
                    SELECT id FROM KeywordSearch
                    UNION
                    SELECT id FROM ExactMatchSearch
                ),
                RankedScores AS (
                    SELECT c.id, d.document, d.metadata, v.distance,
                           v.vector_rank,
                           k.keyword_rank,
                           e.exact_score
                    FROM CandidateIds c
                    INNER JOIN Documents d ON d.id = c.id
                    LEFT JOIN VectorSearch v ON v.id = c.id
                    LEFT JOIN KeywordSearch k ON k.id = c.id
                    LEFT JOIN ExactMatchSearch e ON e.id = c.id
                )
                SELECT TOP (@topK)
                    id, document, metadata, distance,
                    (1.0 / (@rrfConstant + ISNULL(vector_rank, 9999))) + (1.0 / (@rrfConstant + ISNULL(keyword_rank, 9999))) + ISNULL(exact_score, 0) AS RRF_Score
                FROM RankedScores
                ORDER BY RRF_Score DESC;
                """
            : """
                WITH VectorBase AS (
                    SELECT
                        id,
                        document,
                        metadata,
                        VECTOR_DISTANCE('cosine', embedding, CAST(CAST(@queryEmbeddingJson AS VARCHAR(MAX)) AS VECTOR(1024))) AS distance
                    FROM Documents
                ),
                VectorSearch AS (
                    SELECT TOP (@vectorCandidateCount)
                        id,
                        document,
                        metadata,
                        distance,
                        ROW_NUMBER() OVER (ORDER BY distance ASC) AS vector_rank
                    FROM VectorBase
                    ORDER BY distance ASC
                ),
                ExactMatchSearch AS (
                    SELECT id, 10000 AS exact_score
                    FROM Documents
                    WHERE @exactKeyword <> '' AND document LIKE '%' + @exactKeyword + '%'
                ),
                CandidateIds AS (
                    SELECT id FROM VectorSearch
                    UNION
                    SELECT id FROM ExactMatchSearch
                ),
                RankedScores AS (
                    SELECT c.id, d.document, d.metadata, v.distance,
                           v.vector_rank,
                           e.exact_score
                    FROM CandidateIds c
                    INNER JOIN Documents d ON d.id = c.id
                    LEFT JOIN VectorSearch v ON v.id = c.id
                    LEFT JOIN ExactMatchSearch e ON e.id = c.id
                )
                SELECT TOP (@topK)
                    id, document, metadata, distance,
                    (1.0 / (@rrfConstant + ISNULL(vector_rank, 9999))) + ISNULL(exact_score, 0) AS RRF_Score
                FROM RankedScores
                ORDER BY RRF_Score DESC;
                """;

        if (!fullTextReady)
        {
            _logger.LogWarning("Full-Text Index is not active; using vector-only search.");
        }

        var stopwatch = Stopwatch.StartNew();
        var ids = new List<string>();
        var documents = new List<string>();
        var metadatas = new List<JsonElement>();
        var distances = new List<double>();
        var citations = new List<Citation>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@queryEmbeddingJson", SqlDbType.NVarChar, -1, queryEmbeddingJson);
        AddParameter(command, "@query", SqlDbType.NVarChar, 4000, query.Length > 4000 ? query[..4000] : query);
        AddParameter(command, "@topK", SqlDbType.Int, 0, topK);
        AddParameter(command, "@vectorCandidateCount", SqlDbType.Int, 0, vectorCandidateCount);
        AddParameter(command, "@rrfConstant", SqlDbType.Float, 0, rrfConstant);
        AddParameter(command, "@exactKeyword", SqlDbType.NVarChar, -1, exactKeyword);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(reader.GetOrdinal("id"));
            var document = reader.GetString(reader.GetOrdinal("document"));
            var metadata = ParseMetadata(reader.GetString(reader.GetOrdinal("metadata")));
            var distance = reader["distance"] == DBNull.Value ? double.MaxValue : Convert.ToDouble(reader["distance"]);
            var score = Convert.ToDouble(reader["RRF_Score"]);

            ids.Add(id);
            documents.Add(document);
            metadatas.Add(metadata);
            distances.Add(distance);
            citations.Add(BuildCitation(id, metadata, score));
        }

        _logger.LogInformation("SQL search finished in {ElapsedMs}ms.", stopwatch.ElapsedMilliseconds);
        return new SearchResponse([ids], [documents], [metadatas], [distances], [citations]);
    }

    private async Task EnsureDatabaseAsync(CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString);
        var databaseName = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName) ||
            string.Equals(databaseName, "master", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(databaseName, "tempdb", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(databaseName, "model", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(databaseName, "msdb", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        builder.InitialCatalog = "master";
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var escapedName = databaseName.Replace("'", "''");
        var delimitedName = databaseName.Replace("]", "]]");
        command.CommandText = $"IF DB_ID(N'{escapedName}') IS NULL CREATE DATABASE [{delimitedName}];";
        await command.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("Ensured SQL Server database {DatabaseName} exists.", databaseName);
    }
    private async Task EnsureFullTextAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await ExistsAsync("SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'ftCatalog'", cancellationToken))
            {
                await RunDdlAsync("CREATE FULLTEXT CATALOG ftCatalog AS DEFAULT;", cancellationToken);
                _logger.LogInformation("Created Full-Text Catalog.");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            if (!await HasActiveFullTextIndexAsync(cancellationToken))
            {
                await RunDdlAsync("""
                    CREATE FULLTEXT INDEX ON Documents(document)
                    KEY INDEX UQ_Documents_id ON ftCatalog
                    WITH CHANGE_TRACKING AUTO;
                    """, cancellationToken);
                _logger.LogInformation("Created Full-Text Index.");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Full-Text Index not ready; search will use vector-only fallback.");
        }
    }

    private async Task EnsureVectorIndexAsync(CancellationToken cancellationToken)
    {
        if (await ExistsAsync("SELECT 1 FROM sys.indexes WHERE name = 'idx_documents_embedding'", cancellationToken))
        {
            return;
        }

        try
        {
            await RunDdlAsync("""
                CREATE VECTOR INDEX idx_documents_embedding
                ON Documents(embedding)
                WITH (METRIC = 'cosine', TYPE = 'diskann');
                """, cancellationToken);
            _logger.LogInformation("Created DiskANN Vector Index.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vector Index not created (fallback to exact search).");
        }
    }

    private Task<bool> TableExistsAsync(CancellationToken cancellationToken) =>
        ExistsAsync("SELECT 1 FROM sysobjects WHERE name='Documents' AND xtype='U'", cancellationToken);

    private Task<bool> HasActiveFullTextIndexAsync(CancellationToken cancellationToken) =>
        ExistsAsync("""
            SELECT 1
            FROM sys.fulltext_indexes
            WHERE object_id = OBJECT_ID('Documents')
            """, cancellationToken);

    private async Task<bool> ExistsAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value;
    }

    private async Task RunDdlAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task TryRunDdlAsync(string sql, CancellationToken cancellationToken)
    {
        try
        {
            await RunDdlAsync(sql, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Optional DDL failed: {Sql}", sql);
        }
    }

    private static void AddParameter(SqlCommand command, string name, SqlDbType type, int size, object value)
    {
        var parameter = command.Parameters.Add(name, type);
        if (size != 0)
        {
            parameter.Size = size;
        }
        parameter.Value = value;
    }

    private static string GetMetadataJson(AddDocumentsRequest request, int index)
    {
        if (request.Metadatas is null || index >= request.Metadatas.Count)
        {
            return "{}";
        }

        var metadata = request.Metadatas[index];
        return metadata.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "{}" : metadata.GetRawText();
    }

    private static Citation BuildCitation(string id, JsonElement metadata, double score)
    {
        return new Citation(
            GetMetadataString(metadata, "source"),
            GetMetadataString(metadata, "page") ?? GetMetadataString(metadata, "slide"),
            id,
            score);
    }

    private static string? GetMetadataString(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object || !metadata.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static JsonElement ParseMetadata(string metadataJson)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);
        }
        catch
        {
            return JsonSerializer.Deserialize<JsonElement>("{}");
        }
    }
}


