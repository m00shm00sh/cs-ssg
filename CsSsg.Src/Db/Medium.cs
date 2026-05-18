namespace CsSsg.Src.Db;

public class Medium : IHasAuthorAndSlug, IHasTag<MediaTag>
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string Slug { get; set; } = null!;

    public string ContentType { get; set; } = null!;

    public Guid AuthorId { get; set; }

    public int ContentLength { get; set; }

    public virtual User Author { get; set; } = null!;

    public virtual ICollection<MediaTag> Tags { get; set; } = new List<MediaTag>();
}
