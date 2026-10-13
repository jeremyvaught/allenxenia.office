using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AllenBuilding.Infrastructure.Persistence;

public static class StorageRegistration
{
    public static IServiceCollection AddOfficeStorage(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment, bool migrations = false)
    {
        var provider = configuration["Database:Provider"];
        if (provider == "Sqlite")
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
                throw new InvalidOperationException("SQLite is for local development/tests. Production requires MySQL.");
            var connection = configuration.GetConnectionString("Office");
            if (string.IsNullOrWhiteSpace(connection))
            {
                var directory = Path.Combine(environment.ContentRootPath, ".data");
                Directory.CreateDirectory(directory);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                connection = $"Data Source={Path.Combine(directory, "office.db")}";
            }
            var sqlite = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection) { ForeignKeys = true };
            services.AddDbContext<SqliteOfficeDbContext>(o => o.UseSqlite(sqlite.ConnectionString));
            services.AddScoped<OfficeDbContext>(sp => sp.GetRequiredService<SqliteOfficeDbContext>());
        }
        else if (provider == "MySql")
        {
            var local = environment.IsDevelopment() || environment.IsEnvironment("Testing");
            var name = migrations && !local ? "OfficeMigrator" : "Office";
            var connection = configuration.GetConnectionString(name);
            if (string.IsNullOrWhiteSpace(connection))
                throw new InvalidOperationException("Set the dedicated runtime or migration connection through restricted configuration.");
            connection = MySqlConnectionPolicy.Validate(connection, local, migrations);
            services.AddDbContext<MySqlOfficeDbContext>(o => o.UseMySQL(connection));
            services.AddScoped<OfficeDbContext>(sp => sp.GetRequiredService<MySqlOfficeDbContext>());
        }
        else throw new InvalidOperationException("Database:Provider must explicitly be Sqlite or MySql.");
        return services;
    }
}
