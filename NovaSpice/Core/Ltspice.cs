using System.Diagnostics;

namespace NovaSpice.Core;

/// <summary>Locates LTspice and runs it in batch mode (-b).</summary>
public static class Ltspice
{
    public static readonly string[] Candidates =
    {
        @"C:\Program Files\ADI\LTspice\LTspice.exe",
        @"%LOCALAPPDATA%\Programs\ADI\LTspice\LTspice.exe",
        @"C:\Program Files\LTC\LTspiceXVII\XVIIx64.exe",
        @"C:\Program Files (x86)\LTC\LTspiceIV\scad3.exe",
    };

    public static string Find(string? saved)
    {
        if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved)) return saved;
        foreach (var c in Candidates)
        {
            var p = Environment.ExpandEnvironmentVariables(c);
            if (File.Exists(p)) return p;
        }
        return "";
    }

    public sealed record Result(int ExitCode, double Seconds, string Output, bool Cancelled);

    public static async Task<Result> RunAsync(string exe, string file, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(file) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-b");
        psi.ArgumentList.Add(file);

        var sw = Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi };
        p.Start();
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return new Result(-1, sw.Elapsed.TotalSeconds, "", true);
        }
        string output = (await outTask) + (await errTask);
        return new Result(p.ExitCode, sw.Elapsed.TotalSeconds, output, false);
    }
}
