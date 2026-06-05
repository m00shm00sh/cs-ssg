namespace CsSsg.Src.Db;

public class MediaRevision
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public byte[] Contents { get; set; } = null!;

    public int ContentLength { get; set; }

    public string ContentType { get; set; } = null!;

    public Guid? AuthorId { get; set; }

    public Guid MediaId { get; set; }

    public virtual User? Author { get; set; }

    public virtual Medium Media { get; set; } = null!;

    public virtual ICollection<Medium> MediaNavigation { get; set; } = new List<Medium>();
}
