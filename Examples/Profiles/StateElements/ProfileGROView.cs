using Bind;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.View;
using DingoUnityExtensions.Extensions;
using DingoUnityExtensions.UnityViewProviders.Core;
using DingoUnityExtensions.UnityViewProviders.Text;
using DingoGameObjectsCMS.Examples.Profiles.Components;
using DingoGameObjectsCMS.Examples.Profiles.Models;
using UnityEngine;

namespace DingoGameObjectsCMS.Examples.Profiles.StateElements
{
    public class ProfileGROView : GameRuntimeObjectView
    {
        [SerializeField] private TextFieldProvider _profileName;
        [SerializeField] private EventContainer _select;
        [SerializeField] private EventContainer _delete;
        [SerializeField] private ValueContainer<bool> _active;

        private ProfilesModel Model => new ProfilesModel(); // TODO Take your model

        protected override void OnGRODestroy(GameRuntimeObject value)
        {
            _active.UpdateValueWithoutNotify(false);
        }

        protected override void UpdateGRO(GameRuntimeObject previousValue, GameRuntimeObject value)
        {
            var profile = value != null ? value.TakeRO<ProfileDescriptor_GRC>() : null;
            _profileName.UpdateValueWithoutNotify(profile?.Name ?? string.Empty);

            RefreshActiveState(Model.ActiveProfile.V);
        }

        private void RefreshActiveState(Hash128 activeId)
        {
            _active.UpdateValueWithoutNotify(Value != null && Value.GUID == activeId);
        }

        private void ProfileNameChange(string newName)
        {
            if (Value == null)
                return;

            var model = Model;
            if (!model.ValidateProfileName(newName, out var normalizedName))
            {
                _profileName.UpdateValueWithoutNotify(string.Empty);
                return;
            }

            if (!model.RenameProfile(Value.GUID, normalizedName))
                return;

            _profileName.UpdateValueWithoutNotify(normalizedName);
        }

        private void SelectProfile()
        {
            if (Value == null)
                return;

            Model.SetProfileActive(Value.GUID);
        }

        private void DeleteProfile()
        {
            if (Value == null)
                return;

            Model.RemoveProfile(Value.GUID);
        }

        protected override void SubscribeOnly()
        {
            _profileName.OnSubmit += ProfileNameChange;
            _select.SafeSubscribe(SelectProfile);
            _delete.SafeSubscribe(DeleteProfile);

            Model.ActiveProfile.SafeSubscribeAndSet(RefreshActiveState);
            base.SubscribeOnly();
        }

        protected override void UnsubscribeOnly()
        {
            _profileName.OnSubmit -= ProfileNameChange;
            _select.UnSubscribe(SelectProfile);
            _delete.UnSubscribe(DeleteProfile);

            Model.ActiveProfile.UnSubscribe(RefreshActiveState);
            base.UnsubscribeOnly();
        }
    }
}
