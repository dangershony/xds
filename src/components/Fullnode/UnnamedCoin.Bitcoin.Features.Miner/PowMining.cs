﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.BouncyCastle.math;
using NBitcoin.Crypto;
using UnnamedCoin.Bitcoin.AsyncWork;
using UnnamedCoin.Bitcoin.Consensus;
using UnnamedCoin.Bitcoin.Features.MemoryPool;
using UnnamedCoin.Bitcoin.Features.MemoryPool.Interfaces;
using UnnamedCoin.Bitcoin.Features.Miner.Interfaces;
using UnnamedCoin.Bitcoin.Interfaces;
using UnnamedCoin.Bitcoin.Mining;
using UnnamedCoin.Bitcoin.Primitives;
using UnnamedCoin.Bitcoin.Utilities;

namespace UnnamedCoin.Bitcoin.Features.Miner
{
    public class ReserveScript
    {
        public ReserveScript()
        {
        }

        public ReserveScript(Script reserveFullNodeScript)
        {
            this.ReserveFullNodeScript = reserveFullNodeScript;
        }

        public Script ReserveFullNodeScript { get; set; }
    }

    public class PowMining : IPowMining
    {
        /// <summary>
        ///     Default for "-blockmintxfee", which sets the minimum feerate for a transaction in blocks created by mining
        ///     code.
        /// </summary>
        public const int DefaultBlockMinTxFee = 1;

        //const int InnerLoopCount = 10_000_000;

        /// <summary>Factory for creating background async loop tasks.</summary>
        private readonly IAsyncProvider asyncProvider;

        /// <summary>Builder that creates a proof-of-work block template.</summary>
        private readonly IBlockProvider blockProvider;

        /// <summary>Thread safe chain of block headers from genesis.</summary>
        private readonly ChainIndexer chainIndexer;

        /// <summary>Manager of the longest fully validated chain of blocks.</summary>
        private readonly IConsensusManager consensusManager;

        /// <summary>Provider of time functions.</summary>
        private readonly IDateTimeProvider dateTimeProvider;

        private readonly IInitialBlockDownloadState initialBlockDownloadState;
        private readonly MinerSettings minerSettings;

        /// <summary>Instance logger.</summary>
        private readonly ILogger logger;

        /// <summary>Factory for creating loggers.</summary>
        private readonly ILoggerFactory loggerFactory;

        /// <summary>Transaction memory pool for managing transactions in the memory pool.</summary>
        private readonly ITxMempool mempool;

        /// <summary>A lock for managing asynchronous access to memory pool.</summary>
        private readonly MempoolSchedulerLock mempoolLock;

        /// <summary>Specification of the network the node runs on - regtest/testnet/mainnet.</summary>
        private readonly Network network;

        /// <summary>Global application life cycle control - triggers when application shuts down.</summary>
        private readonly INodeLifetime nodeLifetime;

        private uint256 hashPrevBlock;

        /// <summary>
        ///     A cancellation token source that can cancel the mining processes and is linked to the
        ///     <see cref="INodeLifetime.ApplicationStopping" />.
        /// </summary>
        private CancellationTokenSource miningCancellationTokenSource;

        /// <summary>The async loop we need to wait upon before we can shut down this feature.</summary>
        private IAsyncLoop miningLoop;

        private OpenCLMiner openCLMiner;

        public PowMining(
            IAsyncProvider asyncProvider,
            IBlockProvider blockProvider,
            IConsensusManager consensusManager,
            ChainIndexer chainIndexer,
            IDateTimeProvider dateTimeProvider,
            ITxMempool mempool,
            MempoolSchedulerLock mempoolLock,
            Network network,
            INodeLifetime nodeLifetime,
            ILoggerFactory loggerFactory,
            IInitialBlockDownloadState initialBlockDownloadState,
            MinerSettings minerSettings)
        {
            this.asyncProvider = asyncProvider;
            this.blockProvider = blockProvider;
            this.chainIndexer = chainIndexer;
            this.consensusManager = consensusManager;
            this.dateTimeProvider = dateTimeProvider;
            this.loggerFactory = loggerFactory;
            this.initialBlockDownloadState = initialBlockDownloadState;
            this.minerSettings = minerSettings;
            this.logger = loggerFactory.CreateLogger(GetType().FullName);
            this.mempool = mempool;
            this.mempoolLock = mempoolLock;
            this.network = network;
            this.nodeLifetime = nodeLifetime;
            this.miningCancellationTokenSource =
                CancellationTokenSource.CreateLinkedTokenSource(this.nodeLifetime.ApplicationStopping);

            if (minerSettings.UseOpenCL)
            {
                this.openCLMiner = new OpenCLMiner(minerSettings, loggerFactory);
            }
        }

