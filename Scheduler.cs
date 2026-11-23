using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
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

    internal string TaskName { get { return "ClaudeAgentScheduler-" + Id; } }
    internal string Folder { get { return Path.Combine(Root, Id); } }
    internal string LogPath { get { return Path.Combine(Folder, "run.log"); } }
    internal string ResultPath { get { return Path.Combine(Folder, "result.txt"); } }
    internal DateTime DeadlineUtc { get { return ScheduledUtc.AddDays(1); } }

    internal void Claim()
    {
        using (File.Open(Path.Combine(Folder, "claimed"), FileMode.CreateNew)) { }
    }

    internal void SetResult(string result)
    {
        string temporary = ResultPath + ".tmp";
        File.WriteAllText(temporary, result);
        File.Move(temporary, ResultPath);
    }

    internal void Save()
    {
        Directory.CreateDirectory(Folder);
        using (var file = File.Create(Path.Combine(Folder, "job.tmp")))
            Serializer.Serialize(file, this);
        File.Move(Path.Combine(Folder, "job.tmp"), Path.Combine(Folder, "job.xml"));
    }

    internal static Job Load(string id)
    {
        Guid parsed;
        if (!Guid.TryParseExact(id, "N", out parsed))
            throw new ArgumentException("예약 ID가 올바르지 않습니다.");
        using (var file = File.OpenRead(Path.Combine(Root, id, "job.xml")))
        {
            var job = (Job)Serializer.Deserialize(file);
            if (job.Id != id) throw new InvalidDataException("예약 ID가 일치하지 않습니다.");
            return job;
        }
    }

    internal static bool ValidAgent(string name)
    {
        return Regex.IsMatch(name ?? "", @"\A[a-zA-Z0-9][a-zA-Z0-9:_-]*\z");
    }
}

internal sealed class WindowsScheduler
{
    private readonly dynamic service;
    private readonly dynamic folder;

    internal WindowsScheduler()
    {
        service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
        service.Connect();
        folder = service.GetFolder("\\");
    }

    internal void Register(Job job)
    {
        const int timeTrigger = 1;
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
        dynamic trigger = task.Triggers.Create(timeTrigger);
        trigger.StartBoundary = job.ScheduledUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        trigger.EndBoundary = job.DeadlineUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        task.Settings.DeleteExpiredTaskAfter = "P1D";
        dynamic action = task.Actions.Create(execAction);
        action.Path = Assembly.GetExecutingAssembly().Location;
        action.Arguments = "--run " + job.Id;
        folder.RegisterTaskDefinition(job.TaskName, task, createOnly, user, null, interactiveToken, null);
    }

    internal string Status(Job job)
    {
        try
        {
            dynamic task = folder.GetTask(job.TaskName);
            int state = task.State;
            if (state == 4) return "실행 중";
            if (state == 2) return "실행 대기";
            int result = task.LastTaskResult;
            if (result != 0x41303 && result != 0 && !File.Exists(job.ResultPath))
                return String.Format("실행 오류 (0x{0:X8})", result);
            if (DateTime.UtcNow >= job.DeadlineUtc) return "만료됨";
            return DateTime.UtcNow < job.ScheduledUtc ? "예약됨" : "시작 대기";
        }
        catch (COMException error)
        {
            if (error.ErrorCode != unchecked((int)0x80070002)) throw;
            return DateTime.UtcNow >= job.DeadlineUtc ? "만료됨" : "등록된 작업 없음";
        }
    }

    internal void Cancel(Job job)
    {
        dynamic task = folder.GetTask(job.TaskName);
        try { job.Claim(); }
        catch (IOException) { throw new InvalidOperationException("실행이 이미 시작되었거나 예약이 취소되었습니다."); }
        job.SetResult("취소됨");
        folder.DeleteTask(job.TaskName, 0);
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--run")
        {
            try { return Run(Job.Load(args[1])); }
            catch { return 1; }
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SchedulerForm());
        return 0;
    }

    private static int Run(Job job)
    {
        var utf8 = new UTF8Encoding(false);
        job.Claim();
        using (var file = new FileStream(job.LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var log = new StreamWriter(file, utf8) { AutoFlush = true })
        {
            int exitCode = 1;
            try
            {
                if (!Job.ValidAgent(job.Agent)) throw new InvalidDataException("에이전트 이름이 올바르지 않습니다.");
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
            job.SetResult(exitCode == 0 ? "완료" : "실패 (" + exitCode + ")");
            return exitCode;
        }
    }

    private static async Task Pump(StreamReader reader, TextWriter log, string prefix)
    {
        string line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            lock (log) log.WriteLine(prefix + line);
    }
}
