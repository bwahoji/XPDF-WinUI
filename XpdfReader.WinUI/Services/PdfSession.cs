namespace XpdfReader.WinUI.Services;

public enum ReaderDisplayMode
{
    Single,
    VerticalContinuous,
    SideBySideSingle,
    SideBySideContinuous,
    HorizontalContinuous
}

public readonly record struct PageLayout(double X, double Y, double Width, double Height);

/// <summary>
/// Holds the per-tab state for one open PDF so switching tabs or
/// re-opening the same file does not re-parse or re-measure the document.
/// </summary>
public sealed class PdfSession : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public PdfDocument? Document { get; set; }
    public string? Path { get; set; }
    public string Name { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public int Page { get; set; } = 1;
    public int Rotation { get; set; }
    public int ZoomMode { get; set; }
    public ReaderDisplayMode DisplayMode { get; set; } = ReaderDisplayMode.Single;
    public PageSize[]? PageSizes { get; set; }
    public PageLayout[]? PageLayouts { get; set; }
    public int PageSizesRotation { get; set; } = -1;
    public bool SizesComplete { get; set; }
    public string PageText { get; set; } = string.Empty;
    public string LastQuery { get; set; } = string.Empty;
    public int LastMatchPage { get; set; }
    public int LastMatchOffset { get; set; } = -1;
    public List<OutlineEntry>? Outline { get; set; }
    public bool ShowOutline { get; set; } = true;

    public bool IsEmpty => Document is null;
    public string Header => string.IsNullOrEmpty(Name) ? "Empty" : Name;

    public void Dispose()
    {
        Document?.Dispose();
        Document = null;
    }
}
