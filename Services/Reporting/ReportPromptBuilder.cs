using Timenotifier.Models;

namespace Timenotifier.Services.Reporting;

public sealed class ReportPromptBuilder
{
    private const int WeeklySummaryLimit = 180;
    private const int DailySummaryLimit = 480;

    public string Build(string reportType, DateOnly startDate, DateOnly endDate, IReadOnlyList<EvidenceItem> evidence)
    {
        return Build(reportType, startDate, endDate, evidence, null, [], []);
    }

    public string Build(
        string reportType,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<EvidenceItem> evidence,
        ReportTemplate? template,
        IReadOnlyList<EvidenceCluster> clusters,
        IReadOnlyList<ReportDraft> sourceDailyDrafts)
    {
        var label = reportType == ReportTypes.Weekly ? "周报" : "日报";
        var included = evidence.Where(item => item.Included).OrderBy(item => item.CapturedAt).ToList();
        var lines = included.Count == 0
            ? "无可用证据。"
            : reportType == ReportTypes.Weekly
                ? FormatWeeklyEvidence(included, clusters)
                : string.Join(Environment.NewLine + Environment.NewLine, included.Select(FormatDailyEvidence));
        var templateInstructions = FormatTemplateInstructions(template);
        var dailyDraftReferences = FormatSourceDailyDrafts(sourceDailyDrafts);

        var requirements = reportType == ReportTypes.Weekly
            ? """
            1. 不要编造证据中没有的信息。
            2. 对不确定或缺失的信息标注“待确认”。
            3. 输出中文 Markdown，固定使用这些二级标题：本周学习/工作总结、下周学习/工作计划、工作中遇到的问题、所需要的支持、解决问题措施及建议。
            4. 每个小节输出 1-3 条编号内容，编号格式使用“1、”。
            5. 本周总结只写本周已经完成或学习到的内容；下周计划只写能从证据合理延伸的计划，信息不足写“待确认”。
            6. 问题/支持/措施小节需要把问题、需要的支持、解决措施或建议合并表达；没有可确认问题时写“暂无可确认问题”。
            7. Codex 证据代表一个完整对话，会包含“目标概览”和“工作结果概括”；需要综合整个对话的多项工作，不要只取最后一条记录。
            8. 不输出证据来源小节，不强制保留证据编号，整体适合粘贴到 Excel 周报单元格。
            """
            : """
            1. 不要编造证据中没有的信息。
            2. 对不确定或缺失的信息标注“待确认”。
            3. 只输出一个二级标题：今日工作内容简述。
            4. 标题下输出 3-5 条最高价值编号内容，编号格式使用“1、”。
            5. 只保留对工作汇报有价值的成果和学习收获，多个相近事项合并成一条。
            6. 每条固定写成“动作/成果 + 业务或学习价值”，避免堆叠“便于”“用于观察”这类空泛表达。
            7. 过滤工具调用、路径、打包、纯测试通过、命令正常退出、点击按钮等过程性信息；验证结论只能合并到对应成果中，不单独成条。
            8. 只写今日做了什么、产出了什么、推进了什么；不写证据来源、不写问题计划小节。
            9. 不要输出“代码评审、技术风险和下一步计划暂无可确认内容”等低价值补尾；没有内容就省略。
            10. 无证据或信息不足时写“暂无可确认内容”，不要编造计划或成果。
            11. Codex 证据代表一个完整对话，会包含“目标概览”和“工作结果概括”；需要综合整个对话的多项工作，不要只取最后一条记录。
            """;

        return $"""
            你是一个严谨的工作报告助手。请只根据下面的证据生成{label}草稿，风格为职场简洁，可直接复制提交。

            时间范围：{startDate:yyyy-MM-dd} 至 {endDate:yyyy-MM-dd}
            {templateInstructions}

            要求：
            {requirements}

            证据：
            {lines}
            {dailyDraftReferences}
            """;
    }

