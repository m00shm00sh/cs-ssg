namespace CsSsg.Src.Db;

public class PostTagHistoryItem : ITagHistoryItem
{
    public Guid Id { get; set; }

    public TagHistoryItemType Type { get; set; }
    
    public string Tag { get; set; } = null!;

    public Guid Hist { get; set; }

    public virtual PostTagHistory HistNavigation { get; set; } = null!;
}
