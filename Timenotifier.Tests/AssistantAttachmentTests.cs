using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;
using SkiaSharp;
using Timenotifier.Models;
using Timenotifier.Services.Assistant;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Timenotifier.Tests;

public sealed class AssistantAttachmentTests
{
    [Theory]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("notes.md", "text/markdown")]
    [InlineData("data.json", "application/json")]
    [InlineData("rows.csv", "text/csv")]
    public async Task Text_formats_decode_bom_aware_unicode(string fileName, string mediaType)
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, fileName);
        var encoding = fileName == "notes.txt" ? Encoding.Unicode : new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await File.WriteAllTextAsync(path, fileName == "data.json" ? "{\"value\":\"known-text\"}" : "known-text", encoding);

        var attachment = await new AssistantAttachmentService().PrepareAsync(path);

        Assert.Equal(fileName, attachment.Metadata.Name);
        Assert.Equal(mediaType, attachment.Metadata.MediaType);
        Assert.Contains("known-text", attachment.ExtractedText);
        Assert.Null(attachment.ImageInput);
        AssertNoPathInOutput(attachment, temp.Path);
    }

    [Fact]
    public async Task Pdf_extracts_page_text_only()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "report.pdf");
        CreatePdf(path, "assistant pdf evidence");

        var attachment = await new AssistantAttachmentService().PrepareAsync(path);

        Assert.Equal("application/pdf", attachment.Metadata.MediaType);
        Assert.Contains("assistant pdf evidence", attachment.ExtractedText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Docx_extracts_paragraphs_and_tables()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "report.docx");
        CreateDocx(path, "paragraph-known", "table-known");

        var attachment = await new AssistantAttachmentService().PrepareAsync(path);

        Assert.Contains("paragraph-known", attachment.ExtractedText);
        Assert.Contains("table-known", attachment.ExtractedText);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", attachment.Metadata.MediaType);
    }

    [Fact]
    public async Task Xlsx_extracts_sheet_shared_inline_and_cached_values_without_formula_text()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "report.xlsx");
        CreateXlsx(path);

        var attachment = await new AssistantAttachmentService().PrepareAsync(path);

        Assert.Contains("Known Sheet", attachment.ExtractedText);
        Assert.Contains("shared-known", attachment.ExtractedText);
        Assert.Contains("inline-known", attachment.ExtractedText);
        Assert.Contains("42", attachment.ExtractedText);
        Assert.DoesNotContain("SUM(", attachment.ExtractedText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TRUE", attachment.ExtractedText);
        Assert.Contains("2026-07-13", attachment.ExtractedText);
        Assert.Contains("12.50%", attachment.ExtractedText);
        Assert.Contains("1,234.50", attachment.ExtractedText);
    }

    [Fact]
    public async Task Pptx_extracts_slide_text_in_presentation_order()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "report.pptx");
        CreatePptx(path, "slide-first", "slide-second");

        var attachment = await new AssistantAttachmentService().PrepareAsync(path);

        Assert.True(attachment.ExtractedText.IndexOf("slide-first", StringComparison.Ordinal)
            < attachment.ExtractedText.IndexOf("slide-second", StringComparison.Ordinal));
        Assert.Equal("application/vnd.openxmlformats-officedocument.presentationml.presentation", attachment.Metadata.MediaType);
    }

    [Theory]
    [InlineData("proof.png", SKEncodedImageFormat.Png)]
    [InlineData("proof.jpg", SKEncodedImageFormat.Jpeg)]
    [InlineData("proof.webp", SKEncodedImageFormat.Webp)]
    public async Task Images_are_decoded_bounded_and_reencoded_as_jpeg(string fileName, SKEncodedImageFormat format)
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, fileName);
        await CreateImageAsync(path, format, 2400, 1200);
        var originalBytes = await File.ReadAllBytesAsync(path);

        var attachment = await new AssistantAttachmentService().PrepareAsync(path);

        var input = Assert.IsType<ModelImageInput>(attachment.ImageInput);
        Assert.True(attachment.IsImage);
        Assert.Equal("image/jpeg", input.MediaType);
        Assert.Equal(fileName, input.DisplayName);
        Assert.True(input.JpegBytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }));
        Assert.False(input.JpegBytes.AsSpan().SequenceEqual(originalBytes));
        using var decoded = SKBitmap.Decode(input.JpegBytes);
        Assert.NotNull(decoded);
        Assert.True(Math.Max(decoded.Width, decoded.Height) <= 1280);
        Assert.Equal(2d, decoded.Width / (double)decoded.Height, precision: 2);
        AssertNoPathInOutput(attachment, temp.Path);
    }

    [Fact]
    public async Task Image_declaring_pixels_over_decode_budget_is_rejected_before_pixel_allocation()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "extreme.png");
        await CreateImageAsync(path, SKEncodedImageFormat.Png, 1, 1);
        RewritePngDimensions(path, 10_000, 10_000);
        Assert.True(10_000L * 10_000 > AssistantAttachmentService.MaxDecodedPixels);

        var error = await Assert.ThrowsAsync<AssistantAttachmentException>(
            () => new AssistantAttachmentService().PrepareAsync(path));

        Assert.Contains("像素", error.Message);
        AssertSafeError(error, temp.Path, "extreme");
    }

    [Fact]
    public async Task Image_dimension_over_budget_is_rejected_even_when_pixel_count_is_within_budget()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "extreme-width.png");
        await CreateImageAsync(path, SKEncodedImageFormat.Png, 1, 1);
        var width = checked((uint)AssistantAttachmentService.MaxImageDimension + 1U);
        RewritePngDimensions(path, width, 1);
        Assert.True((long)width <= AssistantAttachmentService.MaxDecodedPixels);

        var error = await Assert.ThrowsAsync<AssistantAttachmentException>(
            () => new AssistantAttachmentService().PrepareAsync(path));

        Assert.Contains("尺寸", error.Message);
        AssertSafeError(error, temp.Path, "extreme-width");
    }

    [Theory]
    [InlineData("image.jpg", "png")]
    [InlineData("image.png", "jpeg")]
    [InlineData("document.pdf", "png")]
    public async Task Extension_signature_mismatch_is_rejected_safely(string fileName, string actualFormat)
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, fileName);
        if (actualFormat == "png")
        {
            await CreateImageAsync(path, SKEncodedImageFormat.Png, 20, 10);
        }
        else
        {
            await CreateImageAsync(path, SKEncodedImageFormat.Jpeg, 20, 10);
        }

        var error = await Assert.ThrowsAsync<AssistantAttachmentException>(
            () => new AssistantAttachmentService().PrepareAsync(path));

        Assert.Contains("不匹配", error.Message);
        AssertSafeError(error, temp.Path, "pixel-secret");
    }

    [Theory]
    [InlineData("macro.docm")]
    [InlineData("macro.xlsm")]
    [InlineData("macro.pptm")]
    [InlineData("archive.zip")]
    [InlineData("archive.rar")]
    [InlineData("archive.7z")]
    [InlineData("program.exe")]
    [InlineData("unknown.bin")]
    public async Task Unsafe_extensions_are_rejected_with_safe_reason(string fileName)
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, fileName);
        var bytes = Path.GetExtension(fileName) switch
        {
            ".zip" => new byte[] { 0x50, 0x4B, 0x03, 0x04 },
            ".rar" => new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 },
            ".7z" => new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C },
            ".exe" => new byte[] { 0x4D, 0x5A, 0x90, 0x00 },
            _ => new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }
        };
        await File.WriteAllBytesAsync(path, bytes);

        var error = await Assert.ThrowsAsync<AssistantAttachmentException>(
            () => new AssistantAttachmentService().PrepareAsync(path));

        Assert.True(error.Message.Contains("不支持") || error.Message.Contains("禁止"));
        AssertSafeError(error, temp.Path, "MZ");
    }

    [Theory]
    [InlineData("renamed-rar.txt", "rar")]
    [InlineData("renamed-7z.md", "7z")]
    [InlineData("unknown.csv", "unknown")]
    public async Task Archive_and_unknown_binary_signatures_renamed_as_allowed_text_are_rejected(
        string fileName,
        string signature)
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, fileName);
        var bytes = signature switch
        {
            "rar" => new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 },
            "7z" => new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C },
            _ => new byte[] { 0x00, 0xFF, 0x10, 0x80, 0x00, 0x01 }
        };
        await File.WriteAllBytesAsync(path, bytes);

        var error = await Assert.ThrowsAsync<AssistantAttachmentException>(
            () => new AssistantAttachmentService().PrepareAsync(path));

        Assert.True(error.Message.Contains("禁止")
            || error.Message.Contains("不匹配")
            || error.Message.Contains("文本")
            || error.Message.Contains("编码"));
        AssertSafeError(error, temp.Path, signature);
    }

    [Fact]
    public async Task Executable_or_archive_content_renamed_as_text_is_rejected()
    {
        using var temp = new TemporaryDirectory();
        var executable = Path.Combine(temp.Path, "renamed.txt");
        await File.WriteAllBytesAsync(executable, [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00]);
        var archive = Path.Combine(temp.Path, "archive.md");
        await File.WriteAllBytesAsync(archive, [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00]);
        var service = new AssistantAttachmentService();

        var executableError = await Assert.ThrowsAsync<AssistantAttachmentException>(() => service.PrepareAsync(executable));
        var archiveError = await Assert.ThrowsAsync<AssistantAttachmentException>(() => service.PrepareAsync(archive));

        Assert.Contains("禁止", executableError.Message);
        Assert.Contains("不匹配", archiveError.Message);
    }

    [Fact]
    public async Task Null_heavy_or_invalid_utf8_text_is_rejected()
    {
        using var temp = new TemporaryDirectory();
        var nullHeavy = Path.Combine(temp.Path, "null-heavy.txt");
        await File.WriteAllBytesAsync(nullHeavy, Enumerable.Repeat((byte)0, 128).ToArray());
        var invalid = Path.Combine(temp.Path, "invalid.txt");
        await File.WriteAllBytesAsync(invalid, [0xC3, 0x28, 0xC3, 0x28]);
        var service = new AssistantAttachmentService();

        var first = await Assert.ThrowsAsync<AssistantAttachmentException>(() => service.PrepareAsync(nullHeavy));
        var second = await Assert.ThrowsAsync<AssistantAttachmentException>(() => service.PrepareAsync(invalid));

        Assert.Contains("文本", first.Message);
        Assert.Contains("编码", second.Message);
    }

    [Theory]
    [InlineData("wrong.xlsx", "docx")]
    [InlineData("wrong.pptx", "xlsx")]
    [InlineData("wrong.docx", "pptx")]
    public async Task Ooxml_package_type_must_match_extension(string fileName, string actualType)
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, fileName);
        if (actualType == "docx") CreateDocx(path, "known", "table");
        else if (actualType == "xlsx") CreateXlsx(path);
        else CreatePptx(path, "one");

        var error = await Assert.ThrowsAsync<AssistantAttachmentException>(
            () => new AssistantAttachmentService().PrepareAsync(path));

        Assert.Contains("实际类型", error.Message);
    }

    [Fact]
    public async Task Macro_content_inside_docx_is_rejected()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "macro-content.docx");
        CreateDocx(path, "safe-text", "table");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.CreateEntry("word/vbaProject.bin");
        }

        var error = await Assert.ThrowsAsync<AssistantAttachmentException>(
            () => new AssistantAttachmentService().PrepareAsync(path));

        Assert.Contains("宏", error.Message);
    }

    [Fact]
    public async Task Ooxml_entry_over_uncompressed_budget_is_rejected_before_package_parse()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "oversized-entry.docx");
        CreateDocx(path, "safe", "table");
        AddCompressedEntry(path, "word/oversized.dat", AssistantAttachmentService.MaxOfficeEntryUncompressedBytes + 1);

        var error = await Assert.ThrowsAsync<AssistantAttachmentException>(
            () => new AssistantAttachmentService().PrepareAsync(path));

        Assert.Contains("解压", error.Message);
        AssertSafeError(error, temp.Path, "safe");
    }

    [Fact]
    public async Task Ooxml_total_uncompressed_budget_is_rejected_before_package_parse()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "oversized-total.docx");
        CreateDocx(path, "safe", "table");
        var entrySize = AssistantAttachmentService.MaxOfficeTotalUncompressedBytes / 5;
        for (var index = 0; index < 6; index++)
        {
            AddCompressedEntry(path, $"word/padding-{index}.dat", entrySize);
        }

        var error = await Assert.ThrowsAsync<AssistantAttachmentException>(
            () => new AssistantAttachmentService().PrepareAsync(path));

        Assert.Contains("解压", error.Message);
    }

    [Theory]
    [InlineData("broken.pdf")]
    [InlineData("broken.docx")]
    [InlineData("broken.xlsx")]
    [InlineData("broken.pptx")]
    [InlineData("broken.png")]
    public async Task Corrupt_supported_files_are_rejected(string fileName)
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, fileName);
        var extension = Path.GetExtension(fileName);
        var bytes = extension switch
        {
            ".pdf" => Encoding.ASCII.GetBytes("%PDF-corrupt"),
            ".png" => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00],
            _ => [0x50, 0x4B, 0x03, 0x04, 0x00]
        };
        await File.WriteAllBytesAsync(path, bytes);

        var error = await Assert.ThrowsAsync<AssistantAttachmentException>(
            () => new AssistantAttachmentService().PrepareAsync(path));

        Assert.True(error.Message.Contains("损坏") || error.Message.Contains("无法") || error.Message.Contains("实际类型"));
    }

    [Fact]
    public async Task File_count_accepts_five_and_rejects_six_without_mutating_accepted_attachments()
    {
        using var temp = new TemporaryDirectory();
        var service = new AssistantAttachmentService();
        var paths = await CreateTextFilesAsync(temp.Path, 6, 1);
        var prepared = new List<PreparedAssistantAttachment>();
        foreach (var path in paths) prepared.Add(await service.PrepareAsync(path));

        service.ValidateBatch(prepared.Take(5).ToList());
        var acceptedText = prepared[0].ExtractedText;
        await Assert.ThrowsAsync<AssistantAttachmentException>(() => Task.Run(() => service.ValidateBatch(prepared)));

        Assert.Equal(acceptedText, prepared[0].ExtractedText);
        Assert.False(prepared[0].IsTextTruncated);
    }

    [Fact]
    public async Task Per_file_size_accepts_exact_twenty_mib_and_rejects_plus_one()
    {
        using var temp = new TemporaryDirectory();
        var exact = Path.Combine(temp.Path, "exact.txt");
        var over = Path.Combine(temp.Path, "over.txt");
        CreateSizedTextFile(exact, AssistantAttachmentService.MaxFileBytes);
        CreateSizedTextFile(over, AssistantAttachmentService.MaxFileBytes + 1);
        var service = new AssistantAttachmentService();

        var accepted = await service.PrepareAsync(exact);
        var error = await Assert.ThrowsAsync<AssistantAttachmentException>(() => service.PrepareAsync(over));

        Assert.Equal(AssistantAttachmentService.MaxFileBytes, accepted.Metadata.SizeBytes);
        Assert.Contains("20 MiB", error.Message);
    }

    [Fact]
    public async Task Total_size_accepts_exact_fifty_mib_and_rejects_plus_one_atomically()
    {
        using var temp = new TemporaryDirectory();
        var paths = new[] { "a.txt", "b.txt", "c.txt" }.Select(name => Path.Combine(temp.Path, name)).ToArray();
        CreateSizedTextFile(paths[0], 20L * 1024 * 1024);
        CreateSizedTextFile(paths[1], 20L * 1024 * 1024);
        CreateSizedTextFile(paths[2], 10L * 1024 * 1024);
        var service = new AssistantAttachmentService();
        var attachments = new List<PreparedAssistantAttachment>();
        foreach (var path in paths) attachments.Add(await service.PrepareAsync(path));

        service.ValidateBatch(attachments);
        using (var stream = new FileStream(paths[2], FileMode.Append, FileAccess.Write, FileShare.None)) stream.WriteByte((byte)'x');
        var over = await service.PrepareAsync(paths[2]);
        var candidate = new[] { attachments[0], attachments[1], over };
        var before = attachments[0].ExtractedText;

        var error = Assert.Throws<AssistantAttachmentException>(() => service.ValidateBatch(candidate));
        Assert.Contains("50 MiB", error.Message);
        Assert.Equal(before, attachments[0].ExtractedText);
    }

    [Fact]
    public async Task Image_count_accepts_two_and_rejects_three()
    {
        using var temp = new TemporaryDirectory();
        var service = new AssistantAttachmentService();
        var attachments = new List<PreparedAssistantAttachment>();
        for (var index = 0; index < 3; index++)
        {
            var path = Path.Combine(temp.Path, $"{index}.png");
            await CreateImageAsync(path, SKEncodedImageFormat.Png, 20, 10);
            attachments.Add(await service.PrepareAsync(path));
        }

        service.ValidateBatch(attachments.Take(2).ToList());
        var error = Assert.Throws<AssistantAttachmentException>(() => service.ValidateBatch(attachments));

        Assert.Contains("2 张", error.Message);
    }

    [Fact]
    public async Task Text_limit_accepts_exact_and_truncates_plus_one_deterministically_in_attachment_order()
    {
        using var temp = new TemporaryDirectory();
        var firstPath = Path.Combine(temp.Path, "first.txt");
        var secondPath = Path.Combine(temp.Path, "second.txt");
        await File.WriteAllTextAsync(firstPath, new string('a', 50_000));
        await File.WriteAllTextAsync(secondPath, new string('b', 30_001));
        var service = new AssistantAttachmentService();
        var first = await service.PrepareAsync(firstPath);
        var second = await service.PrepareAsync(secondPath);

        service.ValidateBatch([first, second]);

        Assert.Equal(50_000, first.ExtractedText.Length);
        Assert.Equal(30_000, second.ExtractedText.Length);
        Assert.EndsWith(new string('b', 20), second.ExtractedText, StringComparison.Ordinal);
        Assert.True(second.IsTextTruncated);
        Assert.False(first.IsTextTruncated);
        Assert.Contains("80,000", second.TruncationMessage);
        Assert.DoesNotContain(temp.Path, second.TruncationMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Single_text_is_truncated_at_eighty_thousand_with_safe_indicator()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "long.txt");
        await File.WriteAllTextAsync(path, new string('x', 80_001));

        var attachment = await new AssistantAttachmentService().PrepareAsync(path);

        Assert.Equal(80_000, attachment.ExtractedText.Length);
        Assert.True(attachment.IsTextTruncated);
        Assert.Equal("附件文本已截断至 80,000 个字符。", attachment.TruncationMessage);
    }

    [Fact]
    public async Task Single_text_at_exact_eighty_thousand_is_not_truncated()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "exact-text.txt");
        await File.WriteAllTextAsync(path, new string('x', 80_000));

        var attachment = await new AssistantAttachmentService().PrepareAsync(path);

        Assert.Equal(80_000, attachment.ExtractedText.Length);
        Assert.False(attachment.IsTextTruncated);
        Assert.Equal("", attachment.TruncationMessage);
    }

    [Fact]
    public void Bounded_text_collection_stops_enumerating_after_truncation()
    {
        var builderType = typeof(AssistantAttachmentService)
            .GetNestedType("BoundedTextBuilder", BindingFlags.NonPublic)!;
        var builder = Activator.CreateInstance(builderType)!;
        var appendGroup = builderType.GetMethod("AppendGroup", BindingFlags.Instance | BindingFlags.Public)!;

        var error = Record.Exception(() => appendGroup.Invoke(
            builder,
            [OversizedThenFailingFragments(), "", CancellationToken.None]));

        Assert.Null(error);
        Assert.Equal(
            AssistantAttachmentService.MaxExtractedCharacters,
            ((string)builderType.GetMethod(nameof(ToString), Type.EmptyTypes)!.Invoke(builder, null)!).Length);
        Assert.True((bool)builderType.GetProperty("IsTruncated")!.GetValue(builder)!);
    }

    [Fact]
    public void Prepared_output_is_request_scoped_and_session_attachment_fields_accept_metadata_only()
    {
        var publicProperties = typeof(PreparedAssistantAttachment)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        var attachmentElementType = typeof(AssistantMessage)
            .GetProperty(nameof(AssistantMessage.Attachments))!
            .PropertyType
            .GetGenericArguments()
            .Single();

        Assert.DoesNotContain(publicProperties, name => name.Contains("Path", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(typeof(AssistantAttachmentMetadata), attachmentElementType);
        Assert.False(typeof(PreparedAssistantAttachment).IsAssignableTo(attachmentElementType));
        Assert.False(typeof(ModelImageInput).IsAssignableTo(attachmentElementType));
    }

    [Fact]
    public async Task Pre_cancelled_read_propagates_cancellation()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "cancel.txt");
        await File.WriteAllTextAsync(path, "known");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new AssistantAttachmentService().PrepareAsync(path, cancellation.Token));
    }

    [Fact]
    public async Task Cancellation_after_processing_starts_never_returns_partial_attachment()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "cancel-during-read.txt");
        CreateSizedTextFile(path, AssistantAttachmentService.MaxFileBytes);
        using var cancellation = new CancellationTokenSource();
        var task = new AssistantAttachmentService().PrepareAsync(path, cancellation.Token);
        Assert.False(cancellation.IsCancellationRequested);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task File_read_failure_does_not_echo_absolute_path()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "locked.txt");
        await File.WriteAllTextAsync(path, "private-content");
        await using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var error = await Assert.ThrowsAsync<AssistantAttachmentException>(
            () => new AssistantAttachmentService().PrepareAsync(path));

        Assert.Contains("读取", error.Message);
        AssertSafeError(error, temp.Path, "private-content");
    }

    private static void AssertNoPathInOutput(PreparedAssistantAttachment attachment, string path)
    {
        Assert.DoesNotContain(path, attachment.Metadata.Name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(path, attachment.ExtractedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(path, attachment.TruncationMessage, StringComparison.OrdinalIgnoreCase);
        if (attachment.ImageInput is not null)
        {
            Assert.DoesNotContain(path, attachment.ImageInput.DisplayName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Encoding.UTF8.GetBytes(path), attachment.ImageInput.JpegBytes);
        }
    }

    private static void AssertSafeError(Exception error, string path, string content)
    {
        Assert.DoesNotContain(path, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(content, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string[]> CreateTextFilesAsync(string directory, int count, int characters)
    {
        var paths = Enumerable.Range(0, count).Select(index => Path.Combine(directory, $"{index}.txt")).ToArray();
        foreach (var path in paths) await File.WriteAllTextAsync(path, new string('x', characters));
        return paths;
    }

    private static IEnumerable<string?> OversizedThenFailingFragments()
    {
        yield return new string('x', AssistantAttachmentService.MaxExtractedCharacters + 1);
        throw new InvalidOperationException("Text collection continued after the extraction limit.");
    }

    private static async Task CreateImageAsync(
        string path,
        SKEncodedImageFormat format,
        int width,
        int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.SeaGreen);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        await using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static void RewritePngDimensions(string path, uint width, uint height)
    {
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);
        var crc = ComputeCrc32(bytes.AsSpan(12, 17));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(29, 4), crc);
        File.WriteAllBytes(path, bytes);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320U : crc >> 1;
            }
        }
        return ~crc;
    }

    private static void CreatePdf(string path, string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText(text, 12, new PdfPoint(30, 700), font);
        File.WriteAllBytes(path, builder.Build());
    }

    private static void CreateSizedTextFile(string path, long length)
    {
        var buffer = Enumerable.Repeat((byte)'x', 1024 * 1024).ToArray();
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        while (stream.Length < length)
        {
            stream.Write(buffer, 0, (int)Math.Min(buffer.Length, length - stream.Length));
        }
    }

    private static void CreateDocx(string path, string paragraphText, string tableText)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new W.Document(
            new W.Body(
                new W.Paragraph(new W.Run(new W.Text(paragraphText))),
                new W.Table(
                    new W.TableRow(
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text(tableText))))))));
        main.Document.Save();
    }

    private static void CreateXlsx(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new S.Workbook();
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = new S.Stylesheet(
            new S.Fonts(new S.Font()) { Count = 1U },
            new S.Fills(
                new S.Fill(new S.PatternFill { PatternType = S.PatternValues.None }),
                new S.Fill(new S.PatternFill { PatternType = S.PatternValues.Gray125 })) { Count = 2U },
            new S.Borders(new S.Border()) { Count = 1U },
            new S.CellStyleFormats(new S.CellFormat()) { Count = 1U },
            new S.CellFormats(
                new S.CellFormat(),
                new S.CellFormat { NumberFormatId = 14U, ApplyNumberFormat = true },
                new S.CellFormat { NumberFormatId = 10U, ApplyNumberFormat = true },
                new S.CellFormat { NumberFormatId = 4U, ApplyNumberFormat = true }) { Count = 4U });
        stylesPart.Stylesheet.Save();
        var sharedPart = workbookPart.AddNewPart<SharedStringTablePart>();
        sharedPart.SharedStringTable = new S.SharedStringTable(new S.SharedStringItem(new S.Text("shared-known")));
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new S.Worksheet(
            new S.SheetData(
                new S.Row(
                    new S.Cell { DataType = S.CellValues.SharedString, CellValue = new S.CellValue("0") },
                    new S.Cell
                    {
                        DataType = S.CellValues.InlineString,
                        InlineString = new S.InlineString(new S.Text("inline-known"))
                    },
                    new S.Cell
                    {
                        CellFormula = new S.CellFormula("SUM(40,2)"),
                        CellValue = new S.CellValue("42")
                    },
                    new S.Cell { DataType = S.CellValues.Boolean, CellValue = new S.CellValue("1") },
                    new S.Cell
                    {
                        StyleIndex = 1U,
                        CellValue = new S.CellValue(
                            new DateTime(2026, 7, 13).ToOADate().ToString(CultureInfo.InvariantCulture))
                    },
                    new S.Cell { StyleIndex = 2U, CellValue = new S.CellValue("0.125") },
                    new S.Cell { StyleIndex = 3U, CellValue = new S.CellValue("1234.5") })));
        var sheets = workbookPart.Workbook.AppendChild(new S.Sheets());
        sheets.Append(new S.Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = "Known Sheet"
        });
        workbookPart.Workbook.Save();
    }

    private static void AddCompressedEntry(string path, string entryName, long length)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        var buffer = new byte[64 * 1024];
        long written = 0;
        while (written < length)
        {
            var count = (int)Math.Min(buffer.Length, length - written);
            stream.Write(buffer, 0, count);
            written += count;
        }
    }

    private static void CreatePptx(string path, params string[] slideTexts)
    {
        using var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentationPart = document.AddPresentationPart();
        presentationPart.Presentation = new P.Presentation(new P.SlideIdList());
        uint id = 256;
        foreach (var text in slideTexts)
        {
            var slidePart = presentationPart.AddNewPart<SlidePart>();
            slidePart.Slide = new P.Slide(
                new P.CommonSlideData(
                    new P.ShapeTree(
                        new P.NonVisualGroupShapeProperties(
                            new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                            new P.NonVisualGroupShapeDrawingProperties(),
                            new P.ApplicationNonVisualDrawingProperties()),
                        new P.GroupShapeProperties(new A.TransformGroup()),
                        new P.Shape(
                            new P.NonVisualShapeProperties(
                                new P.NonVisualDrawingProperties { Id = 2U, Name = "Text" },
                                new P.NonVisualShapeDrawingProperties(),
                                new P.ApplicationNonVisualDrawingProperties()),
                            new P.ShapeProperties(),
                            new P.TextBody(
                                new A.BodyProperties(),
                                new A.ListStyle(),
                                new A.Paragraph(new A.Run(new A.Text(text))))))));
            slidePart.Slide.Save();
            ((P.SlideIdList)presentationPart.Presentation.SlideIdList!).Append(
                new P.SlideId { Id = id++, RelationshipId = presentationPart.GetIdOfPart(slidePart) });
        }
        presentationPart.Presentation.Save();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Timenotifier.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
