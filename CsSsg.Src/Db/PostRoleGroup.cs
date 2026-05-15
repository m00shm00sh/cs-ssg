namespace CsSsg.Src.Db;

public class PostRoleGroup : IRoleGroup
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid PostId { get; set; }

    public RoleNamespace Namespace { get; set; }
    
    public string Tag { get; set; } = null!;

    public virtual Post Post { get; set; } = null!;
}
