namespace RagApi.Options;

public sealed class SqlServerOptions
{
    public string ConnectionString { get; set; } = "Server=localhost;Database=master;User Id=REPLACE_WITH_SQLSERVER_USER;Password=REPLACE_WITH_SQLSERVER_PASSWORD;TrustServerCertificate=True;";
}
