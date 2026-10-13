using System.Net;
using AllenBuilding.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AllenBuilding.IntegrationTests;

public sealed class OfficeProxyTests
{
    [Theory]
    [InlineData("127.0.0.1", "198.51.100.7", "https", "198.51.100.7", "https")]
    [InlineData("::1", "198.51.100.7", "https", "198.51.100.7", "https")]
    [InlineData("198.51.100.8", "127.0.0.1", "https", "198.51.100.8", "http")]
    [InlineData("127.0.0.1", "198.51.100.6,198.51.100.7", "https", "127.0.0.1", "http")]
    [InlineData("127.0.0.1", "198.51.100.6,198.51.100.7", "http,https", "198.51.100.7", "https")]
    public async Task Only_explicit_trusted_peers_can_forward_one_symmetric_hop_and_host_is_unchanged(string peer, string forwarded, string proto, string expectedPeer, string expectedProto)
    {
        var builder = Builder(true, "127.0.0.1", "::1"); builder.AddOfficeProxy();
        await using var services = builder.Services.BuildServiceProvider();
        var app = new ApplicationBuilder(services); app.UseForwardedHeaders();
        string? seenPeer = null; string? seenProto = null; string? seenHost = null;
        app.Run(context => { seenPeer = context.Connection.RemoteIpAddress?.ToString(); seenProto = context.Request.Scheme; seenHost = context.Request.Host.Host; return Task.CompletedTask; });
        var context = new DefaultHttpContext { RequestServices = services }; context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Scheme = "http"; context.Request.Host = new("office.example.test"); context.Request.Headers["X-Forwarded-For"] = forwarded;
        context.Request.Headers["X-Forwarded-Proto"] = proto; context.Request.Headers["X-Forwarded-Host"] = "attacker.example.test";
        context.Request.Headers["X-Forwarded-Prefix"] = "/attacker";
        await app.Build()(context);
        Assert.Equal(expectedPeer, seenPeer); Assert.Equal(expectedProto, seenProto); Assert.Equal("office.example.test", seenHost); Assert.Equal(PathString.Empty, context.Request.PathBase);
    }
    [Fact]
    public void Disabled_forwarding_accepts_no_headers_and_never_uses_wildcard_trust()
    {
        var builder = Builder(false); builder.AddOfficeProxy(); using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Equal(ForwardedHeaders.None, options.ForwardedHeaders); Assert.Empty(options.KnownProxies); Assert.Empty(options.KnownIPNetworks);
        Assert.False(services.GetRequiredService<OfficeProxySettings>().Enabled);
    }
    [Theory]
    [InlineData("")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("*")]
    [InlineData("127.0.0.0/8")]
    [InlineData("localhost")]
    [InlineData("255.255.255.255")]
    [InlineData("ff02::1")]
    [InlineData("224.0.0.1")]
    [InlineData("::ffff:0.0.0.0")]
    public void Invalid_or_broad_proxy_trust_is_rejected_without_echoing_the_value(string address)
    {
        var builder = Builder(true, address); var error = Assert.Throws<InvalidOperationException>(() => builder.AddOfficeProxy());
        Assert.Equal("Trusted proxy entries must be explicit unicast IP addresses.", error.Message);
    }
    [Fact]
    public void Empty_duplicate_or_unrestricted_proxy_configuration_cannot_start()
    {
        Assert.Throws<InvalidOperationException>(() => Builder(true).AddOfficeProxy());
        Assert.Throws<InvalidOperationException>(() => Builder(true, "127.0.0.1", "127.0.0.1").AddOfficeProxy());
        var builder = Builder(true, "127.0.0.1"); builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ForwardedHeaders_Enabled"] = "true" });
        Assert.Throws<InvalidOperationException>(() => builder.AddOfficeProxy());
    }
    private static WebApplicationBuilder Builder(bool enabled, params string[] addresses)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        var values = new Dictionary<string, string?> { ["Proxy:Enabled"] = enabled.ToString() };
        for (var index = 0; index < addresses.Length; index++) values["Proxy:TrustedAddresses:" + index] = addresses[index];
        builder.Configuration.AddInMemoryCollection(values); return builder;
    }
}
