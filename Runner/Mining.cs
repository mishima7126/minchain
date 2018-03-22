using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static MinChain.BlockchainUtil;
using static MessagePack.MessagePackSerializer;

namespace MinChain
{
    public class Mining
    {
        static readonly ILogger logger = Logging.Logger<Mining>();

        public ConnectionManager ConnectionManager { get; set; }
        public InventoryManager InventoryManager { get; set; }
        public Executor Executor { get; set; }

        public ByteString RecipientAddress { get; set; }

        public bool IsMining { get; private set; }
        CancellationTokenSource cts;

        public static Transaction CreateCoinbase(int height, byte[] recipient)
        {
            var tx = new Transaction
            {
                Timestamp = DateTime.UtcNow,
                InEntries = new List<InEntry>(),
                OutEntries = new List<OutEntry>
                {
                    new OutEntry
                    {
                        Amount = BlockParameter.GetCoinbase(0),
                        RecipientHash = ByteString.CopyFrom(recipient),
                    },
                },
            };

            var data = tx.Original = Serialize(tx);
            tx.Id = ByteString.CopyFrom(ComputeTransactionId(data));
            return tx;
        }

        public static bool Mine(Block seed,
            CancellationToken token = default(CancellationToken))
        {
            var rnd = new Random();
            var nonceSeed = new byte[sizeof(ulong)];
            rnd.NextBytes(nonceSeed);

            ulong nonce = BitConverter.ToUInt64(nonceSeed, 0);
            while (!token.IsCancellationRequested)
            {
                seed.Nonce = nonce++;
                seed.Timestamp = DateTime.UtcNow;

                var data = Serialize(seed);
                var blockId = ComputeBlockId(data);
                if (Hash.Difficulty(blockId) > seed.Difficulty)
                {
                    seed.Id = ByteString.CopyFrom(blockId);
                    seed.Original = data;
                    return true;
                }
            }

            return false;
        }

        public void Start()
        {
            IsMining = true;

            cts = new CancellationTokenSource();
            Task.Run(() => MineFromLastBlock(cts.Token));
        }

        public void Notify()
        {
            if (!IsMining) return;

            // Very easy.
            Stop();
            Start();
        }

        public void Stop()
        {
            IsMining = false;

            if (cts.IsNull()) return;

            cts.Cancel();
            cts.Dispose();
            cts = null;
        }

        void MineFromLastBlock(CancellationToken token)
        {
            // Takeout memory pool transactions.
            var size = 350; // Estimated block header + coinbase size
            var txs = InventoryManager.MemoryPool
                .Select(tx => tx.Value)
                .TakeWhile(tx => (size += tx.Original.Length + 50)
                    < InventoryManager.MaximumBlockSize)
                .ToList(); // Iteration should end immediately.

            // Choose transactions that are valid.
            var blockTime = DateTime.UtcNow;
            ulong coinbase = BlockParameter.GetCoinbase(Executor.Latest.Height + 1);
            var spent = new List<TransactionOutput>();
            txs = txs.Where(tx =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    Executor.Run(tx, blockTime, spentTxo: spent);

                    var exec = tx.ExecInfo;
                    coinbase += exec.TransactionFee;
                    spent.AddRange(exec.RedeemedOutputs);
                    return true;
                }
                catch { return false; }
            }).ToList();

            // Create coinbase transaction structure.
            var coinbaseTx = new Transaction
            {
                Timestamp = blockTime,
                InEntries = new List<InEntry>(),
                OutEntries = new List<OutEntry>
                {
                    new OutEntry
                    {
                        RecipientHash = RecipientAddress,
                        Amount = coinbase,
                    },
                },
            };

            // We need backing byte-encoded behind.
            coinbaseTx = DeserializeTransaction(Serialize(coinbaseTx));
            Executor.Run(coinbaseTx, blockTime, coinbase);
            txs.Insert(0, coinbaseTx);

            // Calculate root hash.
            var txIds = txs.Select(x => x.Id).ToList();
            var block = new Block
            {
                PreviousHash = Executor.Latest.Id,
                Difficulty = BlockParameter.GetNextDifficulty(
                    Executor.Latest.Ancestors(Executor.Blocks)),
                TransactionRootHash = RootHashTransactionIds(txIds),
            };

            if (!Mine(block, token)) return;

            block.TransactionIds = txIds;
            block.Transactions = txs.Select(x => x.Original).ToList();
            block.ParsedTransactions = txs.ToArray();

            logger.LogInformation("Block mined: {0}",
                JsonConvert.SerializeObject(block, Formatting.Indented));

            var msg = new InventoryMessage
            {
                Data = Serialize(block),
                ObjectId = block.Id,
                IsBlock = true,
                Type = InventoryMessageType.Body,
            };
            ConnectionManager.BroadcastAsync(msg);
            InventoryManager.HandleMessage(msg, -1);
        }
    }
}
