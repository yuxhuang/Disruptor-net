using System;
using System.Threading;
using Disruptor.Atomic;
using Disruptor.MemoryLayout;

namespace Disruptor
{
    /// <summary>
    /// Strategy to be used when there are multiple publisher threads claiming sequences.
    /// 
    /// This strategy is reasonably forgiving when the multiple publisher threads are highly contended or working in an
    /// environment where there is insufficient CPUs to handle multiple publisher threads.  It requires 2 CAS operations
    /// for a single publisher, compared to the MultithreadedLowContention strategy which needs only a single CAS and a
    /// lazySet per publication.
    /// </summary>
    [Obsolete("Just ported Mike's algorithm but I've seen it dealocking - wait for a fix on my side or their side")]
    public class MultiThreadedClaimStrategy : IClaimStrategy
    {
        private const int Retries = 1000;
        private readonly int _bufferSize;
        private PaddedAtomicLong _claimSequence = new PaddedAtomicLong(Sequencer.InitialCursorValue);
        private readonly AtomicLongArray _pendingPublication;
        private readonly int _pendingMask;
        private readonly ThreadLocal<MutableLong> _minGatingSequenceThreadLocal = new ThreadLocal<MutableLong>(() => new MutableLong(Sequencer.InitialCursorValue));

        /// <summary>
        /// Construct a new multi-threaded publisher <see cref="IClaimStrategy"/> for a given buffer size.
        /// </summary>
        /// <param name="bufferSize">bufferSize for the underlying data structure.</param>
        /// <param name="pendingBufferSize"></param>
        public MultiThreadedClaimStrategy(int bufferSize, int pendingBufferSize)
        {
            _bufferSize = bufferSize;
            _pendingPublication = new AtomicLongArray(pendingBufferSize);
            _pendingMask = pendingBufferSize - 1;
        }
        
        /// <summary>
        /// Construct a new multi-threaded publisher <see cref="IClaimStrategy"/> for a given buffer size.
        /// </summary>
        /// <param name="bufferSize">bufferSize for the underlying data structure.</param>
        public MultiThreadedClaimStrategy(int bufferSize) : this(bufferSize, 1024)
        {
        }

        /// <summary>
        /// Get the size of the data structure used to buffer events.
        /// </summary>
        public int BufferSize
        {
            get { return _bufferSize; }
        }

        /// <summary>
        /// Get the current claimed sequence.
        /// </summary>
        public long Sequence
        {
            get { return _claimSequence.Value; }
        }

        /// <summary>
        /// Is there available capacity in the buffer for the requested sequence.
        /// </summary>
        /// <param name="availableCapacity">availableCapacity remaining in the buffer.</param>
        /// <param name="dependentSequences">dependentSequences to be checked for range.</param>
        /// <returns>true if the buffer has capacity for the requested sequence.</returns>
        public bool HasAvailableCapacity(int availableCapacity, Sequence[] dependentSequences)
        {
            long wrapPoint = (_claimSequence.Value + availableCapacity) - _bufferSize;
            MutableLong minGatingSequence = _minGatingSequenceThreadLocal.Value;
            if (wrapPoint > minGatingSequence.Value)
            {
                long minSequence = Util.GetMinimumSequence(dependentSequences);
                minGatingSequence.Value = minSequence;

                if (wrapPoint > minSequence)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Claim the next sequence in the <see cref="Sequencer"/>
        /// The caller should be held up until the claimed sequence is available by tracking the dependentSequences.
        /// </summary>
        /// <param name="dependentSequences">dependentSequences to be checked for range.</param>
        /// <returns>the index to be used for the publishing.</returns>
        public long IncrementAndGet(Sequence[] dependentSequences)
        {
            MutableLong minGatingSequence = _minGatingSequenceThreadLocal.Value;
            WaitForCapacity(dependentSequences, minGatingSequence);

            long nextSequence = _claimSequence.IncrementAndGet();
            WaitForFreeSlotAt(nextSequence, dependentSequences, minGatingSequence);

            return nextSequence;
        }

        ///<summary>
        /// Increment sequence by a delta and get the result.
        /// The caller should be held up until the claimed sequence batch is available by tracking the dependentSequences.
        ///</summary>
        ///<param name="delta">delta to increment by.</param>
        /// <param name="dependentSequences">dependentSequences to be checked for range.</param>
        ///<returns>the result after incrementing.</returns>
        public long IncrementAndGet(int delta, Sequence[] dependentSequences)
        {
            long nextSequence = _claimSequence.AddAndGet(delta);
            WaitForFreeSlotAt(nextSequence, dependentSequences, _minGatingSequenceThreadLocal.Value);

            return nextSequence;
        }

        public void SetSequence(long sequence, Sequence[] dependentSequences)
        {
            _claimSequence.LazySet(sequence);
            WaitForFreeSlotAt(sequence, dependentSequences, _minGatingSequenceThreadLocal.Value);
        }

        public void SerialisePublishing(long sequence, Sequence cursor, long batchSize)
        {
            int counter = Retries;
            while (sequence - cursor.Value > _pendingPublication.Length)
            {
                if (--counter == 0)
                {
                    Thread.Sleep(1);
                    counter = Retries;
                }
            }

            long expectedSequence = sequence - batchSize;
            for (long pendingSequence = expectedSequence + 1; pendingSequence <= sequence; pendingSequence++)
            {
                _pendingPublication[(int)pendingSequence & _pendingMask] = pendingSequence;
            }

            if (cursor.Value != expectedSequence)
            {
                return;
            }

            long nextSequence = expectedSequence + 1;
            while (cursor.CompareAndSet(expectedSequence, nextSequence))
            {
                expectedSequence = nextSequence;
                nextSequence++;
                if (_pendingPublication[(int)nextSequence & _pendingMask] != nextSequence)
                {
                    break;
                }
            }
        }

        private void WaitForCapacity(Sequence[] dependentSequences, MutableLong minGatingSequence)
        {
            long wrapPoint = (_claimSequence.Value + 1L) - _bufferSize;
            if (wrapPoint > minGatingSequence.Value)
            {
                long minSequence;
                while (wrapPoint > (minSequence = Util.GetMinimumSequence(dependentSequences)))
                {
                    Thread.Sleep(1);
                    //TODO LockSupport.parkNanos(1L);
                }

                minGatingSequence.Value = minSequence;
            }
        }

        private void WaitForFreeSlotAt(long sequence, Sequence[] dependentSequences, MutableLong minGatingSequence)
        {
            long wrapPoint = sequence - _bufferSize;
            if (wrapPoint > minGatingSequence.Value)
            {
                long minSequence;
                while (wrapPoint > (minSequence = Util.GetMinimumSequence(dependentSequences)))
                {
                    Thread.Sleep(1);
                    // LockSupport.parkNanos(1L);
                }

                minGatingSequence.Value = minSequence;
            }
        }

    }
}