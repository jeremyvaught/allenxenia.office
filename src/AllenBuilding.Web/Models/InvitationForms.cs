using System.ComponentModel.DataAnnotations;

namespace AllenBuilding.Web.Models;

public sealed class InviteForm
{
    [Required] public Guid SuiteId { get; set; }
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
}

public sealed class ActivationForm
{
    public Guid InvitationId { get; set; }
    [Required, StringLength(4096)] public string Token { get; set; } = "";
    [Required, StringLength(128, MinimumLength = 12), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
}

public sealed class ResetRequestForm
{
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
}

public sealed class ResetForm
{
    [Required, StringLength(450)] public string UserId { get; set; } = "";
    [Required, StringLength(4096)] public string Token { get; set; } = "";
    [Required, StringLength(128, MinimumLength = 12), DataType(DataType.Password)] public string Password { get; set; } = "";
}

public sealed class SuiteForm
{
    public Guid BuildingId { get; set; }
    [Required, StringLength(80)] public string Name { get; set; } = "";
}

public sealed class ExistingAccountForm
{
    public Guid OperationId { get; set; }
    public Guid SuiteId { get; set; }
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
    [Required, StringLength(2000)] public string Reason { get; set; } = "";
}
public sealed class InvitationChangeForm
{
    public Guid InvitationId { get; set; }
    public Guid Version { get; set; }
    public bool Revoke { get; set; }
    [Required, StringLength(2000)] public string Reason { get; set; } = "";
}
public sealed record InvitationRow(Guid Id, Guid Version, string Suite, string Email, DateTimeOffset ExpiresAt, string Status);
