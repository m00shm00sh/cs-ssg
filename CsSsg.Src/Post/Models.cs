using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LanguageExt;

using CsSsg.Src.Exceptions;
using CsSsg.Src.Filters;

namespace CsSsg.Src.Post;

public interface IHasTags
{
    IReadOnlyCollection<string> Tags { get; init; }
}

/// <summary>
/// A listing entry representing a Post that can be accessed.
/// </summary>
/// <param name="Slug">Slug (link) name</param>
/// <param name="Title">Post title</param>
/// <param name="AccessLevel">Post access permission level</param>
/// <param name="AuthorHandle">Email of the user that is the post's current author</param>
/// <param name="LastModified">Timestamp of last modification</param>
/// <param name="Tags">Access tags</param>
// NOTE: Entry is always returned from the RepositoryExtensions so there is no need to validate lengths
public readonly record struct Entry(
    string Slug, string Title,
    AccessLevel AccessLevel, string AuthorHandle, DateTimeOffset LastModified,
    IReadOnlyCollection<string> Tags
) : IHasTags;

public static class EntryExtensions
{
    public static bool HasPublicTag(IReadOnlyCollection<string> tags)
        => tags.Contains(RepositoryExtensions.TAG_PUBLIC);
    public static bool HasUnlistedTag(IReadOnlyCollection<string> tags)
        => tags.Contains(RepositoryExtensions.TAG_UNLISTED);
    
    extension<TEntry>(TEntry entry) where TEntry : IHasTags
    {
        public bool IsPublic() => HasPublicTag(entry.Tags);
        
        public bool IsUnlisted() => HasUnlistedTag(entry.Tags);
    }
}

/// <summary>
/// Post contents
/// </summary>
/// <param name="Title">Post title</param>
/// <param name="Body">Post body, as a Markdown string</param>
/// <param name="LastModified">Post modify time (if available)</param>
public readonly partial record struct Contents(string Title, string Body, 
    [field:JsonIgnore] DateTimeOffset? LastModified = null)
{
    /// Computes slug (link) name for given title
    public static string ComputeSlugName(string title)
        => MatchOneOrMoreNonWords().Replace(title, "-").ToLower().Trim('-');
    
    /// Computes slug (link) name from title
    public string ComputeSlugName() => ComputeSlugName(Title);

    [GeneratedRegex(@"[^\w]+")]
    private static partial Regex MatchOneOrMoreNonWords();
}

// ReSharper disable InconsistentNaming (this is a dto for form binding only)
internal readonly record struct EditorFormContents(string title, string contents)
{
    public static implicit operator Contents(EditorFormContents efc)
        => new(efc.title, efc.contents);
}

/// <summary>
/// Base interface for post management commands.
/// <br/>
/// Known commands:
///     <list type="bullet">
///         <item><see cref="IManageCommand.Rename"/></item>
///         <item><see cref="IManageCommand.SetTags"/></item>
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
    ///     Post visibility. Unlisted is a shorthand for public read and
    ///     Public is a shorthand for public read and search.
    /// </summary>
    public enum PostVisibility
    {
        Tags = 1,
        Unlisted,
        Public
    }
    
    /// <summary>
    ///     Post tags.
    /// </summary>
    public readonly record struct PostTags(PostVisibility Visibility, IEnumerable<string> Tags)
    {
        public PostTags() : this(PostVisibility.Tags) { }
        public PostTags(PostVisibility visibility) : this(visibility, []) { }
        
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append("Tags:");
            switch (Visibility)
            {
                case PostVisibility.Unlisted:
                    b.Append(" unlisted");
                    break;
                case PostVisibility.Public:
                    b.Append(" public");
                    break;
            }

            b.AppendJoin("", Tags.Select(s => $" {s}"));
            return b.ToString();
        }
    }

    /// <summary>
    /// Set tags command.
    /// </summary>
    /// <param name="Tags">The new <see cref="IManageCommand.PostTags"/> value</param>
    public record SetTags(PostTags Tags) : IManageCommand;

    /// <summary>
    /// Set new author command.
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
        _unused0 = 2,
        Author = 3,
        Delete = 4,
        Tags = 5,
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
        
            case FormFrom.Tags:
                var visibility = (string?)form["visibility"] switch
                {
                    "unlisted" => PostVisibility.Unlisted,
                    "public" => PostVisibility.Public,
                    _ => PostVisibility.Tags
                };
                IReadOnlyList<string> forbidTags = ["unlisted", "public"];
                var newTags = new PostTags(visibility, [])
                {
                    Tags = ((string?)form["tags"])
                           ?.Split(" ")
                           .Select(Contents.ComputeSlugName)
                           .Where(s => !forbidTags.Contains(s)) 
                        ?? []
                };
                return new SetTags(newTags);

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

    public record struct Stats(string Title, int ContentLength, PostTags Tags);
}
