using System.Security.Cryptography;
using System.Text;
using ProjectTimeline.BusinessLogic;

namespace ProjectTimeline.SearchFactsEvaluator;

public sealed record ScenarioField(string Header, string Value);
public sealed record Scenario(int Ordinal, string Question, IReadOnlyList<ScenarioField> Fields);
public sealed record ScenarioInput(string Path, string Sha256, IReadOnlyList<Scenario> Scenarios)
{
    public static ScenarioInput Parse(byte[] bytes, string path)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new FormatException("CSV must be valid UTF-8."); }
        if (text.StartsWith('\uFEFF')) text = text[1..];
        var rows = ReadRecords(text);
        if (rows.Count < 2) throw new FormatException("CSV requires a header and scenarios.");
        var headers = rows[0];
        if (headers.Any(string.IsNullOrWhiteSpace)
            || headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length
            || Array.IndexOf(headers, "question") < 0)
            throw new FormatException("CSV requires unique nonblank headers and an exact question header.");
        var questionIndex = Array.IndexOf(headers, "question");
        var scenarios = new List<Scenario>();
        foreach (var row in rows.Skip(1))
        {
            var ordinal = scenarios.Count + 1;
            if (row.Length != headers.Length) throw new FormatException($"CSV field count at scenario {ordinal}.");
            try { TimelineFactInformationSearch.ValidateQuestion(row[questionIndex]); }
            catch (ArgumentException) { throw new FormatException($"Invalid question at scenario {ordinal}."); }
            scenarios.Add(new(ordinal, row[questionIndex], headers.Select((h, i) => new ScenarioField(h, row[i])).ToArray()));
        }
        return new(System.IO.Path.GetFullPath(path), Convert.ToHexString(SHA256.HashData(bytes)), scenarios);
    }

    // Strict RFC-style quoting; unlike blank-line-skipping readers, every physical record is accounted for.
    private static List<string[]> ReadRecords(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closed = false;
        var recordStarted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else { quoted = false; closed = true; }
                continue;
            }
            if (c is ',' or '\r' or '\n')
            {
                fields.Add(field.ToString()); field.Clear(); closed = false;
                if (c == ',') { recordStarted = true; continue; }
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                if (!recordStarted) throw new FormatException("Blank CSV records are not allowed.");
                rows.Add(fields.ToArray()); fields.Clear(); recordStarted = false;
                continue;
            }
            if (closed) throw new FormatException("Unexpected text after a quoted CSV field.");
            if (c == '"')
            {
                if (field.Length != 0) throw new FormatException("Unexpected quote in CSV field.");
                quoted = true;
            }
            else field.Append(c);
            recordStarted = true;
        }
        if (quoted) throw new FormatException("Unclosed quoted CSV field.");
        if (recordStarted) { fields.Add(field.ToString()); rows.Add(fields.ToArray()); }
        return rows;
    }
}
