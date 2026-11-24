using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;

public sealed class Job
{
    private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(Job));
    internal static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeAgentScheduler");

    public string Id;
    public string Project;
    public string Agent;
    public string Prompt;
    public string Claude;
    public DateTime ScheduledUtc;
    public bool Daily;
    public int DailyHour;
    public int DailyMinute;

    internal string TaskName { get { return "ClaudeAgentScheduler-" + Id; } }
    internal string Folder { get { return Path.Combine(Root, Id); } }
    internal string LogPath
    {
        get
        {
            if (Daily)
            {
                string latest = Directory.EnumerateFiles(Folder, "run-*.log")
                    .OrderByDescending(path => path, StringComparer.Ordinal).FirstOrDefault();
                if (latest != null) return latest;
            }
            return Path.Combine(Folder, "run.log");
        }
    }

    internal string NewLogPath()
    {
        return Daily
            ? Path.Combine(Folder, "run-" + DateTime.UtcNow.Ticks.ToString("D19", CultureInfo.InvariantCulture) + ".log")
            : LogPath;
    }

    internal void RemovePreviousLogs(string current)
    {
        foreach (string path in Directory.EnumerateFiles(Folder, "run-*.log").Append(Path.Combine(Folder, "run.log")))
        {
            if (path == current) continue;
            try { File.Delete(path); }
            catch (IOException) { } // A viewer may still have the previous log open.
            catch (UnauthorizedAccessException) { }
        }
    }
    internal string ResultPath { get { return Path.Combine(Folder, "result.txt"); } }
    internal string CanceledPath { get { return Path.Combine(Folder, "canceled"); } }
    internal DateTime DeadlineUtc { get { return ScheduledUtc.AddDays(1); } }

    // 단발 예약의 실행 또는 취소 권한을 선점한다. 이미 선점된 예약은 파일 생성에 실패한다.
    internal void Claim()
    {
        using (File.Open(Path.Combine(Folder, "claimed"), FileMode.CreateNew)) { }
    }

    // 결과를 임시 파일에 먼저 기록하고, 반복 예약이면 이전 결과를 교체한다.
    internal void SetResult(string result)
    {
        string temporary = ResultPath + ".tmp";
        File.WriteAllText(temporary, result);
        if (Daily && File.Exists(ResultPath)) File.Replace(temporary, ResultPath, null);
        else File.Move(temporary, ResultPath);
    }

    // 예약 정보를 XML로 직렬화한 뒤 완성된 파일을 job.xml로 옮긴다.
    internal void Save()
    {
        Directory.CreateDirectory(Folder);
        using (var file = File.Create(Path.Combine(Folder, "job.tmp")))
            Serializer.Serialize(file, this);
        File.Move(Path.Combine(Folder, "job.tmp"), Path.Combine(Folder, "job.xml"));
    }

    // 예약 ID의 형식을 확인하고 저장된 XML을 읽어 내부 ID와 대조한다.
    internal static Job Load(string id)
    {
        Guid parsed;
        if (!Guid.TryParseExact(id, "N", out parsed))
            throw new ArgumentException("The schedule ID is invalid.");
        using (var file = File.OpenRead(Path.Combine(Root, id, "job.xml")))
        {
            var job = (Job)Serializer.Deserialize(file);
            if (job.Id != id) throw new InvalidDataException("The schedule ID does not match.");
            return job;
        }
    }

    // CLI에 전달할 에이전트 이름이 허용된 문자로 구성됐는지 검사한다.
    internal static bool ValidAgent(string name)
    {
        return Regex.IsMatch(name ?? "", @"\A[a-zA-Z0-9][a-zA-Z0-9:_-]*\z");
    }
}

internal sealed class WindowsScheduler
{
    private const int TaskNotFound = unchecked((int)0x80070002);
    private readonly dynamic service;
    private readonly dynamic folder;

    // Windows 작업 스케줄러 서비스에 연결하고 루트 작업 폴더를 연다.
    internal WindowsScheduler()
    {
        service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
        service.Connect();
        folder = service.GetFolder("\\");
    }

