using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace AllenBuilding.Web.Hosting;

public sealed record OfficeProxySettings(bool Enabled);
public static class OfficeProxy
{
    public static void AddOfficeProxy(this WebApplicationBuilder builder)
    {
        // Automatic middleware can rewrite the peer before our policy sees it. Reject its unrestricted trust switch.
        if (builder.Configuration.GetValue<bool>("ForwardedHeaders_Enabled") ||
            string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED"), "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("DOTNET_FORWARDEDHEADERS_ENABLED"), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Configure explicit trusted proxy addresses; unrestricted automatic forwarding is not supported.");
        var enabled = builder.Configuration.GetValue<bool>("Proxy:Enabled");
        if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing") && !enabled)
            throw new InvalidOperationException("Hosted deployments require Proxy:Enabled and explicit trusted proxy addresses.");
        var addresses = builder.Configuration.GetSection("Proxy:TrustedAddresses").Get<string[]>() ?? [];
        var trusted = new List<IPAddress>();
        if (enabled)
        {
            if (addresses.Length is < 1 or > 8) throw new InvalidOperationException("Proxy forwarding requires one to eight explicit trusted IP addresses.");
            foreach (var value in addresses)
            {
                if (value is null || value.Length > 64 || !IPAddress.TryParse(value, out var address) ||
                    address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None) ||
                    address.Equals(IPAddress.IPv6None) || address.IsIPv6Multicast || address.Equals(IPAddress.Broadcast) ||
                    (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && address.GetAddressBytes()[0] is >= 224 and <= 239) ||
                    (address.IsIPv4MappedToIPv6 && (address.MapToIPv4().Equals(IPAddress.Any) || address.MapToIPv4().GetAddressBytes()[0] >= 224)))
                    throw new InvalidOperationException("Trusted proxy entries must be explicit unicast IP addresses.");
                trusted.Add(address);
            }
            if (trusted.Distinct().Count() != trusted.Count) throw new InvalidOperationException("Trusted proxy addresses must be distinct.");
        }
        builder.Services.AddSingleton(new OfficeProxySettings(enabled));
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = enabled ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto : ForwardedHeaders.None;
            options.ForwardLimit = 1; options.RequireHeaderSymmetry = true;
            options.KnownProxies.Clear(); options.KnownIPNetworks.Clear();
            foreach (var address in trusted) options.KnownProxies.Add(address);
            // Never accept a client-controlled forwarded host, path base or prefix.
        });
    }
    public static void UseOfficeProxy(this WebApplication app)
    {
        if (app.Services.GetRequiredService<OfficeProxySettings>().Enabled) app.UseForwardedHeaders();
    }
}
