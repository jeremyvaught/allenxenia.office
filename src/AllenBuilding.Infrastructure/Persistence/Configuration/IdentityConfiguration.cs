using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AllenBuilding.Infrastructure.Persistence.Configuration;

internal sealed class OfficeUserConfiguration : IEntityTypeConfiguration<OfficeUser>
{
    public void Configure(EntityTypeBuilder<OfficeUser> e)
    {
        e.Property(x => x.DisplayName).HasMaxLength(120);
    }
}
