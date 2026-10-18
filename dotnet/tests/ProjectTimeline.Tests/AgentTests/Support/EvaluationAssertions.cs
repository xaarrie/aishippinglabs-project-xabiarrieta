using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests.Support;

// Bounded lexical relations + schema invariants, NOT independent semantic verification.
// Each rule searches an Entry component, never serialized whole-result JSON or metadata.
internal static class EvaluationAssertions
{
    private sealed record Rule(string Key, params string[] Patterns);
    private static string Near(string left, string right, int distance = 100) =>
        $@"(?:{left}).{{0,{distance}}}(?:{right})|(?:{right}).{{0,{distance}}}(?:{left})";

    private static readonly IReadOnlyDictionary<string, Rule[]> Rules = new Dictionary<string, Rule[]> {
        ["software-details"] = [
            new("ui-owner", Near(@"\bluis\b", @"\b(ui|user interface|frontend|front.end)\b")),
            new("api-owner", Near(@"\bpablo\b", @"\b(api|backend|back.end)\b")),
            new("password-only", @"password[- ]only|only.{0,30}password|password.{0,30}only"),
            new("no-sso", @"(?:no|without|exclude\w*|not.{0,15}(?:use|include|support)).{0,30}\bsso\b"),
            new("tentative-friday", Near(@"\bfriday\b", @"might|may\b|tentative|perhaps|provisional|unconfirmed|not confirmed")),
            new("security-approval", @"(?:only|subject to|conditional|contingent|pending|provided|if).{0,60}security.{0,35}approv"),
            new("attempts", @"(?:five|5)\s+(?:failed\s+)?(?:login\s+)?attempts", @"human.{0,30}accounts?", @"lock"),
            new("lock-duration", Near(@"lock\w*.{0,25}human.{0,20}accounts?|human.{0,20}accounts?.{0,25}lock\w*", @"(?:ten|10)\s+minutes?")),
            new("service-exemption", @"service\s+accounts?.{0,60}(?:exempt|excluded|not.{0,20}(?:lock|subject))|(?:exempt|exclud\w*|does not apply).{0,60}service\s+accounts?"),
            new("audit-retention", Near(@"audit.{0,15}(?:logs?|records?|retention)", @"(?:thirty|30)[- ]days?"))
        ],
        ["travel-details"] = [
            new("passengers", @"(?:4|cuatro)\s+(?:personas|pasajeros)", @"aeropuerto.{0,20}lisboa", @"alojamiento"),
            new("address-condition", @"(?:solo|unicamente).{0,25}(?:cuando|tras|despues).{0,35}ane.{0,25}confirm.{0,30}direccion"),
            new("total-budget", Near(@"(?:limite|presupuesto|coste|maximo).{0,15}total|total.{0,15}(?:limite|presupuesto|maximo)|\btotal\b", @"80\s*(?:€|eur|euros)"), @"(?:incluid\w*|incluy\w*|con).{0,15}peajes"),
            new("no-surcharge", @"(?:no.{0,15}(?:acept\w*|admit\w*|permit\w*)|sin).{0,30}(?:suplementos|recargos).{0,20}nocturn"),
            new("child-seats", @"(?:dos|2)\s+sillas?", @"3\s+anos", @"6\s+anos"),
            new("luggage", @"(?:cada|por)\s+persona", @"(?:una|1)\s+maleta", @"(?:hasta|maximo|limite).{0,15}10\s*(?:kg|kilogramos)"),
            new("tentative-arrival", @"viernes.{0,20}tarde", @"quiza|tentativ|podria|provisional|no.{0,15}(?:cerrada|confirmada)", @"(?:si|condicionad\w*).{0,25}confirm.{0,25}vuelo"),
            new("receipt-sharing", @"(?:conserv\w*|guard\w*).{0,25}(?:justificante|recibo)", @"(?:repart\w*|divid\w*).{0,35}(?:partes iguales|equitativ\w*)")
        ],
        ["business-details"] = [
            new("retention-range", @"72\s*%.{0,35}80\s*%", Near(@"retention|renewal", @"north.{0,15}pilot")),
            new("cohorts", @"(?:three|3)\b", @"complete|full", @"monthly.{0,20}cohorts?|cohorts?.{0,20}monthly"),
            new("exclude-trials", @"(?:exclud\w*|not includ\w*|without).{0,25}trial\s+accounts?"),
            new("discount-cap", @"(?:not.{0,20}increase|no.{0,15}(?:more|higher)|cap\w*|at most|maximum|up to).{0,45}(?:discounts?.{0,25}5\s*%|5\s*%.{0,25}discounts?)"),
            new("finance-condition", @"(?:only after|subject to|conditional on|requires?).{0,35}finance.{0,35}approv.{0,30}budget"),
            new("support-rationale", @"support.{0,25}(?:team|involv)", @"cancellations?.{0,50}unresolved\s+tickets?", @"because|due to|since|start|originate")
        ]
    };

