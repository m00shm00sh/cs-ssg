using System.Text.RegularExpressions;
using LanguageExt;

namespace CsSsg.Src.Post;

// NOTE: Entry is always returned from the RepositoryExtensions so there is no need to validate lengths
public readonly record struct Entry(
    string Slug, string Title,
    bool IsPublic, string AuthorHandle, DateTime LastModified,
    AccessLevel AccessLevel);

public readonly partial record struct Contents(string Title, string Body)
{
    public static string ComputeSlugName(string title)
        => MatchOneOrMoreNonWords().Replace(title, "-").ToLower().Trim('-');
    
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

public interface ManageCommand
{
    public record Rename(string RenameTo) : ManageCommand;

    public readonly record struct Permissions(bool Public)
    {
        public override string ToString()
            => $"Permissions: Public={Public}";
    }

    public record SetPermissions(Permissions Permissions) : ManageCommand;

    public record SetAuthor(string NewAuthor) : ManageCommand;
    public record Delete : ManageCommand;

    // An all-optional DTO for [FromForm] fails for ASP.NET Minimal (https://github.com/dotnet/aspnetcore/issues/56234)
    // so do the form parsing dance ourselves.
    internal static Either<ManageCommand, ArgumentException> FromForm(IFormCollection form)
    {
        string? newName = null;
        if (!string.IsNullOrWhiteSpace(form["a_rename"]))
        {
            newName = form["newname"];
            if (string.IsNullOrWhiteSpace(newName))
                return new ArgumentException("missing or invalid parameter: newname");
        }

        Permissions? newPerms = null;
        if (!string.IsNullOrWhiteSpace(form["a_perms"]))
        {
            newPerms = new Permissions(((string?)form["cb_public"])?.ToLower() == "on");
        }

        string? newAuthor = null;
        if (!string.IsNullOrWhiteSpace(form["a_author"]))
        {
            newAuthor = form["newauthor"];
            if (string.IsNullOrWhiteSpace(newAuthor))
                return new ArgumentException("missing or invalid parameter: newauthor");
        }

        var confirmDelete = false;
        if (!string.IsNullOrWhiteSpace(form["a_delete"]))
        {
            confirmDelete = ((string?)form["cb_delete"])?.ToLower() == "on";
            if (!confirmDelete)
                return new ArgumentException("missing or invalid parameter: delete confirmation");
        }

        var selectedRename = !string.IsNullOrWhiteSpace(newName);
        var selectedSetPermissions = newPerms is not null;
        var selectedSetAuthor = !string.IsNullOrWhiteSpace(newAuthor);
        var selectedDelete = confirmDelete;
        var numSelected = new[]
        {
            selectedRename, selectedSetPermissions, selectedSetAuthor, selectedDelete
        }.Select(x => x ? 1 : 0).Sum();
    #nullable disable
        if (numSelected > 1)
            return new ArgumentException($"expected one command; got {numSelected}");
        if (selectedRename)
            return new Rename(newName);
        if (selectedSetPermissions)
            return new SetPermissions(newPerms.Value);
        if (selectedSetAuthor)
            return new SetAuthor(newAuthor);
        if (selectedDelete)
            return new Delete();
    #nullable enable
        return new ArgumentException("expected command");
    }

    public record struct Stats(string Title, int ContentLength, Permissions Permissions);
}

public enum AccessLevel
{
    /// no permissions
    None = 1,
    /// permitted to read 
    Read,
    /// permitted to modify
    Write,
    /// permitted to modify and post is public
    WritePublic
}

internal static class AccessLevelExtensions
{
    extension(AccessLevel al)
    {
        public bool IsWrite => al is AccessLevel.Write or AccessLevel.WritePublic;
    }
}
