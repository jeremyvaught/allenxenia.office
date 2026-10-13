using System.Security.Cryptography;
using System.Text.Json;
using AllenBuilding.Application.Billing;
using Microsoft.AspNetCore.DataProtection;

namespace AllenBuilding.Web.Security;

public sealed record AutopayReview(string ActorId, DateTimeOffset PreparedAt, string Suite, ConfirmAutopay? Enrollment = null, StopAutopay? Stop = null,
    AutopayView? Stopping = null, DateTimeOffset? StopEffectiveAt = null);
public sealed record AutopayReviewPage(AutopayReview Review, string Ticket);
public sealed class AutopayReviewTickets(IDataProtectionProvider protection, TimeProvider time)
{
    private readonly IDataProtector _protector = protection.CreateProtector("AllenOffice.AutopayReview.v1");
    public AutopayReviewPage Create(AutopayReview review) => new(review, _protector.Protect(JsonSerializer.Serialize(review)));
    public AutopayReview? Read(string? ticket, string actor)
    {
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 65536) return null;
        try
        {
            var review = JsonSerializer.Deserialize<AutopayReview>(_protector.Unprotect(ticket)); var now = time.GetUtcNow();
            if (review is null || review.ActorId != actor || review.PreparedAt > now || now - review.PreparedAt > TimeSpan.FromMinutes(5) ||
                (review.Enrollment is null) == (review.Stop is null) || review.Enrollment?.ExplicitlyAccepted == true ||
                review.Stop is not null && (review.Stopping is null || review.StopEffectiveAt is null)) return null;
            return review;
        }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
    }
}
