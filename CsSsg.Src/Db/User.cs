namespace CsSsg.Src.Db;

public class User
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string Email { get; set; } = null!;

    public string PassArgon2id { get; set; } = null!;

    public virtual ICollection<Post> Posts { get; set; } = new List<Post>();
}
