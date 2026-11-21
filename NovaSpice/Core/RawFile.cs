using System.Text;

namespace NovaSpice.Core;

/// <summary>One signal. Im is null for real data.</summary>
public sealed class Trace
{
    public double[] Re = Array.Empty<double>();
    public double[]? Im;
    public string Kind = "";
}

public sealed class RawData
{
    public string Path = "", Title = "", Plotname = "";
    public HashSet<string> Flags = new();
    public List<string> Names = new();
    public List<string> Kinds = new();
    public Dictionary<string, Trace> Traces = new();
    public List<(int Start, int End)> Steps = new();
    public List<string> StepLabels = new();

    public double[] Axis => Traces[Names[0]].Re;
    public string AxisName => Names[0];
    public string AxisKind => Kinds[0];
    public bool IsComplex => Flags.Contains("complex");
    public bool IsLogX => Flags.Contains("log");
    public int Points => Axis.Length;
    public bool IsTime => AxisKind == "time";

    public List<(int Start, int End)> StepRanges() =>
        Steps.Count > 0 ? Steps : new List<(int, int)> { (0, Points) };

    public string StepLabel(int k) => k < StepLabels.Count ? StepLabels[k] : $"step {k + 1}";

    public string? Find(string name) =>
        Names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reader / writer for LTspice .raw files (binary + ASCII, real + complex, stepped).</summary>
public static class RawFile
{
    public static RawData Read(string path)
    {
        byte[] buf = File.ReadAllBytes(path);
        bool bom = buf.Length > 1 && buf[0] == 0xFF && buf[1] == 0xFE;
        bool utf16 = bom || (buf.Length > 1 && buf[1] == 0);
        Encoding enc = utf16 ? Encoding.Unicode : Encoding.Latin1;

        int best = -1, start = 0;
        bool binary = true;
        foreach (var marker in new[] { "Binary:", "Values:" })
            foreach (var nl in new[] { "\r\n", "\n" })
            {
                var mb = enc.GetBytes(marker + nl);
                int i = buf.AsSpan().IndexOf(mb);
                if (i >= 0 && (!utf16 || i % 2 == 0) && (best < 0 || i < best))
                {
                    best = i; start = i + mb.Length; binary = marker == "Binary:";
                }
            }
        if (best < 0) throw new InvalidDataException("Not an LTspice .raw file (no Binary:/Values: section).");

        string header = enc.GetString(buf, 0, best).TrimStart('﻿');
        var meta = new Dictionary<string, string>();
        var names = new List<string>();
        var kinds = new List<string>();
        bool inVars = false;
        foreach (var line in header.Split('\n'))
        {
            var ln = line.TrimEnd('\r');
            if (inVars)
            {
                var parts = ln.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && int.TryParse(parts[0], out _))
                {
                    names.Add(parts[1]); kinds.Add(parts[2].ToLowerInvariant());
                    continue;
                }
                if (ln.Trim().Length == 0) continue;
                inVars = false;
            }
            if (ln.StartsWith("Variables:")) { inVars = true; continue; }
            int c = ln.IndexOf(':');
            if (c > 0) meta[ln[..c].Trim().ToLowerInvariant()] = ln[(c + 1)..].Trim();
        }
        if (names.Count == 0) throw new InvalidDataException("No variables listed in the .raw header.");

        int nvars = names.Count;
        int npts = int.Parse(meta.GetValueOrDefault("no. points", "0").Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], Si.Inv);
        var flags = meta.GetValueOrDefault("flags", "").ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        bool cplx = flags.Contains("complex");

        var re = new double[nvars][];
        var im = new double[nvars][];
        int n;

