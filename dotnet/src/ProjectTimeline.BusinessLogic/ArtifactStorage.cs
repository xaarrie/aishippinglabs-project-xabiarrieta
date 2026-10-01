using Microsoft.Extensions.Configuration;

namespace ProjectTimeline.BusinessLogic;
public sealed class ArtifactStorage(IConfiguration configuration)
{
    private readonly string basePath = Path.GetFullPath(configuration["ArtifactStorage:BasePath"] ?? "./artifacts");
    public string GetSafePath(string projectCode, string timelineCode, int entryNumber, string fileName)
    {
        var safeName = Path.GetFileName(fileName); if (safeName != fileName || string.IsNullOrWhiteSpace(safeName)) throw new ArgumentException("Unsafe file name.");
        foreach (var segment in new[] { projectCode, timelineCode }) if (segment.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_')) throw new ArgumentException("Unsafe path segment.");
        var path = Path.GetFullPath(Path.Combine(basePath, projectCode, timelineCode, entryNumber.ToString(), safeName)); if (!path.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Path traversal is not allowed."); return path;
    }
    public async Task SaveAsync(string projectCode, string timelineCode, int entryNumber, string fileName, Stream content, CancellationToken ct) { var path = GetSafePath(projectCode, timelineCode, entryNumber, fileName); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await using var output = File.Create(path); await content.CopyToAsync(output, ct); }
}
