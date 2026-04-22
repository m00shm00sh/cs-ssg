namespace CsSsg.Src.Db;

public class Medium
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string Slug { get; set; } = null!;

    public Stream Contents { get; set; } = null!;

    public string ContentType { get; set; } = null!;

    public bool Public { get; set; }

    public Guid AuthorId { get; set; }

    public virtual User? Author { get; set; }
}
