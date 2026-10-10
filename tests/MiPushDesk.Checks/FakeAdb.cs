internal static class FakeAdb
{
    public static async Task RunAsync(string[] arguments, string directory)
    {
        if (arguments[0] == "pair")
        {
            if (Console.ReadLine() != "123456") throw new InvalidOperationException("Pairing input missing");
            File.WriteAllText(Path.Combine(directory, "pair-input"), arguments.Contains("123456") ? "argument" : "stdin");
            Console.WriteLine("Successfully paired with fixture");
            return;
        }
        if (arguments[0] == "connect") { Console.WriteLine("connected to fixture"); return; }
        if (arguments[0] != "-s") throw new InvalidOperationException("Explicit device selection missing");
        if (arguments[1] == "disconnected")
        {
            Console.Error.WriteLine("device not found " + AccountImportChecks.Secret);
            Environment.ExitCode = 1;
            return;
        }
        if (arguments.Contains("start")) { File.WriteAllText(Path.Combine(directory, "started"), "yes"); return; }
        if (arguments.Contains("broadcast")) { File.WriteAllText(Path.Combine(directory, "stopped"), "yes"); return; }
        if (arguments[2] != "logcat") throw new InvalidOperationException("Unexpected ADB command");
        if (arguments.Contains("-d"))
        {
            if (arguments.Contains("-t")) throw new InvalidOperationException("Global log truncation can hide matching PushService entries");
            if (arguments[1] == "cached") WriteSecret();
            return;
        }
        File.WriteAllText(Path.Combine(directory, "reader-pid"), Environment.ProcessId.ToString());
        while (!File.Exists(Path.Combine(directory, "started"))) await Task.Delay(10);
        if (arguments[1] == "live") WriteSecret();
        await Task.Delay(Timeout.Infinite);
    }

    private static void WriteSecret()
    {
        Console.WriteLine("invalid-sig token = unrelated-token sec = " + AccountImportChecks.Secret);
        Console.WriteLine("invalid-sig token = example-token sec = " + AccountImportChecks.Secret);
        Console.Out.Flush();
    }
}