        if (binary)
        {
            var sizes = new int[nvars];
            for (int j = 0; j < nvars; j++)
                sizes[j] = cplx ? 16 : flags.Contains("double") || j == 0 ? 8 : 4;
            int rec = sizes.Sum();
            n = Math.Min(npts, (buf.Length - start) / rec);
            for (int j = 0; j < nvars; j++) { re[j] = new double[n]; if (cplx) im[j] = new double[n]; }

            if (flags.Contains("fastaccess"))
            {
                int off = start;
                for (int j = 0; j < nvars; j++)
                    for (int p = 0; p < n; p++)
                        off = ReadValue(buf, off, sizes[j], cplx, re[j], im[j], p);
            }
            else
            {
                for (int p = 0; p < n; p++)
                {
                    int off = start + p * rec;
                    for (int j = 0; j < nvars; j++)
                        off = ReadValue(buf, off, sizes[j], cplx, re[j], im[j], p);
                }
            }
        }
        else
        {
            var tok = enc.GetString(buf, start, buf.Length - start)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int per = nvars + 1;
            n = Math.Min(npts, tok.Length / per);
            for (int j = 0; j < nvars; j++) { re[j] = new double[n]; if (cplx) im[j] = new double[n]; }
            for (int p = 0; p < n; p++)
                for (int j = 0; j < nvars; j++)
                {
                    var s = tok[p * per + 1 + j];
                    int comma = s.IndexOf(',');
                    if (comma >= 0)
                    {
                        re[j][p] = double.Parse(s[..comma], Si.Inv);
                        if (cplx) im[j][p] = double.Parse(s[(comma + 1)..], Si.Inv);
                    }
                    else re[j][p] = double.Parse(s, Si.Inv);
                }
        }

        if (kinds[0] == "time")
            for (int p = 0; p < n; p++) re[0][p] = Math.Abs(re[0][p]); // sign bit is a flag

        var raw = new RawData
        {
            Path = path, Title = meta.GetValueOrDefault("title", ""), Plotname = meta.GetValueOrDefault("plotname", ""),
            Flags = flags, Names = names, Kinds = kinds,
        };
        for (int j = 0; j < nvars; j++)
            raw.Traces[names[j]] = new Trace { Re = re[j], Im = j == 0 || !cplx ? null : im[j], Kind = kinds[j] };

        if (flags.Contains("stepped") && n > 1)
        {
            var ax = re[0];
            var starts = new List<int>();
            for (int p = 0; p < n; p++)
                if (ax[p] == ax[0] && (starts.Count == 0 || p - starts[^1] > 1)) starts.Add(p);
            for (int k = 0; k < starts.Count; k++)
                raw.Steps.Add((starts[k], k + 1 < starts.Count ? starts[k + 1] : n));
        }

        var log = System.IO.Path.ChangeExtension(path, ".log");
        if (File.Exists(log))
        {
            try { raw.StepLabels = LogParser.Read(log).Steps; } catch { /* optional */ }
        }
        return raw;
    }

    static int ReadValue(byte[] buf, int off, int size, bool cplx, double[] re, double[]? im, int p)
    {
        if (cplx)
        {
            re[p] = BitConverter.ToDouble(buf, off);
            im![p] = BitConverter.ToDouble(buf, off + 8);
        }
        else re[p] = size == 8 ? BitConverter.ToDouble(buf, off) : BitConverter.ToSingle(buf, off);
        return off + size;
    }

    /// <summary>Write a binary LTspice-style .raw (UTF-16 header, time as double, signals as float).</summary>
    public static void Write(string path, string plotname, IList<string> names, IList<string> kinds,
        IList<double[]> re, IList<double[]>? im, bool stepped, bool log, string title)
    {
        bool cplx = im != null;
        int n = re[0].Length;
        var flags = new List<string> { cplx ? "complex" : "real", "forward" };
        if (log) flags.Add("log");
        if (stepped) flags.Add("stepped");
        var sb = new StringBuilder();
        sb.Append($"Title: * {title}\n");
        sb.Append($"Date: {DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy", Si.Inv)}\n");
        sb.Append($"Plotname: {plotname}\nFlags: {string.Join(' ', flags)}\n");
        sb.Append($"No. Variables: {names.Count}\nNo. Points: {n,11}\n");
        sb.Append("Offset:   0.0000000000000000e+000\nCommand: NovaSpice demo generator\nVariables:\n");
        for (int j = 0; j < names.Count; j++) sb.Append($"\t{j}\t{names[j]}\t{kinds[j]}\n");
        sb.Append("Binary:\n");

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(Encoding.Unicode.GetBytes(sb.ToString()));
        for (int p = 0; p < n; p++)
            for (int j = 0; j < names.Count; j++)
            {
                if (cplx) { bw.Write(re[j][p]); bw.Write(im![j][p]); }
                else if (j == 0) bw.Write(re[j][p]);
                else bw.Write((float)re[j][p]);
            }
    }
}
