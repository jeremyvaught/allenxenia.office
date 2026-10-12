using System.ComponentModel.DataAnnotations;

namespace AllenBuilding.Web.Models;

public sealed class LoginForm
{
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
    [Required, DataType(DataType.Password), StringLength(128)] public string Password { get; set; } = "";
}

public sealed class FactorForm
{
    [Required, StringLength(32)] public string Code { get; set; } = "";
    public bool Recovery { get; set; }
}

public sealed class EnrollmentForm
{
    [Required, StringLength(12)] public string Code { get; set; } = "";
    public string? Key { get; set; }
}

public sealed class ReauthenticateForm
{
    [Required, DataType(DataType.Password), StringLength(128)] public string Password { get; set; } = "";
    [Required, StringLength(12)] public string Code { get; set; } = "";
}
