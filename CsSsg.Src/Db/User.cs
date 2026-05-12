namespace CsSsg.Src.Db;

public class User
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string Email { get; set; } = null!;

    public string PassArgon2id { get; set; } = null!;

    public virtual ICollection<Medium> Media { get; set; } = new List<Medium>();

    public virtual ICollection<MediaRoleUser> MediaRoleUsers { get; set; } = new List<MediaRoleUser>();

    public virtual ICollection<PostRoleUser> PostRoleUsers { get; set; } = new List<PostRoleUser>();

    public virtual ICollection<Post> Posts { get; set; } = new List<Post>();
}
