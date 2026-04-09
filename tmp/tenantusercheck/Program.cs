using Microsoft.AspNetCore.Identity;
using MySqlConnector;

const string tenantConnectionString = "Server=localhost;Port=3307;Database=tenant1.users;Uid=root;Pwd=Xzyk@12345678;AllowPublicKeyRetrieval=True;SslMode=Preferred;";
const string centralConnectionString = "Server=localhost;Port=3307;Database=identityserveradmin;Uid=root;Pwd=Xzyk@12345678;AllowPublicKeyRetrieval=True;SslMode=Disabled;";
const string username = "tenantadmin1";
const string password = "Abc@12345";

var hasher = new PasswordHasher<object>();

await DumpTenantUserAsync(
    connectionString: tenantConnectionString,
    tableName: "aspnetusers",
    label: "tenant1.users/aspnetusers",
    hasIsActiveColumns: true,
    hasCanUseColumns: true,
    hasTenantColumns: false,
    hasBranchColumns: false);

await DumpTenantUserAsync(
    connectionString: centralConnectionString,
    tableName: "users",
    label: "identityserveradmin/users",
    hasIsActiveColumns: false,
    hasCanUseColumns: false,
    hasTenantColumns: true,
    hasBranchColumns: true);

return;

async Task DumpTenantUserAsync(
    string connectionString,
    string tableName,
    string label,
    bool hasIsActiveColumns,
    bool hasCanUseColumns,
    bool hasTenantColumns,
    bool hasBranchColumns)
{
    Console.WriteLine($"=== {label} ===");

    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = BuildQuery(tableName, hasIsActiveColumns, hasCanUseColumns, hasTenantColumns, hasBranchColumns);
    command.Parameters.AddWithValue("@username", username);

    await using var reader = await command.ExecuteReaderAsync();

    var foundAny = false;
    while (await reader.ReadAsync())
    {
        foundAny = true;

        var hash = reader["PasswordHash"]?.ToString();
        var verification = string.IsNullOrWhiteSpace(hash)
            ? PasswordVerificationResult.Failed
            : hasher.VerifyHashedPassword(new object(), hash, password);

        for (var i = 0; i < reader.FieldCount; i++)
        {
            var fieldName = reader.GetName(i);
            var value = await reader.IsDBNullAsync(i) ? "<null>" : reader.GetValue(i)?.ToString();
            Console.WriteLine($"{fieldName}={value}");
        }

        Console.WriteLine($"PasswordHashPresent={!string.IsNullOrWhiteSpace(hash)}");
        Console.WriteLine($"PasswordVerification={verification}");
        Console.WriteLine("---");
    }

    if (!foundAny)
    {
        Console.WriteLine("NO_ROWS");
    }
}

static string BuildQuery(
    string tableName,
    bool hasIsActiveColumns,
    bool hasCanUseColumns,
    bool hasTenantColumns,
    bool hasBranchColumns)
{
    var columns = new List<string>
    {
        "Id",
        "UserName",
        "NormalizedUserName",
        "Email",
        "LockoutEnabled",
        "LockoutEnd",
        "AccessFailedCount",
        "PasswordHash",
        "SecurityStamp",
        "ConcurrencyStamp"
    };

    if (hasIsActiveColumns)
    {
        columns.Add("IsActive");
        columns.Add("IsDeleted");
        columns.Add("FirstTimeLogin");
    }

    if (hasCanUseColumns)
    {
        columns.Add("CanUseMobileApp");
        columns.Add("CanUseAdminPortal");
    }

    if (hasTenantColumns)
    {
        columns.Add("TenantKey");
    }

    if (hasBranchColumns)
    {
        columns.Add("BranchCode");
    }

    var projectedColumns = string.Join("," + Environment.NewLine + "    ", columns);

    return $"""
SELECT
    {projectedColumns}
FROM {tableName}
WHERE NormalizedUserName = UPPER(@username)
   OR UserName = @username
LIMIT 5;
""";
}
