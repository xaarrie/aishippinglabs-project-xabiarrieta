using ProjectTimeline.SearchFactsJudge;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try { return await SearchJudgeConsole.RunAsync(args, Console.Out, Console.Error, ct: cancellation.Token); }
finally { Console.CancelKeyPress -= cancel; }
