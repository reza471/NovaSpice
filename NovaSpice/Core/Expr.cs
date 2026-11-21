using System.Numerics;

namespace NovaSpice.Core;

/// <summary>
/// Waveform expressions in LTspice style: V(out)*I(L1), V(a,b), db(V(out)), deriv(V(c)), 1k*I(R1)...
/// Evaluated element-wise on complex arrays, step by step.
/// </summary>
public static class Expr
{
    public static Trace Evaluate(string text, RawData raw, IReadOnlyDictionary<string, Trace> extras)
    {
        var node = new Parser(text, raw, extras).ParseAll();
        int n = raw.Points;
        var re = new double[n];
        var im = new double[n];
        bool anyIm = false;
        foreach (var (s, e) in raw.StepRanges())
        {
            var ctx = new Ctx(raw, extras, s, e - s);
            var v = node.Eval(ctx);
            for (int i = 0; i < v.Length; i++)
            {
                re[s + i] = v[i].Real;
                im[s + i] = v[i].Imaginary;
                if (v[i].Imaginary != 0) anyIm = true;
            }
        }
        return new Trace { Re = re, Im = raw.IsComplex || anyIm ? im : null, Kind = "expr" };
    }

    sealed class Ctx
    {
        public readonly RawData Raw; public readonly IReadOnlyDictionary<string, Trace> Extras;
        public readonly int Start, Len;
        double[]? x;
        public Ctx(RawData raw, IReadOnlyDictionary<string, Trace> extras, int start, int len)
        { Raw = raw; Extras = extras; Start = start; Len = len; }
        public double[] X => x ??= Raw.Axis.AsSpan(Start, Len).ToArray();
    }

    abstract class Node { public abstract Complex[] Eval(Ctx c); }

    sealed class Num(double v) : Node
    {
        public override Complex[] Eval(Ctx c) { var a = new Complex[c.Len]; Array.Fill(a, new Complex(v, 0)); return a; }
    }

    sealed class Sig(Trace t) : Node
    {
        public override Complex[] Eval(Ctx c)
        {
            var a = new Complex[c.Len];
            for (int i = 0; i < c.Len; i++)
                a[i] = new Complex(t.Re[c.Start + i], t.Im is null ? 0 : t.Im[c.Start + i]);
            return a;
        }
    }

    sealed class Un(Func<Complex, Complex> f, Node a) : Node
    {
        public override Complex[] Eval(Ctx c) { var v = a.Eval(c); for (int i = 0; i < v.Length; i++) v[i] = f(v[i]); return v; }
    }

    sealed class Bin(Func<Complex, Complex, Complex> f, Node a, Node b) : Node
    {
        public override Complex[] Eval(Ctx c)
        {
            var x = a.Eval(c); var y = b.Eval(c);
            for (int i = 0; i < x.Length; i++) x[i] = f(x[i], y[i]);
            return x;
        }
    }

    sealed class Deriv(Node a) : Node
    {
        public override Complex[] Eval(Ctx c)
        {
            var v = a.Eval(c); var x = c.X; int n = v.Length; var d = new Complex[n];
            if (n < 2) return d;
            for (int i = 0; i < n; i++)
            {
                int i0 = Math.Max(0, i - 1), i1 = Math.Min(n - 1, i + 1);
                double dx = x[i1] - x[i0];
                d[i] = dx == 0 ? (i > 0 ? d[i - 1] : Complex.Zero) : (v[i1] - v[i0]) / dx;
            }
            return d;
        }
    }

    sealed class Integ(Node a) : Node
    {
        public override Complex[] Eval(Ctx c)
        {
            var v = a.Eval(c); var x = c.X; var r = new Complex[v.Length];
            for (int i = 1; i < v.Length; i++) r[i] = r[i - 1] + (v[i] + v[i - 1]) * 0.5 * (x[i] - x[i - 1]);
            return r;
        }
    }

