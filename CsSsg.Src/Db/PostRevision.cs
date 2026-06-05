namespace CsSsg.Src.Db;

public class PostRevision
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string DisplayTitle { get; set; } = null!;

    public string Contents { get; set; } = null!;

    public Guid? AuthorId { get; set; }

    public Guid PostId { get; set; }

    public virtual User? Author { get; set; }

    public virtual Post Post { get; set; } = null!;

    public virtual ICollection<Post> Posts { get; set; } = new List<Post>();
}
