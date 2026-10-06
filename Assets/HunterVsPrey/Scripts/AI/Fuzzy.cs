using System;
using System.Collections.Generic;
using UnityEngine;

namespace HunterVsPrey
{
    /// <summary>Membership functions: how much (0..1) a crisp value belongs to a fuzzy set.</summary>
    public static class Mf
    {
        /// <summary>Triangle: 0 at a, 1 at b, 0 at c.</summary>
        public static Func<float, float> Tri(float a, float b, float c) => x =>
            x <= a || x >= c ? (x == b ? 1f : 0f) : x <= b ? (x - a) / (b - a) : (c - x) / (c - b);

        /// <summary>Left shoulder: 1 up to <paramref name="full"/>, falls to 0 at <paramref name="zero"/>.</summary>
        public static Func<float, float> Left(float full, float zero) => x =>
            x <= full ? 1f : x >= zero ? 0f : (zero - x) / (zero - full);

        /// <summary>Right shoulder: 0 up to <paramref name="zero"/>, rises to 1 at <paramref name="full"/>.</summary>
        public static Func<float, float> Right(float zero, float full) => x =>
            x >= full ? 1f : x <= zero ? 0f : (x - zero) / (full - zero);
    }

    /// <summary>A linguistic variable, e.g. "HunterDistance" with terms Close / Medium / Far.</summary>
    public class FuzzyVar
    {
        public readonly string Name;
        public readonly float Min, Max;
        internal readonly int Index;
        internal readonly List<(string name, Func<float, float> mf)> Terms = new();

        internal FuzzyVar(string name, float min, float max, int index) { Name = name; Min = min; Max = max; Index = index; }

        public FuzzyVar Term(string name, Func<float, float> mf) { Terms.Add((name, mf)); return this; }

        internal int TermIndex(string term)
        {
            for (int k = 0; k < Terms.Count; k++) if (Terms[k].name == term) return k;
            throw new ArgumentException($"{Name} has no term '{term}'");
        }

        /// <summary>"HunterDistance is Close" — use with &amp; (AND = min), | (OR = max), ! (NOT = 1-x).</summary>
        public FuzzyExpr this[string term] => new IsExpr(this, TermIndex(term));
    }

    public abstract class FuzzyExpr
    {
        internal abstract float Eval(float[] inputs);
        public static FuzzyExpr operator &(FuzzyExpr a, FuzzyExpr b) => new OpExpr(a, b, Mathf.Min);
        public static FuzzyExpr operator |(FuzzyExpr a, FuzzyExpr b) => new OpExpr(a, b, Mathf.Max);
        public static FuzzyExpr operator !(FuzzyExpr a) => new NotExpr(a);
    }

    class IsExpr : FuzzyExpr
    {
        readonly FuzzyVar v; readonly int term;
        public IsExpr(FuzzyVar v, int term) { this.v = v; this.term = term; }
        internal override float Eval(float[] inputs) => v.Terms[term].mf(inputs[v.Index]);
    }

    class OpExpr : FuzzyExpr
    {
        readonly FuzzyExpr a, b; readonly Func<float, float, float> op;
        public OpExpr(FuzzyExpr a, FuzzyExpr b, Func<float, float, float> op) { this.a = a; this.b = b; this.op = op; }
        internal override float Eval(float[] inputs) => op(a.Eval(inputs), b.Eval(inputs));
    }

    class NotExpr : FuzzyExpr
    {
        readonly FuzzyExpr a;
        public NotExpr(FuzzyExpr a) => this.a = a;
        internal override float Eval(float[] inputs) => 1f - a.Eval(inputs);
    }

    /// <summary>
    /// Mamdani fuzzy inference system with one output:
    /// 1. fuzzify each crisp input with its membership functions,
    /// 2. fire every IF-THEN rule (AND = min, OR = max),
    /// 3. clip each rule's output set at its firing strength and merge them (max),
    /// 4. defuzzify with the centroid (centre of gravity) to get one crisp number.
    /// </summary>
    public class FuzzySystem
    {
        const int Samples = 61;

        public readonly string Name;
        readonly List<FuzzyVar> inputs = new();
        FuzzyVar output;
        readonly List<(FuzzyExpr when, int then)> rules = new();
        float[][] outputCurves; // membership of each output term at each sample point
        readonly float[] values;
        readonly float[] strengths;
        readonly float[] aggregate = new float[Samples];

        public FuzzySystem(string name, int inputCount)
        {
            Name = name;
            values = new float[inputCount];
            strengths = new float[64];
        }

        public FuzzyVar Input(string name, float min, float max)
        {
            var v = new FuzzyVar(name, min, max, inputs.Count);
            inputs.Add(v);
            return v;
        }

        public FuzzyVar Output(string name, float min, float max) => output = new FuzzyVar(name, min, max, -1);

        /// <summary>IF <paramref name="when"/> THEN output is <paramref name="then"/>.</summary>
        public FuzzySystem Rule(FuzzyExpr when, string then)
        {
            rules.Add((when, output.TermIndex(then)));
            outputCurves = null;
            return this;
        }

        public float Evaluate(params float[] crisp)
        {
            if (outputCurves == null) BuildCurves();
            for (int k = 0; k < values.Length; k++) values[k] = Mathf.Clamp(crisp[k], inputs[k].Min, inputs[k].Max);

            // fire rules: strength of each output term = max over the rules that conclude it
            int termCount = output.Terms.Count;
            for (int t = 0; t < termCount; t++) strengths[t] = 0f;
            foreach (var (when, then) in rules)
                strengths[then] = Mathf.Max(strengths[then], when.Eval(values));

            // aggregate clipped output sets, then centroid
            float num = 0f, den = 0f;
            for (int s = 0; s < Samples; s++)
            {
                float mu = 0f;
                for (int t = 0; t < termCount; t++) mu = Mathf.Max(mu, Mathf.Min(strengths[t], outputCurves[t][s]));
                aggregate[s] = mu;
                float x = Mathf.Lerp(output.Min, output.Max, s / (Samples - 1f));
                num += x * mu;
                den += mu;
            }
            return den > 1e-5f ? num / den : output.Min;
        }

        void BuildCurves()
        {
            outputCurves = new float[output.Terms.Count][];
            for (int t = 0; t < output.Terms.Count; t++)
            {
                outputCurves[t] = new float[Samples];
                for (int s = 0; s < Samples; s++)
                    outputCurves[t][s] = output.Terms[t].mf(Mathf.Lerp(output.Min, output.Max, s / (Samples - 1f)));
            }
        }

        /// <summary>Standard Low / Medium / High output on 0..1.</summary>
        public FuzzyVar Output01(string name) => Output(name, 0f, 1f)
            .Term("Low", Mf.Left(0.1f, 0.45f))
            .Term("Medium", Mf.Tri(0.25f, 0.5f, 0.75f))
            .Term("High", Mf.Right(0.55f, 0.9f));
    }
}
