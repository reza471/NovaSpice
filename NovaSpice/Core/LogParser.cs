using System.Text;
using System.Text.RegularExpressions;

namespace NovaSpice.Core;

public sealed class Measurement
{
    public string Name { get; set; } = "";
    public string Expr { get; set; } = "";
    public string Value { get; set; } = "";
    public double? Number { get; set; }
    public string Details { get; set; } = "";
    public int? Step { get; set; }
    public string StepText { get; set; } = "";
    public string Display => Number is double d ? Si.Fmt(d, "", 5) : Value;
}

public sealed class LogInfo
{
    public List<Measurement> Measurements = new();
    public List<string> Steps = new();
    public List<string> Errors = new();
    public List<string> Warnings = new();
    public string Elapsed = "";
    public string Text = "";
}

/// <summary>Parses LTspice .log files: .meas results (simple and stepped tables), .step lines, errors.</summary>
public static partial class LogParser
{
    [GeneratedRegex(@"^(?<n>[A-Za-z_]\w*):\s*(?<e>.*?)=\s*(?<v>\S+)\s*(?<r>.*)$")]
    private static partial Regex Simple1();

    [GeneratedRegex(@"^(?<n>[A-Za-z_]\w*)=\s*(?<v>\S+)\s*(?<r>.*)$")]
    private static partial Regex Simple2();

    public static string ReadText(string path)
    {
        var b = File.ReadAllBytes(path);
        string s = (b.Length > 1 && (b[1] == 0 || (b[0] == 0xFF && b[1] == 0xFE)))
            ? Encoding.Unicode.GetString(b)
            : Encoding.UTF8.GetString(b);
        return s.TrimStart('﻿');
    }

    public static LogInfo Read(string path) => Parse(ReadText(path));

    public static LogInfo Parse(string text)
    {
        var info = new LogInfo { Text = text };
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var s = lines[i].Trim();
            if (s.Length == 0) continue;
            var low = s.ToLowerInvariant();

            if (low.StartsWith(".step"))
            {
                info.Steps.Add(PrettyStep(s[5..].Trim()));
                continue;
            }
            if (low.StartsWith("measurement:"))
            {
                string name = s[(s.IndexOf(':') + 1)..].Trim();
                string[] hdr = i + 1 < lines.Length
                    ? lines[i + 1].Split('\t', StringSplitOptions.RemoveEmptyEntries).Select(h => h.Trim()).ToArray()
                    : Array.Empty<string>();
                i += 2;
                for (; i < lines.Length && lines[i].Trim().Length > 0; i++)
                {
                    var cols = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (cols.Length < 2 || !int.TryParse(cols[0], out int step)) continue;
                    var m = new Measurement { Name = name, Expr = hdr.Length > 1 ? hdr[1] : "", Value = cols[1], Step = step };
                    if (double.TryParse(cols[1], System.Globalization.NumberStyles.Float, Si.Inv, out var d)) m.Number = d;
                    var extra = new List<string>();
                    for (int c = 2; c < cols.Length; c++)
                        extra.Add((c < hdr.Length ? hdr[c] + " " : "") + cols[c]);
                    m.Details = string.Join("  ", extra);
                    m.StepText = step - 1 < info.Steps.Count ? info.Steps[step - 1] : step.ToString(Si.Inv);
                    info.Measurements.Add(m);
                }
                continue;
            }

            var m1 = Simple1().Match(s);
            if (m1.Success) info.Measurements.Add(Make(m1.Groups["n"].Value, m1.Groups["e"].Value, m1.Groups["v"].Value, m1.Groups["r"].Value));
            else
            {
                var m2 = Simple2().Match(s);
                if (m2.Success) info.Measurements.Add(Make(m2.Groups["n"].Value, "", m2.Groups["v"].Value, m2.Groups["r"].Value));
            }

            if (low.StartsWith("total elapsed time")) info.Elapsed = s;
            else if (low.Contains("error") || low.Contains("fatal") || low.Contains("singular matrix")) info.Errors.Add(s);
            else if (low.Contains("warning")) info.Warnings.Add(s);
        }
        return info;
    }

    /// <summary>"c=1e-08 r=1000" → "C = 10n, R = 1k".</summary>
    static string PrettyStep(string s)
    {
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p =>
        {
            int eq = p.IndexOf('=');
            if (eq <= 0 || !double.TryParse(p[(eq + 1)..], System.Globalization.NumberStyles.Float, Si.Inv, out var v)) return p;
            return $"{p[..eq].ToUpperInvariant()} = {Si.Fmt(v, "", 4).Replace(" ", "")}";
        });
        return string.Join(", ", parts);
    }

    static Measurement Make(string name, string expr, string value, string rest)
    {
        var m = new Measurement { Name = name, Expr = expr.Trim(), Value = value, Details = rest.Trim() };
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, Si.Inv, out var d)) m.Number = d;
        return m;
    }
}
