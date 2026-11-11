using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AllenBuilding.Infrastructure.Persistence.Configuration;

internal sealed class RentAccountConfiguration : IEntityTypeConfiguration<RentAccount>
{
    public void Configure(EntityTypeBuilder<RentAccount> e)
    {
        e.ToTable("rent_accounts");
        e.HasKey(x => x.Id);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.HasIndex(x => x.MembershipId).IsUnique();
        e.HasOne<Membership>().WithMany().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProviderRentInvoiceConfiguration : IEntityTypeConfiguration<ProviderRentInvoice>
{
    public void Configure(EntityTypeBuilder<ProviderRentInvoice> e)
    {
        e.ToTable("provider_rent_invoices");
        e.HasKey(x => x.Id);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.Property(x => x.ProviderAccount).HasMaxLength(255);
        e.Property(x => x.CustomerId).HasMaxLength(255);
        e.Property(x => x.SubscriptionId).HasMaxLength(255);
        e.Property(x => x.InvoiceId).HasMaxLength(255);
        e.Property(x => x.LineId).HasMaxLength(255);
        e.Property(x => x.ConsentHash).HasMaxLength(64);
        e.Property(x => x.Currency).HasMaxLength(3);
        e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        e.HasIndex(x => new { x.ProviderAccount, x.LiveMode, x.InvoiceId }).IsUnique();
        e.HasIndex(x => x.PeriodId).IsUnique();
        e.HasIndex(x => x.ObligationId).IsUnique();
        e.HasIndex(x => new { x.AuthorizationId, x.CheckedAt });
        e.HasOne<AutopayAuthorization>().WithMany().HasForeignKey(x => new { x.AuthorizationId, x.AccountId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<BillingPeriod>().WithMany().HasForeignKey(x => new { x.PeriodId, x.AccountId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Obligation>().WithMany().HasForeignKey(x => new { x.ObligationId, x.AccountId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AutopayAuthorizationConfiguration : IEntityTypeConfiguration<AutopayAuthorization>
{
    public void Configure(EntityTypeBuilder<AutopayAuthorization> e)
    {
        e.ToTable("autopay_authorizations");
        e.HasKey(x => x.Id);
        e.HasAlternateKey(x => new { x.Id, x.AccountId });
        e.Property(x => x.Version).IsConcurrencyToken();
        e.Ignore(x => x.AdmitsNewAutomaticWork);
        e.Ignore(x => x.SetupOperationKey);
        e.Ignore(x => x.SubscriptionOperationKey);
        e.Ignore(x => x.StopOperationKey);
        e.Property(x => x.ActorId).HasMaxLength(450);
        e.Property(x => x.StopActorId).HasMaxLength(450);
        e.Property(x => x.Currency).HasMaxLength(3);
        e.Property(x => x.ProviderAccount).HasMaxLength(255);
        e.Property(x => x.CustomerId).HasMaxLength(255);
        e.Property(x => x.PaymentMethodId).HasMaxLength(255);
        e.Property(x => x.SubscriptionId).HasMaxLength(255);
        e.Property(x => x.SetupSessionId).HasMaxLength(255);
        e.Property(x => x.SetupState).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.ConsentHash).HasMaxLength(64);
        e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.StopTiming).HasConversion<string>().HasMaxLength(32);
        e.HasIndex(x => new { x.ActorId, x.OperationId }).IsUnique();
        e.HasIndex(x => new { x.AccountId, x.State, x.FirstPeriodStartsOn });
        e.HasOne<RentAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<RentAgreement>().WithMany().HasForeignKey(x => new { x.AgreementId, x.AccountId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<BillingCustomerBinding>().WithMany()
            .HasForeignKey(x => new { x.CustomerBindingId, x.AccountId, x.ProviderAccount, x.LiveMode, x.CustomerId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId, x.ProviderAccount, x.LiveMode, x.CustomerId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.StopActorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AutopaySlotConfiguration : IEntityTypeConfiguration<AutopaySlot>
{
    public void Configure(EntityTypeBuilder<AutopaySlot> e)
    {
        e.ToTable("autopay_slots");
        e.HasKey(x => x.AccountId);
        e.HasIndex(x => x.AuthorizationId).IsUnique();
        e.HasOne<RentAccount>().WithOne().HasForeignKey<AutopaySlot>(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<AutopayAuthorization>().WithOne()
            .HasForeignKey<AutopaySlot>(x => new { x.AuthorizationId, x.AccountId })
            .HasPrincipalKey<AutopayAuthorization>(x => new { x.Id, x.AccountId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AutopayWorkConfiguration : IEntityTypeConfiguration<AutopayWork>
{
    public void Configure(EntityTypeBuilder<AutopayWork> e)
    {
        e.ToTable("autopay_work");
        e.HasKey(x => x.Id);
        e.Property(x => x.ProviderOperationKey).HasMaxLength(255);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.Ignore(x => x.Profile);
        e.Property(x => x.ApiVersion).HasMaxLength(64);
        e.Property(x => x.PublicOrigin).HasMaxLength(512);
        e.Property(x => x.PaymentMethodConfiguration).HasMaxLength(255);
        e.Property(x => x.LastError).HasMaxLength(64);
        e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.Stage).HasConversion<string>().HasMaxLength(32);
        e.HasIndex(x => new { x.AuthorizationId, x.Stage, x.OriginalOperationId }).IsUnique();
        e.HasIndex(x => new { x.Stage, x.State, x.NextAttemptAt, x.CreatedAt, x.Id });
        e.HasOne<AutopayAuthorization>().WithMany().HasForeignKey(x => new { x.AuthorizationId, x.AccountId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TenancyClosureConfiguration : IEntityTypeConfiguration<TenancyClosure>
{
    public void Configure(EntityTypeBuilder<TenancyClosure> e)
    {
        e.ToTable("tenancy_closures");
        e.HasKey(x => x.AccountId);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.Property(x => x.ActorId).HasMaxLength(450);
        e.Property(x => x.Reason).HasMaxLength(2000);
        e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        e.HasIndex(x => new { x.ActorId, x.OperationId }).IsUnique();
        e.HasIndex(x => new { x.State, x.RequestedAt, x.AccountId });
        e.HasOne<RentAccount>().WithOne().HasForeignKey<TenancyClosure>(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Membership>().WithMany().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RentAgreementConfiguration : IEntityTypeConfiguration<RentAgreement>
{
    public void Configure(EntityTypeBuilder<RentAgreement> e)
    {
        e.ToTable("rent_agreements");
        e.HasKey(x => x.Id);
        e.HasAlternateKey(x => new { x.Id, x.AccountId });
        e.Property(x => x.Version).IsConcurrencyToken();
        e.Property(x => x.Currency).HasMaxLength(3);
        e.Property(x => x.CreatedBy).HasMaxLength(450);
        e.HasIndex(x => new { x.AccountId, x.StartsOn, x.EndsOn });
        e.HasOne<RentAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<RentAgreement>().WithMany().HasForeignKey(x => new { x.SupersededBy, x.AccountId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BillingPeriodConfiguration : IEntityTypeConfiguration<BillingPeriod>
{
    public void Configure(EntityTypeBuilder<BillingPeriod> e)
    {
        e.ToTable("billing_periods");
        e.HasKey(x => x.Id);
        e.HasAlternateKey(x => new { x.Id, x.AccountId });
        e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
        e.HasIndex(x => new { x.AccountId, x.StartsOn, x.EndsOn, x.Kind }).IsUnique();
        e.HasOne<RentAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ObligationConfiguration : IEntityTypeConfiguration<Obligation>
{
    public void Configure(EntityTypeBuilder<Obligation> e)
    {
        e.ToTable("obligations");
        e.HasKey(x => x.Id);
        e.HasAlternateKey(x => new { x.Id, x.AccountId });
        e.Property(x => x.Version).IsConcurrencyToken();
        e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.Currency).HasMaxLength(3);
        e.Property(x => x.Description).HasMaxLength(120);
        e.Property(x => x.IssuedBy).HasMaxLength(450);

        e.HasIndex(x => x.PeriodId).IsUnique();
        e.HasIndex(x => new { x.AccountId, x.DueOn, x.Id });
        e.HasOne<RentAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<BillingPeriod>().WithMany().HasForeignKey(x => new { x.PeriodId, x.AccountId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<RentAgreement>().WithMany().HasForeignKey(x => new { x.AgreementId, x.AccountId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.IssuedBy).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CustomerProvisionConfiguration : IEntityTypeConfiguration<CustomerProvision>
{
    public void Configure(EntityTypeBuilder<CustomerProvision> e)
    {
        e.ToTable("customer_provisions");
        e.HasKey(x => x.Id);
        e.Ignore(x => x.ProviderIdempotencyKey);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.Property(x => x.LeaseToken).IsConcurrencyToken();
        e.Property(x => x.ActorId).HasMaxLength(450);
        e.Property(x => x.ProviderAccount).HasMaxLength(255);
        e.Property(x => x.CustomerId).HasMaxLength(255);
        e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.ApiVersion).HasMaxLength(64);
        e.Property(x => x.LastError).HasMaxLength(64);
        e.Property(x => x.OriginTraceId).HasMaxLength(32);
        e.Property(x => x.OriginSpanId).HasMaxLength(16);
        e.HasIndex(x => new { x.AccountId, x.ProviderAccount, x.LiveMode }).IsUnique();
        e.HasIndex(x => new { x.State, x.NextAttemptAt, x.CreatedAt, x.Id });
        e.HasOne<RentAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BillingCustomerBindingConfiguration : IEntityTypeConfiguration<BillingCustomerBinding>
{
    public void Configure(EntityTypeBuilder<BillingCustomerBinding> e)
    {
        e.ToTable("billing_customers");
        e.HasKey(x => x.Id);
        e.HasAlternateKey(x => new { x.Id, x.AccountId, x.ProviderAccount, x.LiveMode, x.CustomerId });
        e.Property(x => x.ProviderAccount).HasMaxLength(255);
        e.Property(x => x.CustomerId).HasMaxLength(255);
        e.HasIndex(x => new { x.AccountId, x.ProviderAccount, x.LiveMode }).IsUnique();
        e.HasIndex(x => new { x.ProviderAccount, x.LiveMode, x.CustomerId }).IsUnique();
        e.HasOne<RentAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FinancialExceptionConfiguration : IEntityTypeConfiguration<FinancialException>
{
    public void Configure(EntityTypeBuilder<FinancialException> e)
    {
        e.ToTable("financial_exceptions");
        e.HasKey(x => x.Id);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.Failure).HasConversion<string>().HasMaxLength(64);
        e.Property(x => x.OwnerUserId).HasMaxLength(450);
        e.Property(x => x.LastVerifiedState).HasMaxLength(32);
        e.HasIndex(x => x.AttemptId).IsUnique();
        e.HasIndex(x => new { x.AccountId, x.State, x.LastSeenAt });
        e.HasOne<PaymentAttempt>().WithMany().HasForeignKey(x => new { x.AttemptId, x.AccountId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FinancialExceptionHistoryConfiguration : IEntityTypeConfiguration<FinancialExceptionHistory>
{
    public void Configure(EntityTypeBuilder<FinancialExceptionHistory> e)
    {
        e.ToTable("financial_exception_history");
        e.HasKey(x => x.Id);
        e.Property(x => x.Action).HasMaxLength(64);
        e.Property(x => x.ActorUserId).HasMaxLength(450);
        e.Property(x => x.Reason).HasMaxLength(2000);
        e.HasIndex(x => new { x.ExceptionId, x.OccurredAt, x.Id });
        e.HasOne<FinancialException>().WithMany().HasForeignKey(x => x.ExceptionId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CollectionWorkConfiguration : IEntityTypeConfiguration<CollectionWork>
{
    public void Configure(EntityTypeBuilder<CollectionWork> e)
    {
        e.ToTable("collection_work");
        e.HasKey(x => x.AttemptId);
        e.Ignore(x => x.Profile);
        e.Property(x => x.CycleStartedAt).HasDefaultValue(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        e.Property(x => x.LeaseToken).IsConcurrencyToken();
        e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.ApiVersion).HasMaxLength(64);
        e.Property(x => x.PublicOrigin).HasMaxLength(512);
        e.Property(x => x.PaymentMethodConfiguration).HasMaxLength(255);
        e.Property(x => x.LastError).HasMaxLength(64);
        e.Property(x => x.OriginTraceId).HasMaxLength(32);
        e.Property(x => x.OriginSpanId).HasMaxLength(16);
        e.HasIndex(x => new { x.State, x.NextAttemptAt, x.CreatedAt, x.AttemptId });
        e.HasOne<PaymentAttempt>().WithOne().HasForeignKey<CollectionWork>(x => x.AttemptId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PaymentEventInboxConfiguration : IEntityTypeConfiguration<PaymentEventInbox>
{
    public void Configure(EntityTypeBuilder<PaymentEventInbox> e)
    {
        e.ToTable("payment_events");
        e.HasKey(x => x.Id);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.Property(x => x.LeaseToken).HasDefaultValue(Guid.Empty);
        e.Property(x => x.NextAttemptAt).HasDefaultValue(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        e.Property(x => x.LeaseToken).IsConcurrencyToken();
        e.Property(x => x.ProviderAccount).HasMaxLength(255);
        e.Property(x => x.EventId).HasMaxLength(255);
        e.Property(x => x.ResourceId).HasMaxLength(255);
        e.Property(x => x.EventType).HasMaxLength(100);
        e.Property(x => x.ApiVersion).HasMaxLength(64);
        e.Property(x => x.PayloadHash).HasMaxLength(64);
        e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.ReviewReason).HasMaxLength(64);
        e.HasIndex(x => new { x.ProviderAccount, x.LiveMode, x.EventId }).IsUnique();
        e.HasIndex(x => new { x.State, x.NextAttemptAt, x.ReceivedAt, x.Id });
    }
}

internal sealed class PaymentAttemptConfiguration : IEntityTypeConfiguration<PaymentAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentAttempt> e)
    {
        e.ToTable("payment_attempts");
        e.HasKey(x => x.Id);
        e.HasAlternateKey(x => new { x.Id, x.AccountId });
        e.HasAlternateKey(x => new { x.Id, x.AccountId, x.ProviderAccount, x.LiveMode });
        e.Property(x => x.Version).IsConcurrencyToken();
        e.Property(x => x.ActorId).HasMaxLength(450);
        e.Property(x => x.ProviderAccount).HasMaxLength(255);
        e.Property(x => x.CustomerId).HasMaxLength(255);
        e.Property(x => x.SessionId).HasMaxLength(255);
        e.Property(x => x.PaymentId).HasMaxLength(255);
        e.Property(x => x.Currency).HasMaxLength(3);
        e.Property(x => x.PayloadHash).HasMaxLength(64);
        e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        e.Ignore(x => x.ProviderIdempotencyKey);
        e.HasIndex(x => new { x.ActorId, x.OperationId }).IsUnique();
        e.HasIndex(x => new { x.ProviderAccount, x.LiveMode, x.SessionId }).IsUnique();
        e.HasIndex(x => new { x.ProviderAccount, x.LiveMode, x.PaymentId }).IsUnique();
        e.HasIndex(x => new { x.State, x.CreatedAt, x.Id });
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<BillingCustomerBinding>().WithMany()
            .HasForeignKey(x => new { x.CustomerBindingId, x.AccountId, x.ProviderAccount, x.LiveMode, x.CustomerId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId, x.ProviderAccount, x.LiveMode, x.CustomerId }).OnDelete(DeleteBehavior.Restrict);
        e.HasMany(x => x.Items).WithOne().HasForeignKey(x => new { x.AttemptId, x.AccountId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
        e.Navigation(x => x.Items).HasField("_items").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class PaymentAttemptItemConfiguration : IEntityTypeConfiguration<PaymentAttemptItem>
{
    public void Configure(EntityTypeBuilder<PaymentAttemptItem> e)
    {
        e.ToTable("payment_attempt_items");
        e.HasKey(x => new { x.AttemptId, x.ObligationId });
        e.HasAlternateKey(x => new { x.AttemptId, x.ObligationId, x.AccountId });
        e.HasOne<Obligation>().WithMany().HasForeignKey(x => new { x.ObligationId, x.AccountId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CashSourceWorkConfiguration : IEntityTypeConfiguration<CashSourceWork>
{
    public void Configure(EntityTypeBuilder<CashSourceWork> e)
    {
        e.ToTable("cash_source_work");
        e.HasKey(x => x.Id);
        e.Property(x => x.SourceId).HasMaxLength(255);
        e.Property(x => x.ProviderAccount).HasMaxLength(255);
        e.Property(x => x.ActorId).HasMaxLength(450);
        e.Property(x => x.ApiVersion).HasMaxLength(64);
        e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.LeaseToken).IsConcurrencyToken();
        e.Property(x => x.LastError).HasMaxLength(64);
        e.Property(x => x.OriginTraceId).HasMaxLength(32);
        e.Property(x => x.OriginSpanId).HasMaxLength(16);
        e.HasIndex(x => new { x.AttemptId, x.SourceId }).IsUnique();
        e.HasIndex(x => new { x.ActorId, x.OperationId }).IsUnique();
        e.HasIndex(x => new { x.State, x.NextAttemptAt, x.CreatedAt, x.Id });
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<PaymentAttempt>().WithMany().HasForeignKey(x => new { x.AttemptId, x.AccountId, x.ProviderAccount, x.LiveMode })
            .HasPrincipalKey(x => new { x.Id, x.AccountId, x.ProviderAccount, x.LiveMode }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CashMovementConfiguration : IEntityTypeConfiguration<CashMovement>
{
    public void Configure(EntityTypeBuilder<CashMovement> e)
    {
        e.ToTable("cash_movements", table => table.HasCheckConstraint("CK_cash_movement_amount",
            "signed_minor_units != 0 AND signed_minor_units >= -99999999 AND signed_minor_units <= 99999999 AND currency = 'USD' AND ((kind = 'DisputeReinstatement' AND signed_minor_units > 0) OR (kind IN ('Refund', 'DisputeWithdrawal') AND signed_minor_units < 0))"));
        e.HasKey(x => x.Id);
        e.HasAlternateKey(x => new { x.Id, x.AccountId });
        e.HasAlternateKey(x => new { x.Id, x.CaptureId, x.AccountId });
        e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.Currency).HasMaxLength(3);
        foreach (var name in new[] { "ProviderAccount", "CustomerId", "PaymentId", "SourceId", "TransactionId" })
            e.Property<string>(name).HasMaxLength(255);
        e.HasIndex(x => new { x.ProviderAccount, x.LiveMode, x.TransactionId }).IsUnique();
        e.HasIndex(x => new { x.AccountId, x.RecordedAt, x.Id });
        e.HasOne<SettlementPayment>().WithMany().HasForeignKey(x => new { x.CaptureId, x.AccountId, x.AttemptId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId, x.AttemptId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<PaymentAttempt>().WithMany()
            .HasForeignKey(x => new { x.AttemptId, x.AccountId, x.ProviderAccount, x.LiveMode })
            .HasPrincipalKey(x => new { x.Id, x.AccountId, x.ProviderAccount, x.LiveMode })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CashMovementReviewConfiguration : IEntityTypeConfiguration<CashMovementReview>
{
    public void Configure(EntityTypeBuilder<CashMovementReview> e)
    {
        e.ToTable("cash_movement_reviews");
        e.HasKey(x => x.MovementId);
        e.Property(x => x.AgreementHash).HasMaxLength(64);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.Disposition).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.ActorId).HasMaxLength(450);
        e.Property(x => x.Reason).HasMaxLength(2000);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => new { x.AccountId, x.State, x.CreatedAt });
        e.HasOne<CashMovement>().WithOne().HasForeignKey<CashMovementReview>(x => new { x.MovementId, x.AccountId })
            .HasPrincipalKey<CashMovement>(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ObligationCreditConfiguration : IEntityTypeConfiguration<ObligationCredit>
{
    public void Configure(EntityTypeBuilder<ObligationCredit> e)
    {
        e.ToTable("obligation_credits",
            table => table.HasCheckConstraint("CK_credit_amount", "amount_minor_units > 0 AND amount_minor_units <= 99999999 AND currency = 'USD'"));
        e.HasKey(x => x.Id);
        e.Property(x => x.ActorId).HasMaxLength(450);
        e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.Currency).HasMaxLength(3);
        e.Property(x => x.Reason).HasMaxLength(2000);
        e.HasIndex(x => new { x.ActorId, x.OperationId, x.ObligationId }).IsUnique();
        e.HasIndex(x => new { x.ObligationId, x.RecordedAt, x.Id });
        e.HasOne<CashMovement>().WithMany().HasForeignKey(x => new { x.SourceMovementId, x.AccountId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<RefundAllocation>().WithMany().HasForeignKey(x => new { x.SourceMovementId, x.ObligationId })
            .HasPrincipalKey(x => new { x.MovementId, x.ObligationId }).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => new { x.SourceMovementId, x.ObligationId }).IsUnique();
        e.HasOne<Obligation>().WithMany().HasForeignKey(x => new { x.ObligationId, x.AccountId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SettlementPaymentConfiguration : IEntityTypeConfiguration<SettlementPayment>
{
    public void Configure(EntityTypeBuilder<SettlementPayment> e)
    {
        e.ToTable("settlement_payments", table => table.HasCheckConstraint("CK_settlement_amount", "amount_minor_units > 0"));
        e.HasKey(x => x.Id);
        e.HasAlternateKey(x => new { x.Id, x.AccountId, x.AttemptId });
        e.Property(x => x.ProviderAccount).HasMaxLength(255);
        e.Property(x => x.CollectionReference).HasMaxLength(255);
        e.Property(x => x.Currency).HasMaxLength(3);
        e.HasIndex(x => x.AttemptId).IsUnique();
        e.HasIndex(x => new { x.ProviderAccount, x.LiveMode, x.CollectionReference }).IsUnique();
        e.HasIndex(x => new { x.AccountId, x.RecordedAt, x.Id });
        e.HasOne<PaymentAttempt>().WithMany().HasForeignKey(x => new { x.AttemptId, x.AccountId, x.ProviderAccount, x.LiveMode })
            .HasPrincipalKey(x => new { x.Id, x.AccountId, x.ProviderAccount, x.LiveMode }).OnDelete(DeleteBehavior.Restrict);
        e.HasMany(x => x.Allocations).WithOne().HasForeignKey(x => new { x.PaymentId, x.AccountId, x.AttemptId })
            .HasPrincipalKey(x => new { x.Id, x.AccountId, x.AttemptId }).OnDelete(DeleteBehavior.Restrict);
        e.Navigation(x => x.Allocations).HasField("_allocations").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class PaymentAllocationConfiguration : IEntityTypeConfiguration<PaymentAllocation>
{
    public void Configure(EntityTypeBuilder<PaymentAllocation> e)
    {
        e.ToTable("payment_allocations", table => table.HasCheckConstraint("CK_allocation_amount", "amount_minor_units > 0"));
        e.HasKey(x => new { x.PaymentId, x.ObligationId });
        e.HasAlternateKey(x => new { x.PaymentId, x.ObligationId, x.AccountId });
        e.HasIndex(x => new { x.AttemptId, x.ObligationId }).IsUnique();
        e.HasIndex(x => new { x.AccountId, x.ObligationId });
        e.HasOne<PaymentAttemptItem>().WithMany().HasForeignKey(x => new { x.AttemptId, x.ObligationId, x.AccountId })
            .HasPrincipalKey(x => new { x.AttemptId, x.ObligationId, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RefundAllocationConfiguration : IEntityTypeConfiguration<RefundAllocation>
{
    public void Configure(EntityTypeBuilder<RefundAllocation> e)
    {
        e.ToTable("refund_allocations",
            table => table.HasCheckConstraint("CK_refund_allocation_amount", "signed_minor_units < 0 AND signed_minor_units >= -99999999"));
        e.HasKey(x => new { x.MovementId, x.ObligationId });
        e.HasIndex(x => new { x.AccountId, x.ObligationId });
        e.HasIndex(x => new { x.CaptureId, x.ObligationId });
        e.HasOne<CashMovement>().WithMany().HasForeignKey(x => new { x.MovementId, x.CaptureId, x.AccountId })
            .HasPrincipalKey(x => new { x.Id, x.CaptureId, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<PaymentAllocation>().WithMany()
            .HasForeignKey(x => new { x.CaptureId, x.ObligationId, x.AccountId })
            .HasPrincipalKey(x => new { x.PaymentId, x.ObligationId, x.AccountId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CollectionGuardConfiguration : IEntityTypeConfiguration<CollectionGuard>
{
    public void Configure(EntityTypeBuilder<CollectionGuard> e)
    {
        e.ToTable("collection_guards");
        e.HasKey(x => x.ObligationId);
        e.HasOne<PaymentAttemptItem>().WithMany().HasForeignKey(x => new { x.AttemptId, x.ObligationId, x.AccountId })
            .HasPrincipalKey(x => new { x.AttemptId, x.ObligationId, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BillingReceiptConfiguration : IEntityTypeConfiguration<BillingReceipt>
{
    public void Configure(EntityTypeBuilder<BillingReceipt> e)
    {
        e.ToTable("billing_receipts");
        e.HasKey(x => new { x.ActorId, x.OperationId });
        e.Property(x => x.ActorId).HasMaxLength(450);
        e.Property(x => x.PayloadHash).HasMaxLength(64);
        e.HasOne<RentAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}
