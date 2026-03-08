using System.Text.RegularExpressions;
using LanguageExt;

namespace CsSsg.Src.Post;

// NOTE: Entry is always returned from the RepositoryExtensions so there is no need to validate lengths
internal readonly record struct Entry(
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

internal readonly record struct ManageCommand(
    string RenameTo = "",
    ManageCommand.Permissions? NewPermissions = null,
    string ReassignAuthorTo = "",
    bool ConfirmDelete = false)
{

    // An all-optional DTO for [FromForm] fails for ASP.NET Minimal (https://github.com/dotnet/aspnetcore/issues/56234)
    // so do the form parsing dance ourselves.
    public static Either<ManageCommand, ArgumentException> FromForm(IFormCollection form)
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

        return new ManageCommand(newName ?? "", newPerms, newAuthor ?? "", confirmDelete);
    }
    
    public readonly record struct Permissions(bool Public)
    {
        public override string ToString()
            => $"Permissions: Public={Public}";
    }
    
    public enum ActiveCommand
    {
        RenameTo = 1,
        NewPermissions,
        NewAuthor,
        Delete
    }
    
    public Either<ActiveCommand, ArgumentException> GetActiveCommand()
    {
        var selectedRename = !string.IsNullOrWhiteSpace(RenameTo);
        var selectedNewPermissions = NewPermissions.HasValue;
        var selectedNewAuthor = !string.IsNullOrWhiteSpace(ReassignAuthorTo);
        var selectedDelete = ConfirmDelete;
        var numSelected = new[]
        {
            selectedRename, selectedNewPermissions, selectedNewAuthor, selectedDelete
        }.Select(x => x ? 1 : 0).Sum();
        switch (numSelected)
        {
            case 0:
                return new ArgumentException("expected command");
            case > 1:
                return new ArgumentException($"expected one command; got {numSelected}");
        }

        if (selectedRename) return ActiveCommand.RenameTo;
        if (selectedNewPermissions) return ActiveCommand.NewPermissions;
        if (selectedNewAuthor) return ActiveCommand.NewAuthor;
        if (selectedDelete) return ActiveCommand.Delete;
        throw new InvalidOperationException("could not determine command");
    }
}

internal enum AccessLevel
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
