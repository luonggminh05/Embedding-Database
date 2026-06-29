namespace RagApi.Options;

public sealed class SqlServerOptions
{
    public string ConnectionString { get; set; } = "Server=localhost;Database=RagDb;User Id=sa;Password=REPLACE_WITH_SQLSERVER_SA_PASSWORD;TrustServerCertificate=True;";
}
