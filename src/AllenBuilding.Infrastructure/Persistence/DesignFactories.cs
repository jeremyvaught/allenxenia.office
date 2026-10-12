using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class SqliteDesignFactory : IDesignTimeDbContextFactory<SqliteOfficeDbContext>
{
    public SqliteOfficeDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<SqliteOfficeDbContext>().UseSqlite("Data Source=office-design.db").Options);
}

public sealed class MySqlDesignFactory : IDesignTimeDbContextFactory<MySqlOfficeDbContext>
{
    public MySqlOfficeDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<MySqlOfficeDbContext>().UseMySQL("Server=localhost;Database=office_design;User ID=design_only;").Options);
}
