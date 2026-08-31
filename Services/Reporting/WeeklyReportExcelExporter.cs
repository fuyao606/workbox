using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Timenotifier.Models;

namespace Timenotifier.Services.Reporting;

public sealed class WeeklyReportExcelExporter(string? templateRoot = null, string? workbookStateRoot = null)
{
    private const string EnterpriseTemplateRelativePath = "Templates/enterprise-weekly-report-template.xlsx";
    private const int EnterpriseTemplateCompletedWeekCount = 0;
    private const string SchoolTemplateRelativePath = "Templates/school-weekly-report-template.xlsx";
    private const string EnterpriseOutputBaseName = "enterprise-weekly-report";
    private const string SchoolOutputBaseName = "school-weekly-report";
    private static readonly DateOnly EnterpriseHireDate = InternshipWeekCalculator.FirstWeekStart;
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public string ExportEnterprise(
        ReportDraft draft,
        string? outputDirectory = null,
        IReadOnlyList<ReportDraft>? reportHistory = null)
    {
        if (draft.Type != ReportTypes.Weekly)
        {
            throw new InvalidOperationException("只有周报草稿可以导出 Excel。");
        }

        var template = ResolveEnterpriseTemplate();
        if (!File.Exists(template.Path))
        {
            throw new FileNotFoundException("未找到企业周报 Excel 模板。", template.Path);
        }

        var statePath = PrepareStateWorkbook(
            template.Path,
            template.RelativePath,
            rebuildFromTemplate: reportHistory is not null);
        foreach (var reportDraft in SelectDraftsThroughCurrentWeek(draft, reportHistory))
        {
            WriteEnterpriseDraftToWorkbook(statePath, reportDraft, template.PreservesCompletedWeeks);
        }

        var directory = ResolveOutputDirectory(outputDirectory);
        var outputPath = BuildOutputPath(directory, EnterpriseOutputBaseName);
        File.Copy(statePath, outputPath, overwrite: true);
        return outputPath;
    }

    public string ExportSchool(
        ReportDraft draft,
        string? outputDirectory = null,
        IReadOnlyList<ReportDraft>? reportHistory = null)
    {
        if (draft.Type != ReportTypes.Weekly)
        {
            throw new InvalidOperationException("只有周报草稿可以导出 Excel。");
        }

        var templatePath = ResolveTemplatePath(SchoolTemplateRelativePath);
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("未找到学校周报 Excel 模板。", templatePath);
        }

        var statePath = PrepareStateWorkbook(
            templatePath,
            SchoolTemplateRelativePath,
            rebuildFromTemplate: reportHistory is not null);
        foreach (var reportDraft in SelectDraftsThroughCurrentWeek(draft, reportHistory))
        {
            WriteSchoolDraftToWorkbook(statePath, reportDraft);
        }

