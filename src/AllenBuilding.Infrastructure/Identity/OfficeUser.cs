using Microsoft.AspNetCore.Identity;

namespace AllenBuilding.Infrastructure.Identity;

public sealed class OfficeUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public bool MustReplaceRecoveryCodes { get; set; }
}
