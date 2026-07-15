#if MIRROR
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DingoGameObjectsCMS.Mirror
{
    public readonly struct NetworkRoleBootstrapConfig
    {
        public readonly RuntimeNetRole Role;
        public readonly string Address;
        public readonly ushort? Port;

        public NetworkRoleBootstrapConfig(RuntimeNetRole role, string address, ushort? port)
        {
            Role = role;
            Address = address;
            Port = port;
        }
    }

    public static class NetworkRoleBootstrapParser
    {
        public const string NET_ROLE_ARGUMENT = "-netRole";
        public const string ADDRESS_ARGUMENT = "-address";
        public const string PORT_ARGUMENT = "-port";

        public static NetworkRoleBootstrapConfig Parse(string[] arguments, IReadOnlyList<string> playerTags = null)
        {
            arguments ??= Array.Empty<string>();

            var role = RuntimeNetRole.Offline;
            var hasCommandLineRole = false;
            string address = null;
            ushort? port = null;

            for (var i = 0; i < arguments.Length; i++)
            {
                var argument = arguments[i];
                if (string.Equals(argument, NET_ROLE_ARGUMENT, StringComparison.OrdinalIgnoreCase))
                {
                    var value = ReadRequiredValue(arguments, ref i, NET_ROLE_ARGUMENT);
                    if (!TryParseRole(value, out role))
                        throw new ArgumentException($"Unsupported network role '{value}'. Expected offline, server, host, or client.", nameof(arguments));

                    hasCommandLineRole = true;
                    continue;
                }

                if (string.Equals(argument, ADDRESS_ARGUMENT, StringComparison.OrdinalIgnoreCase))
                {
                    address = ReadRequiredValue(arguments, ref i, ADDRESS_ARGUMENT).Trim();
                    continue;
                }

                if (string.Equals(argument, PORT_ARGUMENT, StringComparison.OrdinalIgnoreCase))
                {
                    var value = ReadRequiredValue(arguments, ref i, PORT_ARGUMENT);
                    if (!ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort) || parsedPort == 0)
                        throw new ArgumentException($"Invalid network port '{value}'. Expected an integer from 1 to {ushort.MaxValue}.", nameof(arguments));

                    port = parsedPort;
                }
            }

            if (!hasCommandLineRole)
                role = ParseTaggedRole(playerTags);

            return new NetworkRoleBootstrapConfig(role, address, port);
        }

        private static RuntimeNetRole ParseTaggedRole(IReadOnlyList<string> playerTags)
        {
            var role = RuntimeNetRole.Offline;
            var hasRole = false;
            if (playerTags == null)
                return role;

            for (var i = 0; i < playerTags.Count; i++)
            {
                if (!TryParseRole(playerTags[i], out var taggedRole))
                    continue;
                if (hasRole && role != taggedRole)
                    throw new ArgumentException($"Player tags contain conflicting network roles '{role}' and '{taggedRole}'.", nameof(playerTags));

                role = taggedRole;
                hasRole = true;
            }

            return role;
        }

        private static bool TryParseRole(string value, out RuntimeNetRole role)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "offline":
                    role = RuntimeNetRole.Offline;
                    return true;
                case "server":
                    role = RuntimeNetRole.Server;
                    return true;
                case "host":
                    role = RuntimeNetRole.Host;
                    return true;
                case "client":
                    role = RuntimeNetRole.Client;
                    return true;
                default:
                    role = RuntimeNetRole.Offline;
                    return false;
            }
        }

        private static string ReadRequiredValue(string[] arguments, ref int argumentIndex, string argumentName)
        {
            var valueIndex = argumentIndex + 1;
            if (valueIndex >= arguments.Length || string.IsNullOrWhiteSpace(arguments[valueIndex]) || arguments[valueIndex].StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException($"Command-line argument '{argumentName}' requires a value.", nameof(arguments));

            argumentIndex = valueIndex;
            return arguments[valueIndex];
        }
    }

    public static class NetworkRoleBootstrapTagSource
    {
        public static Func<string[]> Reader;

        public static string[] ReadCurrent()
        {
            return Reader == null ? Array.Empty<string>() : Reader();
        }
    }
}
#endif
