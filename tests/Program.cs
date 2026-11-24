using System.Diagnostics;

if (args.Length != 3) throw new ArgumentException("Expected AgentFlow, Claude fixture and sandbox paths.");
var app = Path.GetFullPath(args[0]);
var fixture = Path.GetFullPath(args[1]);
var sandbox = Path.GetFullPath(args[2]);
var scheduler = new WindowsScheduler();
var jobs = new List<Job>();
var registered = new HashSet<string>();
var failures = 0;

await Test("CLI input, environment, streams and exit codes", async () =>
{
    var job = Create("성공 — café\nsecond line");
    Check(Job.Load(job.Id).Prompt == job.Prompt, "Schedule persisted");
    Check(await Run(job.Id) == 0, "Success exit code");
    var log = File.ReadAllText(job.LogPath);
    foreach (var expected in new[] { "--print --agent test:agent --permission-mode default",
        "cwd: " + job.Project, "CLAUDECODE: <unset>", "성공 — café", "second line", "[stderr] fixture stderr" })
        Check(log.Contains(expected), expected);
    Check(File.ReadAllText(job.ResultPath) == "Completed", "Success result");
    var prior = log;
    Check(await Run(job.Id) == 1 && File.ReadAllText(job.LogPath) == prior, "One-time run claimed only once");

    job = Create("fail", daily: true);
    Check(await Run(job.Id) == 17 && File.ReadAllText(job.ResultPath) == "Failed (17)", "CLI failure");
    job.Prompt = "new run";
    File.Delete(Path.Combine(job.Folder, "job.xml"));
    job.Save();
    var previousLog = job.LogPath;
    using (File.Open(previousLog, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(await Run(job.Id) == 0 && File.ReadAllText(job.ResultPath) == "Completed", "Daily result replaced while previous log is open");
        Check(job.LogPath != previousLog && File.ReadAllText(job.LogPath).Contains(job.Prompt), "Latest log contains the new run");
    }
});

await Test("Missing CLI, invalid schedule ID and invalid agent", async () =>
{
    Check(await Run("../invalid") == 1, "Invalid ID rejected");
    var missing = Create("missing", claude: Path.Combine(sandbox, "missing", "claude.exe"));
    Check(await Run(missing.Id) == 1 && File.ReadAllText(missing.ResultPath) == "Failed (1)", "Missing executable reported");
    var invalid = Create("invalid", agent: "bad agent");
    Check(await Run(invalid.Id) == 1 && File.ReadAllText(invalid.LogPath).Contains("agent name is invalid"), "Invalid agent rejected");
});

await Test("Daily run succeeds while the legacy run.log is open", async () =>
{
    var job = Create("first run after upgrade", daily: true);
    var legacyLog = Path.Combine(job.Folder, "run.log");
    File.WriteAllText(legacyLog, "earlier log");
    Check(job.LogPath == legacyLog, "Existing daily log remains available before the first new run");
    using (File.Open(legacyLog, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(await Run(job.Id) == 0, "An open legacy log does not block the run");
        Check(File.ReadAllText(job.LogPath).Contains(job.Prompt), "Open Log resolves to the new run");
        Check(File.ReadAllText(legacyLog) == "earlier log", "Open legacy log remains intact");
    }
    Check(await Run(job.Id) == 0, "A subsequent daily run succeeds");
    Check(!File.Exists(legacyLog), "Legacy log is cleaned up once it is no longer open");
});

await Test("Daily task registration, status, enable, disable and cancel", async () =>
{
    var job = Create("hold", daily: true);
    try
    {
        scheduler.Register(job, app);
        registered.Add(job.Id);
        Check(scheduler.Status(job, out var enabled) == "Scheduled" && enabled == true, "Registered and enabled");
        scheduler.SetEnabled(job, false);
        Check(scheduler.Status(job, out enabled) == "Off" && enabled == false, "Disabled");
        scheduler.SetEnabled(job, true);
        Check(scheduler.Status(job, out enabled) == "Scheduled" && enabled == true, "Re-enabled");
        using var running = Start(job.Id);
        try
        {
            await WaitUntil(() => File.Exists(Path.Combine(job.Project, "fixture.ready")));
            Check(await Run(job.Id) == 1, "Concurrent run blocked");
        }
        finally { File.WriteAllText(Path.Combine(job.Project, "fixture.release"), "release"); }
        Check(await Exit(running) == 0, "Active run finished");
        Check(scheduler.Status(job, out enabled) == "Last run: Completed", "Last run displayed");
        scheduler.Cancel(job);
        registered.Remove(job.Id);
        Check(scheduler.Status(job, out enabled) == "Canceled" && enabled == null, "Canceled status");
        var previousLog = File.ReadAllText(job.LogPath);
        Check(await Run(job.Id) == 0 && File.ReadAllText(job.LogPath) == previousLog, "Canceled run skipped");
    }
    finally { CleanupTask(job); }
});

await Test("An externally removed daily task can still be canceled", async () =>
{
    var job = Create("must not run after cancellation", daily: true);
    try
    {
        scheduler.Register(job, app);
        registered.Add(job.Id);
        CleanupTask(job);
        Check(scheduler.Status(job, out var enabled) == "Scheduled task not found" && enabled == null,
            "Externally removed task reported");
        scheduler.Cancel(job);
        scheduler.Cancel(job);
        Check(File.Exists(job.CanceledPath) && scheduler.Status(job, out _) == "Canceled",
            "Local cancellation recorded even when the Windows task is missing");
        Check(await Run(job.Id) == 0 && !File.Exists(job.LogPath), "Canceled worker skips execution");
    }
    finally { CleanupTask(job); }
});

await Test("Task launch failure supersedes an earlier result", async () =>
{
    var job = Create("earlier success", scheduledUtc: DateTime.UtcNow.AddSeconds(8));
    try
    {
        File.WriteAllText(job.ResultPath, "Completed");
        scheduler.Register(job, Path.Combine(sandbox, "missing", "AgentFlow.exe"));
        registered.Add(job.Id);
        // Use a one-time trigger to exercise the daily status handling without waiting for a wall-clock minute.
        job.Daily = true;
        await WaitUntil(() => scheduler.Status(job, out _).StartsWith("Run error (", StringComparison.Ordinal), 45);
        Check(File.ReadAllText(job.ResultPath) == "Completed", "The earlier result remains on disk");
        scheduler.SetEnabled(job, false);
        string paused = scheduler.Status(job, out bool? enabled, out _, out _, out string runResult);
        Check(paused == "Off" && enabled == false && runResult.StartsWith("Run error (", StringComparison.Ordinal),
            "Pausing preserves the most recent launch failure");
    }
    finally { CleanupTask(job); }
});

await Test("Windows Task Scheduler launches AgentFlow and the fixture", async () =>
{
    var job = Create("scheduled execution", scheduledUtc: DateTime.UtcNow.AddSeconds(8));
    try
    {
        scheduler.Register(job, app);
        registered.Add(job.Id);
        await WaitUntil(() => File.Exists(job.ResultPath), 45);
        Check(File.ReadAllText(job.ResultPath) == "Completed", "Scheduled result");
        Check(File.ReadAllText(job.LogPath).Contains(job.Prompt), "Scheduled process received prompt");
    }
    finally { CleanupTask(job); }
});

foreach (var job in jobs) Directory.Delete(job.Folder, true);
return failures == 0 ? 0 : 1;

async Task Test(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}

Job Create(string prompt, bool daily = false, string claude = null, string agent = "test:agent", DateTime? scheduledUtc = null)
{
    var id = Guid.NewGuid().ToString("N");
    var tomorrowTime = DateTime.Now.AddHours(12);
    var project = Directory.CreateDirectory(Path.Combine(sandbox, id)).FullName;
    var job = new Job { Id = id, Project = project, Claude = claude ?? fixture, Agent = agent, Prompt = prompt,
        Daily = daily, ScheduledUtc = scheduledUtc ?? DateTime.UtcNow, DailyHour = tomorrowTime.Hour, DailyMinute = tomorrowTime.Minute };
    job.Save();
    jobs.Add(job);
    return job;
}

Process Start(string id)
{
    var start = new ProcessStartInfo(app) { UseShellExecute = false };
    start.ArgumentList.Add("--run");
    start.ArgumentList.Add(id);
    start.Environment["CLAUDECODE"] = "test-parent";
    return Process.Start(start) ?? throw new InvalidOperationException("Could not start AgentFlow.");
}

async Task<int> Run(string id)
{
    using var process = Start(id);
    return await Exit(process);
}

static async Task<int> Exit(Process process)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
    return process.ExitCode;
}

static async Task WaitUntil(Func<bool> condition, int seconds = 15)
{
    var elapsed = Stopwatch.StartNew();
    while (!condition())
    {
        if (elapsed.Elapsed >= TimeSpan.FromSeconds(seconds)) throw new TimeoutException("Expected file was not produced.");
        await Task.Delay(100);
    }
}

static void Check(bool condition, string expectation)
{
    if (!condition) throw new InvalidOperationException(expectation);
}

void CleanupTask(Job job)
{
    if (!registered.Remove(job.Id)) return;
    dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!);
    service.Connect();
    service.GetFolder("\\").DeleteTask(job.TaskName, 0);
}
