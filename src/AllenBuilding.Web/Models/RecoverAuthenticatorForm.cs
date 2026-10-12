using System.ComponentModel.DataAnnotations;

namespace AllenBuilding.Web.Models;

public sealed class RecoverAuthenticatorForm
{
    [Required, DataType(DataType.Password), StringLength(128)] public string Password { get; set; } = "";
}
