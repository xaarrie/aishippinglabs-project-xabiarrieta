using System.Text.Json;

namespace ProjectTimeline.SearchFactsJudge;

public sealed record SearchJudgePrediction(SearchResponseIdentity Identity, string Question, string? Answer,
    JsonElement ResponseEvidence, JsonElement ToolEvidence, HumanSearchAnnotation Human, string Status,
    SearchResponseJudgment? Judgment, string? ErrorCode);

public sealed record SearchJudgeMetrics(int TotalSource, int TotalLabels, int Unlabeled, int Attempted,
    int Success, int Error, int Canceled, int NotAttempted, int TP, int FP, int FN, int TN,
    double? Coverage, double? LabelCoverage, double? Accuracy, double? PrecisionBad, double? RecallBad)
{
    public string EvaluationKind => "CalibrationNotHeldOut";

    public static SearchJudgeMetrics Calculate(int sourceCount, IReadOnlyList<SearchJudgePrediction> rows)
    {
        var success = rows.Where(r => r.Status == "Success").ToArray();
        var tp = success.Count(r => r.Human.Label == "Bad" && r.Judgment!.Label == "bad");
        var fp = success.Count(r => r.Human.Label == "Good" && r.Judgment!.Label == "bad");
        var fn = success.Count(r => r.Human.Label == "Bad" && r.Judgment!.Label == "good");
        var tn = success.Count(r => r.Human.Label == "Good" && r.Judgment!.Label == "good");
        var errors = rows.Count(r => r.Status == "Error");
        var canceled = rows.Count(r => r.Status == "Canceled");
        return new(sourceCount, rows.Count, sourceCount - rows.Count, success.Length + errors + canceled,
            success.Length, errors, canceled, rows.Count(r => r.Status == "NotAttempted"), tp, fp, fn, tn,
            Ratio(success.Length, rows.Count), Ratio(rows.Count, sourceCount), Ratio(tp + tn, success.Length),
            Ratio(tp, tp + fp), Ratio(tp, tp + fn));
    }

    private static double? Ratio(int numerator, int denominator) => denominator == 0 ? null : (double)numerator / denominator;
}
