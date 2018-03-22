using MessagePack.Resolvers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using static System.Console;

namespace MinChain
{
    // Taken from https://msdn.microsoft.com/magazine/mt694089.aspx
    public static class Logging //エントリーポイント（開始地点）　Pogram.csが最初に走り、genkeyやconfigに順次飛ぶ

    {
        public static ILoggerFactory Factory { get; } = new LoggerFactory();
        public static ILogger Logger<T>() => Factory.CreateLogger<T>();
    }

    public class Program
    {
        static readonly Dictionary<string, Action<string[]>> commands =
            new Dictionary<string, Action<string[]>>
            {
                { "genkey", KeyGenerator.Exec }, //genkey を入力したらKeyGeneratorにジャンプ
                { "config", Configurator.Exec }, //~ を入力したら~にジャンプ
                { "genesis", Genesis.Exec },
                { "run", Runner.Run },
            };

        public static void Main(string[] args) //引数args（文字列を複数受け取る）

        {
            Logging.Factory.AddConsole(LogLevel.Debug);

            JsonConvert.DefaultSettings = () => new JsonSerializerSettings
            {
                Converters = new List<JsonConverter>
                {
                    new IPEndPointConverter()
                }
            };

            CompositeResolver.RegisterAndSetAsDefault(
                ByteString.ByteStringResolver.Instance,
                BuiltinResolver.Instance,
                DynamicEnumResolver.Instance,
                DynamicGenericResolver.Instance,
                DynamicObjectResolver.Instance);

            if (args.Length == 0)
            {
                WriteLine("No command provided.");
                goto ListCommands; //ListCommandsに移動
            }

            Action<string[]> func;
            var cmd = (args[0] ?? string.Empty).ToLower();
            if (!commands.TryGetValue(cmd, out func))
            {
                WriteLine($"Command '{cmd}' not found.");
                goto ListCommands;
            }

            func(args.Skip(1).ToArray());
            return;

        ListCommands:
            WriteLine("List of commands are:");
            commands.Keys.ToList().ForEach(name => WriteLine($"\t{name}"));
        }
    }
}
