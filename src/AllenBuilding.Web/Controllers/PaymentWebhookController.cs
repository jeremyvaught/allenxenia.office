using AllenBuilding.Application.Billing;
using AllenBuilding.Infrastructure.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AllenBuilding.Web.Controllers;

// Stripe authenticates this one endpoint with its signature, never a browser cookie/CSRF token.
[AllowAnonymous, IgnoreAntiforgeryToken, EnableRateLimiting("PaymentWebhook")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PaymentWebhookController(IPaymentWebhookReceiver receiver) : Controller
{
    [HttpPost("payments/stripe/webhook"), RequestSizeLimit(StripeWebhookReceiver.MaximumBodyBytes)]
    public async Task<IActionResult> Receive()
    {
        if (receiver is DisabledPaymentWebhookReceiver) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (Request.ContentLength > StripeWebhookReceiver.MaximumBodyBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (!Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(Request.ContentType, out var media) ||
            !media.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            (media.Charset.HasValue && !media.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))) return BadRequest();
        var signatures = Request.Headers["Stripe-Signature"];
        if (signatures.Count != 1 || signatures[0] is not { Length: > 0 and <= 4096 } signature) return BadRequest();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var body = new byte[StripeWebhookReceiver.MaximumBodyBytes + 1];
        var length = 0;
        try
        {
            while (length < body.Length)
            {
                var read = await Request.Body.ReadAsync(body.AsMemory(length), deadline.Token);
                if (read == 0) break;
                length += read;
            }
            if (length > StripeWebhookReceiver.MaximumBodyBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge);
            var result = await receiver.ReceiveAsync(body.AsMemory(0, length), signature, HttpContext.RequestAborted);
            return result switch
            {
                WebhookCode.Accepted or WebhookCode.Duplicate or WebhookCode.Ignored => Ok(),
                WebhookCode.Rejected => BadRequest(),
                _ => StatusCode(StatusCodes.Status503ServiceUnavailable)
            };
        }
        catch (OperationCanceledException) { return StatusCode(StatusCodes.Status503ServiceUnavailable); }
        catch (IOException) { return StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }
}
