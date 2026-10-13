using AllenBuilding.Application.Diagnostics;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AllenBuilding.Infrastructure.Persistence;

public abstract class OfficeDbContext(DbContextOptions options) : IdentityDbContext<OfficeUser>(options)
{
    public DbSet<BillingCustomerBinding> BillingCustomers => Set<BillingCustomerBinding>();
    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();
    public DbSet<PaymentAttemptItem> PaymentAttemptItems => Set<PaymentAttemptItem>();
    public DbSet<CollectionGuard> CollectionGuards => Set<CollectionGuard>();
    public DbSet<RentAccount> RentAccounts => Set<RentAccount>();
    public DbSet<RentAgreement> RentAgreements => Set<RentAgreement>();
    public DbSet<BillingPeriod> BillingPeriods => Set<BillingPeriod>();
    public DbSet<Obligation> Obligations => Set<Obligation>();
    public DbSet<BillingReceipt> BillingReceipts => Set<BillingReceipt>();
    public DbSet<HelpArticle> HelpArticles => Set<HelpArticle>();
    public DbSet<HelpRevision> HelpRevisions => Set<HelpRevision>();
    public DbSet<Building> Buildings => Set<Building>();
    public DbSet<Suite> Suites => Set<Suite>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<ActiveGrant> ActiveGrants => Set<ActiveGrant>();
    public DbSet<Contractor> Contractors => Set<Contractor>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<BuildingManager> BuildingManagers => Set<BuildingManager>();
    public DbSet<AccessReceipt> AccessReceipts => Set<AccessReceipt>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<AccountAudit> AccountAudits => Set<AccountAudit>();
    public DbSet<AccountMessage> AccountMessages => Set<AccountMessage>();
    public DbSet<WorkOrderHistory> WorkOrderHistory => Set<WorkOrderHistory>();
    public DbSet<MaintenanceReceipt> MaintenanceReceipts => Set<MaintenanceReceipt>();

