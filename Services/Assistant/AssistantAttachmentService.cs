using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;
using SkiaSharp;
using Timenotifier.Models;
using UglyToad.PdfPig;

namespace Timenotifier.Services.Assistant;

public sealed class AssistantAttachmentException(string message) : InvalidOperationException(message);

public sealed class PreparedAssistantAttachment
{
    private readonly string _availableExtractedText;
    private readonly bool _wasIndividuallyTruncated;
    private readonly bool _legacyIsImage;

    public PreparedAssistantAttachment(
        AssistantAttachmentMetadata metadata,
        string extractedText,
        ModelImageInput? imageInput = null,
        bool wasIndividuallyTruncated = false)
    {
        Metadata = metadata;
        _availableExtractedText = extractedText;
        _wasIndividuallyTruncated = wasIndividuallyTruncated;
        ExtractedText = extractedText;
        ImageInput = imageInput;
        ApplyTextLimit(extractedText.Length);
    }

    // Retained only to keep already-referenced callers source-compatible. The path is deliberately discarded.
    public PreparedAssistantAttachment(
        AssistantAttachmentMetadata metadata,
        string sourcePath,
        string extractedText,
        bool isImage)
        : this(metadata, extractedText)
    {
        _legacyIsImage = isImage;
    }

    public AssistantAttachmentMetadata Metadata { get; }
    public string ExtractedText { get; private set; }
    public ModelImageInput? ImageInput { get; }
    public bool IsImage => ImageInput is not null || _legacyIsImage;
    public bool IsTextTruncated { get; private set; }
    public string TruncationMessage { get; private set; } = "";

    // The current model gateway still references this member internally. It never exposes or retains the input path.
    internal string SourcePath => "";

    internal void ApplyTextLimit(int availableCharacters)
    {
        var length = Math.Min(Math.Max(availableCharacters, 0), _availableExtractedText.Length);
        ExtractedText = _availableExtractedText[..length];
        IsTextTruncated = _wasIndividuallyTruncated || length < _availableExtractedText.Length;
        TruncationMessage = IsTextTruncated
            ? "附件文本已截断至 80,000 个字符。"
            : "";
    }
}

public interface IAssistantAttachmentService
{
    Task<PreparedAssistantAttachment> PrepareAsync(string path, CancellationToken cancellationToken = default);
    void ValidateBatch(IReadOnlyCollection<PreparedAssistantAttachment> attachments);
}

public sealed class AssistantAttachmentService : IAssistantAttachmentService
{
    public const int MaxFileCount = 5;
    public const long MaxFileBytes = 20L * 1024 * 1024;
    public const long MaxBatchBytes = 50L * 1024 * 1024;
    public const int MaxImageCount = 2;
    public const int MaxExtractedCharacters = 80_000;
    public const int MaxImageEdge = 1280;
    public const int MaxImageDimension = 32_768;
    public const long MaxDecodedPixels = 32_000_000;
    public const long MaxOfficeEntryUncompressedBytes = 16L * 1024 * 1024;
    public const long MaxOfficeTotalUncompressedBytes = 64L * 1024 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    private static readonly byte[] Rar4Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
    private static readonly byte[] Rar5Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
    private const string TruncationMessage = "附件文本已截断至 80,000 个字符。";

