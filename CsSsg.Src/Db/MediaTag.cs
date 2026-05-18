namespace CsSsg.Src.Db;

public class MediaTag : ITag
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid MediaId { get; set; }

    public string Tag { get; set; } = null!;

    public virtual Medium Media { get; set; } = null!;
}
