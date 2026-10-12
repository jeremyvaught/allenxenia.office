using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Identity;

public sealed class BootstrapManager(OfficeDbContext db, UserManager<OfficeUser> users)
{
    public async Task CreateAsync(string email, string password)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (await db.Users.AnyAsync() || await db.BuildingManagers.AnyAsync())
            throw new InvalidOperationException("Bootstrap is only permitted in an empty account database.");
        var user = new OfficeUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Building manager" };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors.Select(x => x.Description)));
        var building = await db.Buildings.SingleOrDefaultAsync() ?? new Building { Name = "Allen Building" };
        if (db.Entry(building).State == EntityState.Detached) db.Buildings.Add(building);
        db.BuildingManagers.Add(new BuildingManager { BuildingId = building.Id, UserId = user.Id });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
