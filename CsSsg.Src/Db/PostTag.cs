namespace CsSsg.Src.Db;

public class PostTag : ITag
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid PostId { get; set; }

    public string Tag { get; set; } = null!;

    public virtual Post Post { get; set; } = null!;
}
