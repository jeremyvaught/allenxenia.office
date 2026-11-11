using AllenBuilding.Domain.Billing;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Domain.Maintenance;
using AllenBuilding.Domain.Tenancy;
using AllenBuilding.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AllenBuilding.Infrastructure.Persistence.Configuration;

internal sealed class HelpArticleConfiguration : IEntityTypeConfiguration<HelpArticle>
{
    public void Configure(EntityTypeBuilder<HelpArticle> e)
    {
        e.ToTable("help_articles");
        e.HasKey(x => x.Id);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        e.HasIndex(x => new { x.BuildingId, x.State, x.Id });
        e.HasOne<Building>().WithMany().HasForeignKey(x => x.BuildingId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class HelpRevisionConfiguration : IEntityTypeConfiguration<HelpRevision>
{
    public void Configure(EntityTypeBuilder<HelpRevision> e)
    {
        e.ToTable("help_revisions");
        e.HasKey(x => x.Id);
        e.Property(x => x.Title).HasMaxLength(160);
        e.Property(x => x.Body).HasColumnType("TEXT");
        e.Property(x => x.Audience).HasConversion<string>().HasMaxLength(32);
        e.Property(x => x.EditorId).HasMaxLength(450);
        e.HasIndex(x => new { x.ArticleId, x.EditedAt, x.Id });
        e.HasOne<HelpArticle>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OfficeUser>().WithMany().HasForeignKey(x => x.EditorId).OnDelete(DeleteBehavior.Restrict);
    }
}
