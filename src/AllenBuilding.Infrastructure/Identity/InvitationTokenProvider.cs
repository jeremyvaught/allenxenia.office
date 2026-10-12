using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AllenBuilding.Infrastructure.Identity;

public sealed class InvitationTokenOptions : DataProtectionTokenProviderOptions
{
    public InvitationTokenOptions() { Name = "OfficeInvitation"; TokenLifespan = TimeSpan.FromDays(7); }
}

public sealed class InvitationTokenProvider(IDataProtectionProvider protection, IOptions<InvitationTokenOptions> options,
    ILogger<DataProtectorTokenProvider<OfficeUser>> logger)
    : DataProtectorTokenProvider<OfficeUser>(protection, options, logger);
