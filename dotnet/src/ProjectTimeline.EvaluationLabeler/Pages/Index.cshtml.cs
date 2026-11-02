using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ProjectTimeline.EvaluationLabeler.Pages;

public sealed class IndexModel(LabelStore store) : PageModel
{
    public Dataset Dataset => store.Dataset;
    public LabelSnapshot State { get; private set; } = null!;
    public EvaluationRecord Record { get; private set; } = null!;
    public Annotation? Annotation { get; private set; }

    public IActionResult OnGet()
    {
        if (Request.Query.Keys.Any(key => key != "record")) return BadRequest();
        State = store.Snapshot;
        var selection = State.Resume(Dataset);
        if (Request.Query.TryGetValue("record", out var value)
            && (value.Count != 1 || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out selection)
                || selection < 0 || selection >= Dataset.Records.Length)) return BadRequest();
        Record = Dataset.Records[selection];
        Annotation = State.Labels.GetValueOrDefault(selection);
        return Page();
    }

    public IActionResult OnGetDownload()
    {
        if (Request.Query.Count != 1 || Request.Query["handler"] != "Download") return BadRequest();
        try { return File(store.DownloadCsv(), "text/csv; charset=utf-8", "evaluation-labels.csv"); }
        catch (LabelerException ex)
        {
            return new JsonResult(new { code = ex.Code,
                message = "No CSV downloaded. Save a label first; if storage changed, restart to validate before retrying." })
                { StatusCode = ex.Status };
        }
    }

    public async Task<IActionResult> OnPostLabelAsync()
    {
        if (!Request.HasFormContentType || Request.Query.Count != 1 || Request.Query["handler"] != "Label")
            return BadRequest();
        var form = await Request.ReadFormAsync();
        string[] allowed = ["record", "label", "category", "expectedRevision", "__RequestVerificationToken"];
        if (form.Count != allowed.Length || form.Keys.Any(key => !allowed.Contains(key))
            || form.Any(field => field.Value.Count != 1)
            || !int.TryParse(form["record"], NumberStyles.None, CultureInfo.InvariantCulture, out var record)
            || !long.TryParse(form["expectedRevision"], NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
            return BadRequest();
        try
        {
            var saved = store.Save(record, form["label"].ToString(), form["category"].ToString(), revision);
            var state = store.Snapshot;
            return new JsonResult(new { record, saved.Label, saved.FailureCategory, saved.Revision,
                labeled = state.Labeled, total = Dataset.Records.Length, pending = state.Pending });
        }
        catch (LabelerException ex)
        {
            var message = ex.Status == 409
                ? "Labels changed in another tab or on disk. Reload before editing; this click was not saved."
                : "This click was not saved. Check storage or selection, then reload or retry.";
            return new JsonResult(new { code = ex.Code, message }) { StatusCode = ex.Status };
        }
    }
}
