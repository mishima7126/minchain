using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static MessagePack.MessagePackSerializer;

namespace MinChain
{
    public partial class Runner
    {
        static readonly ILogger logger = Logging.Logger<Runner>();

        public static void Run(string[] args) =>
            new Runner().RunInternal(args);//Runnerオブジェクトを作る（RunInternalに関する関数）

        Configuration config;//インスタンス固有の変数定義（staticが付いてないため,staticが付いている場合はクラスで定義されている）
        KeyPair myKeys;
        Block genesis;

        ConnectionManager connectionManager;
        InventoryManager inventoryManager;
        Executor executor;
        Mining miner;

        void RunInternal(string[] args)
        {
            if (!LoadConfiguration(args)) return;

            connectionManager = new ConnectionManager();//
            inventoryManager = new InventoryManager();//自分自身がどんなブロックを持ってるか管理する
            executor = new Executor();//
            miner = new Mining();//マイニング処理用のもじゅーる

            connectionManager.NewConnectionEstablished += NewPeer;
            connectionManager.MessageReceived += HandleMessage;
            executor.BlockExecuted += miner.Notify;

            inventoryManager.ConnectionManager = connectionManager;
            inventoryManager.Executor = executor;
            executor.InventoryManager = inventoryManager;
            miner.ConnectionManager = connectionManager;
            miner.InventoryManager = inventoryManager;
            miner.Executor = executor;

            inventoryManager.Blocks.Add(genesis.Id, genesis.Original);//inventorymanagerにgenesisブロックを
            executor.ProcessBlock(genesis.Original, genesis.PreviousHash);

            connectionManager.Start(config.ListenOn);
            var t = Task.Run(async () =>
            {
                foreach (var ep in config.InitialEndpoints)
                    await connectionManager.ConnectToAsync(ep);
            });

            if (config.Mining)
            {
                miner.RecipientAddress = ByteString.CopyFrom(myKeys.Address);
                miner.Start();
            }

            Console.ReadLine();

            connectionManager.Dispose();
        }

        bool LoadConfiguration(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Should provide configuration file path.");
                return false;
            }

            try
            {
                config = JsonConvert.DeserializeObject<Configuration>(
                    File.ReadAllText(Path.GetFullPath(args[0])));
            }
            catch (Exception exp)
            {
                logger.LogError(
                    "Failed to load configuration file. Run 'config' command.",
                    exp);
                return false;
            }

            try
            {
                myKeys = KeyPair.LoadFrom(config.KeyPairPath);//configで書き出された鍵ペアを読み込む
            }
            catch (Exception exp)
            {
                logger.LogError(
                    $"Failed to load key from {config.KeyPairPath}.",
                    exp);
                return false;
            }

            try
            {
                var bytes = File.ReadAllBytes(config.GenesisPath);
                genesis = BlockchainUtil.DeserializeBlock(bytes);//configファイルのgenesis.binを読み込み、genesis変数にgenesisブロックを復元（ここまでが初期化処理）
            }
            catch (Exception exp)
            {
                logger.LogError(
                    $"Failed to load the genesis from {config.GenesisPath}.",
                    exp);
                return false;
            }

            return true;
        }

        void NewPeer(int peerId)
        {
            var peers = connectionManager.GetPeers()
                .Select(x => x.ToString());
            connectionManager.SendAsync(new Hello
            {
                Genesis = genesis.Id,
                KnownBlocks = executor.Blocks.Keys.ToList(),
                MyPeers = peers.ToList(),
            }, peerId);
        }

        Task HandleMessage(Message message, int peerId)
        {
            switch (message.Type)
            {
                case MessageType.Hello:
                    return HandleHello(
                        Deserialize<Hello>(message.Payload),
                        peerId);

                case MessageType.Inventory:
                    return inventoryManager.HandleMessage(
                        Deserialize<InventoryMessage>(message.Payload),
                        peerId);

                default: return Task.CompletedTask;
            }
        }

        async Task HandleHello(Hello hello, int peerId)
        {
            // Check if the peer is on the same network.
            if (!genesis.Id.Equals(hello.Genesis))
                connectionManager.Close(peerId);

            var myBlocks = new HashSet<ByteString>();
            var peerBlocks = new HashSet<ByteString>();
            foreach (var blockId in executor.Blocks.Keys) myBlocks.Add(blockId);
            foreach (var blockId in hello.KnownBlocks) peerBlocks.Add(blockId);

            var messages = peerBlocks.Except(myBlocks)
                .Select(x => new InventoryMessage
                {
                    Type = InventoryMessageType.Request,
                    ObjectId = x,
                    IsBlock = true,
                })
                .ToArray();

            // Send request for unknown blocks.
            foreach (var message in messages)
                await connectionManager.SendAsync(message, peerId);
        }
    }
}
