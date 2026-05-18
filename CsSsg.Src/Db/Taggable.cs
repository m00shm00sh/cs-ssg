namespace CsSsg.Src.Db;

public interface ITag
{
    public string Tag { get; set; }
}

public interface IHasTag<TTag>
where TTag : ITag
{
    public ICollection<TTag> Tags { get; set; }
}