namespace CsSsg.Src.Db;

public class PostTagHistory : ITagHistory<PostTagHistoryItem>
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid PostId { get; set; }

    public Guid? AuthorId { get; set; }

    public int RevisionNumber { get; set; }

    public virtual User? Author { get; set; }

    public virtual Post Post { get; set; } = null!;

    public virtual ICollection<PostTagHistoryItem> Items { get; set; } = new List<PostTagHistoryItem>();
}
