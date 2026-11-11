using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AllenBuilding.Infrastructure.Persistence.Configuration;

internal sealed class AssistantMaintenanceReviewConfiguration : IEntityTypeConfiguration<AssistantMaintenanceReview>
{
    public void Configure(EntityTypeBuilder<AssistantMaintenanceReview> e)
    {
        e.ToTable("assistant_maintenance_reviews");
        e.HasKey(x => x.Id);
        e.Property(x => x.OwnerId).HasMaxLength(450);
        e.Property(x => x.SecurityStamp).HasMaxLength(256);
        e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.Title).HasMaxLength(120);
        e.Property(x => x.Description).HasMaxLength(4000);
        e.Property(x => x.Location).HasMaxLength(160);
        e.Property(x => x.Comment).HasMaxLength(2000);
        e.Property(x => x.SourcesJson).HasMaxLength(4096);
        e.Property(x => x.PayloadHash).HasMaxLength(64);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.Property(x => x.ResultStatus).HasConversion<string>().HasMaxLength(32);
        e.HasIndex(x => x.ConversationId).IsUnique();
        e.HasIndex(x => new { x.OwnerId, x.OperationId }).IsUnique();
        e.HasIndex(x => new { x.MembershipId, x.State });
        e.HasIndex(x => new { x.State, x.ExpiresAt });
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Membership>().WithMany().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.ResultId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkOrderHistoryConfiguration : IEntityTypeConfiguration<WorkOrderHistory>
{
    public void Configure(EntityTypeBuilder<WorkOrderHistory> e)
    {
        e.ToTable("work_order_history");
        e.HasKey(x => x.Id);
        e.Property(x => x.ActorId).HasMaxLength(450);
        e.Property(x => x.Action).HasMaxLength(80);
        e.Property(x => x.Note).HasMaxLength(2000);
        e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.Visibility).HasConversion<string>().HasMaxLength(32);
        e.HasIndex(x => new { x.WorkOrderId, x.OccurredAt, x.Id });
        e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MaintenanceReceiptConfiguration : IEntityTypeConfiguration<MaintenanceReceipt>
{
    public void Configure(EntityTypeBuilder<MaintenanceReceipt> e)
    {
        e.ToTable("maintenance_receipts");
        e.HasKey(x => new { x.ActorId, x.OperationId });
        e.Property(x => x.ActorId).HasMaxLength(450);
        e.Property(x => x.PayloadHash).HasMaxLength(64);
        e.Property(x => x.ResultStatus).HasConversion<string>().HasMaxLength(32);
        e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ContractorConfiguration : IEntityTypeConfiguration<Contractor>
{
    public void Configure(EntityTypeBuilder<Contractor> e)
    {
        e.ToTable("contractors");
        e.HasKey(x => x.Id);
        e.Property(x => x.Name).HasMaxLength(160).IsRequired();
        e.Property(x => x.Trade).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.Email).HasMaxLength(254);
        e.Property(x => x.Phone).HasMaxLength(80);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.HasIndex(x => new { x.BuildingId, x.Active, x.Name, x.Id });
        e.HasOne<Building>().WithMany().HasForeignKey(x => x.BuildingId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkOrderConfiguration : IEntityTypeConfiguration<WorkOrder>
{
    public void Configure(EntityTypeBuilder<WorkOrder> e)
    {
        e.ToTable("work_orders");
        e.HasKey(x => x.Id);
        e.Property(x => x.RequesterUserId).HasMaxLength(450).IsRequired();
        e.Property(x => x.Title).HasMaxLength(120).IsRequired();
        e.Property(x => x.Description).HasMaxLength(4000).IsRequired();
        e.Property(x => x.Location).HasMaxLength(160).IsRequired();
        e.Property(x => x.Resolution).HasMaxLength(2000);
        e.Property(x => x.AssignedManagerId).HasMaxLength(450);
        e.HasOne<Contractor>().WithMany().HasForeignKey(x => x.AssignedContractorId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.AssignedManagerId).OnDelete(DeleteBehavior.Restrict);
        e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.Trade).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.HasIndex(x => new { x.SuiteId, x.Status, x.CreatedAt, x.Id });
        e.HasOne<Suite>().WithMany().HasForeignKey(x => x.SuiteId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Membership>().WithMany()
            .HasForeignKey(x => new { x.RequesterMembershipId, x.SuiteId, x.RequesterUserId })
            .HasPrincipalKey(x => new { x.Id, x.SuiteId, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.RequesterUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
