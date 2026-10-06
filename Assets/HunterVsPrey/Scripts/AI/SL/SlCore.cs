using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace HunterVsPrey
{
    /// <summary>Which brain drives a team's bots.</summary>
    public enum BrainKind { BehaviourTree, SL }

    /// <summary>
    /// Supervised-learning switches. Static so they survive the scene reload between matches.
    /// Pipeline: humans play with "Record my gameplay" on → every 0.1 s the situation (what a bot in their place
    /// would perceive) and what the human did are written to SL/data → SL/train.py trains MLPs →
    /// JSON weights in Resources/SL → SL bots run the MLPs to act like the humans did.
    /// </summary>
    public static class SlSettings
    {
        // Remembered between Play sessions (PlayerPrefs), so "Record" stays on until you turn it off.
        public static BrainKind HunterBrain
        {
            get => (BrainKind)PlayerPrefs.GetInt("HvP.HunterBrain", 0);
            set => PlayerPrefs.SetInt("HvP.HunterBrain", (int)value);
        }
        public static BrainKind PreyBrain
        {
            get => (BrainKind)PlayerPrefs.GetInt("HvP.PreyBrain", 0);
            set => PlayerPrefs.SetInt("HvP.PreyBrain", (int)value);
        }

        /// <summary>Record the human player's gameplay as SL training data.</summary>
        public static bool Recording
        {
            get => PlayerPrefs.GetInt("HvP.Record", 0) == 1;
            set { PlayerPrefs.SetInt("HvP.Record", value ? 1 : 0); PlayerPrefs.Save(); }
        }

        /// <summary>Decisions are sampled 10× per second per bot.</summary>
        public const float SampleInterval = 0.1f;
    }

    /// <summary>
    /// Writes training data as CSV, one file per match per dataset, so train.py can split train/validation by match
    /// (samples from the same match are too similar to test on fairly).
    /// Classification rows: features..., label (class index). Regression rows: features..., target.
    ///
    /// Every row goes straight to disk (flushed), so stopping Play, closing Unity or a crash mid-match keeps
    /// everything recorded up to that moment. A half-played match is still a valid file.
    /// </summary>
    public static class SlRecorder
    {
        public static string DataFolder => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "SL", "data"));
        public static int SamplesThisMatch { get; private set; }

        class Open { public StreamWriter w; public string path; public int rows; }
        static readonly Dictionary<string, Open> writers = new();
        static string matchTag;
        static bool hooked;

        public static void BeginMatch()
        {
            CloseAll();
            matchTag = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            SamplesThisMatch = 0;
            if (!hooked) { hooked = true; Application.quitting += CloseAll; } // stopping Play in the editor counts as quitting
        }

        public static void Write(string dataset, string[] header, float[] x, float y)
        {
            if (!SlSettings.Recording) return;
            if (!writers.TryGetValue(dataset, out var o))
            {
                Directory.CreateDirectory(DataFolder);
                string path = Path.Combine(DataFolder, $"{dataset}_{matchTag ?? DateTime.Now.ToString("yyyyMMdd_HHmmss")}.csv");
                var w = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true }; // every row hits the disk
                w.WriteLine(string.Join(",", header) + ",target");
                writers[dataset] = o = new Open { w = w, path = path };
            }
            var sb = new StringBuilder();
            foreach (var v in x) sb.Append(v.ToString("0.####", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(y.ToString("0.####", CultureInfo.InvariantCulture));
            o.w.WriteLine(sb.ToString());
            o.rows++;
            if (!dataset.EndsWith("_target")) SamplesThisMatch++; // one per 0.1 s of play
        }

        /// <summary>Closes this match's files; files with only a header (nothing recorded) are deleted.</summary>
        public static void CloseAll()
        {
            foreach (var o in writers.Values)
            {
                o.w.Dispose();
                if (o.rows == 0) File.Delete(o.path);
            }
            writers.Clear();
        }

        /// <summary>What's saved on disk so far: matches and rows per dataset (for the menu).</summary>
        public static string Summary()
        {
            if (!Directory.Exists(DataFolder)) return "nothing recorded yet";
            int preyFiles = 0, hunterFiles = 0, preyRows = 0, hunterRows = 0;
            foreach (var f in Directory.GetFiles(DataFolder, "*.csv"))
            {
                string name = Path.GetFileName(f);
                bool prey = name.StartsWith("prey_"), hunter = name.StartsWith("hunter_") && !name.StartsWith("hunter_target_");
                if (!prey && !hunter) continue;
                int rows = 0;
                try { using var r = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)); while (r.ReadLine() != null) rows++; }
                catch (IOException) { continue; }
                rows = Mathf.Max(0, rows - 1); // header
                if (prey) { preyFiles++; preyRows += rows; } else { hunterFiles++; hunterRows += rows; }
            }
            return $"Saved: Prey {preyFiles} matches ({preyRows / 600f:0.0} min) · Hunter {hunterFiles} ({hunterRows / 600f:0.0} min)";
        }
    }

    /// <summary>
    /// Multi-layer perceptron inference in plain C# (no ML package needed).
    /// Loads the JSON that SL/train.py exports: standardise input → (Linear → ReLU)* → Linear.
    /// Classifiers return softmax probabilities, regressors return the raw output.
    /// </summary>
    public class Mlp
    {
        [Serializable]
        class Data
        {
            public string kind;
            public int[] sizes;
            public float[] weights, biases, mean, std;
            public string[] classes, featureNames;
            public float valAccuracy;
        }

        Data d;
        float[][] buffers;
        public string[] Classes => d.classes;
        public float ValAccuracy => d.valAccuracy;
        public int Inputs => d.sizes[0];

        static readonly Dictionary<string, Mlp> cache = new();

        /// <summary>Loads Resources/SL/&lt;name&gt;.json, or null if it hasn't been trained yet.</summary>
        public static Mlp Load(string name)
        {
            if (cache.TryGetValue(name, out var m)) return m;
            var text = Resources.Load<TextAsset>($"SL/{name}");
            if (text != null)
            {
                try
                {
                    m = new Mlp { d = JsonUtility.FromJson<Data>(text.text) };
                    m.buffers = new float[m.d.sizes.Length][];
                    for (int l = 0; l < m.d.sizes.Length; l++) m.buffers[l] = new float[m.d.sizes[l]];
                }
                catch (Exception e)
                {
                    Debug.LogError($"[SL] Could not read model '{name}': {e.Message}");
                    m = null;
                }
            }
            cache[name] = m;
            return m;
        }

        public static bool Exists(string name) => Load(name) != null;

        /// <summary>Checks the model was trained on the same feature layout the game produces now.</summary>
        public bool Matches(string[] featureNames)
        {
            if (d.featureNames == null || d.featureNames.Length != featureNames.Length) return false;
            for (int k = 0; k < featureNames.Length; k++) if (d.featureNames[k] != featureNames[k]) return false;
            return true;
        }

        public float[] Forward(float[] x)
        {
            var a = buffers[0];
            for (int k = 0; k < a.Length; k++) a[k] = (x[k] - d.mean[k]) / (d.std[k] > 1e-6f ? d.std[k] : 1f);
            int w = 0, b = 0;
            for (int l = 1; l < d.sizes.Length; l++)
            {
                var prev = buffers[l - 1];
                var next = buffers[l];
                bool last = l == d.sizes.Length - 1;
                for (int o = 0; o < next.Length; o++)
                {
                    float s = d.biases[b + o];
                    for (int i = 0; i < prev.Length; i++) s += d.weights[w + o * prev.Length + i] * prev[i];
                    next[o] = last || s > 0f ? s : 0f; // ReLU on hidden layers
                }
                w += next.Length * prev.Length;
                b += next.Length;
            }
            var output = buffers[^1];
            if (d.kind == "classifier") Softmax(output);
            return output;
        }

        static void Softmax(float[] z)
        {
            float max = float.MinValue, sum = 0f;
            foreach (var v in z) max = Mathf.Max(max, v);
            for (int k = 0; k < z.Length; k++) { z[k] = Mathf.Exp(z[k] - max); sum += z[k]; }
            for (int k = 0; k < z.Length; k++) z[k] /= sum;
        }

        /// <summary>Best class the bot is allowed to take right now (invalid actions masked out), or -1.</summary>
        public static int ArgMax(float[] probs, bool[] valid, int skip = -1)
        {
            int best = -1;
            for (int k = 0; k < probs.Length; k++)
                if (valid[k] && k != skip && (best < 0 || probs[k] > probs[best])) best = k;
            return best;
        }
    }

    /// <summary>Small helpers for building feature vectors.</summary>
    public static class Feat
    {
        public static float B(bool v) => v ? 1f : 0f;
        public static float N(float v, float max) => Mathf.Clamp(v / max, -1f, 1f);
    }
}
