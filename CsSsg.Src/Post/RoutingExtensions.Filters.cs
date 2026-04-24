using CsSsg.Src.Filters;
using CsSsg.Src.User;

namespace CsSsg.Src.Post;

internal static class FilterConfigurationExtensions
{
    internal static readonly ContentAccessPermissionFilterConfigurator ContentAccessFilterConfig = new("post",
        async (db, slug, uid, token) =>
            await db.GetPermissionsForContentAsync(uid, slug, token));
    
    internal static readonly WritePermissionFilterConfigurator WriteFilterConfig = new("post",
        (db, uid, token) =>
        {
            if (uid is null)
                return new ValueTask<bool>(false);
            return db.DoesUserHaveCreatePermissionAsync(uid.Value, token);
        }); 
    
    extension(RouteHandlerBuilder route)
    {
        internal RouteHandlerBuilder AddContentAccessPermissionsFilter()
        {
            route.AddEndpointFilter(ContentAccessFilterConfig);
            route.AddEndpointFilter<ContentAccessPermissionFilter>();
            return route;
        }
        
        internal RouteHandlerBuilder AddWritePermissionsFilter()
        {
            route.AddEndpointFilter(WriteFilterConfig);
            route.AddEndpointFilter<WritePermissionFilter>();
            return route;
        }
    }
}