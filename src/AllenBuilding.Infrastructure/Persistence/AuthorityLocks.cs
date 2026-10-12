using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AllenBuilding.Infrastructure.Persistence;

// Call inside a short transaction. SQLite starts an immediate write transaction;
// MySQL requires row locks so revocation and action admission share a serialization point.
public static class AuthorityLocks
{
    public static Task<IDbContextTransaction> BeginAsync(OfficeDbContext db, CancellationToken cancellationToken = default)
        => db.Database.BeginTransactionAsync(db is MySqlOfficeDbContext ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);

    public static async Task ContractorAsync(OfficeDbContext db, Guid id, CancellationToken cancellationToken = default)
    {
        if (db is MySqlOfficeDbContext)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM contractors WHERE id = {id.ToString("D")} FOR UPDATE", cancellationToken);
    }

    public static async Task AssignmentManagersAsync(OfficeDbContext db, string actorId, string? targetId, Guid buildingId, CancellationToken cancellationToken)
    {
        // Shared locks prevent grant removal; fixed ordering avoids cross-assignment cycles.
        foreach (var id in new[] { actorId, targetId }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Order(StringComparer.Ordinal))
            if (db is MySqlOfficeDbContext)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT user_id FROM building_managers WHERE user_id = {id} AND building_id = {buildingId.ToString("D")} FOR SHARE", cancellationToken);
    }

    public static async Task WorkOrderAsync(OfficeDbContext db, Guid orderId, CancellationToken cancellationToken = default)
    {
        if (db is MySqlOfficeDbContext)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM work_orders WHERE id = {orderId.ToString("D")} FOR UPDATE", cancellationToken);
    }
    public static async Task UserAsync(OfficeDbContext db, string userId, CancellationToken cancellationToken = default)
    {
        if (db is MySqlOfficeDbContext)
            // Shared account locks block stamp changes without deadlocking notification
            // foreign-key checks against other managers. Resource locks serialize writes.
            await db.Users.FromSqlInterpolated($"SELECT * FROM asp_net_users WHERE id = {userId} FOR SHARE").AsNoTracking().ToListAsync(cancellationToken);
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
