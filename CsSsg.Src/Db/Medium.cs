namespace CsSsg.Src.Db;

public class Medium : IHasAuthorAndSlug, IHasRoleGroup<MediaRoleGroup>, IHasRoleUser<MediaRoleUser>
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string Slug { get; set; } = null!;

    public Stream Contents { get; set; } = null!;
    
    public int ContentLength { get; set; }

    public string ContentType { get; set; } = null!;

    public Guid AuthorId { get; set; }

    public virtual User Author { get; set; } = null!;

    public virtual ICollection<MediaRoleGroup> RoleGroups { get; set; } = new List<MediaRoleGroup>();

    public virtual ICollection<MediaRoleUser> RoleUsers { get; set; } = new List<MediaRoleUser>();
}
