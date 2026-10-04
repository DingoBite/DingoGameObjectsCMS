using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects.Replay;

namespace DingoGameObjectsCMS.Editor
{
    public enum RuntimeReplayOwnedStorageCleanupPolicy { AggregateFailures, FailFast }

    public class RuntimeReplayOwnedStorageGenerationProfile
    {
        public readonly Type OwnerType;
        public readonly string GeneratedCodePath;
        public readonly RuntimeReplayOwnedStorageCleanupPolicy CleanupPolicy;

        public RuntimeReplayOwnedStorageGenerationProfile(Type ownerType, string generatedCodePath, RuntimeReplayOwnedStorageCleanupPolicy cleanupPolicy)
        {
            OwnerType = ownerType ?? throw new ArgumentNullException(nameof(ownerType));
            if (string.IsNullOrWhiteSpace(generatedCodePath))
            {
                throw new ArgumentException("Owned storage output path is required.", nameof(generatedCodePath));
            }
            if (!Enum.IsDefined(typeof(RuntimeReplayOwnedStorageCleanupPolicy), cleanupPolicy))
            {
                throw new ArgumentOutOfRangeException(nameof(cleanupPolicy));
            }
            GeneratedCodePath = Path.GetFullPath(generatedCodePath);
            CleanupPolicy = cleanupPolicy;
        }
    }

    public static class RuntimeReplayOwnedStorageLifecycleGenerator
    {
        public static bool GenerateAndWrite(params RuntimeReplayOwnedStorageGenerationProfile[] profiles)
        {
            var sources = GenerateAll(profiles);
            var changed = false;
            for (var i = 0; i < profiles.Length; i++)
            {
                changed |= RuntimePatchSchemaGenerationCore.WriteIfChanged(profiles[i].GeneratedCodePath, sources[i]);
            }
            return changed;
        }

