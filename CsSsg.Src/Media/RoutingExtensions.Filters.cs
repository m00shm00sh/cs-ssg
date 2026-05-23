using KotlinScopeFunctions;

using CsSsg.Src.Filters;
using static CsSsg.Src.Post.RepositoryExtensions;
using CsSsg.Src.User;

namespace CsSsg.Src.Media;

internal static class FilterConfigurationExtensions
{
    internal static readonly ContentAccessPermissionFilterConfigurator ContentAccessFilterConfig = new("media",
        async (db, slug, token) =>
            (await db.GetMetadataForMediaAsync(slug, token))
                ?.Let(m => new PostPermissions(m.Item1.AuthorId, m.Item1.Tags, m.Item2))
    );
    
    internal static readonly WritePermissionFilterConfigurator WriteFilterConfig = new("media",
        (db, user, token) =>
        {
            if (user is null)
                return new ValueTask<bool>(false);
            return db.DoesUserHaveCreateMediaPermissionAsync(user, token);
        });
    
    internal static readonly WritePermissionFilterConfigurator WriteMetadataFilterConfig = WriteFilterConfig with
    {
        AllowedAccessLevelsForExistingContent = [AccessLevel.FullControl],
        ForbidCreate = true
    };

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
        
        internal RouteHandlerBuilder AddWriteMetadataPermissionsFilter()
        {
            route.AddEndpointFilter(WriteMetadataFilterConfig);
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