    public string BuildWeeklySegmentPrompt(
        DateOnly segmentDate,
        IReadOnlyList<EvidenceItem> evidence,
        int evidenceStartIndex)
    {
        var lines = evidence.Count == 0
            ? "无可用证据。"
            : FormatWeeklyEvidence(evidence, evidenceStartIndex);

        return $"""
            你是一个严谨的工作报告助手。请只根据下面这一天的证据生成“日摘要”，用于后续合成周报，风格为职场简洁。

            日期：{segmentDate:yyyy-MM-dd}

            要求：
            1. 不要编造证据中没有的信息。
            2. 对不确定或缺失的信息标注“待确认”。
            3. 输出中文 Markdown，控制在 250 字以内。
            4. 按同类工作合并归纳，避免逐条复述证据或写成流水账。
            5. 每条使用“结果/动作 + 影响/产出 + 证据编号”的写法，并保留证据编号，例如 [E{evidenceStartIndex}]。
            6. 无证据或信息不足时写“暂无可确认内容”，不要编造计划或成果。
            7. Codex 证据代表一个完整对话，应综合“目标概览”和“工作结果概括”的多项工作，不要只取最后一条记录。

            证据：
            {lines}
            """;
    }

    public string BuildWeeklyFromSegmentsPrompt(
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<string> segmentSummaries)
    {
        var summaries = segmentSummaries.Count == 0
            ? "无可用日摘要。"
            : string.Join(Environment.NewLine + Environment.NewLine, segmentSummaries);

        return $"""
            你是一个严谨的工作报告助手。请只根据下面的日摘要合成本周周报草稿，风格为职场简洁，可直接复制提交。

            时间范围：{startDate:yyyy-MM-dd} 至 {endDate:yyyy-MM-dd}

            要求：
            1. 不要编造日摘要中没有的信息。
            2. 对不确定或缺失的信息标注“待确认”。
            3. 输出中文 Markdown，固定使用这些二级标题：本周学习/工作总结、下周学习/工作计划、工作中遇到的问题、所需要的支持、解决问题措施及建议。
            4. 每个小节输出 1-3 条编号内容，编号格式使用“1、”。
            5. 按同类工作合并归纳，避免逐日罗列或写成流水账。
            6. 无证据或信息不足时，在对应小节写“暂无可确认内容”，不要编造计划或成果。

            日摘要：
            {summaries}
            """;
    }

    public string BuildWeeklyDigestPrompt(
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<EvidenceItem> evidence)
    {
        return BuildWeeklyDigestPrompt(startDate, endDate, evidence, null, [], []);
    }

    public string BuildWeeklyDigestPrompt(
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<EvidenceItem> evidence,
        ReportTemplate? template,
        IReadOnlyList<EvidenceCluster> clusters,
        IReadOnlyList<ReportDraft> sourceDailyDrafts)
    {
        var ordered = evidence.OrderBy(item => item.CapturedAt).ToList();
        var digest = ordered.Count == 0
            ? "无可用证据。"
            : string.Join(
                Environment.NewLine,
                ordered
                    .Select((item, index) => (Item: item, Index: index + 1))
                    .GroupBy(pair => DateOnly.FromDateTime(pair.Item.CapturedAt.Date))
                    .Select(group =>
                    {
                        var items = string.Join(
                            "；",
                            group.Select(pair =>
                                $"[E{pair.Index}] {Clean(pair.Item.Title, 28)}：{Clean(pair.Item.Summary, 36)}"));
                        return $"【{group.Key:yyyy-MM-dd}】{items}";
                    }));
        var templateInstructions = FormatTemplateInstructions(template);
        var clusterLines = FormatClusters(clusters);
        var dailyDraftReferences = FormatSourceDailyDrafts(sourceDailyDrafts);

        return $"""
            你是一个严谨的工作报告助手。请根据下面的“本周已确认日报引用”和“本周已选证据摘要”生成周报草稿，风格为职场简洁，可直接复制提交。

            时间范围：{startDate:yyyy-MM-dd} 至 {endDate:yyyy-MM-dd}
            {templateInstructions}

            要求：
            1. 优先使用已确认日报；证据摘要只作为补充材料，不要覆盖日报中更明确的内容。
            2. 对不确定或缺失的信息标注“待确认”。
            3. 输出中文 Markdown，固定使用这些二级标题：本周学习/工作总结、下周学习/工作计划、工作中遇到的问题、所需要的支持、解决问题措施及建议。
            4. 每个小节输出 1-3 条编号内容，编号格式使用“1、”。
            5. 本周总结只写本周已经完成或学习到的内容；下周计划只写能从证据合理延伸的计划，信息不足写“待确认”。
            6. 问题/支持/措施小节需要把问题、需要的支持、解决措施或建议合并表达；没有可确认问题时写“暂无可确认问题”。
            7. Codex 证据代表一个完整对话，会包含“目标概览”和“工作结果概括”；需要综合整个对话的多项工作，不要只取最后一条记录。
            8. 不输出证据来源小节，不强制保留证据编号，整体适合粘贴到 Excel 周报单元格。
            9. 总长度控制在 800 字以内。

            本周已选证据摘要：
            {digest}
            {clusterLines}
            {dailyDraftReferences}
            """;
    }

