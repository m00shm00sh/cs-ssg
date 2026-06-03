using static CsSsg.Src.Post.IManageCommand;

namespace CsSsg.Test.Post;

internal class PostTagsEqualityComparer : IEqualityComparer<PostTags>
{
    public static PostTagsEqualityComparer Instance { get; } = new();
    
    public bool Equals(PostTags a, PostTags b)
    {
        if (object.Equals(a, b)) return true;
        if (a.Visibility != b.Visibility) return false;
        var aTags = _sortedTags(a.Tags);
        var bTags = _sortedTags(b.Tags);
        if (aTags.Count != bTags.Count) return false;
        if (aTags.Except(bTags).Any()) return false;
        return true;
    }

    public int GetHashCode(PostTags obj)
        => throw new NotImplementedException();

    private static List<string> _sortedTags(IEnumerable<string> tags)
    {
        List<string> sortedTags;
        
        lock (tags)
        {
            switch (tags)
            {
                case string[] tagsArray:
                    tagsArray.Sort();
                    sortedTags = tagsArray.ToList();
                    break;
                case List<string> tagsList:
                    tagsList.Sort();
                    sortedTags = tagsList;
                    break;
                default:
                    sortedTags = tags.ToList();
                    sortedTags.Sort();
                    break;
            }
        }
        
        return sortedTags;
    }
}