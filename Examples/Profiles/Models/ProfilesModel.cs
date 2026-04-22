using System.Linq;
using Bind;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;
using DingoProjectAppStructure.Core.Model;
using DingoGameObjectsCMS.Examples.Profiles.Components;
using UnityEngine;

namespace DingoGameObjectsCMS.Examples.Profiles.Models
{
    public sealed class ProfilesModel : AppModelBase
    {
        private readonly IReadonlyBind<RuntimeStore> _store;
        private readonly Bind<Hash128> _activeProfile = new(true);

        public IReadonlyBind<Hash128> ActiveProfile => _activeProfile;
        public IReadonlyBind<RuntimeStore> Store => _store;

        public ProfilesModel()
        {
            _store = RS.Bind("profiles");
            _store.SafeSubscribeAndSet(OnStoreChanged);
        }

        private void OnStoreChanged(RuntimeStore runtimeStore)
        {
            if (runtimeStore == null)
            {
                _activeProfile.V = default;
                return;
            }

            var root = runtimeStore.TakeRootRW();
            if (!root.Has<ProfilesState_GRC>())
                root.AddOrReplace(new ProfilesState_GRC());
            if (!root.HasEntityProjection)
                root.CreateEntity();
            SetupActiveProfile();
        }

        public bool SetupActiveProfile()
        {
            if (_store.V == null)
            {
                _activeProfile.V = default;
                return true;
            }

            var activeProfile = FindActiveProfile();
            if (activeProfile != null)
            {
                var state = _store.V.TakeRootRW()?.TakeRW<ProfilesState_GRC>();
                if (state != null)
                    state.ActiveProfileId = activeProfile.GUID;
            }

            _activeProfile.V = activeProfile != null ? activeProfile.GUID : default;
            return _activeProfile.V.isValid;
        }

        public GameRuntimeObject CreateProfile(string name)
        {
            if (_store.V == null)
                return null;
            if (name == null)
                name = BuildNextProfileName();
            else if (!ValidateProfileName(name, out name))
                return null;

            var profile = _store.V.Create();
            profile.AddOrReplace(new UserProfile_GRC());
            profile.AddOrReplace(new ProfileDescriptor_GRC
            {
                Name = name
            });
            profile.CreateEntity();

            var state = _store.V.TakeRootRW()?.TakeRW<ProfilesState_GRC>();
            if (state != null && !state.ActiveProfileId.isValid)
            {
                state.ActiveProfileId = profile.GUID;
                _activeProfile.V = profile.GUID;
            }

            return profile;
        }

        public bool ValidateProfileName(string name, out string normalizedName)
        {
            normalizedName = name?.Trim();
            return !string.IsNullOrWhiteSpace(normalizedName);
        }

        private string BuildNextProfileName()
        {
            var usedNames = _store.V.EnumerateGRONoRoot().Select(gro => gro.TakeRO<ProfileDescriptor_GRC>()?.Name).Where(profileName => !string.IsNullOrWhiteSpace(profileName)).ToHashSet(System.StringComparer.OrdinalIgnoreCase);

            var index = 1;
            while (true)
            {
                var candidate = $"Profile {index}";
                if (!usedNames.Contains(candidate))
                    return candidate;
                index++;
            }
        }


        public bool RemoveProfile(Hash128 id)
        {
            if (_store.V == null)
                return false;

            var profile = FindProfileById(id);
            if (profile == null)
                return false;

            var wasActive = _activeProfile.V == profile.GUID;
            if (!_store.V.Remove(profile.InstanceId))
                return false;

            if (!wasActive)
                return true;

            var state = _store.V.TakeRootRW()?.TakeRW<ProfilesState_GRC>();
            if (state != null)
                state.ActiveProfileId = default;

            SetupActiveProfile();
            return true;
        }

        public bool RenameProfile(Hash128 id, string newName)
        {
            if (_store.V == null)
                return false;
            if (!ValidateProfileName(newName, out newName))
                return false;

            var profile = FindProfileById(id);
            if (profile == null)
                return false;

            var descriptor = profile.TakeRW<ProfileDescriptor_GRC>();
            if (descriptor == null)
                return false;

            descriptor.Name = newName;
            return true;
        }


        public void SetProfileActive(Hash128 id)
        {
            if (_store.V == null)
                return;

            var profile = FindProfileById(id);
            if (profile == null)
                return;

            var state = _store.V.TakeRootRW()?.TakeRW<ProfilesState_GRC>();
            if (state != null)
                state.ActiveProfileId = id;
            _activeProfile.V = profile.GUID;
        }

        public GameRuntimeObject FindActiveProfile()
        {
            if (_store.V == null)
                return null;

            var state = _store.V.TakeRootRO()?.TakeRO<ProfilesState_GRC>();
            if (state != null && state.ActiveProfileId.isValid)
            {
                var selected = FindProfileById(state.ActiveProfileId);
                if (selected != null)
                    return selected;
            }

            return _store.V.EnumerateGRONoRoot().FirstOrDefault(IsProfileObject);
        }

        private GameRuntimeObject FindProfileById(Hash128 id)
        {
            var value = _store.V.TakeRO(id);
            return IsProfileObject(value) ? value : null;
        }

        private static bool IsProfileObject(GameRuntimeObject value) => value != null && value.Has<ProfileDescriptor_GRC>();
    }
}