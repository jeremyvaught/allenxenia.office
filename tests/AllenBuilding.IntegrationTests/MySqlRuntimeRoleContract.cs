using System.Net;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

namespace AllenBuilding.IntegrationTests;

public static class MySqlRuntimeRoleContract
{
    public static async Task VerifyAsync(string connection)
    {
        var supplied = new MySqlConnectionStringBuilder(connection);
        // This proof is only for the disposable loopback database created by the test script.
        Assert.Equal("office_check", supplied.Database); Assert.Equal("office_runtime_contract", supplied.UserID);
        Assert.True(IPAddress.TryParse(supplied.Server, out var ip) && IPAddress.IsLoopback(ip));
        var validated = MySqlConnectionPolicy.Validate(connection, local: false);
        await using var db = new MySqlOfficeDbContext(new DbContextOptionsBuilder<MySqlOfficeDbContext>().UseMySQL(validated).Options);
        var building = new Building { Name = "Runtime role CRUD proof" }; db.Buildings.Add(building); await db.SaveChangesAsync();
        db.ChangeTracker.Clear(); var row = await db.Buildings.SingleAsync(x => x.Id == building.Id); Assert.Equal(building.Name, row.Name);
        row.Name = "Runtime role updated"; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        row = await db.Buildings.SingleAsync(x => x.Id == building.Id); Assert.Equal("Runtime role updated", row.Name);
        db.Buildings.Remove(row); await db.SaveChangesAsync(); Assert.False(await db.Buildings.AnyAsync(x => x.Id == building.Id));
        var probe = "runtime_ddl_probe_" + Guid.NewGuid().ToString("N"); var created = false;
        try
        {
            var exception = await Assert.ThrowsAsync<MySqlException>(async () =>
            {
                await using var connectionForDdl = new MySqlConnection(validated); await connectionForDdl.OpenAsync();
                await using var command = new MySqlCommand($"CREATE TABLE `{probe}` (id int NOT NULL PRIMARY KEY)", connectionForDdl);
                await command.ExecuteNonQueryAsync(); created = true;
            });
            Assert.True(exception.Number is 1044 or 1142); // Denied privilege, not a transport or syntax error.
        }
        finally
        {
            // If the guard fails, remove only the isolated uniquely named table this test just created.
            if (created) await db.Database.ExecuteSqlAsync($"DROP TABLE `{probe}`");
        }
    }
}
