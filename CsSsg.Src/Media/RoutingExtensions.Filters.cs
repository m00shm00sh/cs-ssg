using CsSsg.Src.Filters;
using CsSsg.Src.User;

namespace CsSsg.Src.Media;

internal static class FilterConfigurationExtensions
{
    internal static readonly ContentAccessPermissionFilterConfigurator ContentAccessFilterConfig = new("media",
        async (db, slug, uid, token) =>
            (await db.GetMetadataForMediaAsync(uid, slug, token))?.AccessLevel
    );
    
    internal static readonly WritePermissionFilterConfigurator WriteFilterConfig = new("media",
        (db, uid, token) =>
        {
            if (uid is null)
                return new ValueTask<bool>(false);
            return db.DoesUserHaveCreateMediaPermissionAsync(uid.Value, token);
        });

    internal static readonly IfModifiedSinceFilterConfigurator IfModifiedSinceFilterConfig = new("media",
        async (db, slug, token) =>
            (await db.GetModifyTimeForMediaAsync(slug, token)).ToNullable()
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

        internal RouteHandlerBuilder AddIfModifiedSinceFilter()
        {
            route.AddEndpointFilter(IfModifiedSinceFilterConfig);
            route.AddEndpointFilter<IfModifiedSinceFilter>();
            return route;
        }
    }
}
