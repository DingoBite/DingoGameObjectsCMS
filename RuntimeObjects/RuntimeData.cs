using System;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    [Serializable, Preserve]
    public struct RuntimeInstance : IComponentData
    {
        public long Id;
        public FixedString32Bytes StoreId;
        public uint Epoch;
    }

    [Serializable, Preserve]
    public struct RuntimeRealm : IComponentData
    {
        public StoreRealm Realm;
    }

    [Serializable, Preserve]
    public struct GameAssetKey : IEquatable<GameAssetKey>
    {
        public const string MODS = "mods";
        public const string UNDEFINED = "_undefined";
        public const string NONE = "_none";
        public const string ZERO_V = "0.0.0";

        public string Mod;
        public string Type;
        public string Key;
        public string Version;
        
        public GameAssetKey(string mod = null, string type = null, string key = null, string version = null)
        {
            Mod = string.IsNullOrWhiteSpace(mod) ? UNDEFINED : mod;
            Type = string.IsNullOrWhiteSpace(type) ? NONE : type;
            Key = string.IsNullOrWhiteSpace(key) ? NONE : key;
            Version = version == null ? ZERO_V : version.Trim();
        }

        public override string ToString() => $"{Mod}.{Type}.{Key}.{Version}";

        public bool Equals(GameAssetKey other)
        {
            return Mod == other.Mod && Type == other.Type && Key == other.Key && Version == other.Version;
        }

        public override bool Equals(object obj)
        {
            return obj is GameAssetKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Mod, Type, Key, Version);
        }

        public static bool operator ==(GameAssetKey left, GameAssetKey right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(GameAssetKey left, GameAssetKey right)
        {
            return !left.Equals(right);
        }
    }
}
