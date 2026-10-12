using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AllenBuilding.Infrastructure.Persistence;

public static class StorageRegistration
{
    public static IServiceCollection AddOfficeStorage(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
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
                connection = $"Data Source={Path.Combine(directory, "office.db")}";
            }
            services.AddDbContext<SqliteOfficeDbContext>(o => o.UseSqlite(connection));
            services.AddScoped<OfficeDbContext>(sp => sp.GetRequiredService<SqliteOfficeDbContext>());
        }
        else if (provider == "MySql")
        {
            var connection = configuration.GetConnectionString("Office");
            if (string.IsNullOrWhiteSpace(connection))
                throw new InvalidOperationException("Set ConnectionStrings__Office using restricted local configuration or environment secrets.");
            services.AddDbContext<MySqlOfficeDbContext>(o => o.UseMySQL(connection));
            services.AddScoped<OfficeDbContext>(sp => sp.GetRequiredService<MySqlOfficeDbContext>());
        }
        else throw new InvalidOperationException("Database:Provider must explicitly be Sqlite or MySql.");
        return services;
    }
}
