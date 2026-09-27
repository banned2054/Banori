namespace MediaManager.Data;

public sealed class SubjectTitleMapping
{
    public Guid           Id              { get; set; }
    public string         NormalizedTitle { get; set; } = string.Empty;
    public int            SubjectId       { get; set; }
    public MediaSubject   Subject         { get; set; } = null!;
    public DateTimeOffset UpdatedAtUtc    { get; set; }
}
