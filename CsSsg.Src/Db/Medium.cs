namespace CsSsg.Src.Db;

public class Medium : IIdTable, IHasAuthorAndSlug, IHasTag<MediaTag>, IUsesRowVersion, IHasRevision<MediaRevision>
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

    public virtual MediaRevision? LatestRevision { get; set; }

    public virtual User? LatestRevisionAuthor { get; set; }

    public virtual ICollection<MediaRevision> Revisions { get; set; } = new List<MediaRevision>();

    public virtual ICollection<MediaTag> Tags { get; set; } = new List<MediaTag>();
    
    public uint RowVersion { get; set; }
}
