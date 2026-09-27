namespace MediaManager.Data;

public sealed class MediaSubject
{
    public int            Id          { get; set; }
    public string         Name        { get; set; } = string.Empty;
    public string         NameCn      { get; set; } = string.Empty;
    public string         Platform    { get; set; } = string.Empty;
    public string?        AirDate     { get; set; }
    public string?        ImageUrl    { get; set; }
    public DateTimeOffset CachedAtUtc { get; set; }

    public ICollection<SubjectTitleMapping> TitleMappings { get; set; } = [];
    public ICollection<FeedItem>            FeedItems     { get; set; } = [];
}
