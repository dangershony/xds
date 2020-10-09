using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NBitcoin.BouncyCastle.math;

namespace NBitcoin
{
    public class PosPowChainedHeader : ChainedHeader
    {
        readonly Network network;
        readonly ChainIndexer chainIndexer;

        public PosPowChainedHeader(BlockHeader header, uint256 headerHash, ChainedHeader previous, Network network) : base(header, headerHash, previous)
        {
            this.IsProofOfStake = ((ProvenBlockHeader)header).Coinstake.IsCoinStake;
            this.network = network;
        }

        public PosPowChainedHeader(BlockHeader header, uint256 headerHash, int height, Network network, ChainIndexer chainIndexer) : base(header, headerHash, height)
        {
            this.IsProofOfStake = ((ProvenBlockHeader)header).Coinstake.IsCoinStake;
            this.network = network;
            this.chainIndexer = chainIndexer;
        }

        protected override void CalculateChainWork()
        {
            if (!this.IsProofOfStake)
                this.chainWork = (this.Previous == null ? BigInteger.Zero : this.Previous.ChainWorkBigInteger).Add(GetBlockProof());
            else
            {
                var factor = GetPosPowAdjustmentFactor(this.network.Consensus, this, this.chainIndexer);
                var blockProof = GetBlockProof();
                var adjusted =
                    this.chainWork = blockProof.Divide(factor);
            }
                
        }

        public bool IsProofOfStake { get; }

        /// <summary>
        /// Gets a factor to compare PoS and PoW difficulties based on past averages.
        /// </summary>
        /// <param name="consensus">Consensus</param>
        /// <param name="tip">The current tip</param>
        /// <param name="chainIndexer">ChainIndexer</param>
        /// <returns>The posPowAdjustmentFactor</returns>
        public static BigInteger GetPosPowAdjustmentFactor(IConsensus consensus, ChainedHeader tip, ChainIndexer chainIndexer)
        {
            var posPoWScalingAdjustmentInterval = GetPoSPoWScalingAdjustmentInterval(consensus);  // 2016 blocks

            if (tip.Height <= posPoWScalingAdjustmentInterval)
            {
                return BigInteger.One; // for the first 2016 blocks, do not scale (factor is 1.0)
            }

            // the first time we are here is block 2017
            int posBlockCount = 0;
            int powBlockCount = 0;

            BigInteger sumPosTarget = BigInteger.Zero;
            BigInteger sumPowTarget = BigInteger.Zero;

            var height = tip.Height;

            while (height % posPoWScalingAdjustmentInterval != 0) // go back till last scaling adjustment
            {
                height--;
            }

            int blocksToCount = posPoWScalingAdjustmentInterval;
            var it = tip;

            while (blocksToCount-- > 0)
            {
                var posPowChainedHeader = (PosPowChainedHeader) chainIndexer.GetHeader(it.HashBlock);

                if (posPowChainedHeader.IsProofOfStake)
                {
                    posBlockCount++;
                    sumPosTarget.Add(it.Header.Bits.ToBigInteger());
                    it.Header.Bits.ToBigInteger();
                }
                else
                {
                    powBlockCount++;
                    sumPowTarget.Add(it.Header.Bits.ToBigInteger());
                    it.Header.Bits.ToBigInteger();
                }

                it = it.Previous;
            }

            Debug.Assert(posBlockCount + powBlockCount == posPoWScalingAdjustmentInterval, "invalid sum");
            BigInteger avgPosTarget = sumPosTarget.Divide(BigInteger.ValueOf(posBlockCount));
            BigInteger avgPowTarget = sumPowTarget.Divide(BigInteger.ValueOf(powBlockCount));

            var posPowAdjustmentFactor = avgPosTarget.Divide(avgPowTarget);
            return posPowAdjustmentFactor;
        }

        /// <summary>
        ///     Calculate the PoS / PoW scaling adjustment interval in blocks based on settings defined in <see cref="IConsensus" />.
        ///     Note that this comes down to the same points as the difficulty adjustment.
        /// </summary>
        /// <returns>The PoS / PoW scaling adjustment interval in blocks.</returns>
        static int GetPoSPoWScalingAdjustmentInterval(IConsensus consensus)
        {
            // ‭1,209,600‬ / 600 = 2016
            return (int)consensus.PowTargetTimespan.TotalSeconds / (int)consensus.PowTargetSpacing.TotalSeconds;
        }
    }
}
