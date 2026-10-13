using System.ComponentModel.DataAnnotations;
using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Web.Models;

public sealed class RefundDispositionForm
{
    public Guid OperationId { get; set; }
    public Guid MovementId { get; set; }
    public Guid ReviewVersion { get; set; }
    [Required, EnumDataType(typeof(RefundDisposition))] public RefundDisposition? Disposition { get; set; }
    [Required, StringLength(2000)] public string Reason { get; set; } = "";
    public List<RefundItemForm> Items { get; set; } = [];
}
public sealed class RefundItemForm
{
    public Guid ObligationId { get; set; }
    public Guid Version { get; set; }
    [Required, Range(typeof(decimal), "0", "999999.99")] public decimal? Amount { get; set; }
}
