namespace CsSsg.Src.Db;

public interface ITag
{
    public string Tag { get; set; }
}

public interface IHasTag<TTag> : IHasPermissionsVersion
where TTag : ITag
{
    public ICollection<TTag> Tags { get; set; }
}

public interface IHasPermissionsVersion
{
    public int PVer { get; set; }
}