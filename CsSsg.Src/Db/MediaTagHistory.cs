namespace CsSsg.Src.Db;

public class MediaTagHistory : ITagHistory<MediaTagHistoryItem>
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid MediaId { get; set; }

    public Guid? AuthorId { get; set; }

    public int RevisionNumber { get; set; }

    public virtual User? Author { get; set; }

    public virtual Medium Media { get; set; } = null!;

    public virtual ICollection<MediaTagHistoryItem> Items { get; set; } = new List<MediaTagHistoryItem>();
}
