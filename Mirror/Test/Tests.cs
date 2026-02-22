using System.Collections.Generic;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.Mirror.Test
{
    public static class RuntimeStoreStructureHasher
    {
        public static ulong ComputeHash(RuntimeStore store)
        {
            var h = 1469598103934665603UL;

            var roots = new List<long>(store.Parents.V.Keys);
            roots.Sort();

            foreach (var rootId in roots)
            {
                HashSubtree(store, rootId, ref h);
            }

            return h;
        }

        public static string Dump(RuntimeStore store, int maxDepth = 64)
        {
            var sb = new StringBuilder(8 * 1024);

            var roots = new List<long>(store.Parents.V.Keys);
            roots.Sort();

            sb.AppendLine($"roots={roots.Count}");
            foreach (var r in roots)
            {
                DumpSubtree(store, r, 0, maxDepth, sb);
            }

            return sb.ToString();
        }

        private static void HashSubtree(RuntimeStore store, long id, ref ulong h)
        {
            Mix(ref h, id);

            if (store.TryTakeChildren(id, out var children))
            {
                Mix(ref h, children.Count);
                for (var i = 0; i < children.Count; i++)
                {
                    HashSubtree(store, children[i], ref h);
                }
            }
            else
            {
                Mix(ref h, 0);
            }
        }

        private static void DumpSubtree(RuntimeStore store, long id, int depth, int maxDepth, StringBuilder sb)
        {
            if (depth > maxDepth)
            {
                sb.Append(' ', depth * 2).AppendLine($"- {id} ...");
                return;
            }

            sb.Append(' ', depth * 2).AppendLine($"- {id}");

            if (store.TryTakeChildren(id, out var children))
            {
                for (var i = 0; i < children.Count; i++)
                {
                    DumpSubtree(store, children[i], depth + 1, maxDepth, sb);
                }
            }
        }

        private static void Mix(ref ulong h, long v)
        {
            unchecked
            {
                h ^= (ulong)v;
                h *= 1099511628211UL;
            }
        }

        private static void Mix(ref ulong h, int v) => Mix(ref h, (long)v);
    }

    public static class RuntimeStoreValidator
    {
        public static bool Validate(RuntimeStore store, out string error)
        {
            var visiting = new HashSet<long>();
            var visited = new HashSet<long>();

            var roots = new List<long>(store.Parents.V.Keys);
            foreach (var r in roots)
            {
                if (!Dfs(store, r, visiting, visited, depth: 0, out error))
                    return false;
            }

            error = null;
            return true;
        }

        private static bool Dfs(RuntimeStore store, long id, HashSet<long> visiting, HashSet<long> visited, int depth, out string error)
        {
            if (depth > 2048)
            {
                error = $"Depth overflow at id={id}";
                return false;
            }

            if (!store.TryTakeRO(id, out _))
            {
                error = $"Missing object id={id}";
                return false;
            }

            if (visited.Contains(id))
            {
                error = null;
                return true;
            }

            if (!visiting.Add(id))
            {
                error = $"Cycle detected at id={id}";
                return false;
            }

            if (store.TryTakeChildren(id, out var children))
            {
                for (var i = 0; i < children.Count; i++)
                {
                    if (!Dfs(store, children[i], visiting, visited, depth + 1, out error))
                        return false;
                }
            }

            visiting.Remove(id);
            visited.Add(id);
            error = null;
            return true;
        }
    }
}