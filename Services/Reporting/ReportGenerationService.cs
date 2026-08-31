using Timenotifier.Models;
using Timenotifier.Services.Evidence;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Settings;

namespace Timenotifier.Services.Reporting;

public sealed class ReportGenerationService(
    CompositeEvidenceCollector evidenceCollector,
    IEvidenceStore evidenceStore,
    IAppSettingsStore settingsStore,
    IModelClient modelClient,
    ReportPromptBuilder promptBuilder)
{
    private readonly EvidenceClusterer _clusterer = new();

    public async Task<(ReportDraft Draft, IReadOnlyList<EvidenceItem> Evidence)> GenerateAsync(
        string reportType,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        var evidence = await evidenceCollector.CollectAndStoreAsync(
            new EvidenceRange(startDate, endDate),
            cancellationToken);
        var result = await GenerateFromEvidenceAsync(reportType, startDate, endDate, evidence, null, cancellationToken);
        return (result.Draft, result.Evidence);
    }

    public async Task<(ReportDraft Draft, IReadOnlyList<EvidenceItem> Evidence, IReadOnlyList<EvidenceCluster> Clusters)> GenerateFromEvidenceAsync(
        string reportType,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<EvidenceItem> evidence,
        ReportTemplate? template,
        CancellationToken cancellationToken = default)
    {
        await evidenceStore.UpsertEvidenceAsync(evidence, cancellationToken);
        var included = evidence.Where(item => item.Included).OrderBy(item => item.CapturedAt).ToList();
        var clusters = _clusterer.Cluster(included);
        var sourceDailyDrafts = reportType == ReportTypes.Weekly
            ? await evidenceStore.ListReportDraftsAsync(
                ReportTypes.Daily,
                startDate,
                endDate,
                cancellationToken)
            : [];
        sourceDailyDrafts = sourceDailyDrafts
            .Where(draft => draft.Status == ReportStatuses.Confirmed)
            .OrderBy(draft => draft.StartDate)
            .ToList();
        var prompt = reportType == ReportTypes.Weekly
            ? promptBuilder.BuildWeeklyDigestPrompt(startDate, endDate, included, template, clusters, sourceDailyDrafts)
            : promptBuilder.Build(reportType, startDate, endDate, included, template, clusters, sourceDailyDrafts);
        var settings = await settingsStore.LoadModelSettingsAsync(cancellationToken);
        var content = await modelClient.GenerateAsync(settings, prompt, included, cancellationToken);
        if (settings.QualityMode == ModelQualityMode.HighQuality)
        {
            try
            {
                content = await modelClient.PolishReportAsync(settings, content, included, cancellationToken);
            }
            catch (Exception ex)
            {
                content = $"{content}{Environment.NewLine}{Environment.NewLine}【提示】高质量润色失败，已保留初稿：{ResponsesModelClient.FlattenException(ex)}";
            }
        }

        var draft = new ReportDraft
        {
            Type = reportType,
            StartDate = startDate,
            EndDate = endDate,
            Content = content,
            EvidenceIds = included.Select(item => item.Id).ToList(),
            EvidenceClusterIds = clusters.Select(cluster => cluster.Id).ToList(),
            SourceDailyDraftIds = sourceDailyDrafts.Select(draft => draft.Id).ToList(),
            TemplateId = template?.Id ?? "",
            TemplateName = template?.Name ?? "",
            Status = ReportStatuses.Draft,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        ApplyStructuredFields(draft);

        await evidenceStore.SaveReportDraftAsync(draft, cancellationToken);
        return (draft, evidence, clusters);
    }

    public Task SaveDraftAsync(ReportDraft draft, CancellationToken cancellationToken = default)
    {
        ApplyStructuredFields(draft);
        return evidenceStore.SaveReportDraftAsync(draft, cancellationToken);
    }

    private static void ApplyStructuredFields(ReportDraft draft)
    {
        if (draft.Type == ReportTypes.Weekly)
        {
            var sections = WeeklyReportSectionParser.Parse(draft.Content);
            draft.WorkSummary = string.IsNullOrWhiteSpace(draft.WorkSummary) ? sections.WorkSummary : draft.WorkSummary;
            draft.Problems = string.IsNullOrWhiteSpace(draft.Problems) ? sections.Problems : draft.Problems;
            draft.NextWeekPlan = string.IsNullOrWhiteSpace(draft.NextWeekPlan) ? sections.NextWeekPlan : draft.NextWeekPlan;
            draft.SupportAndSuggestions = string.IsNullOrWhiteSpace(draft.SupportAndSuggestions)
                ? sections.SupportAndSuggestions
                : draft.SupportAndSuggestions;
            draft.Content = WeeklyReportSectionParser.BuildWeeklyMarkdown(
                draft.WorkSummary,
                draft.Problems,
                draft.NextWeekPlan,
                draft.SupportAndSuggestions);
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.DailySummary))
        {
            draft.DailySummary = WeeklyReportSectionParser.ParseDailySummary(draft.Content);
        }
        draft.Content = WeeklyReportSectionParser.BuildDailyMarkdown(draft.DailySummary);
    }
}
