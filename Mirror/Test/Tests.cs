using System.Collections.Generic;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using NUnit.Framework;
using SnakeAndMice.GameComponents.Map.Components;
using Unity.Mathematics;

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

    internal sealed class RuntimeStoreReplicationCodecTests
    {
        [Test]
        public void BuildDeltaPayload_IsDeterministicForSameStates()
        {
            EnsureRuntimeComponentRegistry();

            var store = new RuntimeStore("determinism");
            var obj = CreateObject(store, new int2(1, 2));

            var baseline = RuntimeStoreSnapshotCodec.BuildSnapshot(store);

            SetCell(store, obj.InstanceId, new int2(5, 7));
            var current = RuntimeStoreSnapshotCodec.BuildSnapshot(store);

            var d1 = RuntimeStoreSnapshotCodec.BuildDeltaPayload(baseline, current);
            var d2 = RuntimeStoreSnapshotCodec.BuildDeltaPayload(baseline, current);

            Assert.That(DeltaSignature(d1), Is.EqualTo(DeltaSignature(d2)));
        }

        [Test]
        public void ApplyDelta_RejectsWhenBaselineIsMissing()
        {
            EnsureRuntimeComponentRegistry();

            var serverStore = new RuntimeStore("server");
            var obj = CreateObject(serverStore, new int2(0, 0));

            var baseline = RuntimeStoreSnapshotCodec.BuildSnapshot(serverStore);
            SetCell(serverStore, obj.InstanceId, new int2(3, 4));
            var current = RuntimeStoreSnapshotCodec.BuildSnapshot(serverStore);
            var delta = RuntimeStoreSnapshotCodec.BuildDeltaPayload(baseline, current);

            var coldClientStore = new RuntimeStore("client");
            var ok = RuntimeStoreSnapshotCodec.ApplyDelta(coldClientStore, delta);

            Assert.That(ok, Is.False);
        }

        [Test]
        public void FullSnapshotThenDelta_ReconnectConvergesToServerState()
        {
            EnsureRuntimeComponentRegistry();

            var serverStore = new RuntimeStore("server");
            var obj = CreateObject(serverStore, new int2(10, 10));

            var snapshot0 = RuntimeStoreSnapshotCodec.BuildSnapshot(serverStore);
            var fullPayload = RuntimeStoreSnapshotCodec.BuildFullPayload(snapshot0);

            var clientStore = new RuntimeStore("client");
            Assert.That(RuntimeStoreSnapshotCodec.ApplyFullSnapshot(clientStore, fullPayload), Is.True);

            SetCell(serverStore, obj.InstanceId, new int2(21, 34));
            var snapshot1 = RuntimeStoreSnapshotCodec.BuildSnapshot(serverStore);
            var delta = RuntimeStoreSnapshotCodec.BuildDeltaPayload(snapshot0, snapshot1);

            Assert.That(RuntimeStoreSnapshotCodec.ApplyDelta(clientStore, delta), Is.True);
            Assert.That(StoreSignature(serverStore), Is.EqualTo(StoreSignature(clientStore)));
        }

        [Test]
        public void ApplyDelta_Twice_KeepsSameState()
        {
            EnsureRuntimeComponentRegistry();

            var serverStore = new RuntimeStore("server");
            var obj = CreateObject(serverStore, new int2(2, 2));

            var snapshot0 = RuntimeStoreSnapshotCodec.BuildSnapshot(serverStore);
            SetCell(serverStore, obj.InstanceId, new int2(9, 9));
            var snapshot1 = RuntimeStoreSnapshotCodec.BuildSnapshot(serverStore);
            var delta = RuntimeStoreSnapshotCodec.BuildDeltaPayload(snapshot0, snapshot1);

            var clientStore = new RuntimeStore("client");
            Assert.That(RuntimeStoreSnapshotCodec.ApplyFullSnapshot(clientStore, RuntimeStoreSnapshotCodec.BuildFullPayload(snapshot0)), Is.True);

            Assert.That(RuntimeStoreSnapshotCodec.ApplyDelta(clientStore, delta), Is.True);
            var afterFirst = StoreSignature(clientStore);

            Assert.That(RuntimeStoreSnapshotCodec.ApplyDelta(clientStore, delta), Is.True);
            var afterSecond = StoreSignature(clientStore);

            Assert.That(afterSecond, Is.EqualTo(afterFirst));
        }

        private static void EnsureRuntimeComponentRegistry()
        {
            if (RuntimeComponentTypeRegistry.IsInitialized && RuntimeComponentTypeRegistry.TryGetId(typeof(GridTransform_GRC), out _))
                return;

            var componentType = EscapeJson(typeof(GridTransform_GRC).AssemblyQualifiedName);
            var json =
                "{\"Version\":1,\"Types\":[{" +
                "\"AssemblyQualifiedName\":\"" + componentType + "\"," +
                "\"CreatedAt\":\"tests\"," +
                "\"Order\":0" +
                "}]}";

            RuntimeComponentTypeRegistry.InitializeFromJson(json);
        }

        private static string EscapeJson(string value)
        {
            return value?.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string StoreSignature(RuntimeStore store)
        {
            var snapshot = RuntimeStoreSnapshotCodec.BuildSnapshot(store);
            var ids = new List<long>(snapshot.NodesById.Keys);
            ids.Sort();

            var sb = new StringBuilder(1024);
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                var node = snapshot.NodesById[id];

                sb.Append(id)
                    .Append('|')
                    .Append(node.ParentId)
                    .Append('|')
                    .Append(node.Index)
                    .Append('|')
                    .Append(System.Convert.ToBase64String(node.ObjectData ?? System.Array.Empty<byte>()));

                var compIds = new List<uint>(node.ComponentsByType.Keys);
                compIds.Sort();

                for (var c = 0; c < compIds.Count; c++)
                {
                    var compId = compIds[c];
                    sb.Append('|')
                        .Append(compId)
                        .Append(':')
                        .Append(System.Convert.ToBase64String(node.ComponentsByType[compId] ?? System.Array.Empty<byte>()));
                }

                sb.Append('\n');
            }

            return sb.ToString();
        }

        private static string DeltaSignature(RtStoreDeltaPayload payload)
        {
            var sb = new StringBuilder(512);

            if (payload.StructureChanges != null)
            {
                for (var i = 0; i < payload.StructureChanges.Count; i++)
                {
                    var s = payload.StructureChanges[i];
                    sb.Append("S:")
                        .Append((int)s.Kind)
                        .Append('|')
                        .Append(s.Id)
                        .Append('|')
                        .Append(s.ParentId)
                        .Append('|')
                        .Append(s.Index)
                        .Append('|')
                        .Append((int)s.RemoveMode)
                        .Append('|')
                        .Append(System.Convert.ToBase64String(s.SpawnData ?? System.Array.Empty<byte>()))
                        .Append('\n');
                }
            }

            if (payload.ComponentStructureChanges != null)
            {
                for (var i = 0; i < payload.ComponentStructureChanges.Count; i++)
                {
                    var s = payload.ComponentStructureChanges[i];
                    sb.Append("CS:")
                        .Append(s.ObjectId)
                        .Append('|')
                        .Append(s.CompTypeId)
                        .Append('|')
                        .Append((int)s.Kind)
                        .Append('|')
                        .Append(System.Convert.ToBase64String(s.Payload ?? System.Array.Empty<byte>()))
                        .Append('\n');
                }
            }

            if (payload.ComponentChanges != null)
            {
                for (var i = 0; i < payload.ComponentChanges.Count; i++)
                {
                    var c = payload.ComponentChanges[i];
                    sb.Append("C:")
                        .Append(c.ObjectId)
                        .Append('|')
                        .Append(c.CompTypeId)
                        .Append('|')
                        .Append(System.Convert.ToBase64String(c.Payload ?? System.Array.Empty<byte>()))
                        .Append('\n');
                }
            }

            return sb.ToString();
        }

        private static DingoGameObjectsCMS.RuntimeObjects.Objects.GameRuntimeObject CreateObject(RuntimeStore store, int2 cell)
        {
            var obj = store.Create();
            obj.AddOrReplace(new GridTransform_GRC
            {
                T = new GridTransform
                {
                    Cell = cell,
                    SubCell = int2.zero,
                    SubCell01 = float2.zero,
                    SizeInSubCells = new int2(1, 1),
                },
            });

            return obj;
        }

        private static void SetCell(RuntimeStore store, long id, int2 value)
        {
            Assert.That(store.TryTakeRW(id, out var obj), Is.True);

            var tr = obj.TakeRW<GridTransform_GRC>();
            Assert.That(tr, Is.Not.Null);

            var data = tr.T;
            data.Cell = value;
            tr.T = data;
        }
    }
}
