using System.Security.Cryptography;
using System.Text.Json;
using AllenBuilding.Application.Billing;
using Microsoft.AspNetCore.DataProtection;

namespace AllenBuilding.Web.Security;

public sealed record BillingReviewLine(string Label, string Value);
public sealed record BillingReview(string ActorId, DateTimeOffset PreparedAt, string AccountLabel, string Heading,
    IReadOnlyList<BillingReviewLine> Lines, OpenRentAccount? Open = null, SetRentTerms? Terms = null, IssueCharge? Charge = null, IssueRent? Rent = null, ApplyRefundDisposition? Refund = null);
public sealed record BillingReviewPage(BillingReview Review, string Ticket);

// Only this server can mint a review. The confirm POST accepts the protected ticket,
// never client-supplied action/amount/date fields. Execution still checks current authority.
public sealed class BillingReviewTickets(IDataProtectionProvider protection, TimeProvider time)
{
    private readonly IDataProtector _protector = protection.CreateProtector("AllenOffice.BillingReview.v1");
    public BillingReviewPage Create(BillingReview review) => new(review, _protector.Protect(JsonSerializer.Serialize(review)));
    public BillingReview? Read(string? ticket, string actorId)
    {
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 65536) return null;
        try
        {
            var review = JsonSerializer.Deserialize<BillingReview>(_protector.Unprotect(ticket));
            if (review is null || review.ActorId != actorId || review.PreparedAt > time.GetUtcNow() || time.GetUtcNow() - review.PreparedAt > TimeSpan.FromMinutes(5)) return null;
            var commands = new object?[] { review.Open, review.Terms, review.Charge, review.Rent, review.Refund };
            return commands.Count(x => x is not null) == 1 ? review : null;
        }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
    }
}
