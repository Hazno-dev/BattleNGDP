using System.Globalization;
using System.Runtime.CompilerServices;

namespace BattleNGDP.Core;

public class StringUtility
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodeHexNibble(char value)
    {
        return value switch {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1
        };
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodeHexNibble(byte value)
    {
        return value switch {
            >= (byte)'0' and <= (byte)'9' => value - '0',
            >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
            _ => -1
        };
    }
    
    public static bool IsAsciiWhitespace(byte c) => c is
        (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    public static byte ToLowerAscii(byte c) =>
        c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;

    public static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> s)
    {
        while (!s.IsEmpty && IsAsciiWhitespace(s[0])) {
            s = s[1..];
        }
        
        while (!s.IsEmpty && IsAsciiWhitespace(s[^1])) {
            s = s[..^1];
        }
        
        return s;
    }
    
    public static string BytesToString(long value) => 
        BytesToString((ulong)Math.Abs(value));
    
    //https://stackoverflow.com/questions/281640/how-do-i-get-a-human-readable-file-size-in-bytes-abbreviation-using-net
    public static string BytesToString(ulong value)
    {
        string suffix;
        double readable;
        switch (value)
        {
            case >= 0x1000000000000000:
                suffix = "EiB";
                readable = value >> 50;
                break;
            case >= 0x4000000000000:
                suffix = "PiB";
                readable = value >> 40;
                break;
            case >= 0x10000000000:
                suffix = "TiB";
                readable = value >> 30;
                break;
            case >= 0x40000000:
                suffix = "GiB";
                readable = value >> 20;
                break;
            case >= 0x100000:
                suffix = "MiB";
                readable = value >> 10;
                break;
            case >= 0x400:
                suffix = "KiB";
                readable = value;
                break;
            default:
                return value.ToString("0 B");
        }

        return (readable / 1024).ToString("0.## ", CultureInfo.InvariantCulture) + suffix;
    }
    
    public static int GetDirectoryDepth(string directory)
    {
        if (directory.Length == 0)
            return 0;

        int depth = 1;

        foreach (char c in directory)
        {
            if (c is '/' or '\\')
                depth++;
        }

        return depth;
    }
}