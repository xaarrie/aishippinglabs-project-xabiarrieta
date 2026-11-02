using System.Text;

namespace ProjectTimeline.EvaluationLabeler;

internal static class Csv
{
    // Ordinary metadata remains directly joinable. Escape spreadsheet formulas/leading controls
    // and original apostrophes; stripping exactly one prefix also accepts old always-prefixed v1 CSVs.
    public static string Metadata(string text) => text.StartsWith('\'') || Unsafe(text) ? "'" + text : text;
    public static string DecodeMetadata(string text, int line)
        => text.StartsWith('\'') ? text[1..] : !Unsafe(text) ? text : throw new LabelerException("csv-metadata", line);

    private static bool Unsafe(string text)
    {
        foreach (var c in text)
        {
            if (char.IsControl(c)) return true;
            if (char.IsWhiteSpace(c)) continue;
            return c is '=' or '+' or '-' or '@';
        }
        return false;
    }

    public static string Row(IEnumerable<string> cells)
        => string.Join(",", cells.Select(cell => "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")) + "\r\n";

    public static List<(int Line, string[] Cells)> Parse(string text)
    {
        if (text.StartsWith('\uFEFF')) text = text[1..];
        var rows = new List<(int, string[])>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var closed = false;
        var line = 1;
        var startLine = line;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else { quoted = false; closed = true; }
                }
                else { cell.Append(c); if (c == '\n') line++; }
                continue;
            }
            if (c == '"' && cell.Length == 0 && !closed) { quoted = true; continue; }
            if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); closed = false; continue; }
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                cells.Add(cell.ToString()); cell.Clear();
                rows.Add((startLine, cells.ToArray())); cells.Clear(); closed = false;
                i++; line++; startLine = line;
                continue;
            }
            if (closed || c is '"' or '\r' or '\n') throw new LabelerException("csv-syntax", line);
            cell.Append(c);
        }
        if (quoted) throw new LabelerException("csv-unclosed-quote", startLine);
        if (closed || cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString()); rows.Add((startLine, cells.ToArray()));
        }
        return rows;
    }
}
