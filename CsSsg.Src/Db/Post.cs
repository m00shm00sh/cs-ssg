namespace CsSsg.Src.Db;

public class Post : IHasAuthorAndSlug, IHasTag<PostTag>
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string Slug { get; set; } = null!;

    public string DisplayTitle { get; set; } = null!;

    public string Contents { get; set; } = null!;

    public Guid AuthorId { get; set; }

    public virtual User Author { get; set; } = null!;

    public virtual ICollection<PostTag> Tags { get; set; } = new List<PostTag>();
    
    public int PVer { get; set; }
}
