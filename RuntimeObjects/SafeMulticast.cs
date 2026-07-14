using System;
using System.Collections.Generic;
using UnityEngine;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    public class SafeMulticast<T>
    {
        private readonly List<Action<T>> _handlers = new();
        private readonly List<Action<T>> _wrappers = new();
        private Action<T> _combined;

        public void Subscribe(Action<T> handler)
        {
            if (handler == null)
                return;

            var invocationList = handler.GetInvocationList();
            foreach (var invocation in invocationList)
            {
                var leaf = (Action<T>)invocation;
                var wrapper = CreateWrapper(leaf);
                _handlers.Add(leaf);
                _wrappers.Add(wrapper);
                _combined += wrapper;
            }
        }

        public void Unsubscribe(Action<T> handler)
        {
            if (handler == null)
                return;

            var removalList = handler.GetInvocationList();
            var startIndex = FindLastMatchingSequence(removalList);
            if (startIndex < 0)
                return;

            _handlers.RemoveRange(startIndex, removalList.Length);
            _wrappers.RemoveRange(startIndex, removalList.Length);
            RebuildCombined();
        }

        public void Invoke(T value) => _combined?.Invoke(value);

        public void Clear()
        {
            _combined = null;
            _handlers.Clear();
            _wrappers.Clear();
        }

        private int FindLastMatchingSequence(Delegate[] removalList)
        {
            for (var start = _handlers.Count - removalList.Length; start >= 0; start--)
            {
                var matches = true;
                for (var offset = 0; offset < removalList.Length; offset++)
                {
                    if (_handlers[start + offset] == (Action<T>)removalList[offset])
                        continue;

                    matches = false;
                    break;
                }

                if (matches)
                    return start;
            }

            return -1;
        }

        private void RebuildCombined()
        {
            _combined = null;
            foreach (var wrapper in _wrappers)
            {
                _combined += wrapper;
            }
        }

        private static Action<T> CreateWrapper(Action<T> handler)
        {
            return value =>
            {
                try
                {
                    handler(value);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            };
        }
    }

    public class SafeMulticast<T1, T2>
    {
        private readonly List<Action<T1, T2>> _handlers = new();
        private readonly List<Action<T1, T2>> _wrappers = new();
        private Action<T1, T2> _combined;

        public void Subscribe(Action<T1, T2> handler)
        {
            if (handler == null)
                return;

            var invocationList = handler.GetInvocationList();
            foreach (var invocation in invocationList)
            {
                var leaf = (Action<T1, T2>)invocation;
                var wrapper = CreateWrapper(leaf);
                _handlers.Add(leaf);
                _wrappers.Add(wrapper);
                _combined += wrapper;
            }
        }

        public void Unsubscribe(Action<T1, T2> handler)
        {
            if (handler == null)
                return;

            var removalList = handler.GetInvocationList();
            var startIndex = FindLastMatchingSequence(removalList);
            if (startIndex < 0)
                return;

            _handlers.RemoveRange(startIndex, removalList.Length);
            _wrappers.RemoveRange(startIndex, removalList.Length);
            RebuildCombined();
        }

        public void Invoke(T1 first, T2 second) => _combined?.Invoke(first, second);

        public void Clear()
        {
            _combined = null;
            _handlers.Clear();
            _wrappers.Clear();
        }

        private int FindLastMatchingSequence(Delegate[] removalList)
        {
            for (var start = _handlers.Count - removalList.Length; start >= 0; start--)
            {
                var matches = true;
                for (var offset = 0; offset < removalList.Length; offset++)
                {
                    if (_handlers[start + offset] == (Action<T1, T2>)removalList[offset])
                        continue;

                    matches = false;
                    break;
                }

                if (matches)
                    return start;
            }

            return -1;
        }

        private void RebuildCombined()
        {
            _combined = null;
            foreach (var wrapper in _wrappers)
            {
                _combined += wrapper;
            }
        }

        private static Action<T1, T2> CreateWrapper(Action<T1, T2> handler)
        {
            return (first, second) =>
            {
                try
                {
                    handler(first, second);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            };
        }
    }
}
