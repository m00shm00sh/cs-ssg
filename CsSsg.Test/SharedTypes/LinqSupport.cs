namespace CsSsg.Test.SharedTypes;

public static class LinqSupport
{
    extension<TSource>(IEnumerable<TSource> source)
    {
        public IEnumerable<TSource> SelectIndices(params int[] indices)
        => source.Where((_, i) => indices.Contains(i));
    }
}