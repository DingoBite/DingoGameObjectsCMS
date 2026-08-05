using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects.DotsState;

namespace DingoGameObjectsCMS.Editor
{
    /// <summary>
    /// Reconciles active component Types with compact numeric ids. Removed
    /// components leave only reserved numeric ids; no CLR-name tombstones or
    /// string type identity are retained.
    /// </summary>
    public static class RuntimeDotsStateSchemaReconciler
    {
        public const int FORMAT_VERSION = 1;

        public static RuntimeDotsStateSchemaManifest Reconcile(
            RuntimeDotsStateSchemaManifest existing,
            IReadOnlyList<RuntimeDotsStateComponentSchema> discovered,
            int codecVersion)
        {
            if (discovered == null)
            {
                throw new ArgumentNullException(nameof(discovered));
            }
            if (codecVersion <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(codecVersion));
            }
            if (existing != null
                && existing.FormatVersion != FORMAT_VERSION)
            {
                throw new InvalidOperationException(
                    $"Unsupported DOTS state schema format {existing.FormatVersion}. Expected {FORMAT_VERSION}.");
            }

            ValidateDiscovered(discovered);
            var previousComponents = existing?.Components?
                                         .Where(value => value != null)
                                         .ToArray()
                                     ?? Array.Empty<
                                         RuntimeDotsStateComponentSchema>();
            ValidateExisting(previousComponents);

            var previousByType = previousComponents
                .Where(value => value.RuntimeType != null)
                .ToDictionary(value => value.RuntimeType);
            var reservedIds = new HashSet<int>(
                existing?.ReservedComponentTypeIds
                ?? Enumerable.Empty<int>());
            for (var i = 0; i < previousComponents.Length; i++)
            {
                if (previousComponents[i].RuntimeType == null)
                {
                    reservedIds.Add(
                        previousComponents[i].ComponentTypeId);
                }
            }

            var activeTypes = new HashSet<Type>(
                discovered.Select(value => value.RuntimeType));
            for (var i = 0; i < previousComponents.Length; i++)
            {
                var previous = previousComponents[i];
                if (previous.RuntimeType != null
                    && !activeTypes.Contains(previous.RuntimeType))
                {
                    reservedIds.Add(previous.ComponentTypeId);
                }
            }

            var unavailableIds = new HashSet<int>(reservedIds);
            for (var i = 0; i < previousComponents.Length; i++)
            {
                unavailableIds.Add(
                    previousComponents[i].ComponentTypeId);
            }
            var activeIds = new HashSet<int>();
            var components = new List<RuntimeDotsStateComponentSchema>(
                discovered.Count);
            foreach (var current in discovered.OrderBy(
                         value => TypeSortKey(value.RuntimeType),
                         StringComparer.Ordinal))
            {
                var component = CloneCurrent(current);
                if (previousByType.TryGetValue(
                        current.RuntimeType,
                        out var previous))
                {
                    component.ComponentTypeId =
                        previous.ComponentTypeId;
                }
                else
                {
                    component.ComponentTypeId = TakeNextId(unavailableIds);
                    unavailableIds.Add(component.ComponentTypeId);
                }

                if (!activeIds.Add(component.ComponentTypeId)
                    || reservedIds.Contains(component.ComponentTypeId))
                {
                    throw new InvalidOperationException(
                        $"DOTS state component id {component.ComponentTypeId} is already active or reserved.");
                }

                component.LayoutHash = CalculateLayoutHash(
                    component.RuntimeType);
                components.Add(component);
            }

            components.Sort((first, second) =>
                first.ComponentTypeId.CompareTo(second.ComponentTypeId));
            var manifest = new RuntimeDotsStateSchemaManifest
            {
                FormatVersion = FORMAT_VERSION,
                CodecVersion = codecVersion,
                Components = components,
                ReservedComponentTypeIds = reservedIds
                    .OrderBy(value => value)
                    .ToList(),
            };
            manifest.SchemaHash = CalculateSchemaHash(manifest);
            return manifest;
        }

