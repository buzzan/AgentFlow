using System.Text;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;
var prompt = Console.In.ReadToEnd();
Console.WriteLine("args: " + string.Join(" ", args));
Console.WriteLine("cwd: " + Environment.CurrentDirectory);
Console.WriteLine("CLAUDECODE: " + (Environment.GetEnvironmentVariable("CLAUDECODE") ?? "<unset>"));
Console.WriteLine(prompt);
Console.Error.WriteLine("fixture stderr");
if (prompt.StartsWith("hold", StringComparison.Ordinal))
{
    File.WriteAllText("fixture.ready", "ready");
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (!File.Exists("fixture.release") && DateTime.UtcNow < deadline) Thread.Sleep(50);
    if (!File.Exists("fixture.release")) return 124;
}
return prompt.StartsWith("fail", StringComparison.Ordinal) ? 17 : 0;
