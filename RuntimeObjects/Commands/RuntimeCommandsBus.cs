using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoProjectAppStructure.Core.Model;
using DingoUnityExtensions;

namespace DingoGameObjectsCMS.RuntimeObjects.Commands
{
    public sealed class RuntimeCommandsBus : AppModelBase
    {
        public const int UPDATE_ORDER = RuntimeStore.UPDATE_ORDER - 1;

        private readonly List<GameRuntimeCommand> _queue = new(capacity: 64);
        private readonly List<GameRuntimeCommand> _processing = new(capacity: 64);

        private bool _scheduled;
        private bool _flushInProgress;
        private bool _rescheduleRequested;

        public int QueuedCount => _queue.Count;

        public event Action<GameRuntimeCommand> BeforeExecute;
        public event Action<GameRuntimeCommand> AfterExecute;
        public event Action<GameRuntimeCommand, Exception> ExecuteFailed;

        public void Enqueue(GameRuntimeCommand command)
        {
            if (command == null)
                return;

            _queue.Add(command);
            ScheduleFlush();
        }

        public void EnqueueRange(IEnumerable<GameRuntimeCommand> commands)
        {
            if (commands == null)
                return;

            foreach (var c in commands)
            {
                if (c != null)
                    _queue.Add(c);
            }

            ScheduleFlush();
        }

        public void Clear()
        {
            _queue.Clear();
            _processing.Clear();
        }

        private void ScheduleFlush()
        {
            if (_scheduled)
            {
                if (_flushInProgress)
                    _rescheduleRequested = true;
                return;
            }

            _scheduled = true;
            CoroutineParent.AddLateUpdater(this, Flush, UPDATE_ORDER);
        }

        private void Flush()
        {
            _flushInProgress = true;
            try
            {
                if (_queue.Count == 0)
                    return;

                _processing.Clear();
                _processing.AddRange(_queue);
                _queue.Clear();

                foreach (var cmd in _processing)
                {
                    if (cmd == null)
                        continue;

                    BeforeExecute?.Invoke(cmd);

                    try
                    {
                        ExecuteCommand(cmd);
                    }
                    catch (Exception e)
                    {
                        ExecuteFailed?.Invoke(cmd, e);
                    }

                    AfterExecute?.Invoke(cmd);
                }

                _processing.Clear();
            }
            finally
            {
                _flushInProgress = false;
                var needReschedule = _rescheduleRequested || _queue.Count > 0;
                _rescheduleRequested = false;

                _scheduled = false;
                CoroutineParent.RemoveLateUpdater(this);

                if (needReschedule)
                    ScheduleFlush();
            }
        }

        private static void ExecuteCommand(GameRuntimeCommand command)
        {
            var components = command.Components;
            if (components == null || components.Count == 0)
                return;

            foreach (var c in components)
            {
                if (c is ICommandLogic logic)
                    logic.Execute(command);
            }
        }
    }
}