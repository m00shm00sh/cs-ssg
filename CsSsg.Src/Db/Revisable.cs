namespace CsSsg.Src.Db;

public interface IRevision
{
    public int RevisionNumber { get; set; }
    
    public Guid? AuthorId { get; set; }
} 

public interface IHasRevision<TRevision>
where TRevision : IRevision
{
    public ICollection<TRevision> ContentRevisions { get; set; }
    
    public Guid? LatestContentRevisionId { get; set; }
    
    public TRevision? LatestContentRevision { get; set; }
    
    public Guid? LatestRevisionAuthorId { get; set; }
    
    public User? LatestRevisionAuthor { get; set; }
    
    public int NumberOfRevisions { get; set; }
}
