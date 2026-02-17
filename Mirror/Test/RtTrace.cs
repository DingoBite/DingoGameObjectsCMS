namespace DingoGameObjectsCMS.Mirror.Test
{
    public static class RtTrace
    {
        public const bool Enabled = true;

        public static void Log(string side, string ev, string store, params object[] kv)
        {
            if (!Enabled)
                return;
            UnityEngine.Debug.Log($"{side} {ev} store={store} " + string.Join(" ", kv));
        }
    }
}