        /// <inheritdoc />
        public void Mine(Script reserveScript)
        {
            if (this.miningLoop != null)
                return;

            this.miningCancellationTokenSource =
                CancellationTokenSource.CreateLinkedTokenSource(this.nodeLifetime.ApplicationStopping);

            this.miningLoop = this.asyncProvider.CreateAndRunAsyncLoop("PowMining.Mine", token =>
            {
                try
                {
                    GenerateBlocks(new ReserveScript { ReserveFullNodeScript = reserveScript }, int.MaxValue,
                        int.MaxValue);
                }
                catch (OperationCanceledException)
                {
                    // Application stopping, nothing to do as the loop will be stopped.
                }
                catch (MinerException me)
                {
                    // Block not accepted by peers or invalid. Should not halt mining.
                    this.logger.LogDebug("Miner exception occurred in miner loop: {0}", me.ToString());
                }
                catch (ConsensusErrorException cee)
                {
                    // Issues constructing block or verifying it. Should not halt mining.
                    this.logger.LogDebug("Consensus error exception occurred in miner loop: {0}", cee.ToString());
                }
                catch (ConsensusException ce)
                {
                    // All consensus exceptions should be ignored. It means that the miner
                    // run into problems while constructing block or verifying it
                    // but it should not halted the staking operation.
                    this.logger.LogDebug("Consensus exception occurred in miner loop: {0}", ce.ToString());
                }
                catch
                {
                    this.logger.LogTrace("(-)[UNHANDLED_EXCEPTION]");
                    throw;
                }

                return Task.CompletedTask;
            },
                this.miningCancellationTokenSource.Token,
                TimeSpans.Second,
                TimeSpans.TenSeconds);
        }

        /// <inheritdoc />
        public void StopMining()
        {
            this.miningCancellationTokenSource.Cancel();
            this.miningLoop?.Dispose();
            this.miningLoop = null;
            this.miningCancellationTokenSource.Dispose();
            this.miningCancellationTokenSource = null;
            this.openCLMiner?.Dispose();
            this.openCLMiner = null;
        }

        /// <inheritdoc />
        public List<uint256> GenerateBlocks(ReserveScript reserveScript, ulong amountOfBlocksToMine, ulong maxTries)
        {
            var context = new MineBlockContext(amountOfBlocksToMine, (ulong)this.chainIndexer.Height, maxTries,
                reserveScript);

            while (context.MiningCanContinue)
            {
                if (!ConsensusIsAtTip(context))
                    continue;

                if (!BuildBlock(context))
                    continue;

                if (!MineBlock(context))
                    continue;

                if (!ValidateMinedBlock(context))
                    continue;

                if (!ValidateAndConnectBlock(context))
                    continue;

                OnBlockMined(context);
            }

            return context.Blocks;
        }

        //<inheritdoc/>
        public int IncrementExtraNonce(Block block, ChainedHeader previousHeader, int extraNonce)
        {
            if (this.hashPrevBlock != block.Header.HashPrevBlock)
            {
                extraNonce = 0;
                this.hashPrevBlock = block.Header.HashPrevBlock;
            }

            extraNonce++;

            // BIP34 require the coinbase first input to start with the block height.
            var height = previousHeader.Height + 1;
            block.Transactions[0].Inputs[0].ScriptSig = new Script(Op.GetPushOp(height)) + OpcodeType.OP_0;

            this.blockProvider.BlockModified(previousHeader, block);

            Guard.Assert(block.Transactions[0].Inputs[0].ScriptSig.Length <= 100);
            return extraNonce;
        }

        /// <summary>
        ///     Ensures that the node is synced before mining is allowed to start.
        /// </summary>
        private bool ConsensusIsAtTip(MineBlockContext context)
        {
            this.miningCancellationTokenSource.Token.ThrowIfCancellationRequested();

            if (context.ChainTip != this.consensusManager.Tip)
            {
                context.ChainTip = this.consensusManager.Tip;
                context.BlockTemplate = null;
            }

            // Genesis on a regtest network is a special case. We need to regard ourselves as outside of IBD to
            // bootstrap the mining.
            if (context.ChainTip.Height == 0)
                return true;

            if (this.initialBlockDownloadState.IsInitialBlockDownload())
            {
                Task.Delay(TimeSpan.FromMinutes(1), this.nodeLifetime.ApplicationStopping).GetAwaiter().GetResult();
                return false;
            }

            return true;
        }

