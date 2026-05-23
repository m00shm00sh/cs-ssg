using CsSsg.Src.Filters;
using CsSsg.Src.User;

namespace CsSsg.Src.Post;

internal static class FilterConfigurationExtensions
{
    internal static readonly ContentAccessPermissionFilterConfigurator ContentAccessFilterConfig = new("post",
        async (db, slug, token) =>
            await db.GetPermissionsForContentAsync(slug, token));
    
    
    
    internal static readonly WritePermissionFilterConfigurator WriteFilterConfig = new("post",
        (db, user, token) =>
        {
            if (user is null)
                return new ValueTask<bool>(false);
            return db.DoesUserHaveCreatePermissionAsync(user, token);
        });

    internal static readonly WritePermissionFilterConfigurator WriteMetadataFilterConfig = WriteFilterConfig with
    {
        AllowedAccessLevelsForExistingContent = [AccessLevel.FullControl],
        ForbidCreate = true
    };

    internal static readonly IfModifiedSinceFilterConfigurator IfModifiedSinceConfig = new("post",
        async (db, slug, token) =>
            (await db.GetModifyTimeAsync(slug, token)).ToNullable()
    );
    
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
        
        internal RouteHandlerBuilder AddWriteMetadataPermissionsFilter()
        {
            route.AddEndpointFilter(WriteMetadataFilterConfig);
            route.AddEndpointFilter<WritePermissionFilter>();
            return route;
        }

        internal RouteHandlerBuilder AddIfModifiedSinceFilter()
        {
            route.AddEndpointFilter(IfModifiedSinceConfig);
            route.AddEndpointFilter<IfModifiedSinceFilter>();
            return route;
        }
    }
}