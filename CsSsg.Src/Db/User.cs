namespace CsSsg.Src.Db;

public class User : IHasTag<UserRole>
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string Email { get; set; } = null!;

    public string PassArgon2id { get; set; } = null!;

    public int PVer { get; set; }

    public virtual ICollection<MediaRevision> MediaRevisions { get; set; } = new List<MediaRevision>();

    public virtual ICollection<Medium> MediumAuthors { get; set; } = new List<Medium>();

    public virtual ICollection<Medium> MediumLatestRevisionAuthors { get; set; } = new List<Medium>();

    public virtual ICollection<Post> PostAuthors { get; set; } = new List<Post>();

    public virtual ICollection<Post> PostLatestRevisionAuthors { get; set; } = new List<Post>();

    public virtual ICollection<PostRevision> PostRevisions { get; set; } = new List<PostRevision>();

    public virtual ICollection<UserRole> Tags { get; set; } = new List<UserRole>();
}
