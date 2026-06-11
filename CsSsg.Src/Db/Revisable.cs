namespace CsSsg.Src.Db;

public interface IRevision
{
    public int RevisionNumber { get; set; }
} 

public interface IHasRevision<TRevision>
where TRevision : IRevision
{
    public ICollection<TRevision> Revisions { get; set; }
    
    public Guid? LatestRevisionId { get; set; }
    
    public TRevision? LatestRevision { get; set; }
    
    public Guid? LatestRevisionAuthorId { get; set; }
    
    public User? LatestRevisionAuthor { get; set; }
    
    public int NumberOfRevisions { get; set; }
}
