using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Fixtures for the Windows side of Keep's tests (Game Mode, Game Bar, the graphics choices, the build look): a registry, a
/// change notification and the PC's record, all in memory. Nothing here touches this PC's real registry, power plans or
/// graphics choices.
/// </summary>
internal static class WindowsKit
{
    /// <summary>The PC's section of the record, in memory, as <see cref="Keep"/> would give and save it.</summary>
    internal sealed class PcRecord
    {
        internal Keep.KeepPc Pc { get; } = new();
        internal int Saves { get; private set; }
        /// <summary>Saving fails, as when another program holds the file.</summary>
        internal bool FailSaves { get; set; }
        /// <summary>Called inside every save, before the change is made.</summary>
        internal Action? OnSave { get; set; }

        internal Keep.KeepPc Load() => Pc;

        internal bool Save(Action<Keep.KeepPc> change)
        {
            if (FailSaves) return false;
            OnSave?.Invoke();
            Saves++;
            change(Pc);
            return true;
        }
    }

    /// <summary>A registry of keys and values in memory, which says what was written to it and can refuse or ignore a write.</summary>
    internal sealed class FakeRegistry : IUserRegistry
    {
        private readonly Dictionary<string, Dictionary<string, RegValue>> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every change asked for, in order: "set key|name", "delete key|name", "write-tree key", "delete-tree key".</summary>
        internal List<string> Writes { get; } = [];
        /// <summary>Writes of these (key, name) are accepted and not kept: the value reads back as it was.</summary>
        internal HashSet<(string Key, string Name)> Ignored { get; } = [];
        /// <summary>Writes of these whole keys are accepted and not kept.</summary>
        internal HashSet<string> IgnoredTrees { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Reading these keys as a tree fails, as one too big to keep does.</summary>
        internal HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Called before each change Exo makes (not before a test's own setup).</summary>
        internal Action? BeforeWrite { get; set; }

        private void Made(string key)
        {
            var path = "";
            foreach (var part in key.Split('\\'))
            {
                path = path.Length == 0 ? part : path + "\\" + part;
                _keys.Add(path);
            }
        }

        /// <summary>The test's own setup or another program's write: not counted as Exo's.</summary>
        internal void Put(string key, string name, RegValue? value)
        {
            if (value is null) { if (_values.TryGetValue(key, out var existing)) existing.Remove(name); return; }
            Made(key);
            if (!_values.TryGetValue(key, out var at)) _values[key] = at = new Dictionary<string, RegValue>(StringComparer.OrdinalIgnoreCase);
            at[name] = value;
        }

        internal void PutTree(string key, RegTree tree)
        {
            Made(key);
            foreach (var entry in tree.Values) Put(key, entry.Name, entry.Value);
            foreach (var (name, child) in tree.Keys) PutTree(key + "\\" + name, child);
        }

        public RegValue? Get(string key, string name) => _values.TryGetValue(key, out var at) && at.TryGetValue(name, out var value) ? value : null;

        public void Set(string key, string name, RegValue value)
        {
            BeforeWrite?.Invoke();
            Writes.Add($"set {key}|{name}");
            if (Ignored.Contains((key, name))) return;
            Put(key, name, value);
        }

        public void Delete(string key, string name)
        {
            BeforeWrite?.Invoke();
            Writes.Add($"delete {key}|{name}");
            if (Ignored.Contains((key, name))) return;
            Put(key, name, null);
        }

        public RegTree? ReadTree(string key)
        {
            if (Unreadable.Contains(key)) throw new IOException("The key holds more than Exo keeps.");
            if (!_keys.Contains(key)) return null;
            var tree = new RegTree();
            if (_values.TryGetValue(key, out var at))
                foreach (var (name, value) in at.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)) tree.Values.Add(new RegEntry(name, value));
            foreach (var child in _keys.Where(k => k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase) && !k[(key.Length + 1)..].Contains('\\')).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                tree.Keys[child[(key.Length + 1)..]] = ReadTree(child)!;
            return tree;
        }

        public void WriteTree(string key, RegTree tree)
        {
            BeforeWrite?.Invoke();
            Writes.Add($"write-tree {key}");
            if (IgnoredTrees.Contains(key)) return;
            Remove(key);
            PutTree(key, tree);
        }

        public void DeleteTree(string key)
        {
            BeforeWrite?.Invoke();
            Writes.Add($"delete-tree {key}");
            if (IgnoredTrees.Contains(key)) return;
            Remove(key);
        }

        private void Remove(string key)
        {
            foreach (var k in _keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _keys.Remove(k);
                _values.Remove(k);
            }
        }

        /// <summary>Everything in it as sorted lines, so two states are the same exactly when their lines are.</summary>
        internal string Snapshot() => string.Join('\n',
            _keys.Select(k => "key " + k.ToLowerInvariant())
                .Concat(_values.SelectMany(pair => pair.Value.Select(v => $"{pair.Key.ToLowerInvariant()}|{v.Key.ToLowerInvariant()}|{v.Value.Kind}|{v.Value.Text}|{v.Value.Number}|{(v.Value.Bytes is null ? "" : Convert.ToBase64String(v.Value.Bytes))}")))
                .Order(StringComparer.Ordinal));
    }

    /// <summary>A key's change notification: the test fires it as the registry would.</summary>
    internal sealed class FakeWatch : IKeyWatch
    {
        private Action? _changed;
        internal bool Running => _changed is not null;
        internal int Starts { get; private set; }
        internal int Stops { get; private set; }

        public bool Start(Action changed)
        {
            if (_changed is null) Starts++;
            _changed = changed;
            return true;
        }

        public void Stop()
        {
            if (_changed is not null) Stops++;
            _changed = null;
        }

        /// <summary>Something wrote to the key; nothing happens when nobody is watching.</summary>
        internal void Fire() => _changed?.Invoke();
    }
}
