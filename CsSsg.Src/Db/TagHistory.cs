namespace CsSsg.Src.Db;

public interface ITagHistoryItem
{
    public TagHistoryItemType Type { get; set; }

    public string Tag { get; set; }

    public Guid Hist { get; set; }
}

public interface ITagHistory<TTagHistoryItem> : IRevision
    where TTagHistoryItem : ITagHistoryItem
{
    public ICollection<TTagHistoryItem> Items { get; set; }
}

public interface IHasTagHistory<TTagHistory, TTagHistoryItem>
    where TTagHistory : ITagHistory<TTagHistoryItem>
    where TTagHistoryItem : ITagHistoryItem
{
    public ICollection<TTagHistory> TagHistories { get; set; }
}