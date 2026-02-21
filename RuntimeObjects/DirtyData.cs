using Unity.Collections;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    public readonly struct DirtyItem
    {
        public readonly FixedString32Bytes StoreId;
        public readonly long InstanceId;
        public readonly uint CompTypeId;
        public readonly ulong FieldMask;
        public readonly NativeArray<byte> Delta;

        public DirtyItem(FixedString32Bytes storeId, long instanceId, uint compTypeId, ulong fieldMask, NativeArray<byte> delta)
        {
            StoreId = storeId;
            InstanceId = instanceId;
            CompTypeId = compTypeId;
            FieldMask = fieldMask;
            Delta = delta;
        }
    }
    
    public readonly struct DirtyKey
    {
        public readonly GameRuntimeObject Obj;
        public readonly uint CompTypeId;

        public DirtyKey(GameRuntimeObject obj, uint compTypeId)
        {
            Obj = obj;
            CompTypeId = compTypeId;
        }
    }

    public enum CompStructOpKind : byte
    {
        Add = 1,
        Remove = 2
    }

    public readonly struct CompStructOp
    {
        public readonly FixedString32Bytes StoreId;
        public readonly long InstanceId;
        public readonly uint CompTypeId;
        public readonly CompStructOpKind Kind;

        public CompStructOp(FixedString32Bytes storeId, long instanceId, uint compTypeId, CompStructOpKind kind)
        {
            StoreId = storeId;
            InstanceId = instanceId;
            CompTypeId = compTypeId;
            Kind = kind;
        }
    }
}