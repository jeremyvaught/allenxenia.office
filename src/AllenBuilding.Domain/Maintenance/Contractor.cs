namespace AllenBuilding.Domain.Maintenance;

public sealed class Contractor
{
    private Contractor() { }
    public Guid Id { get; private set; }
    public Guid BuildingId { get; private set; }
    public string Name { get; private set; } = "";
    public Trade Trade { get; private set; }
    public string Email { get; private set; } = "";
    public string Phone { get; private set; } = "";
    public bool Active { get; private set; }
    public Guid Version { get; private set; }

    public static Contractor Create(Guid id, Guid buildingId, string name, Trade trade, string email, string phone)
    {
        if (id == Guid.Empty || buildingId == Guid.Empty) throw new ArgumentException("A building and contact are required.");
        var contact = new Contractor { Id = id, BuildingId = buildingId };
        contact.Update(name, trade, email, phone, true);
        return contact;
    }

    public void Update(string name, Trade trade, string email, string phone, bool active)
    {
        name = name?.Trim() ?? ""; email = email?.Trim() ?? ""; phone = phone?.Trim() ?? "";
        if (name.Length is < 1 or > 160 || email.Length > 254 || phone.Length > 80 || !Enum.IsDefined(trade))
            throw new ArgumentException("Invalid contractor contact.");
        if (email.Length > 0 && (!System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email))
            throw new ArgumentException("Enter an email address without a display name.");
        Name = name; Trade = trade; Email = email; Phone = phone; Active = active; Version = Guid.NewGuid();
    }
}
