using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AgentFlow;

internal sealed record Schedule(Job Job, string Status, string Result, bool? Enabled, DateTime? NextRun,
    DateTime? LastRun, string Log, bool Canceled, bool CanCancel)
{
    internal bool Failed => Result.StartsWith("Failed", StringComparison.Ordinal) || Result.StartsWith("Run error", StringComparison.Ordinal);
    public string Agent => Job.Agent;
    public string ProjectPath => Job.Project;
    public string ProjectName => Path.GetFileName(Path.TrimEndingDirectorySeparator(ProjectPath)) is { Length: > 0 } name ? name : ProjectPath;
    public string Time => Job.Daily
        ? DateTime.Today.AddHours(Job.DailyHour).AddMinutes(Job.DailyMinute).ToString("HH:mm")
        : Job.ScheduledUtc.ToLocalTime().ToString("MMM d, HH:mm");
    public string ResultText => Result switch { "Completed" => "Succeeded", "Off" => "Paused", _ => Result };
    public string LastRunText => LastRun switch
    {
        null => "—",
        DateTime run when run.Date == DateTime.Today => run.ToString("'Today', HH:mm"),
        DateTime run when run.Date == DateTime.Today.AddDays(-1) => run.ToString("'Yesterday', HH:mm"),
        DateTime run => run.ToString("MMM d, HH:mm")
    };
    public string ToggleName => $"Enable {Agent}, {Time}";
    public string ToggleId => $"EnableSchedule-{Job.Id}";
}

internal static class ScheduleStore
{
    internal static Schedule[] Load()
    {
        if (!Directory.Exists(Job.Root)) return Array.Empty<Schedule>();
        var rows = new List<Schedule>();
        var scheduler = new WindowsScheduler();
        foreach (string folder in Directory.EnumerateDirectories(Job.Root))
        {
            if (!File.Exists(Path.Combine(folder, "job.xml"))) continue;
            try { rows.Add(Read(scheduler, Job.Load(Path.GetFileName(folder)))); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                ArgumentException or InvalidOperationException or InvalidDataException)
            {
                Trace.TraceWarning("Could not load schedule in '{0}': {1}", folder, error.Message);
            }
        }
        return rows.OrderByDescending(row => row.Job.ScheduledUtc).ToArray();
    }

    private static Schedule Read(WindowsScheduler scheduler, Job job)
    {
        ArgumentException.ThrowIfNullOrEmpty(job.Project);
        string status = scheduler.Status(job, out bool? enabled, out DateTime? next, out DateTime? last, out string result);
        result = (result ?? status) switch
        {
            "완료" => "Completed",
            "취소됨" => "Canceled",
            string value when value.StartsWith("실패 (", StringComparison.Ordinal) => "Failed (" + value.Substring(4),
            string value => value
        };
        bool canceled = status == "Canceled" || result == "Canceled";
        string log = job.LogPath;
        if (!File.Exists(log)) log = null;
        if (last == null && log != null) last = File.GetLastWriteTime(log);
        return new Schedule(job, status, result, enabled, next, last, log, canceled,
            !canceled && (job.Daily || !File.Exists(job.ResultPath)));
    }

    internal static Job Register(string project, string claude, string agent, string prompt, TimeSpan time)
    {
        if (!Directory.Exists(project)) throw new ArgumentException("Select a project folder.");
        if (!File.Exists(claude) || !string.Equals(Path.GetFileName(claude), "claude.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select the Claude Code claude.exe file.");
        if (!Job.ValidAgent(agent)) throw new ArgumentException("Enter a valid agent name.");
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Enter task instructions.");
        var job = new Job
        {
            Id = Guid.NewGuid().ToString("N"), Project = Path.GetFullPath(project), Claude = Path.GetFullPath(claude),
            Agent = agent, Prompt = prompt, ScheduledUtc = DateTime.UtcNow,
            Daily = true, DailyHour = time.Hours, DailyMinute = time.Minutes
        };
        var scheduler = new WindowsScheduler();
        job.Save();
        try { scheduler.Register(job); }
        catch
        {
            File.Delete(Path.Combine(job.Folder, "job.xml"));
            throw;
        }
        return job;
    }
}
