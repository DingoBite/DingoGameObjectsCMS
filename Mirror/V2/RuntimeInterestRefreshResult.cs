using System;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public enum RuntimeInterestRefreshStatus : byte
    {
        Enqueued = 1,
        NoChange = 2,
        NotReady = 3,
        InvalidStore = 4,
        InvalidProjection = 5,
        NeedsBaseline = 6,
    }

    public readonly struct RuntimeInterestRefreshResult
    {
        public readonly RuntimeInterestRefreshStatus Status;
        public readonly ulong DeliverySequence;
        public readonly string Detail;

        public bool IsAccepted => Status == RuntimeInterestRefreshStatus.Enqueued
                                  || Status == RuntimeInterestRefreshStatus.NoChange
                                  || Status == RuntimeInterestRefreshStatus.NotReady
                                  || Status == RuntimeInterestRefreshStatus.NeedsBaseline;

        public RuntimeInterestRefreshResult(
            RuntimeInterestRefreshStatus status,
            ulong deliverySequence = 0,
            string detail = null)
        {
            if (status == 0)
                throw new ArgumentOutOfRangeException(nameof(status));
            if (status == RuntimeInterestRefreshStatus.Enqueued && deliverySequence == 0)
                throw new ArgumentOutOfRangeException(nameof(deliverySequence));

            Status = status;
            DeliverySequence = deliverySequence;
            Detail = detail ?? string.Empty;
        }
    }
}
