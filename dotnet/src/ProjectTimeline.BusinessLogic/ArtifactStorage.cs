using Microsoft.Extensions.Configuration;

namespace ProjectTimeline.BusinessLogic;

public sealed class ArtifactStorage(IConfiguration configuration)
{
    public const long DefaultMaxBytes = 10 * 1024 * 1024;
    private readonly string basePath = Path.GetFullPath(configuration["ArtifactStorage:BasePath"] ?? "./artifacts");
    public long MaxBytes => configuration.GetValue("ArtifactStorage:MaxBytes", DefaultMaxBytes);

    public string GetSafePath(string projectCode, string timelineCode, int entryNumber, string storageName)
    {
        var safeName = Path.GetFileName(storageName);
        if (safeName != storageName || string.IsNullOrWhiteSpace(safeName)) throw new ArgumentException("Unsafe file name.");
        foreach (var segment in new[] { projectCode, timelineCode }) if (segment.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_')) throw new ArgumentException("Unsafe path segment.");
        var path = Path.GetFullPath(Path.Combine(basePath, projectCode, timelineCode, entryNumber.ToString(), safeName));
        if (!path.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Path traversal is not allowed.");
        return path;
    }

    public async Task<string> SaveAsync(string projectCode, string timelineCode, int entryNumber, string originalName, string? contentType, long length, Stream content, CancellationToken ct)
    {
        if (length <= 0) throw new ArgumentException("The artifact must not be empty.");
        if (length > MaxBytes) throw new ArgumentException($"The artifact exceeds the configured maximum of {MaxBytes} bytes.");
        var name = Path.GetFileName(originalName);
        if (name != originalName || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Unsafe file name.");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var allowed = new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".md", ".markdown" };
        if (!allowed.Contains(extension)) throw new ArgumentException("The artifact file type is not allowed.");
        var normalized = (contentType ?? "").Split(';', 2)[0].Trim().ToLowerInvariant();
        var expected = extension switch { ".pdf" => new[] { "application/pdf" }, ".doc" => new[] { "application/msword", "application/octet-stream" }, ".docx" => new[] { "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/zip", "application/octet-stream" }, ".xls" => new[] { "application/vnd.ms-excel", "application/octet-stream" }, ".xlsx" => new[] { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "application/zip", "application/octet-stream" }, _ => new[] { "text/markdown", "text/plain", "application/octet-stream" } };
        if (!expected.Contains(normalized)) throw new ArgumentException("The artifact content type is not valid for its extension.");
        var bytes = new byte[Math.Min(length, 8)]; var read = await content.ReadAsync(bytes.AsMemory(), ct); if (read == 0) throw new ArgumentException("The artifact must not be empty.");
        if (extension == ".pdf" && !bytes.AsSpan(0, read).StartsWith("%PDF-"u8)) throw new ArgumentException("The PDF signature is invalid.");
        if (new[] { ".doc", ".xls" }.Contains(extension) && !bytes.AsSpan(0, read).StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 })) throw new ArgumentException("The Office binary signature is invalid.");
        if (new[] { ".docx", ".xlsx" }.Contains(extension) && !bytes.AsSpan(0, read).StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 })) throw new ArgumentException("The Office container signature is invalid.");
        var storageName = $"{Guid.NewGuid():N}{extension}";
        var path = GetSafePath(projectCode, timelineCode, entryNumber, storageName);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = File.Create(path);
            await output.WriteAsync(bytes.AsMemory(0, read), ct);
            await content.CopyToAsync(output, ct);
            return storageName;
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }
    public void Delete(string projectCode, string timelineCode, int entryNumber, string storageName) { var path = GetSafePath(projectCode, timelineCode, entryNumber, storageName); if (File.Exists(path)) File.Delete(path); }
}