    // 로그인한 사용자 계정에 단발 또는 일일 트리거를 등록하고 AgentFlow의 --run 명령을 연결한다.
    internal void Register(Job job, string executablePath = null)
    {
        const int timeTrigger = 1;
        const int dailyTrigger = 2;
        const int execAction = 0;
        const int createOnly = 2;
        const int interactiveToken = 3;
        string user = WindowsIdentity.GetCurrent().Name;
        dynamic task = service.NewTask(0);
        task.RegistrationInfo.Description = "Run a Claude Code agent in the selected project.";
        task.Principal.UserId = user;
        task.Principal.LogonType = interactiveToken;
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.StartWhenAvailable = true;
        task.Settings.AllowDemandStart = false;
        dynamic trigger = task.Triggers.Create(job.Daily ? dailyTrigger : timeTrigger);
        if (job.Daily)
        {
            trigger.DaysInterval = (short)1;
            // An offset-free boundary follows the Windows local wall clock.
            DateTime now = DateTime.Now;
            DateTime first = now.Date.AddHours(job.DailyHour).AddMinutes(job.DailyMinute);
            if (first <= now) first = first.AddDays(1);
            trigger.StartBoundary = first.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            task.Settings.MultipleInstances = 2; // Ignore new triggers while a run is active.
        }
        else
        {
            trigger.StartBoundary = job.ScheduledUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            trigger.EndBoundary = job.DeadlineUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            task.Settings.DeleteExpiredTaskAfter = "P1D";
        }
        dynamic action = task.Actions.Create(execAction);
        action.Path = executablePath ?? Environment.ProcessPath;
        action.Arguments = "--run " + job.Id;
        folder.RegisterTaskDefinition(job.TaskName, task, createOnly, user, null, interactiveToken, null);
    }

    // 등록된 작업을 삭제하지 않고 활성화 또는 비활성화한다.
    internal void SetEnabled(Job job, bool enabled)
    {
        dynamic task = folder.GetTask(job.TaskName);
        task.Enabled = enabled;
    }

    // 작업 스케줄러의 상태와 저장된 실행 결과를 조합해 화면에 표시할 상태를 반환한다.
    // 활성화 상태를 조회할 수 없는 경우 enabled는 null이다.
    internal string Status(Job job, out bool? enabled)
    {
        return Status(job, out enabled, out _, out _, out _);
    }

    internal string Status(Job job, out bool? enabled, out DateTime? nextRun, out DateTime? lastRun, out string runResult)
    {
        enabled = null;
        nextRun = null;
        lastRun = null;
        runResult = ReadResult(job, out DateTime resultWrittenUtc);
        if (job.Daily && File.Exists(job.CanceledPath)) return "Canceled";
        try
        {
            dynamic task = folder.GetTask(job.TaskName);
            int state = task.State;
            if (job.Daily || DateTime.UtcNow < job.DeadlineUtc)
                enabled = (bool)task.Enabled;
            DateTime previous = task.LastRunTime;
            // Task Scheduler reports a placeholder date before the first run.
            if (previous.ToUniversalTime() >= job.ScheduledUtc) lastRun = previous;
            if (enabled == true)
            {
                DateTime next = task.NextRunTime;
                if (next >= DateTime.Today) nextRun = next;
            }
            int result = task.LastTaskResult;
            bool failed = result is not (0 or 0x41301 or 0x41303 or 0x41325);
            if (failed && (runResult == null || previous.ToUniversalTime() > resultWrittenUtc))
                runResult = String.Format("Run error (0x{0:X8})", result);
            if (state == 4) return "Running";
            if (enabled == false) return "Off";
            if (state == 2) return "Queued";
            if (job.Daily)
            {
                if (runResult == null) return "Scheduled";
                return runResult.StartsWith("Run error (", StringComparison.Ordinal) ? runResult : "Last run: " + runResult;
            }
            if (failed && !File.Exists(job.ResultPath)) return runResult;
            if (DateTime.UtcNow >= job.DeadlineUtc) return "Expired";
            return DateTime.UtcNow < job.ScheduledUtc ? "Scheduled" : "Waiting to start";
        }
        catch (Exception error) when (IsMissingTask(error))
        {
            return !job.Daily && DateTime.UtcNow >= job.DeadlineUtc ? "Expired" : "Scheduled task not found";
        }
    }

