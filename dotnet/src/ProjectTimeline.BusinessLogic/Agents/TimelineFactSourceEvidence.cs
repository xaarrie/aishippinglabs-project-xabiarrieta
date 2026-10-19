using System.Globalization;
using System.Security.Cryptography;

namespace ProjectTimeline.BusinessLogic.Agents;

public sealed record TimelineFactSourceSpan(string Id, string Source, int Start, int Length, string Text);

/// <summary>Lossless UTF-16 source positions, at most 256 code units per span and 81 spans per entry.
/// Surrogate pairs are never split. IDs bind the complete original input, field and position.</summary>
public static class TimelineFactSourceEvidence
{
    public static IReadOnlyList<TimelineFactSourceSpan> Create(TimelineFactExtraction source)
    {
        TimelineFactStructuredResponse.ValidateSource(source);
        // Hash exact UTF-16 code units (including unusual unpaired surrogates), not a normalized encoding.
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(source.Title.Length);
            foreach (var character in source.Title) writer.Write((ushort)character);
            writer.Write(source.Content.Length);
            foreach (var character in source.Content) writer.Write((ushort)character);
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
        var spans = new List<TimelineFactSourceSpan>();
        Add(source.Title, "Title", "T");
        Add(source.Content, "Content", "C");
        return spans.AsReadOnly();

        void Add(string text, string field, string prefix)
        {
            for (var start = 0; start < text.Length;)
            {
                var length = Math.Min(256, text.Length - start);
                if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1])
                    && char.IsLowSurrogate(text[start + length])) length--;
                spans.Add(new($"{fingerprint}:{prefix}:{start.ToString(CultureInfo.InvariantCulture)}", field, start, length, text.Substring(start, length)));
                start += length;
            }
        }
    }

    internal static string Reconstruct(TimelineFactExtraction source, IReadOnlyList<TimelineFactSourceSpan> spans,
        IReadOnlyList<string> evidenceIds)
    {
        if (evidenceIds is null || evidenceIds.Count is < 1 or > 81
            || evidenceIds.Any(string.IsNullOrWhiteSpace) || evidenceIds.Distinct(StringComparer.Ordinal).Count() != evidenceIds.Count)
            throw new TimelineFactProviderException("InvalidResponse", "InvalidEvidence");
        var lookup = spans.ToDictionary(x => x.Id, StringComparer.Ordinal);
        if (evidenceIds.Any(id => !lookup.ContainsKey(id)))
            throw new TimelineFactProviderException("InvalidResponse", "InvalidEvidence");
        var selected = evidenceIds.Select(id => lookup[id]).ToArray();
        if (selected.Any(x => x.Source != selected[0].Source))
            throw new TimelineFactProviderException("InvalidResponse", "InvalidEvidence");
        var original = selected[0].Source == "Title" ? source.Title : source.Content;
        var start = selected.Min(x => x.Start);
        var end = selected.Max(x => x.Start + x.Length);
        // Include intervening text, never concatenate disjoint fragments or accept model-written text.
        return original.Substring(start, end - start);
    }
}
