using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.View
{
    public enum GameRuntimeObjectOperationKind : byte
    {
        Snapshot = 0,
        Structure = 1,
        ComponentStructure = 2,
        Component = 3,
        Release = 4,
    }

    public readonly struct GameRuntimeObjectOperation
    {
        public readonly GameRuntimeObjectOperationKind Kind;
        public readonly RuntimeStore Store;
        public readonly long Id;
        public readonly GameRuntimeObject PreviousValue;
        public readonly GameRuntimeObject Value;
        public readonly RuntimeStructureDirty StructureDirty;
        public readonly ObjectStructDirty ComponentStructureDirty;
        public readonly ObjectComponentDirty ComponentDirty;

        public GameRuntimeObject TargetValue => Value ?? PreviousValue;

        private GameRuntimeObjectOperation(
            GameRuntimeObjectOperationKind kind,
            RuntimeStore store,
            long id,
            GameRuntimeObject previousValue,
            GameRuntimeObject value,
            RuntimeStructureDirty structureDirty,
            ObjectStructDirty componentStructureDirty,
            ObjectComponentDirty componentDirty)
        {
            Kind = kind;
            Store = store;
            Id = id;
            PreviousValue = previousValue;
            Value = value;
            StructureDirty = structureDirty;
            ComponentStructureDirty = componentStructureDirty;
            ComponentDirty = componentDirty;
        }

        public static GameRuntimeObjectOperation Snapshot(RuntimeStore store, long id, GameRuntimeObject previousValue, GameRuntimeObject value) =>
            new(GameRuntimeObjectOperationKind.Snapshot, store, id, previousValue, value, default, default, default);

        public static GameRuntimeObjectOperation Structure(RuntimeStore store, RuntimeStructureDirty dirty, GameRuntimeObject previousValue, GameRuntimeObject value) =>
            new(GameRuntimeObjectOperationKind.Structure, store, dirty.Id, previousValue, value, dirty, default, default);

        public static GameRuntimeObjectOperation ComponentStructure(RuntimeStore store, ObjectStructDirty dirty, GameRuntimeObject previousValue, GameRuntimeObject value) =>
            new(GameRuntimeObjectOperationKind.ComponentStructure, store, dirty.Id, previousValue, value, default, dirty, default);

        public static GameRuntimeObjectOperation Component(RuntimeStore store, ObjectComponentDirty dirty, GameRuntimeObject previousValue, GameRuntimeObject value) =>
            new(GameRuntimeObjectOperationKind.Component, store, dirty.Id, previousValue, value, default, default, dirty);

        public static GameRuntimeObjectOperation Release(RuntimeStore store, long id, GameRuntimeObject previousValue) =>
            new(GameRuntimeObjectOperationKind.Release, store, id, previousValue, null, default, default, default);
    }
}
