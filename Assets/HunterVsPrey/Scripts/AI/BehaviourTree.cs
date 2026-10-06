using System;
using System.Collections.Generic;
using System.Text;

namespace HunterVsPrey
{
    public enum BtStatus { Success, Failure, Running }

    /// <summary>
    /// Minimal behaviour tree. The tree is re-ticked from the root every frame ("reactive"), so a
    /// higher-priority branch (e.g. Evade) instantly interrupts a lower one (e.g. Collect).
    /// While ticking it records the path of nodes that led to the running action, for the debug HUD.
    /// </summary>
    public class BehaviourTree
    {
        readonly BtNode root;
        readonly List<BtNode> stack = new();
        readonly List<BtNode> active = new(), previous = new();
        bool marked;

        public BehaviourTree(BtNode root) => this.root = root;

        public BtStatus Tick()
        {
            previous.Clear();
            previous.AddRange(active);
            active.Clear();
            marked = false;
            return root.Tick(this);
        }

        /// <summary>Was this node on the running path last frame? (Used for hysteresis so bots don't flicker between branches.)</summary>
        public bool WasActive(string name)
        {
            foreach (var n in previous) if (n.Name == name) return true;
            return false;
        }

        /// <summary>Is this node on the running path this frame? (Used to label SL training data.)</summary>
        public bool IsActive(string name)
        {
            foreach (var n in active) if (n.Name == name) return true;
            return false;
        }

        /// <summary>"Hunter > Hunt > Pursue" — the branch currently in control.</summary>
        public string ActivePath
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var n in active)
                {
                    if (string.IsNullOrEmpty(n.Name)) continue;
                    if (sb.Length > 0) sb.Append(" > ");
                    sb.Append(n.Name);
                }
                return sb.ToString();
            }
        }

        internal void Push(BtNode n) => stack.Add(n);
        internal void Pop() => stack.RemoveAt(stack.Count - 1);

        internal void MarkActive()
        {
            if (marked) return; // first action to run this frame owns the path
            marked = true;
            active.AddRange(stack);
        }
    }

    public abstract class BtNode
    {
        public readonly string Name;
        protected BtNode(string name) => Name = name;

        public BtStatus Tick(BehaviourTree t)
        {
            t.Push(this);
            var s = OnTick(t);
            t.Pop();
            return s;
        }

        protected abstract BtStatus OnTick(BehaviourTree t);
    }

    /// <summary>Priority selector: tries children in order, returns the first that doesn't fail.</summary>
    public class Selector : BtNode
    {
        readonly BtNode[] children;
        public Selector(string name, params BtNode[] children) : base(name) => this.children = children;

        protected override BtStatus OnTick(BehaviourTree t)
        {
            foreach (var c in children)
            {
                var s = c.Tick(t);
                if (s != BtStatus.Failure) return s;
            }
            return BtStatus.Failure;
        }
    }

    /// <summary>Sequence: runs children in order, stops at the first that fails or is still running.</summary>
    public class Sequence : BtNode
    {
        readonly BtNode[] children;
        public Sequence(string name, params BtNode[] children) : base(name) => this.children = children;

        protected override BtStatus OnTick(BehaviourTree t)
        {
            foreach (var c in children)
            {
                var s = c.Tick(t);
                if (s != BtStatus.Success) return s;
            }
            return BtStatus.Success;
        }
    }

    /// <summary>Leaf that checks something (often a fuzzy output against a threshold).</summary>
    public class Condition : BtNode
    {
        readonly Func<bool> test;
        public Condition(string name, Func<bool> test) : base(name) => this.test = test;
        protected override BtStatus OnTick(BehaviourTree t) => test() ? BtStatus.Success : BtStatus.Failure;
    }

    /// <summary>Leaf that does something this frame (writes the bot's ActorInput).</summary>
    public class BtAction : BtNode
    {
        readonly Func<BtStatus> run;
        public BtAction(string name, Func<BtStatus> run) : base(name) => this.run = run;

        protected override BtStatus OnTick(BehaviourTree t)
        {
            var s = run();
            if (s != BtStatus.Failure) t.MarkActive();
            return s;
        }
    }
}
