namespace CsSsg.Src.Db;

public class PostRoleUser
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid PostId { get; set; }

    public RoleNamespace Namespace { get; set; }
    
    public Guid User { get; set; }

    public virtual Post Post { get; set; } = null!;

    public virtual User UserNavigation { get; set; } = null!;
}
