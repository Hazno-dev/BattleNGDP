using System.Diagnostics.CodeAnalysis;
using System.Text;
using BattleNGDP.CASC;
using BattleNGDP.Core;

namespace BattleNGDP.TACT;

public abstract class TACTConfig
{
    private Dictionary<string, string> m_values;

    protected bool TryGetValue(string key, [NotNullWhen(true)] out string? value) =>
        m_values.TryGetValue(key, out value);

    protected string? GetValue(string key) =>
        m_values.GetValueOrDefault(key);

    protected TACTConfig()
    {
        m_values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    protected TACTConfig(ReadOnlySpan<byte> data)
    {
        m_values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (!data.IsEmpty) {
            var newline = data.IndexOf((byte) '\n');
            ReadOnlySpan<byte> line;
            if (newline < 0) {
                line = data;
                data = [];
            } else {
                line = data[..newline];
                data = data[(newline + 1)..];
            }

            line = line.TrimLine();
            if (line.IsEmpty || line[0] == (byte) '#') {
                continue;
            }

            var separator = line.IndexOf((byte) '=');
            if (separator < 0) {
                throw new InvalidDataException($"Invalid config line: {Encoding.UTF8.GetString(line)}");
            }

            var key = Encoding.UTF8.GetString(line[..separator].TrimLine());
            var value = Encoding.UTF8.GetString(line[(separator + 1)..].TrimLine());
            m_values[key] = value;
        }
    }

    protected IReadOnlyList<string>? GetValues(string key, int start)
    {
        if (!key.Contains("{0}")) {
            throw new ArgumentException($"Invalid config key: {key} - missing \"{{0}}\"");
        }

        var values = new List<string>();
        var count = start;
        while (true) {
            var keyStr = string.Format(key, count);
            if (!m_values.TryGetValue(keyStr, out var value)) {
                break;
            }

            values.Add(value);
            count++;
        }

        return values.Count > 0 ? values : null;
    }
}

public struct BuildConfigKeyValuePair
{
    public FullKey CKey;
    public FullKey? EKey;

    public static BuildConfigKeyValuePair? Create(string? str)
    {
        if (string.IsNullOrEmpty(str)) {
            return null;
        }

        var span = str.AsSpan().Trim();
        var splitIdx = span.IndexOf(' ');
        if (splitIdx >= 0) {
            return new BuildConfigKeyValuePair {
                CKey = FullKey.FromString(span[..splitIdx]),
                EKey = FullKey.FromString(span[(splitIdx + 1)..])
            };
        }

        return new BuildConfigKeyValuePair {
            CKey = FullKey.FromString(span),
            EKey = null
        };
    }

    public static BuildConfigKeyValuePair[]? CreateMulti(IReadOnlyList<string>? str)
    {
        if (str == null) {
            return null;
        }

        var entryList = new List<BuildConfigKeyValuePair>(str.Count);
        foreach (var strLine in str) {
            entryList.Add(Create(strLine)!.Value);
        }

        return [.. entryList];
    }
}

public struct BuildConfigKeyValues
{
    public FullKey[] Keys;

    public static BuildConfigKeyValues? Create(string? str)
    {
        if (string.IsNullOrEmpty(str)) {
            return null;
        }

        var keys = str.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new BuildConfigKeyValues {
            Keys = [.. keys.Select(FullKey.FromString)]
        };
    }

    public static BuildConfigKeyValues[]? CreateMulti(IReadOnlyList<string>? str)
    {
        if (str == null) {
            return null;
        }

        var entryList = new List<BuildConfigKeyValues>(str.Count);
        foreach (var strLine in str) {
            entryList.Add(Create(strLine)!.Value);
        }

        return [.. entryList];
    }
}

public struct BuildConfigSizeValuePair
{
    public int CSize;
    public int? ESize;

    public static BuildConfigSizeValuePair? Create(string? str)
    {
        if (string.IsNullOrEmpty(str)) {
            return null;
        }

        var span = str.AsSpan().Trim();
        var splitIdx = span.IndexOf(' ');
        if (splitIdx >= 0) {
            return new BuildConfigSizeValuePair {
                CSize = int.Parse(span[..splitIdx]),
                ESize = int.Parse(span[(splitIdx + 1)..])
            };
        }

        return new BuildConfigSizeValuePair {
            CSize = int.Parse(span),
            ESize = null
        };
    }

    public static BuildConfigSizeValuePair[]? CreateMulti(IReadOnlyList<string>? str)
    {
        if (str == null) {
            return null;
        }

        var entryList = new List<BuildConfigSizeValuePair>(str.Count);
        foreach (var strLine in str) {
            entryList.Add(Create(strLine)!.Value);
        }

        return [.. entryList];
    }
}

public struct BuildConfigSizeValues
{
    public int[] Sizes;

    public static BuildConfigSizeValues? Create(string? str)
    {
        if (string.IsNullOrEmpty(str)) {
            return null;
        }

        var sizes = str.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new BuildConfigSizeValues {
            Sizes = [.. sizes.Select(int.Parse)]
        };
    }

    public static BuildConfigSizeValues[]? CreateMulti(IReadOnlyList<string>? str)
    {
        if (str == null) {
            return null;
        }

        var entryList = new List<BuildConfigSizeValues>(str.Count);
        foreach (var strLine in str) {
            entryList.Add(Create(strLine)!.Value);
        }

        return [.. entryList];
    }
}