        var directory = ResolveOutputDirectory(outputDirectory);
        var week = InternshipWeekCalculator.GetWeekForStart(draft.StartDate);
        var outputPath = BuildOutputPath(directory, $"{SchoolOutputBaseName}-{week.WeekLabel}");
        File.Copy(statePath, outputPath, overwrite: true);
        return outputPath;
    }

    private static void WriteEnterpriseDraftToWorkbook(
        string workbookPath,
        ReportDraft draft,
        bool preservesCompletedWeeks)
    {
        var week = InternshipWeekCalculator.GetWeekForStart(draft.StartDate);
        if (preservesCompletedWeeks && week.WeekNumber <= EnterpriseTemplateCompletedWeekCount)
        {
            return;
        }

        using var archive = OpenWorkbook(workbookPath, out var sharedStrings, out var sheet);
        var strings = sharedStrings.Root ?? throw new InvalidDataException("sharedStrings.xml 格式异常。");
        var sections = WeeklyReportSectionParser.FromDraft(draft);

        var row = 1 + week.WeekNumber;
        EnsureEnterpriseRow(sheet, row);

        SetSharedString(sheet, strings, $"A{row}", week.ChineseWeekLabel);
        SetSharedString(sheet, strings, $"B{row}", week.DateRangeLabel);
        if (!preservesCompletedWeeks)
        {
            SetNumericValue(sheet, "D2", EnterpriseHireDate.ToDateTime(TimeOnly.MinValue).ToOADate());
        }
        SetSharedString(sheet, strings, $"I{row}", sections.WorkSummary);
        SetSharedString(sheet, strings, $"J{row}", sections.NextWeekPlan);
        SetSharedString(sheet, strings, $"K{row}", sections.ProblemsAndSupport);

        SaveXml(archive, "xl/sharedStrings.xml", sharedStrings);
        SaveXml(archive, "xl/worksheets/sheet1.xml", sheet);
    }

    private static void EnsureEnterpriseRow(XDocument sheet, int rowNumber)
    {
        var root = sheet.Root ?? throw new InvalidDataException("工作表 XML 格式异常。");
        var sheetData = root.Element(SpreadsheetNs + "sheetData")
            ?? throw new InvalidDataException("工作表缺少 sheetData。");
        var rows = sheetData.Elements(SpreadsheetNs + "row").ToList();
        var existingRow = rows.FirstOrDefault(row => GetRowNumber((string?)row.Attribute("r")) == rowNumber);
        if (existingRow is not null)
        {
            return;
        }

        var templateRow = rows.LastOrDefault()
            ?? throw new InvalidDataException("企业周报模板缺少可复制的行样式。");
        var newRow = new XElement(templateRow);
        newRow.SetAttributeValue("r", rowNumber);
        foreach (var cell in newRow.Elements(SpreadsheetNs + "c"))
        {
            var column = GetColumnName((string?)cell.Attribute("r"));
            cell.SetAttributeValue("r", $"{column}{rowNumber}");
            cell.Attribute("t")?.Remove();
            cell.Elements(SpreadsheetNs + "f").Remove();
            cell.Elements(SpreadsheetNs + "is").Remove();
            cell.Elements(SpreadsheetNs + "v").Remove();
        }

        sheetData.Add(newRow);

        var dimension = root.Element(SpreadsheetNs + "dimension");
        if (dimension is not null)
        {
            var currentReference = (string?)dimension.Attribute("ref") ?? $"A1:K{rowNumber}";
            var firstCell = currentReference.Split(':')[0];
            var lastCell = currentReference.Split(':').Last();
            var lastColumn = GetColumnName(lastCell);
            var lastRow = Math.Max(GetRowNumber(lastCell), rowNumber);
            dimension.SetAttributeValue("ref", $"{firstCell}:{lastColumn}{lastRow}");
        }

        var mergeCells = root.Element(SpreadsheetNs + "mergeCells");
        if (mergeCells is null)
        {
            return;
        }

        foreach (var mergeCell in mergeCells.Elements(SpreadsheetNs + "mergeCell"))
        {
            var references = ((string?)mergeCell.Attribute("ref"))?.Split(':');
            if (references is not { Length: 2 })
            {
                continue;
            }

            var startColumn = GetColumnName(references[0]);
            var endColumn = GetColumnName(references[1]);
            if (!string.Equals(startColumn, endColumn, StringComparison.OrdinalIgnoreCase)
                || GetRowNumber(references[0]) != 2
                || !"CDEFGH".Contains(startColumn, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (GetRowNumber(references[1]) < rowNumber)
            {
                mergeCell.SetAttributeValue("ref", $"{startColumn}2:{endColumn}{rowNumber}");
            }
        }
    }

    private static int GetRowNumber(string? cellReference)
    {
        if (string.IsNullOrWhiteSpace(cellReference))
        {
            return 0;
        }

        var digits = new string(cellReference.SkipWhile(char.IsLetter).ToArray());
        return int.TryParse(digits, out var rowNumber) ? rowNumber : 0;
    }

    private static string GetColumnName(string? cellReference)
    {
        if (string.IsNullOrWhiteSpace(cellReference))
        {
            return "A";
        }

        return new string(cellReference.TakeWhile(char.IsLetter).ToArray());
    }

    private static void WriteSchoolDraftToWorkbook(string workbookPath, ReportDraft draft)
    {
        using var archive = OpenWorkbook(workbookPath, out var sharedStrings, out var sheet);
        var strings = sharedStrings.Root ?? throw new InvalidDataException("sharedStrings.xml 格式异常。");
        var sections = WeeklyReportSectionParser.FromDraft(draft);
        var week = InternshipWeekCalculator.GetWeekForStart(draft.StartDate);
        var row = 8 + week.WeekNumber;

        SetSharedString(sheet, strings, $"A{row}", week.WeekLabel);
        SetSharedString(sheet, strings, $"B{row}", week.DateRangeLabel);
        SetSharedString(sheet, strings, $"C{row}", sections.WorkSummary);
        SetSharedString(sheet, strings, $"E{row}", sections.Problems);
        SetSharedString(sheet, strings, $"F{row}", sections.NextWeekPlan);

        NormalizeDocumentFonts(sharedStrings);
        NormalizeDocumentFonts(sheet);
        NormalizeWorkbookFonts(archive);
        SaveXml(archive, "xl/sharedStrings.xml", sharedStrings);
        SaveXml(archive, "xl/worksheets/sheet1.xml", sheet);
    }

    private static void NormalizeDocumentFonts(XDocument document)
    {
        foreach (var element in document.Descendants())
        {
            if (element.Name == SpreadsheetNs + "rFont" || element.Name == SpreadsheetNs + "name")
            {
                element.SetAttributeValue("val", "微软雅黑");
            }
        }
    }

    private static void NormalizeWorkbookFonts(ZipArchive archive)
    {
        NormalizeXmlFonts(archive, "xl/styles.xml");
        NormalizeXmlFonts(archive, "xl/sharedStrings.xml");
        foreach (var entryName in archive.Entries
                     .Select(entry => entry.FullName)
                     .Where(name => name.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                         && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            NormalizeXmlFonts(archive, entryName);
        }
    }

    private static void NormalizeXmlFonts(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            return;
        }

        var document = LoadXml(entry);
        var before = document.ToString(SaveOptions.DisableFormatting);
        NormalizeDocumentFonts(document);

        if (!string.Equals(before, document.ToString(SaveOptions.DisableFormatting), StringComparison.Ordinal))
        {
            SaveXml(archive, entryName, document);
        }
    }

    private static ZipArchive OpenWorkbook(string workbookPath, out XDocument sharedStrings, out XDocument sheet)
    {
        var archive = ZipFile.Open(workbookPath, ZipArchiveMode.Update);
        try
        {
            var sharedStringsEntry = archive.GetEntry("xl/sharedStrings.xml")
                ?? throw new InvalidDataException("模板缺少 sharedStrings.xml。");
            var sheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml")
                ?? throw new InvalidDataException("模板缺少 Sheet1。");

            sharedStrings = LoadXml(sharedStringsEntry);
            sheet = LoadXml(sheetEntry);
            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private static void SetSharedString(XDocument sheet, XElement sharedStrings, string cellReference, string value)
    {
        var cell = sheet
            .Descendants(SpreadsheetNs + "c")
            .FirstOrDefault(element => string.Equals((string?)element.Attribute("r"), cellReference, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"模板缺少单元格 {cellReference}。");

        cell.SetAttributeValue("t", "s");
        cell.Elements(SpreadsheetNs + "v").Remove();
        cell.Add(new XElement(SpreadsheetNs + "v", AppendSharedString(sharedStrings, value)));
    }

    private static void SetNumericValue(XDocument sheet, string cellReference, double value)
    {
        var cell = sheet
            .Descendants(SpreadsheetNs + "c")
            .FirstOrDefault(element => string.Equals((string?)element.Attribute("r"), cellReference, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"模板缺少单元格 {cellReference}。");

        cell.SetAttributeValue("t", null);
        cell.Elements(SpreadsheetNs + "v").Remove();
        cell.Add(new XElement(SpreadsheetNs + "v", value.ToString(CultureInfo.InvariantCulture)));
    }

    private static int AppendSharedString(XElement sharedStrings, string value)
    {
        var count = sharedStrings.Elements(SpreadsheetNs + "si").Count();
        sharedStrings.Add(new XElement(
            SpreadsheetNs + "si",
            new XElement(SpreadsheetNs + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), value ?? "")));
        sharedStrings.SetAttributeValue("count", count + 1);
        sharedStrings.SetAttributeValue("uniqueCount", count + 1);
        return count;
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static void SaveXml(ZipArchive archive, string entryName, XDocument document)
    {
        archive.GetEntry(entryName)?.Delete();
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        document.Save(writer, SaveOptions.DisableFormatting);
    }

    private static string ResolveOutputDirectory(string? outputDirectory)
    {
        var directory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : Environment.ExpandEnvironmentVariables(outputDirectory);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private string ResolveTemplatePath(string templateRelativePath)
    {
        if (!string.IsNullOrWhiteSpace(templateRoot))
        {
            return Path.Combine(templateRoot, templateRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        var baseDirectory = AppContext.BaseDirectory;
        var deployed = Path.Combine(baseDirectory, templateRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(deployed))
        {
            return deployed;
        }

        var source = Path.Combine(Directory.GetCurrentDirectory(), templateRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return source;
    }

    private (string Path, string RelativePath, bool PreservesCompletedWeeks) ResolveEnterpriseTemplate()
    {
        var preferredPath = ResolveTemplatePath(EnterpriseTemplateRelativePath);
        if (File.Exists(preferredPath))
        {
            return (preferredPath, EnterpriseTemplateRelativePath, true);
        }

        return (preferredPath, EnterpriseTemplateRelativePath, false);
    }

    private static IReadOnlyList<ReportDraft> SelectDraftsThroughCurrentWeek(
        ReportDraft currentDraft,
        IReadOnlyList<ReportDraft>? reportHistory)
    {
        if (reportHistory is null)
        {
            return [currentDraft];
        }

        var currentWeekNumber = InternshipWeekCalculator.GetWeekForStart(currentDraft.StartDate).WeekNumber;
        Dictionary<int, ReportDraft> latestByWeek = [];
        foreach (var draft in reportHistory.Where(draft => draft.Type == ReportTypes.Weekly))
        {
            var weekNumber = InternshipWeekCalculator.GetWeekForStart(draft.StartDate).WeekNumber;
            if (weekNumber > currentWeekNumber)
            {
                continue;
            }

            if (!latestByWeek.TryGetValue(weekNumber, out var existing)
                || draft.UpdatedAt > existing.UpdatedAt)
            {
                latestByWeek[weekNumber] = draft;
            }
        }

        latestByWeek[currentWeekNumber] = currentDraft;
        return latestByWeek
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .ToList();
    }

    private string PrepareStateWorkbook(
        string templatePath,
        string templateRelativePath,
        bool rebuildFromTemplate)
    {
        var statePath = ResolveStateWorkbookPath(templateRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        if (rebuildFromTemplate || !File.Exists(statePath))
        {
            File.Copy(templatePath, statePath, overwrite: rebuildFromTemplate);
        }

        return statePath;
    }

    private string ResolveStateWorkbookPath(string templateRelativePath)
    {
        var root = string.IsNullOrWhiteSpace(workbookStateRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Timenotifier",
                "weekly-report-workbooks")
            : Environment.ExpandEnvironmentVariables(workbookStateRoot);
        return Path.Combine(root, templateRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string BuildUniqueOutputPath(string directory, string baseName)
    {
        var safeBaseName = SanitizeFileName(baseName);
        var path = Path.Combine(directory, safeBaseName + ".xlsx");
        if (!File.Exists(path))
        {
            return path;
        }

        for (var index = 2; index < 100; index++)
        {
            var candidate = Path.Combine(directory, $"{safeBaseName}-{index}.xlsx");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{SanitizeFileName(baseName)}-{DateTime.Now:HHmmss}.xlsx");
    }

    private static string BuildOutputPath(string directory, string baseName)
    {
        return Path.Combine(directory, SanitizeFileName(baseName) + ".xlsx");
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(fileName.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
    }
}
