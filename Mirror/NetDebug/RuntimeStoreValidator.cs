using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.Mirror.NetDebug
{
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