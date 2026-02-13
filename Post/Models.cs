using System.Text.RegularExpressions;


namespace CsSsg.Post
{
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

    internal enum AccessLevel
    {
        /// no permissions
        None = 1,
        /// permitted to read 
        Read,
        /// permitted to modify
        Write
    }
}
