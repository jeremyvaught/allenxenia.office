using AllenBuilding.Domain.Billing;

namespace AllenBuilding.Application.Billing;

public enum InvoiceMappingCode { Recorded, Conflict, Invalid, Unavailable }
public sealed record InvoiceMappingResult(InvoiceMappingCode Code, Guid? MappingId = null, Guid? ObligationId = null);
public interface IRentInvoiceMappingStore
{
    Task<InvoiceMappingResult> ApplyAsync(CanonicalRentInvoice fact, CancellationToken cancellationToken);
}
