using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectTimeline.EvaluationLabeler;
using ProjectTimeline.SearchFactsJudge;
using ProjectTimeline.BusinessLogic.Agents.Framework;

// Artifact-only, single-pass unlabeled inference. No human truth or quality metrics.
if (args.Length != 4) return 1;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
try
{
    var dataset = Dataset.Load(args[0]);
    using var lineage = JsonDocument.Parse(File.ReadAllBytes(args[2]));
    var mapping = lineage.RootElement.GetProperty("mapping").EnumerateArray().ToArray();
    if (dataset.Records.Length != 17 || mapping.Length != 17) return 2;
    var rubricHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(args[3])));
    if (rubricHash != "4d9d3788a647bdeed81004c7b10391e8c04c4235f9d098e9fb1eef95a77c0992") return 3;
    var sourceMappings = new Dictionary<string,string>[17];
    string[] keys = ["sourceRunId", "sourceScenarioId", "sourceOrdinal", "sourceInputSha256", "sourceDatasetSha256"];
    for (var i=0; i<17; i++)
    {
        var r = dataset.Records[i]; var m = mapping[i];
        if (r.Ordinal != i+1 || r.Question != m.GetProperty("question").GetString()
            || r.RunId == m.GetProperty("sourceRunId").GetString()) return 4;
        var fields = r.Raw.GetProperty("scenario").GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("header").GetString()!, f => f.GetProperty("value").GetString()!, StringComparer.Ordinal);
        sourceMappings[i] = keys.ToDictionary(k => k, k => fields[k], StringComparer.Ordinal);
        if (keys.Any(k => fields[k] != m.GetProperty(k).GetString())) return 5;
    }
    var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey)) return 6;
    using var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    using var writer = new StreamWriter(file, new UTF8Encoding(false), leaveOpen:true);
    using var judge = SearchResponseJudge.CreateOpenAI(apiKey, promptPath:args[3]);
    using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(1500));
    for (var i=0; i<17; i++)
    {
        var record=dataset.Records[i]; var input=SearchJudgeInput.FromRecord(record);
        SearchResponseJudgment? judgment=null; string? error=null; var status="Success";
        var started=DateTimeOffset.UtcNow;
        using var cost=AgentCostSession.Start();
        try
        {
            if (overall.IsCancellationRequested) { status="NotAttempted"; error="OverallDeadline"; }
            else judgment = await judge.JudgeAsync(input, overall.Token);
        }
        catch (SearchJudgeException e) { status="Error"; error=e.Code; }
        catch (OperationCanceledException) { status="Canceled"; error="OverallDeadline"; }
        catch (Exception) { status="Error"; error="JudgeFailed"; }
        finally { cost.Dispose(); }
        var result = new
        {
            identity = new { datasetSha256=dataset.Sha256, record.RunId, record.ScenarioId, record.Ordinal },
            sourceMapping=sourceMappings[i], input.Question, input.Answer,
            responseEvidence=input.Payload.GetProperty("responseEvidence"), toolEvidence=input.Payload.GetProperty("toolEvidence"),
            status, judgment, errorCode=error, startedUtc=started, endedUtc=DateTimeOffset.UtcNow,
            model=SearchResponseJudge.DefaultModel, rubricSha256=rubricHash, cost=cost.GetSnapshot()
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(result,json));
        await writer.FlushAsync(); file.Flush(true);
        Console.WriteLine($"{i+1}/17: {status}" + (error is null ? "" : $" ({error})"));
    }
    return 0;
}
catch (Exception) { Console.WriteLine("Stopped: HarnessValidationOrExecutionFailed. No exception details exported."); return 10; }
