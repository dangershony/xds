﻿using System;
using System.Collections.Generic;

namespace NBitcoin
{
    /// <summary>
    ///     An indexer that provides methods to query the best chain (the chain that is validated by the full consensus rules)
    /// </summary>
    public class ChainIndexer
    {
        /// <remarks>This object has to be protected by <see cref="lockObject" />.</remarks>
        readonly Dictionary<int, ChainedHeader> blocksByHeight;

        /// <remarks>This object has to be protected by <see cref="lockObject" />.</remarks>
        readonly Dictionary<uint256, ChainedHeader> blocksById;

        /// <summary>Locks access to <see cref="blocksByHeight" />, <see cref="blocksById" />.</summary>
        readonly object lockObject = new object();

        public ChainIndexer()
        {
            this.blocksByHeight = new Dictionary<int, ChainedHeader>();
            this.blocksById = new Dictionary<uint256, ChainedHeader>();
        }

        public ChainIndexer(Network network) : this()
        {
            this.Network = network;

            ChainedHeader chainedHeader;
            if (network.Consensus.UsePosPowScaling)
                chainedHeader = new PosPowChainedHeader(network.GetGenesis().Header, network.GetGenesis().GetHash(), 0, network, this);
            else 
                chainedHeader = new ChainedHeader(network.GetGenesis().Header, network.GetGenesis().GetHash(), 0);
            Initialize(chainedHeader);
        }

        public ChainIndexer(Network network, ChainedHeader chainedHeader) : this()
        {
            this.Network = network;

            Initialize(chainedHeader);
        }

        public Network Network { get; }

        /// <summary>
        ///     The tip of the best known validated chain.
        /// </summary>
        public virtual ChainedHeader Tip { get; private set; }

        /// <summary>
        ///     The tip height of the best known validated chain.
        /// </summary>
        public int Height => this.Tip.Height;

        public ChainedHeader Genesis => GetHeader(0);

        /// <summary>
        ///     Get a <see cref="ChainedHeader" /> based on it's height.
        /// </summary>
        public ChainedHeader this[int key] => GetHeader(key);

        /// <summary>
        ///     Get a <see cref="ChainedHeader" /> based on it's hash.
        /// </summary>
        public ChainedHeader this[uint256 id] => GetHeader(id);

        public void Initialize(ChainedHeader chainedHeader)
        {
            lock (this.lockObject)
            {
                this.blocksById.Clear();
                this.blocksByHeight.Clear();

                var iterator = chainedHeader;

                while (iterator != null)
                {
                    this.blocksById.Add(iterator.HashBlock, iterator);
                    this.blocksByHeight.Add(iterator.Height, iterator);

                    if (iterator.Height == 0)
                        if (this.Network.GenesisHash != iterator.HashBlock)
                            throw new InvalidOperationException("Wrong network");

                    iterator = iterator.Previous;
                }

                this.Tip = chainedHeader;
            }
        }

        /// <summary>
        ///     Returns the first chained block header that exists in the chain from the list of block hashes.
        /// </summary>
        /// <param name="hashes">Hash to search for.</param>
        /// <returns>First found chained block header or <c>null</c> if not found.</returns>
        public ChainedHeader FindFork(IEnumerable<uint256> hashes)
        {
            if (hashes == null)
                throw new ArgumentNullException("hashes");

            // Find the first block the caller has in the main chain.
            foreach (var hash in hashes)
            {
                var chainedHeader = GetHeader(hash);
                if (chainedHeader != null)
                    return chainedHeader;
            }

            return null;
        }

        /// <summary>
        ///     Finds the first chained block header that exists in the chain from the block locator.
        /// </summary>
        /// <param name="locator">The block locator.</param>
        /// <returns>The first chained block header that exists in the chain from the block locator.</returns>
        public ChainedHeader FindFork(BlockLocator locator)
        {
            if (locator == null)
                throw new ArgumentNullException("locator");

            return FindFork(locator.Blocks);
        }

        /// <summary>
        ///     Enumerate chain block headers after given block hash to genesis block.
        /// </summary>
        /// <param name="blockHash">Block hash to enumerate after.</param>
        /// <returns>Enumeration of chained block headers after given block hash.</returns>
        public IEnumerable<ChainedHeader> EnumerateAfter(uint256 blockHash)
        {
            var block = GetHeader(blockHash);

            if (block == null)
                return new ChainedHeader[0];

            return EnumerateAfter(block);
        }

        /// <summary>
        ///     Enumerates chain block headers from the given chained block header to tip.
        /// </summary>
        /// <param name="block">Chained block header to enumerate from.</param>
        /// <returns>Enumeration of chained block headers from given chained block header to tip.</returns>
        public IEnumerable<ChainedHeader> EnumerateToTip(ChainedHeader block)
        {
            if (block == null)
                throw new ArgumentNullException("block");

            return EnumerateToTip(block.HashBlock);
        }

        /// <summary>
        ///     Enumerates chain block headers from given block hash to tip.
        /// </summary>
        /// <param name="blockHash">Block hash to enumerate from.</param>
        /// <returns>Enumeration of chained block headers from the given block hash to tip.</returns>
        public IEnumerable<ChainedHeader> EnumerateToTip(uint256 blockHash)
        {
            var block = GetHeader(blockHash);
            if (block == null)
                yield break;

            yield return block;

            foreach (var chainedBlock in EnumerateAfter(blockHash))
                yield return chainedBlock;
        }

        /// <summary>
        ///     Enumerates chain block headers after the given chained block header to genesis block.
        /// </summary>
        /// <param name="block">The chained block header to enumerate after.</param>
        /// <returns>Enumeration of chained block headers after the given block.</returns>
        public virtual IEnumerable<ChainedHeader> EnumerateAfter(ChainedHeader block)
        {
            var i = block.Height + 1;
            var prev = block;

            while (true)
            {
                var b = GetHeader(i);
                if (b == null || b.Previous != prev)
                    yield break;

                yield return b;
                i++;
                prev = b;
            }
        }


        public void Add(ChainedHeader addTip)
        {
            lock (this.lockObject)
            {
                if (this.Tip.HashBlock != addTip.Previous.HashBlock)
                    throw new InvalidOperationException("New tip must be consecutive");

                this.blocksById.Add(addTip.HashBlock, addTip);
                this.blocksByHeight.Add(addTip.Height, addTip);

                this.Tip = addTip;
            }
        }


        public void Remove(ChainedHeader removeTip)
        {
            lock (this.lockObject)
            {
                if (this.Tip.HashBlock != removeTip.HashBlock)
                    throw new InvalidOperationException("Trying to remove item that is not the tip.");

                this.blocksById.Remove(removeTip.HashBlock);
                this.blocksByHeight.Remove(removeTip.Height);

                this.Tip = this.blocksById[removeTip.Previous.HashBlock];
            }
        }

        /// <summary>
        ///     Get a <see cref="ChainedHeader" /> based on it's hash.
        /// </summary>
        public virtual ChainedHeader GetHeader(uint256 id)
        {
            lock (this.lockObject)
            {
                ChainedHeader result;
                this.blocksById.TryGetValue(id, out result);
                return result;
            }
        }

        /// <summary>
        ///     Get a <see cref="ChainedHeader" /> based on it's height.
        /// </summary>
        public virtual ChainedHeader GetHeader(int height)
        {
            lock (this.lockObject)
            {
                ChainedHeader result;
                this.blocksByHeight.TryGetValue(height, out result);
                return result;
            }
        }

        public override string ToString()
        {
            return this.Tip == null ? "no tip" : this.Tip.ToString();
        }
    }
}