    public static void Fact(string scenario, string key, EntryReviewResult result)
    {
        var rule = Rules[scenario].Single(rule => rule.Key == key);
        Assert.True(Fragments(result.Entry).Any(fragment => rule.Patterns.All(pattern => Match(fragment, pattern))),
            $"Entry is missing lexical relation '{scenario}/{key}'. Inspect the actual response; lexical checks are not a semantic audit.");
    }

    public static void Facts(string scenario, EntryReviewResult result)
    {
        foreach (var rule in Rules[scenario]) Fact(scenario, rule.Key, result);
        if (scenario == "software-details") CorrectSoftwareOwners(result);
    }

    public static void CorrectSoftwareOwners(EntryReviewResult result)
    {
        foreach (var task in result.Entry.Tasks.Where(task => !string.IsNullOrWhiteSpace(task.Owner)))
        {
            var subject = $"{task.Title} {task.Description}";
            if (Match(subject, @"\b(ui|user interface|frontend|front.end)\b"))
                Assert.True(Match(task.Owner!, @"\bluis\b") && !Match(task.Owner!, @"\bpablo\b"), "UI owner was swapped or broadened.");
            if (Match(subject, @"\b(api|backend|back.end)\b"))
                Assert.True(Match(task.Owner!, @"\bpablo\b") && !Match(task.Owner!, @"\bluis\b"), "API owner was swapped or broadened.");
        }
    }

    public static void Ready(EntryReviewResult result)
    {
        Assert.Equal(Status.Ready, result.Status);
        Assert.Empty(result.Questions);
        Assert.Empty(Pending(result));
    }

    public static void ReadyWithoutOptionalInventions(EntryReviewResult result)
    {
        Ready(result);
        Assert.All(result.Entry.Tasks, task => { Assert.Null(task.TargetDate); Assert.Empty(task.AcceptanceCriteria); });
        Assert.All(result.Entry.Objectives, objective => Assert.Empty(objective.AcceptanceCriteria));
        Assert.DoesNotContain(Fragments(result.Entry).Concat(result.DigestChanges.Select(change => change.Description)),
            text => Match(text,
                @"\b(?:luis|pablo|ane|priya|omar)\b.{0,20}(?:is|as|will be|acts as).{0,20}\b(?:leader|team lead|lider)\b" +
                @"|(?:leader|team lead|lider)\s*[:=]\s*\w+|\b20\d{2}-\d{2}-\d{2}\b" +
                @"|(?:deadline|fecha limite|acceptance criteria|criterios de aceptacion)\s*[:=]\s*(?!not\b|none\b|unspecified\b|unknown\b|no\b)\w+"));
    }

    public static void Assignment(EntryReviewResult result, string subject, params string[] people)
    {
        foreach (var person in people)
            Assert.Contains(Fragments(result.Entry), text => Match(text, Near(person, subject)));
    }

    public static void OpenQuestions(EntryReviewResult result, params string[] expectedIds)
    {
        Assert.Equal(Status.NeedsClarification, result.Status);
        Assert.NotEmpty(result.Questions);
        var ids = result.Questions.Select(question => question.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches(@"^Q[1-9]\d*$", id));
        if (expectedIds.Length > 0) Assert.Equal(expectedIds.Order(), ids.Order());
        var pending = Pending(result).ToArray();
        Assert.All(pending, id => Assert.Contains(id, ids));
        Assert.All(ids, id => Assert.Contains(id, pending));
    }

