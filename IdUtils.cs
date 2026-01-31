using System;
using UnityEngine;

namespace DingoGameObjectsCMS
{
    public static class IdUtils
    {
        public static Hash128 NewHash128FromGuid() => Hash128.Parse(Guid.NewGuid().ToString("N"));
    }
}