        public static string CalculateSchemaHash(
            RuntimeDotsStateSchemaManifest manifest)
        {
            if (manifest?.Components == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            var builder = new StringBuilder();
            builder.Append("format=")
                .Append(manifest.FormatVersion)
                .Append("\ncodec=")
                .Append(manifest.CodecVersion)
                .Append('\n');
            foreach (var component in manifest.Components
                         .Where(value => value != null)
                         .OrderBy(value => value.ComponentTypeId))
            {
                RequireActiveSchema(component);
                builder.Append(component.ComponentTypeId).Append('|');
                AppendTypeIdentity(builder, component.RuntimeType);
                builder.Append((byte)component.Classification).Append('|')
                    .Append((byte)component.Kind).Append('|')
                    .Append(component.Enableable ? '1' : '0').Append('|')
                    .Append(component.LayoutHash).Append('\n');
            }
            builder.Append("reserved|");
            foreach (var id in (manifest.ReservedComponentTypeIds
                                ?? new List<int>())
                         .OrderBy(value => value))
            {
                builder.Append(id).Append(',');
            }

            return Sha256Hex(builder.ToString());
        }

        public static ulong CalculateLayoutHash(Type runtimeType)
        {
            if (runtimeType == null)
            {
                throw new ArgumentNullException(nameof(runtimeType));
            }

            var builder = new StringBuilder();
            AppendTypeLayout(
                builder,
                runtimeType,
                new HashSet<Type>());
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(
                Encoding.UTF8.GetBytes(builder.ToString()));
            ulong result = 0;
            for (var i = 0; i < sizeof(ulong); i++)
            {
                result = (result << 8) | bytes[i];
            }
            return result;
        }

        private static int TakeNextId(HashSet<int> usedIds)
        {
            var id = 0;
            while (usedIds.Contains(id))
            {
                id++;
            }
            return id;
        }

        private static void ValidateDiscovered(
            IReadOnlyList<RuntimeDotsStateComponentSchema> discovered)
        {
            var runtimeTypes = new HashSet<Type>();
            for (var i = 0; i < discovered.Count; i++)
            {
                var component = discovered[i]
                                ?? throw new InvalidOperationException(
                                    "Discovered DOTS state schema contains null.");
                RequireActiveSchema(component);
                if (!runtimeTypes.Add(component.RuntimeType))
                {
                    throw new InvalidOperationException(
                        $"Discovered DOTS state schema contains duplicate type '{component.RuntimeType.FullName}'.");
                }
            }
        }

        private static void ValidateExisting(
            IReadOnlyList<RuntimeDotsStateComponentSchema> components)
        {
            var ids = new HashSet<int>();
            var runtimeTypes = new HashSet<Type>();
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i];
                if (component.ComponentTypeId < 0
                    || !ids.Add(component.ComponentTypeId))
                {
                    throw new InvalidOperationException(
                        $"Existing DOTS state schema has invalid or duplicate id {component.ComponentTypeId}.");
                }
                if (component.RuntimeType != null
                    && !runtimeTypes.Add(component.RuntimeType))
                {
                    throw new InvalidOperationException(
                        $"Existing DOTS state schema contains duplicate type '{component.RuntimeType.FullName}'.");
                }
            }
        }

        private static void RequireActiveSchema(
            RuntimeDotsStateComponentSchema component)
        {
            if (component.RuntimeType == null)
            {
                throw new InvalidOperationException(
                    $"Active DOTS state component id {component.ComponentTypeId} requires RuntimeType = typeof(T).");
            }
        }

        private static RuntimeDotsStateComponentSchema CloneCurrent(
            RuntimeDotsStateComponentSchema source)
        {
            return new RuntimeDotsStateComponentSchema
            {
                ComponentTypeId = source.ComponentTypeId,
                RuntimeType = source.RuntimeType,
                Classification = source.Classification,
                Kind = source.Kind,
                Enableable = source.Enableable,
            };
        }

        private static void AppendTypeLayout(
            StringBuilder builder,
            Type type,
            HashSet<Type> traversal)
        {
            AppendTypeIdentity(builder, type);
            if (type.IsPrimitive || type.IsEnum || type.IsPointer)
            {
                return;
            }
            if (!traversal.Add(type))
            {
                throw new InvalidOperationException(
                    $"DOTS state value '{type.FullName}' has a recursive layout.");
            }

            var fields = type.GetFields(
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic)
                .OrderBy(field => field.Name, StringComparer.Ordinal)
                .ThenBy(
                    field => TypeSortKey(field.FieldType),
                    StringComparer.Ordinal)
                .ToArray();
            builder.Append('{');
            for (var i = 0; i < fields.Length; i++)
            {
                builder.Append(fields[i].Name.Length)
                    .Append(':')
                    .Append(fields[i].Name)
                    .Append('|');
                AppendTypeLayout(
                    builder,
                    fields[i].FieldType,
                    traversal);
            }
            builder.Append('}');
            traversal.Remove(type);
        }

        private static void AppendTypeIdentity(
            StringBuilder builder,
            Type type)
        {
            var value = TypeSortKey(type);
            builder.Append(value.Length)
                .Append(':')
                .Append(value)
                .Append('|');
        }

        private static string TypeSortKey(Type type)
        {
            return $"{type.Assembly.GetName().Name}:{type.FullName}";
        }

        private static string Sha256Hex(string value)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
            var hash = new StringBuilder(bytes.Length * 2);
            for (var i = 0; i < bytes.Length; i++)
            {
                hash.Append(bytes[i].ToString("x2"));
            }
            return hash.ToString();
        }
    }
}