    static readonly Dictionary<string, Func<Complex, Complex>> Unary = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sqrt"] = Complex.Sqrt, ["exp"] = Complex.Exp, ["log"] = Complex.Log, ["ln"] = Complex.Log,
        ["log10"] = Complex.Log10, ["sin"] = Complex.Sin, ["cos"] = Complex.Cos, ["tan"] = Complex.Tan,
        ["atan"] = Complex.Atan, ["sinh"] = Complex.Sinh, ["cosh"] = Complex.Cosh, ["tanh"] = Complex.Tanh,
        ["abs"] = z => z.Imaginary == 0 ? Math.Abs(z.Real) : z.Magnitude,
        ["mag"] = z => z.Magnitude,
        ["db"] = z => 20 * Math.Log10(Math.Max(z.Magnitude, 1e-300)),
        ["ph"] = z => z.Phase * 180 / Math.PI,
        ["re"] = z => z.Real, ["real"] = z => z.Real,
        ["im"] = z => z.Imaginary, ["imag"] = z => z.Imaginary,
        ["sgn"] = z => Math.Sign(z.Real),
    };

    static readonly HashSet<string> Special = new(StringComparer.OrdinalIgnoreCase) { "deriv", "integ", "min", "max", "pow" };

    sealed class Parser(string src, RawData raw, IReadOnlyDictionary<string, Trace> extras)
    {
        int pos;

        public Node ParseAll()
        {
            var n = ParseSum();
            Ws();
            if (pos < src.Length) throw new FormatException($"Unexpected '{src[pos]}' at position {pos + 1}");
            return n;
        }

        void Ws() { while (pos < src.Length && char.IsWhiteSpace(src[pos])) pos++; }
        bool Eat(string s) { Ws(); if (string.CompareOrdinal(src, pos, s, 0, s.Length) == 0) { pos += s.Length; return true; } return false; }
        void Expect(char ch) { if (!Eat(ch.ToString())) throw new FormatException($"Expected '{ch}'"); }

        Node ParseSum()
        {
            var a = ParseProduct();
            while (true)
            {
                if (Eat("+")) a = new Bin((x, y) => x + y, a, ParseProduct());
                else if (Eat("-")) a = new Bin((x, y) => x - y, a, ParseProduct());
                else return a;
            }
        }

        Node ParseProduct()
        {
            var a = ParseUnary();
            while (true)
            {
                Ws();
                if (pos + 1 < src.Length && src[pos] == '*' && src[pos + 1] == '*') return a;
                if (Eat("*")) a = new Bin((x, y) => x * y, a, ParseUnary());
                else if (Eat("/")) a = new Bin((x, y) => x / y, a, ParseUnary());
                else return a;
            }
        }

        Node ParseUnary()
        {
            if (Eat("-")) return new Un(z => -z, ParseUnary());
            if (Eat("+")) return ParseUnary();
            var b = ParsePrimary();
            if (Eat("**") || Eat("^")) return new Bin(Complex.Pow, b, ParseUnary());
            return b;
        }

        Node ParsePrimary()
        {
            Ws();
            if (pos >= src.Length) throw new FormatException("Unexpected end of expression");
            char ch = src[pos];
            if (char.IsDigit(ch) || ch == '.') return ParseNumber();
            if (ch == '(') { pos++; var e = ParseSum(); Expect(')'); return e; }
            if (char.IsLetter(ch) || ch == '_')
            {
                int s = pos;
                while (pos < src.Length && (char.IsLetterOrDigit(src[pos]) || src[pos] == '_')) pos++;
                string id = src[s..pos];
                Ws();
                if (pos < src.Length && src[pos] == '(')
                {
                    if (Unary.TryGetValue(id, out var f))
                    {
                        pos++; var a = ParseSum(); Expect(')');
                        return new Un(f, a);
                    }
                    if (Special.Contains(id))
                    {
                        pos++;
                        var args = new List<Node> { ParseSum() };
                        while (Eat(",")) args.Add(ParseSum());
                        Expect(')');
                        string lid = id.ToLowerInvariant();
                        return lid switch
                        {
                            "deriv" => new Deriv(args[0]),
                            "integ" => new Integ(args[0]),
                            "min" when args.Count == 2 => new Bin((x, y) => x.Real <= y.Real ? x : y, args[0], args[1]),
                            "max" when args.Count == 2 => new Bin((x, y) => x.Real >= y.Real ? x : y, args[0], args[1]),
                            "pow" when args.Count == 2 => new Bin(Complex.Pow, args[0], args[1]),
                            _ => throw new FormatException($"Wrong number of arguments for {id}()"),
                        };
                    }
                    // A signal like V(out), I(R1), Ix(U1:OUT) or V(a,b)
                    int depth = 0, b0 = pos;
                    for (; pos < src.Length; pos++)
                    {
                        if (src[pos] == '(') depth++;
                        else if (src[pos] == ')' && --depth == 0) { pos++; break; }
                    }
                    if (depth != 0) throw new FormatException("Unbalanced parentheses");
                    string inside = src[(b0 + 1)..(pos - 1)].Trim();
                    string full = $"{id}({inside})";
                    if (Lookup(full) is Trace t) return new Sig(t);
                    if (inside.Contains(',') && id.Equals("V", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = inside.Split(',', 2);
                        var ta = Lookup($"V({parts[0].Trim()})") ?? throw new FormatException($"Unknown signal V({parts[0].Trim()})");
                        var tb = Lookup($"V({parts[1].Trim()})") ?? throw new FormatException($"Unknown signal V({parts[1].Trim()})");
                        return new Bin((x, y) => x - y, new Sig(ta), new Sig(tb));
                    }
                    throw new FormatException($"Unknown signal or function: {full}");
                }
                if (id.Equals("pi", StringComparison.OrdinalIgnoreCase)) return new Num(Math.PI);
                if (id == "e") return new Num(Math.E);
                if (Lookup(id) is Trace t2) return new Sig(t2);
                throw new FormatException($"Unknown name: {id}");
            }
            throw new FormatException($"Unexpected '{ch}'");
        }

        Trace? Lookup(string name)
        {
            var f = raw.Find(name);
            if (f != null) return raw.Traces[f];
            foreach (var kv in extras)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        Node ParseNumber()
        {
            int s = pos;
            while (pos < src.Length && (char.IsDigit(src[pos]) || src[pos] == '.')) pos++;
            if (pos < src.Length && (src[pos] is 'e' or 'E') && pos + 1 < src.Length &&
                (char.IsDigit(src[pos + 1]) || ((src[pos + 1] is '+' or '-') && pos + 2 < src.Length && char.IsDigit(src[pos + 2]))))
            {
                pos += 2;
                while (pos < src.Length && char.IsDigit(src[pos])) pos++;
            }
            double v = double.Parse(src[s..pos], Si.Inv);
            int u = pos;
            while (pos < src.Length && (char.IsLetter(src[pos]) || src[pos] == 'µ')) pos++;
            return new Num(v * Si.SuffixScale(src[u..pos]));
        }
    }
}
