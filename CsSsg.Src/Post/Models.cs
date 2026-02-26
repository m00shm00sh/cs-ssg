using System.Text.RegularExpressions;

namespace CsSsg.Src.Post;

// NOTE: Entry is always returned from the RepositoryExtensions so there is no need to validate lengths
internal readonly record struct Entry(
    string Slug,
    string Title,
    DateTime LastModified,
    AccessLevel AccessLevel);

internal readonly partial record struct Contents(
    string Title,
    string Body)
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
        => new Contents(efc.title, efc.contents);
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
