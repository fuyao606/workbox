using Timenotifier.Models;
using Timenotifier.Services.Reporting;

namespace Timenotifier.Views;

/// <summary>
/// Presentation wrapper for one review card. It keeps the underlying evidence item
/// intact while exposing structured, readable summary fields to the XAML template.
/// </summary>
public sealed class TimelineEntry
{
    public TimelineEntry(EvidenceItem evidence)
    {
        Evidence = evidence;
        SummaryDetails = EvidenceSummaryDisplayFormatter.Format(evidence.Summary);
    }

    public EvidenceItem Evidence { get; }
    public string Kind => Evidence.Kind;
    public string Title => Evidence.Title;
    public string DisplayTimelineTime => Evidence.DisplayTimelineTime;
    public string DisplayKind => Evidence.DisplayKind;
    public string DisplayWindowInfo => Evidence.DisplayWindowInfo;
    public string DisplayTags => Evidence.DisplayTags;
    public string OcrText => Evidence.OcrText;
    public bool CanPreviewImage => Evidence.CanPreviewImage;
    public EvidenceSummaryDisplay SummaryDetails { get; }

    public bool Included
    {
        get => Evidence.Included;
        set => Evidence.Included = value;
    }
}
