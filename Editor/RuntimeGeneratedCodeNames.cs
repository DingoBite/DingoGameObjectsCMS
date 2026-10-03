using System;
using Microsoft.CSharp;

namespace DingoGameObjectsCMS.Editor
{
    public static class RuntimeGeneratedCodeNames
    {
        public static string RequireNamespace(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Generated namespace is required.", parameterName);
            }

            var result = value.Trim();
            var segments = result.Split('.');
            for (var i = 0; i < segments.Length; i++)
            {
                RequireIdentifier(segments[i], parameterName);
            }
            return result;
        }

        public static string RequireIdentifier(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Generated C# identifier is required.", parameterName);
            }

            var result = value.Trim();
            using var provider = new CSharpCodeProvider();
            if (!provider.IsValidIdentifier(result))
            {
                throw new ArgumentException($"Generated C# identifier '{result}' is invalid.", parameterName);
            }
            return result;
        }
    }
}
