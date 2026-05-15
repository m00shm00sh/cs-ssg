namespace CsSsg.Src.Db;

public class Post : IHasRoleGroup<PostRoleGroup>, IHasRoleUser<PostRoleUser>
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string Slug { get; set; } = null!;

    public string DisplayTitle { get; set; } = null!;

    public string Contents { get; set; } = null!;

    public Guid? AuthorId { get; set; }

    public virtual User? Author { get; set; }

    public virtual ICollection<PostRoleGroup> RoleGroups { get; set; } = new List<PostRoleGroup>();

    public virtual ICollection<PostRoleUser> RoleUsers { get; set; } = new List<PostRoleUser>();
}
