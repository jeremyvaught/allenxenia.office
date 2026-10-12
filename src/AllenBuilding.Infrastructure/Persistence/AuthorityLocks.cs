using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

// Call inside a short transaction. SQLite starts an immediate write transaction;
// MySQL requires row locks so revocation and action admission share a serialization point.
public static class AuthorityLocks
{
    public static async Task UserAsync(OfficeDbContext db, string userId, CancellationToken cancellationToken = default)
    {
        if (db is MySqlOfficeDbContext)
            await db.Users.FromSqlInterpolated($"SELECT * FROM asp_net_users WHERE id = {userId} FOR UPDATE").AsNoTracking().ToListAsync(cancellationToken);
    }

    public static async Task ManagerAsync(OfficeDbContext db, string userId, Guid buildingId, CancellationToken cancellationToken = default)
    {
        if (db is MySqlOfficeDbContext)
            await db.BuildingManagers.FromSqlInterpolated($"SELECT * FROM building_managers WHERE user_id = {userId} AND building_id = {buildingId.ToString("D")} FOR UPDATE")
                .AsNoTracking().ToListAsync(cancellationToken);
    }

    public static async Task MembershipAsync(OfficeDbContext db, Guid membershipId, CancellationToken cancellationToken = default)
    {
        if (db is MySqlOfficeDbContext)
            await db.Memberships.FromSqlInterpolated($"SELECT * FROM memberships WHERE id = {membershipId.ToString("D")} FOR UPDATE")
                .AsNoTracking().ToListAsync(cancellationToken);
    }
}