        /// <summary>
        ///     Creates a proof of work or proof of stake block depending on the network the node is running on.
        ///     <para>
        ///         If the node is on a POS network, make sure the POS consensus rules are valid. This is required for
        ///         generation of blocks inside tests, where it is possible to generate multiple blocks within one second.
        ///     </para>
        /// </summary>
        private bool BuildBlock(MineBlockContext context)
        {
            if (context.BlockTemplate == null)
                context.BlockTemplate = this.blockProvider.BuildPowBlock(context.ChainTip, context.ReserveScript.ReserveFullNodeScript);

            if (this.network.Consensus.IsProofOfStake)
                if (context.BlockTemplate.Block.Header.Time <= context.ChainTip.Header.Time)
                    return false;

            return true;
        }

        private bool MineBlock(MineBlockContext context)
        {
            if (minerSettings.UseOpenCL && openCLMiner.CanMine())
                return MineBlockOpenCL(context);
            else
                return MineBlockCpu(context);
        }

        private bool MineBlockOpenCL(MineBlockContext context)
        {
            var block = context.BlockTemplate.Block;
            block.Header.Nonce = 0;
            context.ExtraNonce = this.IncrementExtraNonce(block, context.ChainTip, context.ExtraNonce);

            var iterations = uint.MaxValue / (uint)this.minerSettings.OpenCLWorksizeSplit;
            var nonceStart = ((uint)context.ExtraNonce - 1) * iterations;

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var headerBytes = block.Header.ToBytes(this.network.Consensus.ConsensusFactory);
            var bits = block.Header.Bits.ToUInt256();
            var foundNonce = this.openCLMiner.FindPow(headerBytes, bits.ToBytes(), nonceStart, iterations);

            stopwatch.Stop();

            if (foundNonce > 0)
            {
                block.Header.Nonce = foundNonce;
                if (block.Header.CheckProofOfWork())
                {
                    return true;
                }
            }

            this.LogMiningInformation(context.ExtraNonce, iterations, stopwatch.Elapsed.TotalSeconds, block.Header.Bits.Difficulty, $"{this.openCLMiner.GetDeviceName()}");

            if (context.ExtraNonce >= this.minerSettings.OpenCLWorksizeSplit)
            {
                block.Header.Time += 1;
                context.ExtraNonce = 0;
            }

            return false;
        }

        /// <summary>
        ///     Executes until the required work (difficulty) has been reached. This is the "mining" process.
        /// </summary>
        private bool MineBlockCpu(MineBlockContext context)
        {
            context.ExtraNonce = IncrementExtraNonce(context.BlockTemplate.Block, context.ChainTip, context.ExtraNonce);

            var block = context.BlockTemplate.Block;
            block.Header.Nonce = 0;

            uint looplength = 2_000_000;
            int threads = this.minerSettings.MineThreadCount; // Environment.ProcessorCount;
            int batch = threads;
            var totalNonce = batch * looplength;
            uint winnernonce = 0;
            bool found = false;

            ParallelOptions options = new ParallelOptions() { MaxDegreeOfParallelism = threads, CancellationToken = this.miningCancellationTokenSource.Token };

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            int fromInclusive = context.ExtraNonce * batch;
            int toExclusive = fromInclusive + batch;

            Parallel.For(fromInclusive, toExclusive, options, (index, state) =>
            {
                if (this.miningCancellationTokenSource.Token.IsCancellationRequested)
                    return;

                uint256 bits = block.Header.Bits.ToUInt256();

                var headerbytes = block.Header.ToBytes(this.network.Consensus.ConsensusFactory);
                uint nonce = (uint)index * looplength;

                var end = nonce + looplength;

                //this.logger.LogDebug($"nonce={nonce}, end={end}, index={index}, context.ExtraNonce={context.ExtraNonce}, looplength={looplength}");

                while (nonce < end)
                {
                    if (CheckProofOfWork(headerbytes, nonce, bits))
                    {
                        winnernonce = nonce;
                        found = true;
                        state.Stop();

                        return;
                    }

                    if (state.IsStopped)
                        return;

                    ++nonce;
                }
            });

            stopwatch.Stop();

            if (found)
            {
                block.Header.Nonce = winnernonce;
                if (block.Header.CheckProofOfWork())
                    return true;
            }

            this.LogMiningInformation(context.ExtraNonce, totalNonce, stopwatch.Elapsed.TotalSeconds, block.Header.Bits.Difficulty, $"{threads} threads");

            return false;
        }

