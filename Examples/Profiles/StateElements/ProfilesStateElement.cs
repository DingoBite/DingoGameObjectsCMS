using System.Collections.Generic;
using Bind;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.View;
using DingoProjectAppStructure.Core.AppRootCore;
using DingoUnityExtensions.Extensions;
using DingoUnityExtensions.UnityViewProviders.Core;
using DingoGameObjectsCMS.Examples.Profiles.Models;
using UnityEngine;

namespace DingoGameObjectsCMS.Examples.Profiles.StateElements
{
    public class ProfilesStateElement : AppStateElementBehaviour
    {
        [SerializeField] private GameRuntimeObjectsCollection _profiles;
        [SerializeField] private GameRuntimeObjectView _profilesStateView;
        [SerializeField] private EventContainer _newProfile;

        private RuntimeStore _store;

        private ProfilesModel Model => new ProfilesModel(); // TODO Take your model

        private void OnStoreChanged(RuntimeStore store)
        {
            _store?.Parents.UnSubscribe(UpdateObjects);

            _store = store;

            if (_store != null)
                _store.Parents.SafeSubscribeAndSet(UpdateObjects);
            else
                UpdateObjects(null);
        }

        private void UpdateObjects(IReadOnlyDictionary<long, GameRuntimeObject> dict)
        {
            _profilesStateView.UpdateValueWithoutNotify(dict?.GetValueOrDefault(RuntimeStore.STORE_ROOT_OBJECT_ID));
            _profiles.DefaultUpdateValueWithoutNotify(dict);
        }

        private void CreateProfile()
        {
            Model.CreateProfile(null);
        }

        protected override void SubscribeOnly()
        {
            _newProfile.SafeSubscribe(CreateProfile);
            Model.Store.SafeSubscribeAndSet(OnStoreChanged);
            base.SubscribeOnly();
        }

        protected override void UnsubscribeOnly()
        {
            _newProfile.UnSubscribe(CreateProfile);
            Model.Store.UnSubscribe(OnStoreChanged);
            _store?.Parents.UnSubscribe(UpdateObjects);
            base.UnsubscribeOnly();
        }
    }
}
