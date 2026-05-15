namespace CsSsg.Src.Db;

public interface IRoleUser
{
    public RoleNamespace Namespace { get; set; }
    
    public Guid User { get; set; }
}

public interface IHasRoleUser<TRoleUser>
where TRoleUser : IRoleUser
{
    public ICollection<TRoleUser> RoleUsers { get; set; }
}