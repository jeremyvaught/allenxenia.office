using System.ComponentModel.DataAnnotations;
using AllenBuilding.Application.Knowledge;
using AllenBuilding.Domain.Knowledge;

namespace AllenBuilding.Web.Models;

public sealed class ArticleForm
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BuildingId { get; set; }
    public Guid? Version { get; set; }
    [Required, StringLength(160)] public string Title { get; set; } = "";
    [Required, StringLength(10000)] public string Body { get; set; } = "";
    [EnumDataType(typeof(ArticleAudience))] public ArticleAudience Audience { get; set; }
    [DataType(DataType.Date)] public DateOnly ReviewOn { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(6);
    public DraftArticle Command() => new(Id, BuildingId, Version, Title, Body, Audience, ReviewOn);
}