    public static void TeamUpdate(EntryReviewResult result, bool existingMarta)
    {
        ReadyWithoutOptionalInventions(result);
        Assignment(result, "login", "luis", "pablo");
        var updates = result.DigestChanges.Where(change => Match(change.Description, @"login|team")).ToArray();
        var update = Assert.Single(updates);
        Assert.Equal(ChangeAction.Update, update.Action);
        Assert.Contains(Fragments(result.Entry), text => Match(text, @"prior|previous|earlier|context") && Match(text, @"plan\w*") && Match(text, "login|team"));
        Assert.DoesNotContain(result.Entry.Tasks, task => Match(task.Status ?? "", @"complete|done|finished|active|in progress"));
        var text = string.Join(" ", Fragments(result.Entry).Concat(updates.Select(change => change.Description)));
        Assert.False(Match(text, @"exclusiv\w*|only members|sole members|replace\w*.{0,25}marta|marta.{0,25}remov\w*|\b(leader|frontend|backend|ui|api)\b"),
            "Invented roles, exclusive membership or replacement of an existing team.");
        if (existingMarta)
        {
            Assert.Contains(Fragments(result.Entry), fragment => Match(fragment, @"marta") && Match(fragment, @"prior|previous|existing|already|context|earlier"));
            Assert.True(Match(update.Description, @"marta"), "The proposed team update silently drops existing Marta.");
        }
    }

    public static void PartialAnswers(EntryReviewResult result)
    {
        OpenQuestions(result, "Q2");
        Assert.Contains(result.Questions, question => Match(question.Description, "owner|reporting|responsib"));
        Assert.DoesNotContain("Q1", Pending(result));
        DateAndSource(result);
        Assert.Contains(Fragments(result.Entry), text => Match(text, @"luis.{0,40}(?:or|/).{0,20}pablo|pablo.{0,40}(?:or|/).{0,20}luis") && Match(text, "owner|reporting"));
        Assert.DoesNotContain(result.Entry.Tasks, task => Match(task.Owner ?? "", @"^(luis|pablo)$"));
        Assert.DoesNotContain(Fragments(result.Entry), text => Match(text,
            @"(?:luis|pablo)\s+(?:owns\s+reporting|is (?:the )?(?:confirmed )?(?:reporting )?owner)|(?:reporting )?owner\s*[:=]\s*(?:luis|pablo)\s*(?:[.;]|$)"));
    }

    public static void CompletedAnswers(EntryReviewResult result, string expectedOwner = "Pablo",
        IReadOnlyCollection<string>? dateAnswerIds = null, IReadOnlyCollection<string>? ownerAnswerIds = null)
    {
        Ready(result);
        DateAndSource(result, dateAnswerIds ?? ["Q1"]);
        Assignment(result, "reporting", Regex.Escape(expectedOwner));
        var owner = $@"\b{Regex.Escape(Normalize(expectedOwner))}\b";
        var ownership = owner + @"\s+(?:(?:owns|will own)\s+(?:the\s+)?reporting" +
            @"|(?:(?:is|as|will be)\s+(?:the\s+)?)?(?:reporting\s+owner|(?:person\s+)?responsible\s+for\s+reporting)" +
            @"|(?:will\s+)?(?:take|takes|has)\s+responsibility\s+for\s+reporting)" +
            @"|\breporting\s+(?:(?:owner|responsibility)\s*(?:[:=]|is|will be|belongs to)\s*" + owner +
            @"|(?:is|will be)\s+owned\s+by\s+" + owner + ")";
        ClarifiedAnswer(result, ownership, ownerAnswerIds ?? ["Q2"], "reporting ownership");
        Assert.DoesNotContain(result.Entry.Tasks, task => Match($"{task.Title} {task.Description}", "reporting")
            && Match(task.Owner ?? "", @"\bluis\b"));
    }

    private static void DateAndSource(EntryReviewResult result, IReadOnlyCollection<string>? dateAnswerIds = null)
    {
        foreach (var key in new[] { "password-only", "no-sso", "audit-retention" }) Fact("software-details", key, result);
        Assert.Contains(Fragments(result.Entry), text => Match(text, "login|rollout"));
        ClarifiedAnswer(result, "2026-10-23", dateAnswerIds ?? ["Q1"], "launch date");
        Assert.DoesNotContain(result.Entry.Tasks, task => task.TargetDate == "2026-10-16");
    }

