namespace CsSsg.Src.Db;

public class MediaTagHistoryItem : ITagHistoryItem
{
    public Guid Id { get; set; }

    public TagHistoryItemType Type { get; set; }

    public string Tag { get; set; } = null!;

    public Guid Hist { get; set; }

    public virtual MediaTagHistory HistNavigation { get; set; } = null!;
}
