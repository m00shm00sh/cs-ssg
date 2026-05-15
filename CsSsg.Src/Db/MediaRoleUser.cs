namespace CsSsg.Src.Db;

public class MediaRoleUser : IRoleUser
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid MediaId { get; set; }

    public RoleNamespace Namespace { get; set; }
    
    public Guid User { get; set; }

    public virtual Medium Media { get; set; } = null!;

    public virtual User UserNavigation { get; set; } = null!;
}