    private static void ClarifiedAnswer(EntryReviewResult result, string factPattern,
        IReadOnlyCollection<string> answeredIds, string factName)
    {
        Assert.NotEmpty(answeredIds);
        Assert.True(AnswerStatements(result.Entry).Any(statement => Match(statement, factPattern)
            && HasAnswerAttribution(statement, answeredIds)),
            $"Entry lacks locally linked user-answer provenance for {factName}. Source repetition or unrelated answer labels do not count.");
    }

    private static bool HasAnswerAttribution(string statement, IReadOnlyCollection<string> answeredIds)
    {
        var references = Regex.Matches(statement, @"\bQ[1-9]\d*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)).Select(match => match.Value).ToArray();
        // An explicit but unrelated ID cannot fall back to a generic 'user clarification' hit.
        if (references.Length > 0 && !references.Any(reference => answeredIds.Contains(reference, StringComparer.OrdinalIgnoreCase)))
            return false;
        return Match(statement, @"\buser(?:'s)?\s+(?:answers?|responses?|repl(?:y|ies)|clarifications?|confirmed|clarified|specified|selected|named)\b" +
            @"|(?:confirmed|clarified|specified)\s+by\s+(?:the\s+)?user\b")
            || (references.Length > 0 && Match(statement, @"\b(?:answers?|responses?|repl(?:y|ies)|clarifications?|resolved|confirmed)\b|^\s*Q[1-9]\d*\s*[:=-]"));
    }

    // Do not join task fields: a date answer in Details must not attribute an Owner field or
    // a separate source sentence to the user. This local lexical boundary can reject valid
    // cross-sentence paraphrases; it is not independent semantic verification.
    private static IEnumerable<string> AnswerStatements(OptimizedEntry entry)
    {
        var fields = new[] { entry.Title, entry.Context }.Concat(entry.AdditionalDetails).Concat(entry.Assumptions).Concat(entry.Risks)
            .Concat(entry.Tasks.SelectMany(task => new[] { task.Title, task.Description, task.Owner ?? "" }
                .Concat(task.Details).Concat(task.Constraints).Concat(task.Quantities).Concat(task.AcceptanceCriteria)))
            .Concat(entry.Objectives.SelectMany(objective => new[] { objective.Title, objective.Description }
                .Concat(objective.Details).Concat(objective.AcceptanceCriteria)))
            .Concat(entry.Decisions.SelectMany(decision => new[] { decision.Description, decision.Rationale ?? "" }));
        return fields.SelectMany(field => Regex.Split(field, @"[.;!?\r\n]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
    }

    internal static IEnumerable<string> Pending(EntryReviewResult result) => result.Entry.Tasks.SelectMany(task => task.PendingQuestionIds)
        .Concat(result.Entry.Objectives.SelectMany(objective => objective.PendingQuestionIds))
        .Concat(result.Entry.Decisions.SelectMany(decision => decision.PendingQuestionIds));

    internal static IEnumerable<string> Fragments(OptimizedEntry entry)
    {
        yield return entry.Title;
        yield return entry.Context;
        foreach (var detail in entry.AdditionalDetails.Concat(entry.Assumptions).Concat(entry.Risks)) yield return detail;
        foreach (var task in entry.Tasks)
            yield return string.Join(" ", new[] { task.Title, task.Description, task.Owner, task.Status, task.TargetDate }
                .Concat(task.Details).Concat(task.Constraints).Concat(task.Quantities).Concat(task.AcceptanceCriteria));
        foreach (var objective in entry.Objectives)
            yield return string.Join(" ", new[] { objective.Title, objective.Description }.Concat(objective.Details).Concat(objective.AcceptanceCriteria));
        foreach (var decision in entry.Decisions) yield return $"{decision.Description} {decision.Rationale}";
    }

    private static bool Match(string text, string pattern) => Regex.IsMatch(Normalize(text), pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static string Normalize(string text) => new(text.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray());
}
