using Microsoft.AspNetCore.Identity;

namespace AllenBuilding.Infrastructure.Identity;

public sealed class OfficeUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
}