    public async Task<PreparedAssistantAttachment> PrepareAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new AssistantAttachmentException("附件不存在或已被移动。");
        }

        FileInfo file;
        try
        {
            file = new FileInfo(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new AssistantAttachmentException("附件路径无效。");
        }

        long sourceLength;
        try
        {
            file.Refresh();
            sourceLength = file.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new AssistantAttachmentException("附件读取失败。");
        }

        if (sourceLength > MaxFileBytes)
        {
            throw new AssistantAttachmentException("单个附件不能超过 20 MiB。");
        }

        var extension = file.Extension.ToLowerInvariant();
        if (extension is ".docm" or ".dotm" or ".xlsm" or ".xltm" or ".xlam" or ".pptm" or ".potm" or ".ppsm")
        {
            throw new AssistantAttachmentException("禁止添加含宏的 Office 文件。");
        }

        if (!IsSupportedExtension(extension))
        {
            throw new AssistantAttachmentException("不支持该附件格式。");
        }

        byte[] bytes;
        try
        {
            bytes = await ReadFileBoundedAsync(file.FullName, sourceLength, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new AssistantAttachmentException("附件读取失败。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.LongLength > MaxFileBytes)
        {
            throw new AssistantAttachmentException("单个附件不能超过 20 MiB。");
        }
        RejectDangerousOrMismatchedContainer(bytes, extension);
        cancellationToken.ThrowIfCancellationRequested();

        var prepared = extension switch
        {
            ".txt" or ".md" or ".json" or ".csv" => PrepareText(file, extension, bytes, cancellationToken),
            ".png" or ".jpg" or ".jpeg" or ".webp" => PrepareImage(file, extension, bytes, cancellationToken),
            ".pdf" => PreparePdf(file, bytes, cancellationToken),
            ".docx" => PrepareDocx(file, bytes, cancellationToken),
            ".xlsx" => PrepareXlsx(file, bytes, cancellationToken),
            ".pptx" => PreparePptx(file, bytes, cancellationToken),
            _ => throw new AssistantAttachmentException("不支持该附件格式。")
        };
        cancellationToken.ThrowIfCancellationRequested();
        return prepared;
    }

    public void ValidateBatch(IReadOnlyCollection<PreparedAssistantAttachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        var ordered = attachments.ToList();
        if (ordered.Count > MaxFileCount)
        {
            throw new AssistantAttachmentException("每条消息最多添加 5 个附件。");
        }

        if (ordered.Any(item => item.Metadata.SizeBytes < 0 || item.Metadata.SizeBytes > MaxFileBytes))
        {
            throw new AssistantAttachmentException("单个附件不能超过 20 MiB。");
        }

        if (ExceedsLimit(ordered.Select(item => item.Metadata.SizeBytes), MaxBatchBytes))
        {
            throw new AssistantAttachmentException("每条消息的附件总大小不能超过 50 MiB。");
        }

        if (ordered.Count(item => item.IsImage) > MaxImageCount)
        {
            throw new AssistantAttachmentException("每条消息最多添加 2 张图片。");
        }

        var encodedImageSizes = ordered
            .Where(item => item.ImageInput is not null)
            .Select(item => (long)item.ImageInput!.JpegBytes.Length)
            .ToList();
        if (encodedImageSizes.Any(size => size > MaxFileBytes)
            || ExceedsLimit(encodedImageSizes, MaxBatchBytes))
        {
            throw new AssistantAttachmentException("处理后的图片超过附件大小限制。");
        }

        // All rejecting checks precede mutation so a failed add cannot alter an accepted selection.
        var remaining = MaxExtractedCharacters;
        foreach (var attachment in ordered)
        {
            attachment.ApplyTextLimit(remaining);
            remaining -= attachment.ExtractedText.Length;
        }
    }

    private static PreparedAssistantAttachment PrepareText(
        FileInfo file,
        string extension,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string text;
        try
        {
            text = DecodeText(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new AssistantAttachmentException("附件不是有效的 UTF-8 或 UTF-16 文本编码。");
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (IsBinaryText(text, cancellationToken))
        {
            throw new AssistantAttachmentException("附件内容不是受支持的文本内容。");
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (extension == ".json")
        {
            try
            {
                using var _ = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                throw new AssistantAttachmentException("JSON 附件内容无效。");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        var mediaType = extension switch
        {
            ".md" => "text/markdown",
            ".json" => "application/json",
            ".csv" => "text/csv",
            _ => "text/plain"
        };
        cancellationToken.ThrowIfCancellationRequested();
        return Prepared(file, bytes.LongLength, mediaType, text, imageInput: null);
    }

    private static PreparedAssistantAttachment PrepareImage(
        FileInfo file,
        string extension,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        if (!ImageSignatureMatches(bytes, extension))
        {
            throw new AssistantAttachmentException("图片签名与扩展名不匹配。");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new SKMemoryStream(bytes);
            using var codec = SKCodec.Create(stream);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            {
                throw new AssistantAttachmentException("图片无法解码或内容已损坏。");
            }
            if (codec.Info.Width > MaxImageDimension || codec.Info.Height > MaxImageDimension)
            {
                throw new AssistantAttachmentException("图片声明的尺寸超过安全限制。");
            }
            if ((long)codec.Info.Width * codec.Info.Height > MaxDecodedPixels)
            {
                throw new AssistantAttachmentException("图片声明的解码像素数量超过安全限制。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var scale = Math.Min(1d, MaxImageEdge / (double)Math.Max(codec.Info.Width, codec.Info.Height));
            var width = Math.Max(1, (int)Math.Round(codec.Info.Width * scale));
            var height = Math.Max(1, (int)Math.Round(codec.Info.Height * scale));
            using var bitmap = DecodeBoundedBitmap(codec, width, height, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            if (surface is null)
            {
                throw new AssistantAttachmentException("图片无法转换。");
            }

            surface.Canvas.Clear(SKColors.White);
            using (var paint = new SKPaint { IsAntialias = true })
            {
                surface.Canvas.DrawBitmap(
                    bitmap,
                    new SKRect(0, 0, width, height),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
                    paint);
            }
            surface.Canvas.Flush();
            cancellationToken.ThrowIfCancellationRequested();
            using var image = surface.Snapshot();
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 88);
            cancellationToken.ThrowIfCancellationRequested();
            var jpegBytes = encoded?.ToArray();
            if (jpegBytes is null
                || jpegBytes.Length < 3
                || !jpegBytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
            {
                throw new AssistantAttachmentException("图片无法转换为 JPEG。");
            }
            if (jpegBytes.LongLength > MaxFileBytes)
            {
                throw new AssistantAttachmentException("处理后的图片超过 20 MiB 限制。");
            }

            var input = new ModelImageInput(jpegBytes, "image/jpeg", file.Name);
            var prepared = Prepared(file, bytes.LongLength, OriginalImageMediaType(extension), "", input);
            cancellationToken.ThrowIfCancellationRequested();
            return prepared;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not AssistantAttachmentException)
        {
            throw new AssistantAttachmentException("图片无法解码或内容已损坏。");
        }
    }

    private static PreparedAssistantAttachment PreparePdf(
        FileInfo file,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        if (!bytes.AsSpan().StartsWith("%PDF-"u8))
        {
            throw new AssistantAttachmentException("PDF 签名与扩展名不匹配。");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = PdfDocument.Open(bytes);
            cancellationToken.ThrowIfCancellationRequested();
            var text = new BoundedTextBuilder();
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                text.Append(page.Text?.Trim(), text.Length == 0 ? "" : Environment.NewLine);
                if (text.IsTruncated) break;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Prepared(file, bytes.LongLength, "application/pdf", text.ToString(), null, text.IsTruncated);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not AssistantAttachmentException)
        {
            throw new AssistantAttachmentException("PDF 无法解析或内容已损坏。");
        }
    }

    private static PreparedAssistantAttachment PrepareDocx(
        FileInfo file,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        ValidateOfficePackage(bytes, OfficePackageKind.Word, cancellationToken);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var document = WordprocessingDocument.Open(stream, false);
            cancellationToken.ThrowIfCancellationRequested();
            if (document.DocumentType != WordprocessingDocumentType.Document)
            {
                throw new AssistantAttachmentException("Office 文件实际类型与 DOCX 扩展名不匹配。");
            }

            var text = new BoundedTextBuilder();
            var body = document.MainDocumentPart?.Document?.Body;
            foreach (var paragraph in body?.Descendants<W.Paragraph>() ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                text.AppendGroup(
                    paragraph.Descendants<W.Text>().Select(item => item.Text),
                    text.Length == 0 ? "" : Environment.NewLine,
                    cancellationToken);
                if (text.IsTruncated) break;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Prepared(
                file,
                bytes.LongLength,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                text.ToString(),
                null,
                text.IsTruncated);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not AssistantAttachmentException)
        {
            throw new AssistantAttachmentException("Office 文件实际类型与 DOCX 扩展名不匹配或内容已损坏。");
        }
    }

    private static PreparedAssistantAttachment PrepareXlsx(
        FileInfo file,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        ValidateOfficePackage(bytes, OfficePackageKind.Spreadsheet, cancellationToken);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var document = SpreadsheetDocument.Open(stream, false);
            cancellationToken.ThrowIfCancellationRequested();
            if (document.DocumentType != SpreadsheetDocumentType.Workbook)
            {
                throw new AssistantAttachmentException("Office 文件实际类型与 XLSX 扩展名不匹配。");
            }

            var workbookPart = document.WorkbookPart
                ?? throw new AssistantAttachmentException("XLSX 缺少工作簿内容。");
            var workbook = workbookPart.Workbook
                ?? throw new AssistantAttachmentException("XLSX 缺少工作簿内容。");
            var sharedStrings = new List<string>();
            foreach (var item in workbookPart.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>() ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                sharedStrings.Add(item.InnerText);
            }

            var text = new BoundedTextBuilder();
            foreach (var sheet in workbook.Sheets?.Elements<S.Sheet>() ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                text.Append($"[{sheet.Name?.Value ?? "工作表"}]", text.Length == 0 ? "" : Environment.NewLine);
                if (text.IsTruncated) break;
                if (sheet.Id?.Value is not string relationshipId
                    || workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
                {
                    continue;
                }

                foreach (var cell in worksheetPart.Worksheet?.Descendants<S.Cell>() ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var value = ReadCellText(cell, sharedStrings, workbookPart, workbook);
                    text.Append(value.Trim(), Environment.NewLine);
                    if (text.IsTruncated) break;
                }
                if (text.IsTruncated) break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Prepared(
                file,
                bytes.LongLength,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                text.ToString(),
                null,
                text.IsTruncated);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not AssistantAttachmentException)
        {
            throw new AssistantAttachmentException("Office 文件实际类型与 XLSX 扩展名不匹配或内容已损坏。");
        }
    }

    private static PreparedAssistantAttachment PreparePptx(
        FileInfo file,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        ValidateOfficePackage(bytes, OfficePackageKind.Presentation, cancellationToken);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var document = PresentationDocument.Open(stream, false);
            cancellationToken.ThrowIfCancellationRequested();
            if (document.DocumentType != PresentationDocumentType.Presentation)
            {
                throw new AssistantAttachmentException("Office 文件实际类型与 PPTX 扩展名不匹配。");
            }

            var presentationPart = document.PresentationPart
                ?? throw new AssistantAttachmentException("PPTX 缺少演示文稿内容。");
            var presentation = presentationPart.Presentation
                ?? throw new AssistantAttachmentException("PPTX 缺少演示文稿内容。");
            var text = new BoundedTextBuilder();
            var slideIds = presentation.SlideIdList?.ChildElements
                .OfType<DocumentFormat.OpenXml.Presentation.SlideId>() ?? [];
            var index = 1;
            foreach (var slideId in slideIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (slideId.RelationshipId?.Value is string relationshipId
                    && presentationPart.GetPartById(relationshipId) is SlidePart slidePart)
                {
                    text.Append($"[第 {index} 页]", text.Length == 0 ? "" : Environment.NewLine);
                    if (text.IsTruncated) break;
                    var slide = slidePart.Slide
                        ?? throw new AssistantAttachmentException("PPTX 幻灯片内容无效。");
                    text.AppendGroup(
                        slide.Descendants<A.Text>().Select(item => item.Text),
                        " ",
                        cancellationToken);
                    if (text.IsTruncated) break;
                }
                index++;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Prepared(
                file,
                bytes.LongLength,
                "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                text.ToString(),
                null,
                text.IsTruncated);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not AssistantAttachmentException)
        {
            throw new AssistantAttachmentException("Office 文件实际类型与 PPTX 扩展名不匹配或内容已损坏。");
        }
    }

    private static string ReadCellText(
        S.Cell cell,
        IReadOnlyList<string> sharedStrings,
        WorkbookPart workbookPart,
        S.Workbook workbook)
    {
        if (cell.DataType?.Value == S.CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? "";
        }

        var raw = cell.CellValue?.Text ?? "";
        if (cell.DataType?.Value == S.CellValues.SharedString
            && int.TryParse(raw, out var index)
            && index >= 0
            && index < sharedStrings.Count)
        {
            return sharedStrings[index];
        }
        if (cell.DataType?.Value == S.CellValues.Boolean)
        {
            return raw == "1" ? "TRUE" : "FALSE";
        }
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric)
            || cell.StyleIndex?.Value is not uint styleIndex)
        {
            return raw;
        }

        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        var cellFormat = stylesheet?.CellFormats?.Elements<S.CellFormat>().ElementAtOrDefault((int)styleIndex);
        var numberFormatId = cellFormat?.NumberFormatId?.Value ?? 0U;
        var formatCode = stylesheet?.NumberingFormats?.Elements<S.NumberingFormat>()
            .FirstOrDefault(item => item.NumberFormatId?.Value == numberFormatId)
            ?.FormatCode?.Value ?? "";
        if (numberFormatId is >= 14 and <= 22 or >= 45 and <= 47
            || IsDateFormatCode(formatCode))
        {
            var uses1904Epoch = workbook.WorkbookProperties?.Date1904?.Value == true;
            var date = DateTime.FromOADate(numeric + (uses1904Epoch ? 1462d : 0d));
            return numberFormatId is >= 18 and <= 22 or >= 45 and <= 47
                || formatCode.Contains("h", StringComparison.OrdinalIgnoreCase)
                ? date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        if (numberFormatId is 9 or 10 || formatCode.Contains('%'))
        {
            var decimals = numberFormatId == 10 || formatCode.Contains(".00", StringComparison.Ordinal) ? 2 : 0;
            return numeric.ToString(decimals == 2 ? "0.00%" : "0%", CultureInfo.InvariantCulture);
        }
        if (numberFormatId == 4 || formatCode.Contains("#,##0.00", StringComparison.Ordinal))
        {
            return numeric.ToString("N2", CultureInfo.InvariantCulture);
        }
        if (numberFormatId == 3 || formatCode.Contains("#,##0", StringComparison.Ordinal))
        {
            return numeric.ToString("N0", CultureInfo.InvariantCulture);
        }
        return raw;
    }

    private static bool IsDateFormatCode(string formatCode)
    {
        if (string.IsNullOrWhiteSpace(formatCode)) return false;
        var normalized = formatCode.ToLowerInvariant();
        return normalized.Contains('y')
            || normalized.Contains('d')
            || (normalized.Contains('m') && !normalized.Contains('%'));
    }

    private static SKBitmap DecodeBoundedBitmap(
        SKCodec codec,
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken)
    {
        var scale = Math.Min(
            targetWidth / (float)codec.Info.Width,
            targetHeight / (float)codec.Info.Height);
        var codecDimensions = codec.GetScaledDimensions(scale);
        var codecInfo = new SKImageInfo(
            codecDimensions.Width,
            codecDimensions.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        if (codecDimensions.Width > 0
            && codecDimensions.Height > 0
            && Math.Max(codecDimensions.Width, codecDimensions.Height) <= MaxImageEdge)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sampled = SKBitmap.Decode(codec, codecInfo);
            if (sampled is not null) return sampled;
        }

        if (codec.EncodedFormat == SKEncodedImageFormat.Png)
        {
            // Skia's PNG codec does not support scaled/scanline output consistently. The pixel-budget
            // preflight bounds this one full decode before it is immediately reduced to the model size.
            cancellationToken.ThrowIfCancellationRequested();
            using var fullSize = SKBitmap.Decode(codec);
            if (fullSize is null)
            {
                throw new AssistantAttachmentException("图片无法按安全尺寸解码。");
            }
            var bounded = new SKBitmap(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var boundedCanvas = new SKCanvas(bounded);
            using var boundedPaint = new SKPaint { IsAntialias = true };
            boundedCanvas.DrawBitmap(
                fullSize,
                new SKRect(0, 0, targetWidth, targetHeight),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
                boundedPaint);
            boundedCanvas.Flush();
            cancellationToken.ThrowIfCancellationRequested();
            return bounded;
        }

        var sourceInfo = new SKImageInfo(
            codec.Info.Width,
            codec.Info.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        if (codec.StartScanlineDecode(sourceInfo) != SKCodecResult.Success)
        {
            throw new AssistantAttachmentException("图片无法按安全尺寸解码。");
        }

        var target = new SKBitmap(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var sourceRow = new SKBitmap(sourceInfo.Width, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(target);
        using var paint = new SKPaint { IsAntialias = true };
        var targetY = 0;
        for (var sourceY = 0; sourceY < sourceInfo.Height; sourceY++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (codec.GetScanlines(sourceRow.GetPixels(), 1, sourceRow.RowBytes) != 1)
            {
                target.Dispose();
                throw new AssistantAttachmentException("图片无法按安全尺寸解码。");
            }

            while (targetY < targetHeight
                && Math.Min(
                    sourceInfo.Height - 1,
                    (int)(((targetY + 0.5d) * sourceInfo.Height) / targetHeight)) == sourceY)
            {
                canvas.DrawBitmap(
                    sourceRow,
                    new SKRect(0, targetY, targetWidth, targetY + 1),
                    new SKSamplingOptions(SKFilterMode.Linear),
                    paint);
                targetY++;
            }
        }
        canvas.Flush();
        if (targetY != targetHeight)
        {
            target.Dispose();
            throw new AssistantAttachmentException("图片无法按安全尺寸解码。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return target;
    }

    private static void ValidateOfficePackage(
        byte[] bytes,
        OfficePackageKind expectedKind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!bytes.AsSpan().StartsWith(ZipSignature))
        {
            throw new AssistantAttachmentException("Office 文件签名与扩展名不匹配。");
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            long declaredTotalUncompressedBytes = 0;
            long actualTotalUncompressedBytes = 0;
            var decompressionBuffer = new byte[64 * 1024];
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Length > MaxOfficeEntryUncompressedBytes
                    || entry.Length < 0
                    || entry.Length > MaxOfficeTotalUncompressedBytes - declaredTotalUncompressedBytes)
                {
                    throw new AssistantAttachmentException("Office 文件解压大小超过安全限制。");
                }
                declaredTotalUncompressedBytes += entry.Length;
                var name = entry.FullName.Replace('\\', '/');
                if (name.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("macrosheets/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AssistantAttachmentException("禁止添加含宏的 Office 文件。");
                }

                ValidateOfficeEntryContents(
                    entry,
                    decompressionBuffer,
                    ref actualTotalUncompressedBytes,
                    cancellationToken);
            }

            var contentTypes = archive.GetEntry("[Content_Types].xml");
            if (contentTypes is null)
            {
                throw new AssistantAttachmentException("Office 文件实际类型无效或内容已损坏。");
            }
            var declarations = ReadZipTextEntry(contentTypes, cancellationToken);
            if (declarations.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase)
                || declarations.Contains("vbaProject", StringComparison.OrdinalIgnoreCase))
            {
                throw new AssistantAttachmentException("禁止添加含宏的 Office 文件。");
            }

            var requiredMainContentType = expectedKind switch
            {
                OfficePackageKind.Word => "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
                OfficePackageKind.Spreadsheet => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
                _ => "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"
            };
            if (!declarations.Contains(requiredMainContentType, StringComparison.OrdinalIgnoreCase))
            {
                throw new AssistantAttachmentException("Office 文件实际类型与扩展名不匹配。");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not AssistantAttachmentException)
        {
            throw new AssistantAttachmentException("Office 文件无法解析或内容已损坏。");
        }
    }

    private static void ValidateOfficeEntryContents(
        ZipArchiveEntry entry,
        byte[] buffer,
        ref long actualTotalUncompressedBytes,
        CancellationToken cancellationToken)
    {
        using var stream = entry.Open();
        long actualEntryUncompressedBytes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0) break;
            if (actualEntryUncompressedBytes > MaxOfficeEntryUncompressedBytes - read
                || actualTotalUncompressedBytes > MaxOfficeTotalUncompressedBytes - read)
            {
                throw new AssistantAttachmentException("Office 文件解压大小超过安全限制。");
            }
            actualEntryUncompressedBytes += read;
            actualTotalUncompressedBytes += read;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string ReadZipTextEntry(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        var builder = new StringBuilder((int)Math.Min(entry.Length, 64 * 1024));
        var buffer = new char[4096];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (builder.Length + read > 1024 * 1024)
            {
                throw new AssistantAttachmentException("Office 包清单超过安全限制。");
            }
            builder.Append(buffer, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return builder.ToString();
    }

    private static void RejectDangerousOrMismatchedContainer(byte[] bytes, string extension)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith("MZ"u8))
        {
            throw new AssistantAttachmentException("禁止添加可执行文件内容。");
        }
        if (span.StartsWith(Rar4Signature) || span.StartsWith(Rar5Signature) || span.StartsWith(SevenZipSignature))
        {
            throw new AssistantAttachmentException("禁止添加压缩归档内容。");
        }
        if (span.StartsWith(ZipSignature) && extension is not ".docx" and not ".xlsx" and not ".pptx")
        {
            throw new AssistantAttachmentException("附件签名与扩展名不匹配；不支持普通压缩归档。");
        }

        if (extension is ".txt" or ".md" or ".json" or ".csv")
        {
            if (span.StartsWith(PngSignature)
                || span.StartsWith("%PDF-"u8)
                || IsJpeg(span)
                || IsWebp(span))
            {
                throw new AssistantAttachmentException("附件签名与扩展名不匹配。");
            }
        }
    }

    private static bool IsSupportedExtension(string extension) => extension is
        ".png" or ".jpg" or ".jpeg" or ".webp"
        or ".txt" or ".md" or ".json" or ".csv"
        or ".pdf" or ".docx" or ".xlsx" or ".pptx";

    private static bool ImageSignatureMatches(byte[] bytes, string extension)
    {
        var span = bytes.AsSpan();
        return extension switch
        {
            ".png" => span.StartsWith(PngSignature),
            ".jpg" or ".jpeg" => IsJpeg(span),
            ".webp" => IsWebp(span),
            _ => false
        };
    }

    private static bool IsJpeg(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private static bool IsWebp(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12
        && bytes[..4].SequenceEqual("RIFF"u8)
        && bytes.Slice(8, 4).SequenceEqual("WEBP"u8);

    private static string OriginalImageMediaType(string extension) => extension switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };

    private static PreparedAssistantAttachment Prepared(
        FileInfo file,
        long sizeBytes,
        string mediaType,
        string text,
        ModelImageInput? imageInput,
        bool extractionWasTruncated = false)
    {
        var trimmed = text.Trim();
        var individuallyTruncated = extractionWasTruncated || trimmed.Length > MaxExtractedCharacters;
        var available = individuallyTruncated ? trimmed[..MaxExtractedCharacters] : trimmed;
        return new PreparedAssistantAttachment(
            new AssistantAttachmentMetadata
            {
                Name = file.Name,
                MediaType = mediaType,
                SizeBytes = sizeBytes
            },
            available,
            imageInput,
            individuallyTruncated);
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true)
                .GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true)
                .GetString(bytes, 2, bytes.Length - 2);
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(bytes)
            .TrimStart('\uFEFF');
    }

    private static bool IsBinaryText(string text, CancellationToken cancellationToken)
    {
        if (text.IndexOf('\0') >= 0) return true;
        if (text.Length == 0) return false;
        var suspicious = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 0x0FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var character = text[index];
            if (char.IsControl(character)
                && character is not '\r' and not '\n' and not '\t' and not '\f')
            {
                suspicious++;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return suspicious > Math.Max(4, text.Length / 20);
    }

    private static async Task<byte[]> ReadFileBoundedAsync(
        string path,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new MemoryStream((int)Math.Min(expectedLength, MaxFileBytes));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (output.Length + read > MaxFileBytes)
            {
                throw new AssistantAttachmentException("单个附件不能超过 20 MiB。");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static bool ExceedsLimit(IEnumerable<long> sizes, long limit)
    {
        long total = 0;
        foreach (var size in sizes)
        {
            if (size < 0 || size > limit - total) return true;
            total += size;
        }
        return false;
    }

    private enum OfficePackageKind
    {
        Word,
        Spreadsheet,
        Presentation
    }

    private sealed class BoundedTextBuilder
    {
        private readonly StringBuilder _builder = new(MaxExtractedCharacters);

        public bool IsTruncated { get; private set; }
        public bool IsFull => _builder.Length >= MaxExtractedCharacters;
        public int Length => _builder.Length;

        public void Append(string? value, string separator = "")
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            AppendRaw(separator);
            AppendRaw(value);
        }

        public void AppendGroup(
            IEnumerable<string?> fragments,
            string separator,
            CancellationToken cancellationToken)
        {
            var started = false;
            foreach (var fragment in fragments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(fragment)) continue;
                if (!started)
                {
                    AppendRaw(separator);
                    if (IsTruncated) return;
                    started = true;
                }
                AppendRaw(fragment);
                if (IsTruncated) return;
            }
        }

        public override string ToString() => _builder.ToString();

        private void AppendRaw(string value)
        {
            if (value.Length == 0) return;
            var remaining = MaxExtractedCharacters - _builder.Length;
            if (remaining <= 0)
            {
                IsTruncated = true;
                return;
            }
            var count = Math.Min(remaining, value.Length);
            _builder.Append(value, 0, count);
            if (count < value.Length) IsTruncated = true;
        }
    }
}
