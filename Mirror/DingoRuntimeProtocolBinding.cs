#if MIRROR
using System;

namespace DingoGameObjectsCMS.Mirror
{
    public class DingoRuntimeProtocolBinding : IDisposable
    {
        private DingoNetworkManager _manager;

        public bool IsDisposed => _manager == null;

        public DingoRuntimeProtocolBinding(DingoNetworkManager manager)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        }

        public void Dispose()
        {
            var manager = _manager;
            if (manager == null)
            {
                return;
            }
            _manager = null;
            manager.UnbindRuntimeProtocol(this);
        }
    }
}
#endif