        private void LogMiningInformation(int extraNonce, long totalHashes, double totalSeconds, double difficultly, string minerInfo)
        {
            var MHashedPerSec = Math.Round((totalHashes / totalSeconds) / 1_000_000, 4);

            var currentDifficulty = BigInteger.ValueOf((long)difficultly);
            var MHashedPerSecTotal = currentDifficulty.Multiply(Target.Pow256)
                                         .Divide(Target.Difficulty1.ToBigInteger()).Divide(BigInteger.ValueOf(10 * 60))
                                         .LongValue / 1_000_000.0;

            this.logger.LogInformation($"Difficulty={difficultly}, extraNonce={extraNonce}, " +
                                       $"hashes={totalHashes}, execution={totalSeconds} sec, " +
                                       $"hash-rate={MHashedPerSec} MHash/sec ({minerInfo}), " +
                                       $"network hash-rate ~{MHashedPerSecTotal} MHash/sec");
        }

        private static bool CheckProofOfWork(byte[] header, uint nonce, uint256 bits)
        {
            var bytes = BitConverter.GetBytes(nonce);
            header[76] = bytes[0];
            header[77] = bytes[1];
            header[78] = bytes[2];
            header[79] = bytes[3];

            var headerHash = Sha512T.GetHash(header);

            var res = headerHash <= bits;

            return res;
        }

        /// <summary>
        ///     Ensures that the block was properly mined by checking the block's work against the next difficulty target.
        /// </summary>
        private bool ValidateMinedBlock(MineBlockContext context)
        {
            var chainedHeader = new ChainedHeader(context.BlockTemplate.Block.Header,
                context.BlockTemplate.Block.GetHash(), context.ChainTip);
            if (chainedHeader.ChainWork <= context.ChainTip.ChainWork)
                return false;

            var block = context.BlockTemplate.Block;

            return true;
        }

        /// <summary>
        ///     Validate the mined block by passing it to the consensus rule engine.
        ///     <para>
        ///         On successful block validation the block will be connected to the chain.
        ///     </para>
        /// </summary>
        private bool ValidateAndConnectBlock(MineBlockContext context)
        {
            var chainedHeader = this.consensusManager.BlockMinedAsync(context.BlockTemplate.Block).GetAwaiter()
                .GetResult();

            if (chainedHeader == null)
            {
                this.logger.LogTrace("(-)[BLOCK_VALIDATION_ERROR]:false");
                return false;
            }

            context.ChainedHeaderBlock = new ChainedHeaderBlock(context.BlockTemplate.Block, chainedHeader);

            return true;
        }

        private void OnBlockMined(MineBlockContext context)
        {
            this.logger.LogInformation("==================================================================");

            this.logger.LogInformation("Mined new {0} block: '{1}'.",
                BlockStake.IsProofOfStake(context.ChainedHeaderBlock.Block) ? "POS" : "POW",
                context.ChainedHeaderBlock.ChainedHeader);
            this.logger.LogInformation("==================================================================");

            context.CurrentHeight++;

            context.Blocks.Add(context.BlockTemplate.Block.GetHash());
            context.BlockTemplate = null;
        }

        /// <summary>
        ///     Context class that holds information on the current state of the mining process (per block).
        /// </summary>
        private class MineBlockContext
        {
            private readonly ulong amountOfBlocksToMine;
            public readonly ReserveScript ReserveScript;
            public readonly List<uint256> Blocks = new List<uint256>();

            public MineBlockContext(ulong amountOfBlocksToMine, ulong chainHeight, ulong maxTries,
                ReserveScript reserveScript)
            {
                this.amountOfBlocksToMine = amountOfBlocksToMine;
                this.ChainHeight = chainHeight;
                this.CurrentHeight = chainHeight;
                this.MaxTries = maxTries;
                this.ReserveScript = reserveScript;
            }

            public BlockTemplate BlockTemplate { get; set; }
            public ulong ChainHeight { get; }
            public ChainedHeaderBlock ChainedHeaderBlock { get; internal set; }
            public ulong CurrentHeight { get; set; }
            public ChainedHeader ChainTip { get; set; }
            public int ExtraNonce { get; set; }
            public int ExtraNonceStart { get; set; }

            public ulong MaxTries { get; set; }
            public bool MiningCanContinue => this.CurrentHeight < this.ChainHeight + this.amountOfBlocksToMine;
        }
    }
}