        public static bool AreOutputsCurrent(params RuntimeReplayOwnedStorageGenerationProfile[] profiles)
        {
            var sources = GenerateAll(profiles);
            for (var i = 0; i < profiles.Length; i++)
            {
                if (!File.Exists(profiles[i].GeneratedCodePath) || File.ReadAllText(profiles[i].GeneratedCodePath) != sources[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static string[] GenerateAll(RuntimeReplayOwnedStorageGenerationProfile[] profiles)
        {
            if (profiles == null || profiles.Length == 0)
            {
                throw new ArgumentException("Explicit owned storage profiles are required.", nameof(profiles));
            }
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var owners = new HashSet<Type>();
            var sources = new string[profiles.Length];
            for (var i = 0; i < profiles.Length; i++)
            {
                var profile = profiles[i] ?? throw new ArgumentException("An owned storage profile is null.", nameof(profiles));
                if (!paths.Add(profile.GeneratedCodePath) || !owners.Add(profile.OwnerType))
                {
                    throw new InvalidOperationException("Owned storage profiles contain a duplicate owner or output path.");
                }
                sources[i] = Generate(profile);
            }
            return sources;
        }

        public static string Generate(RuntimeReplayOwnedStorageGenerationProfile profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }
            var owner = profile.OwnerType;
            if (!owner.IsClass || owner.IsAbstract || !owner.IsPublic || owner.IsNested || owner.IsGenericType || !typeof(IDisposable).IsAssignableFrom(owner)
                || owner.GetConstructor(Type.EmptyTypes) == null || !HasVoidMethod(owner, "Reset", true) || !HasVoidMethod(owner, "Dispose", true))
            {
                throw new InvalidOperationException($"Owned storage owner '{owner}' must be a public, concrete, top-level non-generic IDisposable class with a public parameterless constructor and its own Reset and Dispose.");
            }
            var ownerNamespace = RuntimeGeneratedCodeNames.RequireNamespace(owner.Namespace, nameof(profile.OwnerType));
            var ownerName = RuntimeGeneratedCodeNames.RequireIdentifier(owner.Name, nameof(profile.OwnerType));
            var fields = new SortedDictionary<int, FieldInfo>();
            for (var declaring = owner; declaring != null; declaring = declaring.BaseType)
            {
                foreach (var field in declaring.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var declaration = field.GetCustomAttribute<RuntimeReplayOwnedStorageAttribute>();
                    if (declaration == null && !IsNativeRows(field.FieldType))
                    {
                        continue;
                    }
                    if (declaration == null || declaring != owner || !field.IsPublic || !field.IsInitOnly || field.IsStatic || declaration.Order < 0
                        || !field.FieldType.IsClass || field.FieldType.IsAbstract || !field.FieldType.IsVisible || field.FieldType.ContainsGenericParameters
                        || !typeof(IDisposable).IsAssignableFrom(field.FieldType) || field.FieldType.GetConstructor(Type.EmptyTypes) == null
                        || !HasVoidMethod(field.FieldType, "Reset") || !HasVoidMethod(field.FieldType, "Dispose"))
                    {
                        throw new InvalidOperationException($"Owned storage field '{declaring.FullName}.{field.Name}' requires a non-negative order and a public readonly declared instance field of concrete parameterless IDisposable storage with public Reset and Dispose.");
                    }
                    RuntimeGeneratedCodeNames.RequireIdentifier(field.Name, nameof(profile.OwnerType));
                    if (fields.ContainsKey(declaration.Order))
                    {
                        throw new InvalidOperationException($"Owned storage owner '{owner}' repeats order {declaration.Order}.");
                    }
                    fields.Add(declaration.Order, field);
                }
            }
            if (fields.Count == 0)
            {
                throw new InvalidOperationException($"Owned storage owner '{owner}' declares no owned fields.");
            }
            var source = new StringBuilder();
            source.AppendLine("// <auto-generated />").AppendLine("using System;").AppendLine()
                .AppendLine($"namespace {ownerNamespace}").AppendLine("{").AppendLine($"    public partial class {ownerName}").AppendLine("    {")
                .AppendLine($"        public {ownerName}()").AppendLine("        {").AppendLine("            try").AppendLine("            {");
            foreach (var field in fields.Values)
            {
                source.AppendLine($"                this.{field.Name} = new {TypeName(field.FieldType)}();");
            }
            source.AppendLine("            }");
            if (profile.CleanupPolicy == RuntimeReplayOwnedStorageCleanupPolicy.AggregateFailures)
            {
                source.AppendLine("            catch (global::System.Exception initializationFailure)").AppendLine("            {")
                    .AppendLine("                try { Dispose(); }").AppendLine("                catch (global::System.Exception cleanupFailure) { throw new global::System.AggregateException(initializationFailure, cleanupFailure); }");
            }
            else
            {
                source.AppendLine("            catch").AppendLine("            {").AppendLine("                Dispose();");
            }
            source.AppendLine("                throw;").AppendLine("            }").AppendLine("        }").AppendLine()
                .AppendLine("        public void Reset()").AppendLine("        {");
            foreach (var field in fields.Values)
            {
                source.AppendLine($"            this.{field.Name}.Reset();");
            }
            source.AppendLine("        }").AppendLine().AppendLine("        public void Dispose()").AppendLine("        {");
            if (profile.CleanupPolicy == RuntimeReplayOwnedStorageCleanupPolicy.AggregateFailures)
            {
                source.AppendLine("            global::System.Exception failure = null;");
            }
            foreach (var field in fields.Values)
            {
                if (profile.CleanupPolicy == RuntimeReplayOwnedStorageCleanupPolicy.AggregateFailures)
                {
                    source.AppendLine($"            try {{ this.{field.Name}?.Dispose(); }}")
                        .AppendLine("            catch (global::System.Exception exception) { failure = failure == null ? exception : new global::System.AggregateException(failure, exception); }");
                }
                else
                {
                    source.AppendLine($"            this.{field.Name}?.Dispose();");
                }
            }
            if (profile.CleanupPolicy == RuntimeReplayOwnedStorageCleanupPolicy.AggregateFailures)
            {
                source.AppendLine("            if (failure != null) { throw failure; }");
            }
            return source.AppendLine("        }").AppendLine("    }").AppendLine("}").ToString();
        }

        private static bool HasVoidMethod(Type type, string name, bool declared = false)
        {
            var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            return method != null && !method.IsGenericMethod && method.ReturnType == typeof(void) && (!declared || method.DeclaringType == type);
        }

        private static bool IsNativeRows(Type type)
        {
            for (; type != null; type = type.BaseType)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RuntimeReplayNativeRows<>))
                {
                    return true;
                }
            }
            return false;
        }

        private static string TypeName(Type type)
        {
            if (type.IsGenericType)
            {
                var name = type.GetGenericTypeDefinition().FullName;
                if (type.IsNested)
                {
                    throw new InvalidOperationException($"Nested generic storage type '{type}' is unsupported.");
                }
                return "global::" + name.Substring(0, name.IndexOf('`')) + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
            }
            return "global::" + type.FullName.Replace('+', '.');
        }
    }
}
