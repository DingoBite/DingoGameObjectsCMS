using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;

namespace DingoGameObjectsCMS.RuntimeObjects.Commands
{
    public delegate byte[] RuntimeReplayCommandEncoder(
        GameRuntimeCommand command,
        RuntimePersistentPatchCodecContext context);

    public delegate GameRuntimeCommand RuntimeReplayCommandDecoder(
        byte[] payload,
        RuntimePersistentPatchCodecContext context);

    public delegate byte[] RuntimeReplayPayloadMigration(
        byte[] payload,
        RuntimePersistentPatchCodecContext context);

    class RuntimeReplayPayloadMigrationRegistration
    {
        public readonly ushort FromCodecVersion;
        public readonly string StableKey;
        public readonly RuntimeReplayPayloadMigration Migration;

        public RuntimeReplayPayloadMigrationRegistration(
            ushort fromCodecVersion,
            string stableKey,
            RuntimeReplayPayloadMigration migration)
        {
            FromCodecVersion = fromCodecVersion;
            StableKey = stableKey;
            Migration = migration;
        }
    }

    class RuntimeReplayCommandRegistration
    {
        private readonly Dictionary<ushort, RuntimeReplayPayloadMigrationRegistration> _migrations = new();
        private readonly RuntimeReplayCommandEncoder _encoder;
        private readonly RuntimeReplayCommandDecoder _decoder;
        private readonly Func<GameRuntimeCommand, bool> _matches;

        public readonly uint TypeId;
        public readonly string StableKey;
        public readonly ushort CurrentCodecVersion;
        public readonly Type CommandComponentType;
        public readonly int MaxPayloadBytes;

        public RuntimeReplayCommandRegistration(
            uint typeId,
            string stableKey,
            ushort currentCodecVersion,
            Type commandComponentType,
            int maxPayloadBytes,
            RuntimeReplayCommandEncoder encoder,
            RuntimeReplayCommandDecoder decoder,
            Func<GameRuntimeCommand, bool> matches)
        {
            TypeId = typeId;
            StableKey = stableKey;
            CurrentCodecVersion = currentCodecVersion;
            CommandComponentType = commandComponentType;
            MaxPayloadBytes = maxPayloadBytes;
            _encoder = encoder;
            _decoder = decoder;
            _matches = matches;
        }

        public bool Matches(GameRuntimeCommand command)
        {
            return _matches(command);
        }

        public byte[] Encode(GameRuntimeCommand command, RuntimePersistentPatchCodecContext context)
        {
            return _encoder(command, context);
        }

        public GameRuntimeCommand Decode(byte[] payload, RuntimePersistentPatchCodecContext context)
        {
            return _decoder(payload, context);
        }

        public void AddMigration(
            ushort fromCodecVersion,
            string stableKey,
            RuntimeReplayPayloadMigration migration)
        {
            if (!_migrations.TryAdd(
                    fromCodecVersion,
                    new RuntimeReplayPayloadMigrationRegistration(
                        fromCodecVersion,
                        stableKey,
                        migration)))
            {
                throw new InvalidOperationException(
                    $"Replay command '{StableKey}' already has a payload migration from codec version {fromCodecVersion}.");
            }
        }

        public bool TryTakeMigration(
            ushort fromCodecVersion,
            out RuntimeReplayPayloadMigrationRegistration migration)
        {
            return _migrations.TryGetValue(fromCodecVersion, out migration);
        }

        public void AppendCatalog(StringBuilder builder)
        {
            builder.Append("command|")
                .Append(TypeId).Append('|')
                .Append(StableKey).Append('|')
                .Append(CurrentCodecVersion).Append('|')
                .Append(MaxPayloadBytes).Append('\n');

            var migrations = new List<RuntimeReplayPayloadMigrationRegistration>(_migrations.Values);
            migrations.Sort(
                (first, second) => first.FromCodecVersion.CompareTo(second.FromCodecVersion));
            foreach (var migration in migrations)
            {
                builder.Append("migration|")
                    .Append(TypeId).Append('|')
                    .Append(migration.FromCodecVersion).Append('|')
                    .Append(migration.FromCodecVersion + 1).Append('|')
                    .Append(migration.StableKey).Append('\n');
            }
        }
    }

    public class RuntimeReplayCommandRegistry
    {
        public const int DEFAULT_MAX_PAYLOAD_BYTES = 64 * 1024;
        private const int CATALOG_FORMAT_VERSION = 1;

        private readonly Dictionary<uint, RuntimeReplayCommandRegistration> _registrationsById = new();
        private readonly Dictionary<Type, RuntimeReplayCommandRegistration> _registrationsByComponentType = new();
        private readonly HashSet<string> _stableKeys = new(StringComparer.Ordinal);
        private RuntimeReplayCommandRegistration[] _sealedRegistrations = Array.Empty<RuntimeReplayCommandRegistration>();

