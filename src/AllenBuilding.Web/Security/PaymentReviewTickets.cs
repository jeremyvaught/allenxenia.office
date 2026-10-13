using System.Security.Cryptography;
using System.Text.Json;
using AllenBuilding.Application.Billing;
using Microsoft.AspNetCore.DataProtection;

namespace AllenBuilding.Web.Security;

public sealed record PaymentReview(string ActorId, DateTimeOffset PreparedAt, string Suite, CollectReviewedBalances Command, IReadOnlyList<BillingReviewLine> Lines);
public sealed record PaymentReviewPage(PaymentReview Review, string Ticket);
public sealed class PaymentReviewTickets(IDataProtectionProvider protection, TimeProvider time)
{
    private readonly IDataProtector _protector = protection.CreateProtector("AllenOffice.PayerPaymentReview.v1");
    public PaymentReviewPage Create(PaymentReview review) => new(review, _protector.Protect(JsonSerializer.Serialize(review)));
    public PaymentReview? Read(string? ticket, string actorId)
    {
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 65536) return null;
        try
        {
            var review = JsonSerializer.Deserialize<PaymentReview>(_protector.Unprotect(ticket)); var now = time.GetUtcNow();
            return review is null || review.ActorId != actorId || review.PreparedAt > now || now - review.PreparedAt > TimeSpan.FromMinutes(5) ? null : review;
        }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
    }
}
