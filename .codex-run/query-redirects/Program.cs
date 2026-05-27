using MySqlConnector;

var connStr = "Server=rm-gs50m8i4y99y7t087ko.mysql.singapore.rds.aliyuncs.com;Port=3306;Database=identityserveradmin;Uid=root;Pwd=Xzyk@12345678;AllowPublicKeyRetrieval=True;SslMode=Disabled;";
await using var conn = new MySqlConnection(connStr);
await conn.OpenAsync();

Console.WriteLine("=== Tables containing 'Key' or 'DataProtect' ===");
await using (var cmd = new MySqlCommand("SHOW TABLES LIKE '%ataProt%'", conn))
await using (var rdr = await cmd.ExecuteReaderAsync())
{
    while (await rdr.ReadAsync()) Console.WriteLine($"  {rdr.GetString(0)}");
}
await using (var cmd = new MySqlCommand("SHOW TABLES LIKE '%Key%'", conn))
await using (var rdr = await cmd.ExecuteReaderAsync())
{
    while (await rdr.ReadAsync()) Console.WriteLine($"  {rdr.GetString(0)}");
}

Console.WriteLine("\n=== DataProtectionKeys row count ===");
try
{
    await using var cmd = new MySqlCommand("SELECT COUNT(*) FROM DataProtectionKeys", conn);
    Console.WriteLine($"  count = {await cmd.ExecuteScalarAsync()}");
}
catch (Exception ex) { Console.WriteLine($"  ERROR: {ex.Message}"); }

Console.WriteLine("\n=== DataProtectionKeys recent rows ===");
try
{
    await using var cmd = new MySqlCommand("SELECT Id, FriendlyName, LEFT(Xml, 50) FROM DataProtectionKeys ORDER BY Id DESC LIMIT 5", conn);
    await using var rdr = await cmd.ExecuteReaderAsync();
    while (await rdr.ReadAsync()) Console.WriteLine($"  Id={rdr.GetInt32(0)} Name={rdr.GetString(1)} XmlStart={rdr.GetString(2)}");
}
catch (Exception ex) { Console.WriteLine($"  ERROR: {ex.Message}"); }
