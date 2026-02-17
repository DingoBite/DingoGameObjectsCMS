using Mirror;
using Unity.Multiplayer.PlayMode;
using UnityEngine;

namespace DingoGameObjectsCMS.Mirror.Test
{
    public sealed class MirrorAutoBoot : MonoBehaviour
    {
        [SerializeField] private NetworkManager _nm;
        [SerializeField] private string _addr = "localhost";

        private void Awake()
        {
            if (_nm == null)
                _nm = NetworkManager.singleton;
        }

        private void Start()
        {
            if (_nm == null)
                return;

            if (CurrentPlayer.IsMainEditor)
            {
                _nm.StartHost();
            }
            else
            {
                _nm.networkAddress = _addr;
                _nm.StartClient();
            }
        }
    }
}