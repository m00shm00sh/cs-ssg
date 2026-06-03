namespace CsSsg.Src.Db;

public interface IHasAuthorAndSlug
{
    public Guid AuthorId { get; set; }
    public string Slug { get; set; }
}
