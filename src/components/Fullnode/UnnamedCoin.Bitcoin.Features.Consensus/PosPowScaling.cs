using System;
using System.Diagnostics;
using NBitcoin;
using NBitcoin.BouncyCastle.math;

namespace UnnamedCoin.Bitcoin.Features.Consensus
{
    public class PosPowScaling
    {
        /// <summary>
        /// Gets a factor to compare PoS and PoW difficulties based on past averages.
        /// </summary>
        /// <param name="consensus">Consensus</param>
        /// <param name="tip">The current tip</param>
        /// <param name="stakeChain">StakeChain</param>
        /// <returns>The posPowAdjustmentFactor</returns>
        public static double GetPosPowAdjustmentFactor(IConsensus consensus, ChainedHeader tip, IStakeChain stakeChain)
        {
            var posPoWScalingAdjustmentInterval = GetPoSPoWScalingAdjustmentInterval(consensus);  // 2016 blocks

            if (tip.Height <= posPoWScalingAdjustmentInterval)
            {
                return 1.0; // for the first 2016 blocks, do not scale (factor is 1.0)
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
                BlockStake blockStake = stakeChain.Get(it.HashBlock);

                if (blockStake.IsProofOfStake())
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

            double posPowAdjustmentFactor = avgPosTarget.Multiply(BigInteger.ValueOf(1000)).Divide(avgPowTarget).LongValue / 1000.0;
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