        public bool IsSealed { get; private set; }
        public string CatalogHash { get; private set; }
        public int Count => _registrationsById.Count;

        public void Register<TCommandComponent>(
            uint typeId,
            string stableKey,
            ushort currentCodecVersion,
            RuntimeReplayCommandEncoder encoder,
            RuntimeReplayCommandDecoder decoder,
            int maxPayloadBytes = DEFAULT_MAX_PAYLOAD_BYTES)
            where TCommandComponent : GameRuntimeComponent, ICommandLogic
        {
            RequireMutable();
            if (typeId == 0)
                throw new ArgumentOutOfRangeException(nameof(typeId));
            ValidateStableKey(stableKey, nameof(stableKey));
            if (currentCodecVersion == 0)
                throw new ArgumentOutOfRangeException(nameof(currentCodecVersion));
            if (encoder == null)
                throw new ArgumentNullException(nameof(encoder));
            if (decoder == null)
                throw new ArgumentNullException(nameof(decoder));
            if (maxPayloadBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
            if (_registrationsById.ContainsKey(typeId))
            {
                throw new InvalidOperationException(
                    $"Replay command type id '{typeId}' is already registered.");
            }
            if (_stableKeys.Contains(stableKey))
            {
                throw new InvalidOperationException(
                    $"Replay command stable key '{stableKey}' is already registered.");
            }

            var componentType = typeof(TCommandComponent);
            if (_registrationsByComponentType.ContainsKey(componentType))
            {
                throw new InvalidOperationException(
                    $"Replay command component '{componentType.FullName}' is already registered.");
            }

            var registration = new RuntimeReplayCommandRegistration(
                typeId,
                stableKey,
                currentCodecVersion,
                componentType,
                maxPayloadBytes,
                encoder,
                decoder,
                command => command.TryGet<TCommandComponent>(out _));
            _registrationsById.Add(typeId, registration);
            _registrationsByComponentType.Add(componentType, registration);
            _stableKeys.Add(stableKey);
        }

        public void RegisterPayloadMigration(
            uint typeId,
            ushort fromCodecVersion,
            string stableKey,
            RuntimeReplayPayloadMigration migration)
        {
            RequireMutable();
            if (!_registrationsById.TryGetValue(typeId, out var registration))
            {
                throw new KeyNotFoundException(
                    $"Replay command type id '{typeId}' is not registered.");
            }
            if (fromCodecVersion == 0
                || fromCodecVersion >= registration.CurrentCodecVersion)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fromCodecVersion),
                    fromCodecVersion,
                    $"Migration source must be between 1 and {registration.CurrentCodecVersion - 1}.");
            }