    private void StampMessages()
    {
        var origin = OfficeTelemetry.Capture();
        foreach (var entry in ChangeTracker.Entries<AccountMessage>().Where(x => x.State == EntityState.Added))
        {
            entry.Entity.OriginTraceId = origin?.TraceId; entry.Entity.OriginSpanId = origin?.SpanId; entry.Entity.OriginOperationId = origin?.OperationId;
        }
    }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    { StampMessages(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    { StampMessages(); return base.SaveChanges(acceptAllChangesOnSuccess); }

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.Entity<OfficeUser>().Property(x => x.DisplayName).HasMaxLength(120);
        model.Entity<WorkOrderHistory>(e =>
        {
            e.ToTable("work_order_history"); e.HasKey(x => x.Id);
            e.Property(x => x.ActorId).HasMaxLength(450);
            e.Property(x => x.Action).HasMaxLength(80);
            e.Property(x => x.Note).HasMaxLength(2000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Visibility).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.WorkOrderId, x.OccurredAt, x.Id });
            e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<MaintenanceReceipt>(e =>
        {
            e.ToTable("maintenance_receipts"); e.HasKey(x => new { x.ActorId, x.OperationId });
            e.Property(x => x.ActorId).HasMaxLength(450);
            e.Property(x => x.PayloadHash).HasMaxLength(64);
            e.Property(x => x.ResultStatus).HasConversion<string>().HasMaxLength(32);
            e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<AccountAudit>(e =>
        {
            e.ToTable("account_audit"); e.HasKey(x => x.Id);
            e.Property(x => x.ActorId).HasMaxLength(450);
            e.Property(x => x.Action).HasMaxLength(80);
            e.Property(x => x.SubjectId).HasMaxLength(450);
            e.Property(x => x.Reason).HasMaxLength(2000);
        });
        model.Entity<AccountMessage>(e =>
        {
            e.ToTable("account_messages"); e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(450);
            e.Property(x => x.Kind).HasMaxLength(80);
            e.Property(x => x.ProtectedPayload).HasColumnType("TEXT");
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.LeaseToken).IsConcurrencyToken();
            e.Property(x => x.NextAttemptAt).HasDefaultValue(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
            e.Property(x => x.LastError).HasMaxLength(80);
            e.Property(x => x.OriginTraceId).HasMaxLength(32); e.Property(x => x.OriginSpanId).HasMaxLength(16);
            e.HasIndex(x => new { x.State, x.NextAttemptAt, x.CreatedAt, x.Id });
            e.HasOne<Building>().WithMany().HasForeignKey(x => x.BuildingId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<BuildingManager>(e =>
        {
            e.ToTable("building_managers"); e.HasKey(x => new { x.BuildingId, x.UserId });
            e.Property(x => x.UserId).HasMaxLength(450);
            e.HasOne<Building>().WithMany().HasForeignKey(x => x.BuildingId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<AccessReceipt>(e =>
        {
            e.ToTable("access_receipts"); e.HasKey(x => new { x.ActorId, x.OperationId });
            e.Property(x => x.ActorId).HasMaxLength(450); e.Property(x => x.PayloadHash).HasMaxLength(64);
            e.HasOne<Membership>().WithMany().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<Invitation>(e =>
        {
            e.ToTable("invitations"); e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(450);
            e.Property(x => x.IssuedBy).HasMaxLength(450);
            e.Property(x => x.RevokedBy).HasMaxLength(450);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasOne<Suite>().WithMany().HasForeignKey(x => x.SuiteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<Building>(e =>
        {
            e.ToTable("buildings"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(160).IsRequired();
            e.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
        });
        model.Entity<Suite>(e =>
        {
            e.ToTable("suites"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(80).IsRequired();
            e.HasIndex(x => new { x.BuildingId, x.Name }).IsUnique();
            e.HasOne<Building>().WithMany().HasForeignKey(x => x.BuildingId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<Membership>(e =>
        {
            e.ToTable("memberships"); e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.Id, x.SuiteId, x.UserId });
            e.Property(x => x.UserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.RevokedBy).HasMaxLength(450);
            e.Property(x => x.RevocationReason).HasMaxLength(2000);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.UserId, x.SuiteId });
            e.HasOne<Suite>().WithMany().HasForeignKey(x => x.SuiteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ActiveGrant>(e =>
        {
            e.ToTable("active_grants"); e.HasKey(x => new { x.UserId, x.SuiteId });
            e.Property(x => x.UserId).HasMaxLength(450);
            e.HasIndex(x => x.MembershipId).IsUnique();
            e.HasOne<Membership>().WithMany()
                .HasForeignKey(x => new { x.MembershipId, x.SuiteId, x.UserId })
                .HasPrincipalKey(x => new { x.Id, x.SuiteId, x.UserId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Suite>().WithMany().HasForeignKey(x => x.SuiteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<RentAccount>(e =>
        {
            e.ToTable("rent_accounts"); e.HasKey(x => x.Id); e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.MembershipId).IsUnique();
            e.HasOne<Membership>().WithMany().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<RentAgreement>(e =>
        {
            e.ToTable("rent_agreements"); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.Id, x.AccountId });
            e.Property(x => x.Version).IsConcurrencyToken(); e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.CreatedBy).HasMaxLength(450); e.HasIndex(x => new { x.AccountId, x.StartsOn, x.EndsOn });
            e.HasOne<RentAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<RentAgreement>().WithMany().HasForeignKey(x => new { x.SupersededBy, x.AccountId })
                .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<BillingPeriod>(e =>
        {
            e.ToTable("billing_periods"); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.Id, x.AccountId });
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.AccountId, x.StartsOn, x.EndsOn, x.Kind }).IsUnique();
            e.HasOne<RentAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<Obligation>(e =>
        {
            e.ToTable("obligations"); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.Id, x.AccountId }); e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.Description).HasMaxLength(120); e.Property(x => x.IssuedBy).HasMaxLength(450);

            e.HasIndex(x => x.PeriodId).IsUnique(); e.HasIndex(x => new { x.AccountId, x.DueOn, x.Id });
            e.HasOne<RentAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BillingPeriod>().WithMany().HasForeignKey(x => new { x.PeriodId, x.AccountId })
                .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<RentAgreement>().WithMany().HasForeignKey(x => new { x.AgreementId, x.AccountId })
                .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.IssuedBy).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<BillingCustomerBinding>(e =>
        {
            e.ToTable("billing_customers"); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.Id, x.AccountId, x.ProviderAccount, x.LiveMode, x.CustomerId });
            e.Property(x => x.ProviderAccount).HasMaxLength(255); e.Property(x => x.CustomerId).HasMaxLength(255);
            e.HasIndex(x => new { x.AccountId, x.ProviderAccount, x.LiveMode }).IsUnique();
            e.HasIndex(x => new { x.ProviderAccount, x.LiveMode, x.CustomerId }).IsUnique();
            e.HasOne<RentAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<PaymentAttempt>(e =>
        {
            e.ToTable("payment_attempts"); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.Id, x.AccountId });
            e.Property(x => x.Version).IsConcurrencyToken(); e.Property(x => x.ActorId).HasMaxLength(450);
            e.Property(x => x.ProviderAccount).HasMaxLength(255); e.Property(x => x.CustomerId).HasMaxLength(255);
            e.Property(x => x.SessionId).HasMaxLength(255); e.Property(x => x.PaymentId).HasMaxLength(255);
            e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.PayloadHash).HasMaxLength(64);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Ignore(x => x.ProviderIdempotencyKey);
            e.HasIndex(x => new { x.ActorId, x.OperationId }).IsUnique();
            e.HasIndex(x => new { x.ProviderAccount, x.LiveMode, x.SessionId }).IsUnique();
            e.HasIndex(x => new { x.ProviderAccount, x.LiveMode, x.PaymentId }).IsUnique();
            e.HasIndex(x => new { x.State, x.CreatedAt, x.Id });
            e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BillingCustomerBinding>().WithMany().HasForeignKey(x => new { x.CustomerBindingId, x.AccountId, x.ProviderAccount, x.LiveMode, x.CustomerId })
                .HasPrincipalKey(x => new { x.Id, x.AccountId, x.ProviderAccount, x.LiveMode, x.CustomerId }).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => new { x.AttemptId, x.AccountId })
                .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
            e.Navigation(x => x.Items).HasField("_items").UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        model.Entity<PaymentAttemptItem>(e =>
        {
            e.ToTable("payment_attempt_items"); e.HasKey(x => new { x.AttemptId, x.ObligationId });
            e.HasAlternateKey(x => new { x.AttemptId, x.ObligationId, x.AccountId });
            e.HasOne<Obligation>().WithMany().HasForeignKey(x => new { x.ObligationId, x.AccountId })
                .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<CollectionGuard>(e =>
        {
            e.ToTable("collection_guards"); e.HasKey(x => x.ObligationId);
            e.HasOne<PaymentAttemptItem>().WithMany().HasForeignKey(x => new { x.AttemptId, x.ObligationId, x.AccountId })
                .HasPrincipalKey(x => new { x.AttemptId, x.ObligationId, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<BillingReceipt>(e =>
        {
            e.ToTable("billing_receipts"); e.HasKey(x => new { x.ActorId, x.OperationId });
            e.Property(x => x.ActorId).HasMaxLength(450); e.Property(x => x.PayloadHash).HasMaxLength(64);
            e.HasOne<RentAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<HelpArticle>(e =>
        {
            e.ToTable("help_articles"); e.HasKey(x => x.Id);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.BuildingId, x.State, x.Id });
            e.HasOne<Building>().WithMany().HasForeignKey(x => x.BuildingId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<HelpRevision>(e =>
        {
            e.ToTable("help_revisions"); e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(160); e.Property(x => x.Body).HasColumnType("TEXT");
            e.Property(x => x.Audience).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.EditorId).HasMaxLength(450);
            e.HasIndex(x => new { x.ArticleId, x.EditedAt, x.Id });
            e.HasOne<HelpArticle>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.EditorId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<Contractor>(e =>
        {
            e.ToTable("contractors"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(160).IsRequired();
            e.Property(x => x.Trade).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Email).HasMaxLength(254); e.Property(x => x.Phone).HasMaxLength(80);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.BuildingId, x.Active, x.Name, x.Id });
            e.HasOne<Building>().WithMany().HasForeignKey(x => x.BuildingId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<WorkOrder>(e =>
        {
            e.ToTable("work_orders"); e.HasKey(x => x.Id);
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
        });

        var mysql = Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) == true;
        var utc = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        var date = new ValueConverter<DateOnly, DateTime>(value => value.ToDateTime(TimeOnly.MinValue), value => DateOnly.FromDateTime(value));
        var guid = new ValueConverter<Guid, string>(value => value.ToString("D"), value => Guid.Parse(value));
        foreach (var entity in model.Model.GetEntityTypes())
        {
            entity.SetTableName(SnakeCase(entity.GetTableName()!));
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(SnakeCase(property.Name));
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(utc);
                    property.SetColumnType(mysql ? "datetime(6)" : "TEXT");
                }
                if (mysql && (property.ClrType == typeof(DateOnly) || property.ClrType == typeof(DateOnly?)))
                {
                    property.SetValueConverter(date); property.SetColumnType("date");
                }
                if (property.ClrType == typeof(Guid) || property.ClrType == typeof(Guid?))
                {
                    property.SetValueConverter(guid);
                    property.SetMaxLength(36);
                    if (mysql) property.SetColumnType("varchar(36)");
                }
                if (mysql && (property.ClrType == typeof(string) || property.GetValueConverter()?.ProviderClrType == typeof(string)))
                    property.SetCollation("utf8mb4_bin");
                if (mysql && property.ClrType == typeof(string) && property.Name is "ProviderAccount" or "CustomerId" or "SessionId" or "PaymentId")
                    MySql.EntityFrameworkCore.Extensions.MySQLPropertyBuilderExtensions.ForMySQLHasCollation(model.Entity(entity.ClrType).Property(property.Name), "utf8mb4_bin");
            }
        }
    }
    private static string SnakeCase(string value) => string.Concat(value.Select((character, index) =>
        index > 0 && char.IsUpper(character) ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
}

public sealed class ActiveGrant
{
    public string UserId { get; set; } = "";
    public Guid SuiteId { get; set; }
    public Guid MembershipId { get; set; }
}

public sealed class SqliteOfficeDbContext(DbContextOptions<SqliteOfficeDbContext> options) : OfficeDbContext(options);
public sealed class MySqlOfficeDbContext(DbContextOptions<MySqlOfficeDbContext> options) : OfficeDbContext(options);
