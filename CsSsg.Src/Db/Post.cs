namespace CsSsg.Src.Db;

public class Post : IIdTable, IHasAuthorAndSlug, IHasTag<PostTag>, IUsesRowVersion, IHasRevision<PostRevision>
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string Slug { get; set; } = null!;

    public Guid AuthorId { get; set; }

    public int PVer { get; set; }

    public Guid? LatestRevisionId { get; set; }

    public Guid? LatestRevisionAuthorId { get; set; }
    
    public int NumberOfRevisions { get; set; }
    
    public virtual User Author { get; set; } = null!;

    public virtual PostRevision? LatestRevision { get; set; }

    public virtual User? LatestRevisionAuthor { get; set; }

    public virtual ICollection<PostRevision> Revisions { get; set; } = new List<PostRevision>();

    public virtual ICollection<PostTag> Tags { get; set; } = new List<PostTag>();
    
    public uint RowVersion { get; set; }
}
