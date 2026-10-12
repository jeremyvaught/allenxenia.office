namespace AllenBuilding.Application.Access;

public enum AccessCode { Success, ValidationFailed, Unavailable, Conflict }
public sealed record GrantExistingAccount(Guid OperationId, Guid SuiteId, string Email, string Reason);
public sealed record AccessResult(AccessCode Code, Guid? MembershipId = null);
