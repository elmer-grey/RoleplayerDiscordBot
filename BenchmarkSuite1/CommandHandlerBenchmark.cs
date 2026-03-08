using System;
using System.Collections.Generic;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using Discord.WebSocket;
using Microsoft.VSDiagnostics;

namespace RPBot.Benchmarks
{
    [CPUUsageDiagnoser]
    public class CommandHandlerBenchmark
    {
        private object _handler;
        private MethodInfo _getAllCommands;
        [GlobalSetup]
        public void Setup()
        {
            // Создаём экземпляр CommandHandler с null-клиентом и пустым списком гильдий.
            var ctor = typeof(RPBot.CommandHandler).GetConstructor(new Type[] { typeof(DiscordSocketClient), typeof(List<ulong>) });
            _handler = ctor.Invoke(new object[] { null, new List<ulong>() });
            _getAllCommands = typeof(RPBot.CommandHandler).GetMethod("GetAllCommands", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        [Benchmark]
        public int GetAllCommands_Count()
        {
            var res = (List<Discord.SlashCommandBuilder>)_getAllCommands.Invoke(_handler, null);
            return res.Count;
        }
    }
}