    private static string ReadResult(Job job, out DateTime writtenUtc)
    {
        writtenUtc = DateTime.MinValue;
        try
        {
            using var stream = File.Open(job.ResultPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = new StreamReader(stream);
            writtenUtc = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return null; }
    }

    // Windows 작업을 삭제하고 취소를 기록한다. 단발 예약은 실행 전에 선점할 수 있을 때만 취소한다.
    internal void Cancel(Job job)
    {
        if (job.Daily)
        {
            if (File.Exists(job.CanceledPath)) return;
            try { folder.DeleteTask(job.TaskName, 0); }
            catch (Exception error) when (IsMissingTask(error)) { }
            using (File.Open(job.CanceledPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) { }
            return;
        }
        dynamic task = folder.GetTask(job.TaskName);
        try { job.Claim(); }
        catch (IOException) { throw new InvalidOperationException("The run has already started or the schedule was canceled."); }
        job.SetResult("Canceled");
        folder.DeleteTask(job.TaskName, 0);
    }

    private static bool IsMissingTask(Exception error)
    {
        return error is (COMException or FileNotFoundException) && error.HResult == TaskNotFound;
    }
}

internal static class Program
{
    [STAThread]
    // --run 요청은 지정된 예약을 실행하고, 일반 시작은 UI를 연다.
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--run")
        {
            try { return Run(Job.Load(args[1])); }
            catch { return 1; }
        }
        using var windowInstance = new Mutex(false, @"Local\AgentFlow.MainWindow", out bool firstWindow);
        if (!firstWindow) return 0;

        AgentFlow.ScheduleCenterApp.Run();
        return Environment.ExitCode;
    }

    // 단발 예약은 한 번만 선점하고, 일일 예약은 잠금 파일로 동시 실행을 막고 취소 여부를 확인한다.
    private static int Run(Job job)
    {
        if (!job.Daily)
        {
            job.Claim();
            return Execute(job);
        }
        using (var lease = File.Open(Path.Combine(job.Folder, "running"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            if (File.Exists(job.CanceledPath)) return 0;
            return Execute(job);
        }
    }

    // 프로젝트 폴더에서 Claude CLI를 시작하고 작업 지시를 표준 입력으로 전달한다.
    // 표준 출력과 오류, 종료 코드를 이번 실행의 로그에 기록하고 실행 결과를 별도로 저장한다.
    private static int Execute(Job job)
    {
        var utf8 = new UTF8Encoding(false);
        string logPath = job.NewLogPath();
        using (var file = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var log = new StreamWriter(file, utf8) { AutoFlush = true })
        {
            int exitCode = 1;
            try
            {
                if (!Job.ValidAgent(job.Agent)) throw new InvalidDataException("The agent name is invalid.");
                log.WriteLine("[{0:O}] {1} / {2}", DateTimeOffset.Now, job.Project, job.Agent);
                var start = new ProcessStartInfo
                {
                    FileName = job.Claude,
                    Arguments = "--print --agent " + job.Agent + " --permission-mode default",
                    WorkingDirectory = job.Project,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = utf8,
                    StandardErrorEncoding = utf8
                };
                start.EnvironmentVariables.Remove("CLAUDECODE");
                using (var process = Process.Start(start))
                {
                    Task output = Pump(process.StandardOutput, log, "");
                    Task errors = Pump(process.StandardError, log, "[stderr] ");
                    try
                    {
                        using (Stream input = process.StandardInput.BaseStream)
                        {
                            byte[] prompt = utf8.GetBytes(job.Prompt);
                            input.Write(prompt, 0, prompt.Length);
                        }
                    }
                    catch (IOException error) { lock (log) log.WriteLine(error.Message); }
                    process.WaitForExit();
                    Task.WaitAll(output, errors);
                    exitCode = process.ExitCode;
                }
            }
            catch (Exception error) { log.WriteLine(error); }
            log.WriteLine("[{0:O}] exit code {1}", DateTimeOffset.Now, exitCode);
            job.SetResult(exitCode == 0 ? "Completed" : "Failed (" + exitCode + ")");
            if (job.Daily) job.RemovePreviousLogs(logPath);
            return exitCode;
        }
    }

    // 출력 스트림을 줄 단위로 읽고, 두 스트림이 같은 로그에 쓸 때 한 줄씩 기록되도록 동기화한다.
    private static async Task Pump(StreamReader reader, TextWriter log, string prefix)
    {
        string line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            lock (log) log.WriteLine(prefix + line);
    }
}
