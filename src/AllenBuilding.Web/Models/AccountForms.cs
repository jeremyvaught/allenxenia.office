using System.ComponentModel.DataAnnotations;

namespace AllenBuilding.Web.Models;

public sealed class LoginForm
{
    [StringLength(2048)] public string? ReturnUrl { get; set; }
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
    [Required, DataType(DataType.Password), StringLength(128)] public string Password { get; set; } = "";
}

public sealed class FactorForm
{
    [StringLength(2048)] public string? ReturnUrl { get; set; }
    [Required, StringLength(32)] public string Code { get; set; } = "";
    public bool Recovery { get; set; }
}

public sealed class EnrollmentForm
{
    [Required, StringLength(12)] public string Code { get; set; } = "";
    public string? Key { get; set; }
    public bool FirstSetup { get; set; }
}

public sealed class OperatorEnrollmentForm
{
    [Required, StringLength(32, MinimumLength = 32)] public string Code { get; set; } = "";
}

public sealed class ReauthenticateForm
{
    [Required, DataType(DataType.Password), StringLength(128)] public string Password { get; set; } = "";
    [Required, StringLength(12)] public string Code { get; set; } = "";
}