    private static string FormatWeeklyEvidence(IReadOnlyList<EvidenceItem> evidence)
    {
        return FormatWeeklyEvidence(evidence, 1);
    }

    private static string FormatWeeklyEvidence(IReadOnlyList<EvidenceItem> evidence, IReadOnlyList<EvidenceCluster> clusters)
    {
        if (clusters.Count == 0)
        {
            return FormatWeeklyEvidence(evidence, 1);
        }

        var itemMap = evidence.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var indexMap = evidence
            .Select((item, index) => (item.Id, Index: index + 1))
            .ToDictionary(pair => pair.Id, pair => pair.Index, StringComparer.OrdinalIgnoreCase);
        return string.Join(
            Environment.NewLine,
            clusters.Select(cluster =>
            {
                var items = cluster.EvidenceIds
                    .Where(itemMap.ContainsKey)
                    .Select(id =>
                    {
                        var item = itemMap[id];
                        return $"[E{indexMap[id]}] {item.CapturedAt:MM-dd HH:mm} {item.DisplayKind} | {Clean(item.Title, 60)} | {Clean(item.Summary, WeeklySummaryLimit)}";
                    });
                return $"【{cluster.Name}】{Environment.NewLine}{string.Join(Environment.NewLine, items)}";
            }));
    }

    private static string FormatWeeklyEvidence(IReadOnlyList<EvidenceItem> evidence, int startIndex)
    {
        return string.Join(
            Environment.NewLine,
            evidence.Select((item, index) =>
                $"[E{startIndex + index}] {item.CapturedAt:MM-dd HH:mm} {item.DisplayKind} | {Clean(item.Title, 80)} | {Clean(item.Summary, WeeklySummaryLimit)}"));
    }

    private static string FormatDailyEvidence(EvidenceItem item, int index)
    {
        var path = item.ImagePath ?? item.SourcePath;
        var pathLine = string.IsNullOrWhiteSpace(path) ? "" : $"{Environment.NewLine}路径：{path}";
        return $"""
            [E{index + 1}]
            类型：{item.DisplayKind}
            时间：{item.CapturedAt:yyyy-MM-dd HH:mm:ss}
            标题：{item.Title}
            摘要：{Clean(item.Summary, DailySummaryLimit)}{pathLine}
            """;
    }

    private static string FormatTemplateInstructions(ReportTemplate? template)
    {
        if (template is null || string.IsNullOrWhiteSpace(template.PromptInstructions))
        {
            return "";
        }

        return $"{Environment.NewLine}报告模板：{template.Name}{Environment.NewLine}模板要求：{template.PromptInstructions}";
    }

    private static string FormatClusters(IReadOnlyList<EvidenceCluster> clusters)
    {
        if (clusters.Count == 0)
        {
            return "";
        }

        var lines = string.Join(
            Environment.NewLine,
            clusters.Select(cluster => $"- {cluster.Name}：{string.Join(", ", cluster.EvidenceIds)}"));
        return $"{Environment.NewLine}{Environment.NewLine}证据聚类：{Environment.NewLine}{lines}";
    }

    private static string FormatSourceDailyDrafts(IReadOnlyList<ReportDraft> drafts)
    {
        var confirmed = drafts
            .Where(draft => draft.Status == ReportStatuses.Confirmed && !string.IsNullOrWhiteSpace(draft.Content))
            .OrderBy(draft => draft.StartDate)
            .ToList();
        if (confirmed.Count == 0)
        {
            return "";
        }

        var lines = string.Join(
            Environment.NewLine + Environment.NewLine,
            confirmed.Select(draft => $"【{draft.StartDate:yyyy-MM-dd} 已确认日报】{Environment.NewLine}{Clean(draft.Content, 900)}"));
        return $"{Environment.NewLine}{Environment.NewLine}本周已确认日报引用：{Environment.NewLine}{lines}";
    }

    private static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "无";
        }

        var text = value
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ");
        }

        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }
}
