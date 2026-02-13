namespace CsSsg.Db;

public class Post
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string Slug { get; set; } = null!;

    public string DisplayTitle { get; set; } = null!;

    public string Contents { get; set; } = null!;

    public bool Public { get; set; }

    public Guid? AuthorId { get; set; }

    public virtual User? Author { get; set; }
}
