using System;
using System.Collections;
using System.Collections.Generic;

namespace WorldSim.Core.Economy
{
    /// <summary>
    /// Agents keyed by id, enumerated in ascending id order like a SortedDictionary (S5-1). Lookups
    /// are a hash table and enumeration walks two flat lists, which is several times faster than
    /// walking a red-black tree; the systems enumerate citizens dozens of times a day. Ids are
    /// handed out in increasing order, so adding is an append; removing shifts the lists (rare).
    /// Changing the map while enumerating it throws, as SortedDictionary does.
    /// </summary>
    public sealed class IdMap<T> : IEnumerable<KeyValuePair<int, T>>
    {
        private readonly Dictionary<int, T> _map = new Dictionary<int, T>();
        private readonly List<int> _ids = new List<int>();
        private readonly List<T> _vals = new List<T>();
        private int _version;

        public int Count => _ids.Count;
        public ValueList Values => new ValueList(this);
        public KeyList Keys => new KeyList(this);

        public T this[int id]
        {
            get => _map.TryGetValue(id, out T v) ? v : throw new KeyNotFoundException($"id {id}");
            set
            {
                if (_map.ContainsKey(id)) { _map[id] = value; _vals[_ids.BinarySearch(id)] = value; _version++; }
                else Add(id, value);
            }
        }

        public bool TryGetValue(int id, out T value) => _map.TryGetValue(id, out value);
        public bool ContainsKey(int id) => _map.ContainsKey(id);

        public void Add(int id, T value)
        {
            _map.Add(id, value); // throws on a duplicate, like SortedDictionary
            _version++;
            if (_ids.Count == 0 || id > _ids[_ids.Count - 1]) { _ids.Add(id); _vals.Add(value); return; }
            int i = ~_ids.BinarySearch(id);
            _ids.Insert(i, id);
            _vals.Insert(i, value);
        }

        public bool Remove(int id)
        {
            if (!_map.Remove(id)) return false;
            _version++;
            int i = _ids.BinarySearch(id);
            _ids.RemoveAt(i);
            _vals.RemoveAt(i);
            return true;
        }

        public Enumerator GetEnumerator() => new Enumerator(this);
        IEnumerator<KeyValuePair<int, T>> IEnumerable<KeyValuePair<int, T>>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<KeyValuePair<int, T>>
        {
            private readonly IdMap<T> _m;
            private readonly int _version;
            private int _i;

            internal Enumerator(IdMap<T> m) { _m = m; _version = m._version; _i = -1; }

            public KeyValuePair<int, T> Current => new KeyValuePair<int, T>(_m._ids[_i], _m._vals[_i]);
            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_version != _m._version) throw new InvalidOperationException("Collection was modified during enumeration.");
                return ++_i < _m._ids.Count;
            }

            public void Reset() => _i = -1;
            public void Dispose() { }
        }

        public readonly struct ValueList : IEnumerable<T>
        {
            private readonly IdMap<T> _m;
            internal ValueList(IdMap<T> m) { _m = m; }
            public ValueEnumerator GetEnumerator() => new ValueEnumerator(_m);
            IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public struct ValueEnumerator : IEnumerator<T>
        {
            private readonly IdMap<T> _m;
            private readonly int _version;
            private int _i;

            internal ValueEnumerator(IdMap<T> m) { _m = m; _version = m._version; _i = -1; }

            public T Current => _m._vals[_i];
            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_version != _m._version) throw new InvalidOperationException("Collection was modified during enumeration.");
                return ++_i < _m._vals.Count;
            }

            public void Reset() => _i = -1;
            public void Dispose() { }
        }

        public readonly struct KeyList : IEnumerable<int>
        {
            private readonly IdMap<T> _m;
            internal KeyList(IdMap<T> m) { _m = m; }
            public IEnumerator<int> GetEnumerator()
            {
                int version = _m._version;
                for (int i = 0; i < _m._ids.Count; i++)
                {
                    if (version != _m._version) throw new InvalidOperationException("Collection was modified during enumeration.");
                    yield return _m._ids[i];
                }
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
