using AllenBuilding.Application.Diagnostics;
using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using AllenBuilding.Infrastructure.Persistence.Configuration;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AllenBuilding.Infrastructure.Persistence;

public abstract class OfficeDbContext(DbContextOptions options) : IdentityDbContext<OfficeUser>(options)
{
    public DbSet<AssistantMaintenanceReview> AssistantMaintenanceReviews => Set<AssistantMaintenanceReview>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<CashMovementReview> CashMovementReviews => Set<CashMovementReview>();
    public DbSet<CashSourceWork> CashSourceWork => Set<CashSourceWork>();
    public DbSet<ObligationCredit> ObligationCredits => Set<ObligationCredit>();
    public DbSet<SettlementPayment> SettlementPayments => Set<SettlementPayment>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<RefundAllocation> RefundAllocations => Set<RefundAllocation>();
    public DbSet<CustomerProvision> CustomerProvisions => Set<CustomerProvision>();
    public DbSet<BillingCustomerBinding> BillingCustomers => Set<BillingCustomerBinding>();
    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();
    public DbSet<CollectionWork> CollectionWork => Set<CollectionWork>();
    public DbSet<PaymentEventInbox> PaymentEvents => Set<PaymentEventInbox>();
    public DbSet<FinancialException> FinancialExceptions => Set<FinancialException>();
    public DbSet<FinancialExceptionHistory> FinancialExceptionHistory => Set<FinancialExceptionHistory>();
    public DbSet<PaymentAttemptItem> PaymentAttemptItems => Set<PaymentAttemptItem>();
    public DbSet<CollectionGuard> CollectionGuards => Set<CollectionGuard>();
    public DbSet<AutopayAuthorization> AutopayAuthorizations => Set<AutopayAuthorization>();
    public DbSet<AutopaySlot> AutopaySlots => Set<AutopaySlot>();
    public DbSet<AutopayWork> AutopayWork => Set<AutopayWork>();
    public DbSet<ProviderRentInvoice> ProviderRentInvoices => Set<ProviderRentInvoice>();
    public DbSet<TenancyClosure> TenancyClosures => Set<TenancyClosure>();
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
            entry.Entity.OriginTraceId = origin?.TraceId;
            entry.Entity.OriginSpanId = origin?.SpanId;
            entry.Entity.OriginOperationId = origin?.OperationId;
        }
    }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    { StampMessages(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    { StampMessages(); return base.SaveChanges(acceptAllChangesOnSuccess); }

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        // Entity rules live in Persistence/Configuration by feature; provider conventions follow.
        model.ApplyConfiguration(new AssistantMaintenanceReviewConfiguration());
        model.ApplyConfiguration(new OfficeUserConfiguration());
        model.ApplyConfiguration(new WorkOrderHistoryConfiguration());
        model.ApplyConfiguration(new MaintenanceReceiptConfiguration());
        model.ApplyConfiguration(new AccountAuditConfiguration());
        model.ApplyConfiguration(new AccountMessageConfiguration());
        model.ApplyConfiguration(new BuildingManagerConfiguration());
        model.ApplyConfiguration(new AccessReceiptConfiguration());
        model.ApplyConfiguration(new InvitationConfiguration());
        model.ApplyConfiguration(new BuildingConfiguration());
        model.ApplyConfiguration(new SuiteConfiguration());
        model.ApplyConfiguration(new MembershipConfiguration());
        model.ApplyConfiguration(new ActiveGrantConfiguration());
        model.ApplyConfiguration(new RentAccountConfiguration());
        model.ApplyConfiguration(new ProviderRentInvoiceConfiguration());
        model.ApplyConfiguration(new AutopayAuthorizationConfiguration());
        model.ApplyConfiguration(new AutopaySlotConfiguration());
        model.ApplyConfiguration(new AutopayWorkConfiguration());
        model.ApplyConfiguration(new TenancyClosureConfiguration());
        model.ApplyConfiguration(new RentAgreementConfiguration());
        model.ApplyConfiguration(new BillingPeriodConfiguration());
        model.ApplyConfiguration(new ObligationConfiguration());
        model.ApplyConfiguration(new CustomerProvisionConfiguration());
        model.ApplyConfiguration(new BillingCustomerBindingConfiguration());
        model.ApplyConfiguration(new FinancialExceptionConfiguration());
        model.ApplyConfiguration(new FinancialExceptionHistoryConfiguration());
        model.ApplyConfiguration(new CollectionWorkConfiguration());
        model.ApplyConfiguration(new PaymentEventInboxConfiguration());
        model.ApplyConfiguration(new PaymentAttemptConfiguration());
        model.ApplyConfiguration(new PaymentAttemptItemConfiguration());
        model.ApplyConfiguration(new CashSourceWorkConfiguration());
        model.ApplyConfiguration(new CashMovementConfiguration());
        model.ApplyConfiguration(new CashMovementReviewConfiguration());
        model.ApplyConfiguration(new ObligationCreditConfiguration());
        model.ApplyConfiguration(new SettlementPaymentConfiguration());
        model.ApplyConfiguration(new PaymentAllocationConfiguration());
        model.ApplyConfiguration(new RefundAllocationConfiguration());
        model.ApplyConfiguration(new CollectionGuardConfiguration());
        model.ApplyConfiguration(new BillingReceiptConfiguration());
        model.ApplyConfiguration(new HelpArticleConfiguration());
        model.ApplyConfiguration(new HelpRevisionConfiguration());
        model.ApplyConfiguration(new ContractorConfiguration());
        model.ApplyConfiguration(new WorkOrderConfiguration());

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
                    property.SetValueConverter(date);
                    property.SetColumnType("date");
                }
                if (property.ClrType == typeof(Guid) || property.ClrType == typeof(Guid?))
                {
                    property.SetValueConverter(guid);
                    property.SetMaxLength(36);
                    if (mysql)
                        property.SetColumnType("varchar(36)");
                }
                if (mysql && (property.ClrType == typeof(string) || property.GetValueConverter()?.ProviderClrType == typeof(string)))
                    property.SetCollation("utf8mb4_bin");
                if (mysql && property.ClrType == typeof(string) &&
                    property.Name is "ProviderAccount" or "CustomerId" or "SessionId" or "PaymentId" or "CollectionReference"
                        or "EventId" or "ResourceId" or "SourceId" or "TransactionId")
                    MySql.EntityFrameworkCore.Extensions.MySQLPropertyBuilderExtensions.ForMySQLHasCollation(
                        model.Entity(entity.ClrType).Property(property.Name), "utf8mb4_bin");
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
