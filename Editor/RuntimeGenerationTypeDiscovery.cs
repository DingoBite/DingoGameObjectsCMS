using System;
using System.Linq;
using System.Reflection;

namespace DingoGameObjectsCMS.Editor
{
    public static class RuntimeGenerationTypeDiscovery
    {
        public static Type[] GetTypes(Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                var failures = string.Join("; ", (exception.LoaderExceptions ?? Array.Empty<Exception>())
                    .Where(failure => failure != null)
                    .Select(failure => failure.Message));
                throw new InvalidOperationException(
                    $"Runtime generation cannot discover all types in assembly '{assembly.FullName}'. Loader failures: {failures}", exception);
            }
        }
    }
}
