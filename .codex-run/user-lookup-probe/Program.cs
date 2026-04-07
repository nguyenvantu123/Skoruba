using Microsoft.Extensions.Configuration;
using MySqlConnector;

static IConfiguration BuildConfiguration(string basePath)
{
    var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
    var secretsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft",
        "UserSecrets",
        "9c91d295-54c5-4d09-9bd6-fa56fb74011b",
        "secrets.json");

    var builder = new ConfigurationBuilder()
        .SetBasePath(basePath)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
        .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
        .AddJsonFile("serilog.json", optional: true, reloadOnChange: false)
        .AddJsonFile($"serilog.{environment}.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables()
        .AddJsonFile(secretsPath, optional: true, reloadOnChange: false);

    return builder.Build();
}

static async Task DumpUserAsync(string label, string connectionString, string tableName, string username)
{
    Console.WriteLine($"[{label}] connection={Mask(connectionString)}");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();

    var sql = $@"
select Id, UserName, NormalizedUserName, Email, NormalizedEmail
from `{tableName}`
where UserName = @username or NormalizedUserName = @normalized
order by UserName;";

    try
    {
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@username", username);
        command.Parameters.AddWithValue("@normalized", username.ToUpperInvariant());

        await using var reader = await command.ExecuteReaderAsync();
        var any = false;
        while (await reader.ReadAsync())
        {
            any = true;
            Console.WriteLine(
                $"[{label}] row Id={reader["Id"]}; UserName={reader["UserName"]}; NormalizedUserName={reader["NormalizedUserName"]}; Email={reader["Email"]}; NormalizedEmail={reader["NormalizedEmail"]}");
        }

        if (!any)
        {
            Console.WriteLine($"[{label}] no rows for username '{username}'");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{label}] query failed: {ex.Message}");
    }
}

static async Task DumpSampleUsersAsync(string label, string connectionString, string tableName)
{
    Console.WriteLine($"[{label}] sample connection={Mask(connectionString)}");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();

    var sql = $@"
select Id, UserName, NormalizedUserName
from `{tableName}`
order by UserName
limit 10;";

    try
    {
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var any = false;
        while (await reader.ReadAsync())
        {
            any = true;
            Console.WriteLine(
                $"[{label}] sample Id={reader["Id"]}; UserName={reader["UserName"]}; NormalizedUserName={reader["NormalizedUserName"]}");
        }

        if (!any)
        {
            Console.WriteLine($"[{label}] sample no rows");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{label}] sample query failed: {ex.Message}");
    }
}

static async Task DumpTablesAsync(string label, string connectionString)
{
    Console.WriteLine($"[{label}] tables connection={Mask(connectionString)}");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();

    const string sql = "show tables;";
    await using var command = new MySqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
        Console.WriteLine($"[{label}] table={reader.GetString(0)}");
    }
}

static string Mask(string connectionString)
{
    var builder = new MySqlConnectionStringBuilder(connectionString);
    if (!string.IsNullOrWhiteSpace(builder.Password))
    {
        builder.Password = "***";
    }

    return builder.ConnectionString;
}

Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

var stsBasePath = @"E:\Skoruba\src\Skoruba.Duende.IdentityServer.STS.Identity";
var configuration = BuildConfiguration(stsBasePath);

var centralConnection = configuration.GetConnectionString("IdentityDbConnection")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:IdentityDbConnection");

var tenantConnection = configuration["TenantDatabaseSecrets:db/tenants/tenant-a/user-api"]
    ?? throw new InvalidOperationException("Missing TenantDatabaseSecrets:db/tenants/tenant-a/user-api");

foreach (var username in new[] { "tenantadmin1", "user123456", "tenantadmin1@gmail.com", "user123456@gmail.com" })
{
    await DumpUserAsync("central.users", centralConnection, "users", username);
    await DumpUserAsync("central.aspnetusers", centralConnection, "aspnetusers", username);
    await DumpUserAsync("tenant.aspnetusers", tenantConnection, "aspnetusers", username);
    await DumpUserAsync("tenant.users", tenantConnection, "users", username);
}

await DumpSampleUsersAsync("central.users", centralConnection, "users");
await DumpSampleUsersAsync("tenant.aspnetusers", tenantConnection, "aspnetusers");
await DumpTablesAsync("central", centralConnection);
await DumpTablesAsync("tenant", tenantConnection);
