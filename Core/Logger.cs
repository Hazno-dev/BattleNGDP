using System.Runtime.CompilerServices;

namespace BattleNGDP.Core;

public static class Logger
{
    public static void Log(string message, [CallerMemberName] string? source = null) => Console.WriteLine($"[{source}] {message}");
}