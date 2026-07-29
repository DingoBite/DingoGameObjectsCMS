using System;
using System.Globalization;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    public static class GameAssetVersionUtils
    {
        public static bool TryParse(
            string version,
            out int major,
            out int minor,
            out int patch)
        {
            major = 0;
            minor = 0;
            patch = 0;
            if (string.IsNullOrWhiteSpace(version)
                || !string.Equals(version, version.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            var parts = version.Split('.');
            if (parts.Length != 3
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch))
            {
                return false;
            }

            return string.Equals(
                version,
                $"{major}.{minor}.{patch}",
                StringComparison.Ordinal);
        }

        public static void RequireCanonical(string version, string parameterName)
        {
            if (!TryParse(version, out _, out _, out _))
            {
                throw new ArgumentException(
                    $"GameAsset version '{version}' must use canonical numeric major.minor.patch format.",
                    parameterName);
            }
        }

        public static int Compare(string left, string right)
        {
            RequireCanonical(left, nameof(left));
            RequireCanonical(right, nameof(right));
            TryParse(left, out var leftMajor, out var leftMinor, out var leftPatch);
            TryParse(right, out var rightMajor, out var rightMinor, out var rightPatch);

            var result = leftMajor.CompareTo(rightMajor);
            if (result != 0)
                return result;
            result = leftMinor.CompareTo(rightMinor);
            return result != 0 ? result : leftPatch.CompareTo(rightPatch);
        }

        public static string IncrementPatch(string version)
        {
            RequireCanonical(version, nameof(version));
            TryParse(version, out var major, out var minor, out var patch);
            if (patch == int.MaxValue)
                throw new OverflowException($"GameAsset version '{version}' cannot increment its patch part.");
            return $"{major}.{minor}.{patch + 1}";
        }
    }
}
