using LanguageExt;

using CsSsg.Src.Exceptions;
using CsSsg.Src.Filters;

namespace CsSsg.Src.Media;

/// <summary>
/// A listing entry representing a Media that can be accessed.
/// </summary>
/// <param name="Slug">Slug (link) name</param>
/// <param name="ContentType">mime content type</param>
/// <param name="Size">Media size</param>
/// <param name="IsPublic">Whether the media can be viewed anonymously</param>
/// <param name="AuthorHandle">Email of the user that is the post's current author</param>
/// <param name="LastModified">Timestamp of last modification</param>
/// <param name="AccessLevel">Access permissions (see <see cref="Filters.AccessLevel"/>)</param>
// NOTE: Entry is always returned from the RepositoryExtensions so there is no need to validate lengths
public readonly record struct Entry(string Slug, string ContentType, long Size, bool IsPublic, string AuthorHandle, 
    DateTime LastModified, AccessLevel AccessLevel);

/// <summary>
/// Media contents
/// </summary>
/// <param name="ContentType">MIME type</param>
/// <param name="ContentStream">File data stream</param>
public readonly record struct Object(string ContentType, Stream ContentStream)
{
    internal async Task<byte[]> ContentBuffer(CancellationToken token)
    {
        var contentBuf = new byte[ContentStream.Length];
        await ContentStream.CopyToAsync(new MemoryStream(contentBuf, true), token);
        return contentBuf;
    }
}

/// <summary>
/// Base interface for media management commands.
/// <br/>
/// Known commands:
///     <list type="bullet">
///         <item><see cref="IManageCommand.Rename"/></item>
///         <item><see cref="IManageCommand.Permissions"/></item>
///         <item><see cref="IManageCommand.SetAuthor"/></item>
///         <item><see cref="IManageCommand.Delete"/></item>
///     </list>
/// </summary>
public interface IManageCommand
{
    /// <summary>
    /// Rename command.
    /// </summary>
    /// <param name="RenameTo">Name to rename to. This is converted to a slug automatically.</param>
    public record Rename(string RenameTo) : IManageCommand;

    /// <summary>
    /// Permissions structure (<b>not</b> a <see cref="IManageCommand"/>).
    /// </summary>
    /// <param name="Public">Whether the post can be read anonymously.</param>
    public readonly record struct Permissions(bool Public)
    {
        public override string ToString()
            => $"Permissions: Public={Public}";
    }

    /// <summary>
    /// Set permissions command.
    /// </summary>
    /// <param name="Permissions">The new <see cref="IManageCommand.Permissions"/> value</param>
    public record SetPermissions(Permissions Permissions) : IManageCommand;

    /// <summary>
    /// Set new author command
    /// </summary>
    /// <param name="NewAuthor">new author email</param>
    public record SetAuthor(string NewAuthor) : IManageCommand;
    
    /// <summary>
    /// Delete post command (this is essentially a tag-only type).
    /// </summary>
    public record Delete : IManageCommand;

    // Form action the form validator is coming from
    internal enum FormFrom
    {
        Rename = 1,
        Permissions = 2,
        Author = 3,
        Delete = 4,
    }
    
    // An all-optional DTO for [FromForm] fails for ASP.NET Minimal (https://github.com/dotnet/aspnetcore/issues/56234)
    // so do the form parsing dance ourselves.
    internal static Either<ArgumentException, IManageCommand> FromForm(IFormCollection form, FormFrom formId)
    {
        switch (formId)
        {
            case FormFrom.Rename:
                var newName = (string?)form["newname"];
                if (string.IsNullOrWhiteSpace(newName))
                    return new ArgumentException("missing or invalid parameter: newname");
                return new Rename(newName);
        
            case FormFrom.Permissions:
                var newPerms = new Permissions
                {
                    Public = ((string?)form["cb_public"])?.ToLower() == "on"
                };
                return new SetPermissions(newPerms);
            case FormFrom.Author:
                var newAuthor = (string?)form["newauthor"];
                if (string.IsNullOrWhiteSpace(newAuthor))
                    return new ArgumentException("missing or invalid parameter: newauthor");
                return new SetAuthor(newAuthor);
            case FormFrom.Delete:
                var confirmDelete = ((string?)form["cb_delete"])?.ToLower() == "on";
                if (!confirmDelete)
                    return new ArgumentException("missing or invalid parameter: delete confirmation");
                return new Delete();
            default: 
                UnexpectedEnumValueException.VerifyOrThrow(formId);
                throw new ArgumentOutOfRangeException(nameof(formId), $"unhandled form id {formId}");
        }
    }

    public record struct Stats(string ContentType, long Size, Permissions Permissions);
}