            ValidateStableKey(stableKey, nameof(stableKey));
            registration.AddMigration(
                fromCodecVersion,
                stableKey,
                migration ?? throw new ArgumentNullException(nameof(migration)));
        }

        public string Seal()
        {
            if (IsSealed)
                return CatalogHash;

            _sealedRegistrations = new RuntimeReplayCommandRegistration[_registrationsById.Count];
            _registrationsById.Values.CopyTo(_sealedRegistrations, 0);
            Array.Sort(
                _sealedRegistrations,
                (first, second) => first.TypeId.CompareTo(second.TypeId));

            var builder = new StringBuilder();
            builder.Append("runtime-replay-command-catalog|")
                .Append(CATALOG_FORMAT_VERSION)
                .Append('\n');
            foreach (var registration in _sealedRegistrations)
            {
                registration.AppendCatalog(builder);
            }

            using var sha = SHA256.Create();
            CatalogHash = ToLowerHex(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
            IsSealed = true;
            return CatalogHash;
        }

        public bool TryEncode(
            GameRuntimeCommand command,
            RuntimePersistentPatchCodecContext context,
            out RuntimeEncodedCommand encoded)
        {
            RequireSealed();
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            var registration = FindRegistration(command);
            if (registration == null)
            {
                encoded = default;
                return false;
            }

            var payload = registration.Encode(command, context) ?? Array.Empty<byte>();
            RequirePayloadLimit(registration, payload);
            encoded = new RuntimeEncodedCommand(
                registration.TypeId,
                registration.CurrentCodecVersion,
                payload);
            return true;
        }

        public bool TryEncode(
            GameRuntimeCommand command,
            out RuntimeEncodedCommand encoded)
        {
            return TryEncode(command, null, out encoded);
        }

        public RuntimeEncodedCommand Encode(
            GameRuntimeCommand command,
            RuntimePersistentPatchCodecContext context = null)
        {
            if (!TryEncode(command, context, out var encoded))
            {
                throw new NotSupportedException(
                    $"Command '{command?.GetType().FullName}' has no registered replay command component.");
            }

            return encoded;
        }

        public GameRuntimeCommand Decode(
            in RuntimeEncodedCommand encoded,
            RuntimePersistentPatchCodecContext context = null)
        {
            RequireSealed();
            if (!encoded.IsValid)
                throw new ArgumentException("Encoded replay command is invalid.", nameof(encoded));
            if (!_registrationsById.TryGetValue(encoded.TypeId, out var registration))
            {
                throw new NotSupportedException(
                    $"Replay command type id '{encoded.TypeId}' is not registered.");
            }

            var payload = RuntimeEncodedCommand.CopyPayload(encoded.Payload);
            RequirePayloadLimit(registration, payload);
            if (encoded.CodecVersion > registration.CurrentCodecVersion)
            {
                throw new NotSupportedException(
                    $"Replay command '{registration.StableKey}' codec version {encoded.CodecVersion} is newer than supported version {registration.CurrentCodecVersion}.");
            }

            var codecVersion = encoded.CodecVersion;
            while (codecVersion < registration.CurrentCodecVersion)
            {
                if (!registration.TryTakeMigration(codecVersion, out var migration))
                {
                    throw new NotSupportedException(
                        $"Replay command '{registration.StableKey}' has no payload migration from codec version {codecVersion}.");
                }

                payload = migration.Migration(
                              RuntimeEncodedCommand.CopyPayload(payload),
                              context)
                          ?? Array.Empty<byte>();
                RequirePayloadLimit(registration, payload);
                codecVersion++;
            }

            var command = registration.Decode(
                RuntimeEncodedCommand.CopyPayload(payload),
                context);
            if (command == null)
            {
                throw new InvalidOperationException(
                    $"Replay command decoder '{registration.StableKey}' returned null.");
            }

            var decodedRegistration = FindRegistration(command);
            if (!ReferenceEquals(decodedRegistration, registration))
            {
                throw new InvalidOperationException(
                    $"Replay command decoder '{registration.StableKey}' returned a command with a different or ambiguous logical command component.");
            }

            return command;
        }

        public void PrevalidateEnvelope(
            in RuntimeEncodedCommand encoded)
        {
            RequireSealed();
            if (!encoded.IsValid)
            {
                throw new ArgumentException(
                    "Encoded replay command is invalid.",
                    nameof(encoded));
            }
            if (!_registrationsById.TryGetValue(
                    encoded.TypeId,
                    out var registration))
            {
                throw new NotSupportedException(
                    $"Replay command type id '{encoded.TypeId}' is not registered.");
            }

            RequirePayloadLimit(registration, encoded.Payload);
            if (encoded.CodecVersion
                > registration.CurrentCodecVersion)
            {
                throw new NotSupportedException(
                    $"Replay command '{registration.StableKey}' codec version "
                    + $"{encoded.CodecVersion} is newer than supported version "
                    + $"{registration.CurrentCodecVersion}.");
            }

            var codecVersion = encoded.CodecVersion;
            while (codecVersion < registration.CurrentCodecVersion)
            {
                if (!registration.TryTakeMigration(
                        codecVersion,
                        out _))
                {
                    throw new NotSupportedException(
                        $"Replay command '{registration.StableKey}' has no payload "
                        + $"migration from codec version {codecVersion}.");
                }
                codecVersion++;
            }
        }

        private RuntimeReplayCommandRegistration FindRegistration(GameRuntimeCommand command)
        {
            RuntimeReplayCommandRegistration match = null;
            foreach (var registration in _sealedRegistrations)
            {
                if (!registration.Matches(command))
                    continue;
                if (match != null)
                {
                    throw new NotSupportedException(
                        $"Command contains multiple registered replay command components: '{match.CommandComponentType.FullName}' and '{registration.CommandComponentType.FullName}'.");
                }

                match = registration;
            }

            return match;
        }

        private static void RequirePayloadLimit(
            RuntimeReplayCommandRegistration registration,
            byte[] payload)
        {
            if (payload.Length > registration.MaxPayloadBytes)
            {
                throw new InvalidOperationException(
                    $"Replay command '{registration.StableKey}' payload is {payload.Length} bytes; maximum is {registration.MaxPayloadBytes}.");
            }
        }

        private void RequireMutable()
        {
            if (IsSealed)
                throw new InvalidOperationException("Replay command registry is sealed.");
        }

        private void RequireSealed()
        {
            if (!IsSealed)
                throw new InvalidOperationException("Replay command registry must be sealed before encoding or decoding.");
        }

        private static void ValidateStableKey(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value)
                || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
                || value.IndexOfAny(new[] { '|', '\r', '\n' }) >= 0)
            {
                throw new ArgumentException(
                    "Stable replay protocol keys must be non-empty, trimmed, and cannot contain '|', CR, or LF.",
                    parameterName);
            }
        }

        private static string ToLowerHex(byte[] value)
        {
            var builder = new StringBuilder(value.Length * 2);
            for (var i = 0; i < value.Length; i++)
            {
                builder.Append(value[i].ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
