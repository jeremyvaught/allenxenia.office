using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AllenBuilding.Infrastructure.Persistence.Configuration;

internal sealed class AccountAuditConfiguration : IEntityTypeConfiguration<AccountAudit>
{
    public void Configure(EntityTypeBuilder<AccountAudit> e)
    {
        e.ToTable("account_audit");
        e.HasKey(x => x.Id);
        e.Property(x => x.ActorId).HasMaxLength(450);
        e.Property(x => x.Action).HasMaxLength(80);
        e.Property(x => x.SubjectId).HasMaxLength(450);
        e.Property(x => x.Reason).HasMaxLength(2000);
    }
}

internal sealed class AccountMessageConfiguration : IEntityTypeConfiguration<AccountMessage>
{
    public void Configure(EntityTypeBuilder<AccountMessage> e)
    {
        e.ToTable("account_messages");
        e.HasKey(x => x.Id);
        e.Property(x => x.UserId).HasMaxLength(450);
        e.Property(x => x.Kind).HasMaxLength(80);
        e.Property(x => x.ProtectedPayload).HasColumnType("TEXT");
        e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.LeaseToken).IsConcurrencyToken();
        e.Property(x => x.NextAttemptAt).HasDefaultValue(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        e.Property(x => x.LastError).HasMaxLength(80);
        e.Property(x => x.OriginTraceId).HasMaxLength(32);
        e.Property(x => x.OriginSpanId).HasMaxLength(16);
        e.HasIndex(x => new { x.State, x.NextAttemptAt, x.CreatedAt, x.Id });
        e.HasOne<Building>().WithMany().HasForeignKey(x => x.BuildingId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BuildingManagerConfiguration : IEntityTypeConfiguration<BuildingManager>
{
    public void Configure(EntityTypeBuilder<BuildingManager> e)
    {
        e.ToTable("building_managers");
        e.HasKey(x => new { x.BuildingId, x.UserId });
        e.Property(x => x.UserId).HasMaxLength(450);
        e.HasOne<Building>().WithMany().HasForeignKey(x => x.BuildingId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AccessReceiptConfiguration : IEntityTypeConfiguration<AccessReceipt>
{
    public void Configure(EntityTypeBuilder<AccessReceipt> e)
    {
        e.ToTable("access_receipts");
        e.HasKey(x => new { x.ActorId, x.OperationId });
        e.Property(x => x.ActorId).HasMaxLength(450);
        e.Property(x => x.PayloadHash).HasMaxLength(64);
        e.HasOne<Membership>().WithMany().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> e)
    {
        e.ToTable("invitations");
        e.HasKey(x => x.Id);
        e.Property(x => x.UserId).HasMaxLength(450);
        e.Property(x => x.IssuedBy).HasMaxLength(450);
        e.Property(x => x.RevokedBy).HasMaxLength(450);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.HasIndex(x => x.UserId).IsUnique();
        e.HasOne<Suite>().WithMany().HasForeignKey(x => x.SuiteId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BuildingConfiguration : IEntityTypeConfiguration<Building>
{
    public void Configure(EntityTypeBuilder<Building> e)
    {
        e.ToTable("buildings");
        e.HasKey(x => x.Id);
        e.Property(x => x.Name).HasMaxLength(160).IsRequired();
        e.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
    }
}

internal sealed class SuiteConfiguration : IEntityTypeConfiguration<Suite>
{
    public void Configure(EntityTypeBuilder<Suite> e)
    {
        e.ToTable("suites");
        e.HasKey(x => x.Id);
        e.Property(x => x.Name).HasMaxLength(80).IsRequired();
        e.HasIndex(x => new { x.BuildingId, x.Name }).IsUnique();
        e.HasOne<Building>().WithMany().HasForeignKey(x => x.BuildingId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> e)
    {
        e.ToTable("memberships");
        e.HasKey(x => x.Id);
        e.HasAlternateKey(x => new { x.Id, x.SuiteId, x.UserId });
        e.Property(x => x.UserId).HasMaxLength(450).IsRequired();
        e.Property(x => x.RevokedBy).HasMaxLength(450);
        e.Property(x => x.RevocationReason).HasMaxLength(2000);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.HasIndex(x => new { x.UserId, x.SuiteId });
        e.HasOne<Suite>().WithMany().HasForeignKey(x => x.SuiteId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ActiveGrantConfiguration : IEntityTypeConfiguration<ActiveGrant>
{
    public void Configure(EntityTypeBuilder<ActiveGrant> e)
    {
        e.ToTable("active_grants");
        e.HasKey(x => new { x.UserId, x.SuiteId });
        e.Property(x => x.UserId).HasMaxLength(450);
        e.HasIndex(x => x.MembershipId).IsUnique();
        e.HasOne<Membership>().WithMany()
            .HasForeignKey(x => new { x.MembershipId, x.SuiteId, x.UserId })
            .HasPrincipalKey(x => new { x.Id, x.SuiteId, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Suite>().WithMany().HasForeignKey(x => x.SuiteId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
