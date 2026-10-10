using System.Text.Json;

namespace MiPushDesk.Core;

public static class AppErrors
{
    public static string Describe(Exception error) =>
        $"{(string.IsNullOrWhiteSpace(error.Message) ? "操作失败" : error.Message.Trim())}（0x{error.HResult:X8}）";

    public static void Record(string directory, Exception error)
    {
        var failures = new List<object>();
        for (var current = error; current is not null; current = current.InnerException)
            failures.Add(new { type = current.GetType().FullName, code = $"0x{current.HResult:X8}", stack = current.StackTrace });
        File.AppendAllText(Path.Combine(directory, "ui-errors.jsonl"),
            JsonSerializer.Serialize(new { time = DateTimeOffset.Now, failures }) + Environment.NewLine);
    }
}
