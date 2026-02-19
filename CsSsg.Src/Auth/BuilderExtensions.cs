using Microsoft.AspNetCore.DataProtection;

namespace CsSsg.Src.Auth;

internal static class BuilderExtensions
{
    extension(IDataProtectionBuilder builder)
    {
        public IDataProtectionBuilder ApplyBuilder(Action<IDataProtectionBuilder> block)
        {
            block(builder);
            return builder;
        }
    }
}
