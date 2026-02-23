#if MIRROR
using System;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Mirror;
using Unity.Collections;

namespace DingoGameObjectsCMS.Mirror.NetDebug
{
    public sealed class RuntimeStoreNetDebugClient
    {
        private readonly Func<FixedString32Bytes, RuntimeStore> _stores;

        public RuntimeStoreNetDebugClient(Func<FixedString32Bytes, RuntimeStore> stores)
        {
            _stores = stores;

            NetworkClient.RegisterHandler<RtDebugRequestHashMsg>(OnReqHash);
            NetworkClient.RegisterHandler<RtDebugRequestDumpMsg>(OnReqDump);
        }

        private RuntimeStore Get(FixedString32Bytes key) => _stores(key);

        private void OnReqHash(RtDebugRequestHashMsg msg)
        {
            var store = Get(msg.Store);

            var ok = RuntimeStoreValidator.Validate(store, out var err);
            var hash = RuntimeStoreStructureHasher.ComputeHash(store);

            NetworkClient.Send(new RtDebugHashMsg
            {
                Store = msg.Store,
                Hash = hash,
                Valid = ok,
                Error = err ?? ""
            }, Channels.Reliable);
        }

        private void OnReqDump(RtDebugRequestDumpMsg msg)
        {
            var store = Get(msg.Store);
            var dump = RuntimeStoreStructureHasher.Dump(store, msg.MaxDepth <= 0 ? 64 : msg.MaxDepth);

            NetworkClient.Send(new RtDebugDumpMsg
            {
                Store = msg.Store,
                Dump = dump
            }, Channels.Reliable);
        }
    }
}
#endif
