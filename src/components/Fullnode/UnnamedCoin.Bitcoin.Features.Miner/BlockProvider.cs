using System.Collections.Generic;
using System.Linq;
using NBitcoin;
using UnnamedCoin.Bitcoin.Mining;

namespace UnnamedCoin.Bitcoin.Features.Miner
{
    /// <inheritdoc />
    public sealed class BlockProvider : IBlockProvider
    {
        readonly Network network;

        /// <summary>Defines how proof of stake blocks are built.</summary>
        readonly PosBlockDefinition posBlockDefinition;

        /// <summary>Defines how proof of work blocks are built on a Proof-of-Stake network.</summary>
        readonly PosPowBlockDefinition posPowBlockDefinition;

        /// <summary>Defines how proof of work blocks are built.</summary>
        readonly PowBlockDefinition powBlockDefinition;

        /// <param name="definitions">A list of block definitions that the builder can utilize.</param>
        public BlockProvider(Network network, IEnumerable<BlockDefinition> definitions)
        {
            this.network = network;

            this.powBlockDefinition = definitions.OfType<PowBlockDefinition>().FirstOrDefault();
            this.posBlockDefinition = definitions.OfType<PosBlockDefinition>().FirstOrDefault();
            this.posPowBlockDefinition = definitions.OfType<PosPowBlockDefinition>().FirstOrDefault();
        }

        /// <inheritdoc />
        public BlockTemplate BuildPosBlock(ChainedHeader chainTip, Script script)
        {
            return this.posBlockDefinition.Build(chainTip, script);
        }

        /// <inheritdoc />
        public BlockTemplate BuildPowBlock(ChainedHeader chainTip, Script script)
        {
            // when building a PoW block in a permanent PoS + PoW network (PosPowOptions is not null),  use the Bitcoin two week difficulty retarget.
            if (this.network.Consensus.IsProofOfStake && !this.network.Consensus.LongPosPowPowDifficultyAdjustments)
                return this.posPowBlockDefinition.Build(chainTip, script);

            // XDS will choose this execution path.
            return this.powBlockDefinition.Build(chainTip, script);
        }

        /// <inheritdoc />
        public void BlockModified(ChainedHeader chainTip, Block block)
        {
            if (this.network.Consensus.IsProofOfStake)
            {
                if (BlockStake.IsProofOfStake(block))
                    this.posBlockDefinition.BlockModified(chainTip, block);
                else
                    this.posPowBlockDefinition.BlockModified(chainTip, block);
            }

            this.powBlockDefinition.BlockModified(chainTip, block);
        }
    }
}