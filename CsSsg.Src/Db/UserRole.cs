namespace CsSsg.Src.Db;

public class UserRole : ITag
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid UserId { get; set; }

    public RoleNamespace Namespace { get; set; }
    
    public string Tag { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
