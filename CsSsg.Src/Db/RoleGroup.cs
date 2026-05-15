namespace CsSsg.Src.Db;

public interface IRoleGroup
{
    public RoleNamespace Namespace { get; set; }
    
    public string Tag { get; set; }
}

public interface IHasRoleGroup<TRoleGroup>
where TRoleGroup : IRoleGroup
{
    public ICollection<TRoleGroup> RoleGroups { get; set; }
}