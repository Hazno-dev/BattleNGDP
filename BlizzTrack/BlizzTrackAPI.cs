using System.Text.Json;
using BattleNGDP.Core;

namespace BattleNGDP.BlizzTrack;

public static class BlizzTrackAPI
{
    private static HttpClient s_client = new();
    
    private static JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    
    public static async Task<(IList<BlizzTrackManifest<T>>, uint newCount)> GetManifests<T>(string tact) where T : struct, IBlizzTrackManifest
    {
        var latestSeqns = await GetSeqns(tact, T.TrackType, 1);
        var stored = BlizzTrackStorage.GetTact(tact).Get<T>();
        
        if (stored.Count == latestSeqns.Total && stored.Count > 0 && stored.GetValueAtIndex(0).Seqn == latestSeqns.Results[0].Seqn) {
            return (stored.Values, 0);
        }
        
        Logger.Log("Loading new manifests from BlizzTrack...");
        var curCount = stored.Count;
        await UpdateStorage(stored, latestSeqns);
        BlizzTrackStorage.Save();
        return (stored.Values, (uint)(stored.Count - curCount));
    }

    private static async Task<BlizzTrackSeqns> GetSeqns(string tact, BlizzTrackType type, uint page)
    {
        var uri = new Uri($"https://blizztrack.com/api/manifest/{tact}/seqn?file={type.GetString()}&page={page}");
        var res = await s_client.GetAsync(uri);
        var json = await res.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<BlizzTrackResult<BlizzTrackSeqns>>(json, s_options);
        if (result.Result.Results.Count > 0) {
            result.Result.Results.Sort();
        }

        return result.Result;
    }

    private static async Task UpdateStorage<T>(SortedList<BlizzTrackSeqnValue, BlizzTrackManifest<T>> stored, BlizzTrackSeqns seqnPage) where T : struct, IBlizzTrackManifest
    {
        while (true) {
            if (seqnPage.Results.Count == 0) {
                return;
            }

            for (var i = 0; i < seqnPage.Results.Count; i++) {
                var seqn = seqnPage.Results[i];
                if (stored.ContainsKey(new BlizzTrackSeqnValue(seqn.Seqn))) {
                    continue;
                }

                var pathStr = seqn.Refs[T.TrackType.GetString()];
                var res = await s_client.GetAsync(new Uri($"https://blizztrack.com/{pathStr.TrimStart('/')}"));
                var json = await res.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<BlizzTrackResult<BlizzTrackManifest<T>>>(json, s_options);
                stored.Add(new BlizzTrackSeqnValue(seqn.Seqn), result.Result);
            }

            var nextSeqn = await GetSeqns(seqnPage.Metadata.Tact, T.TrackType, seqnPage.Page + 1);
            seqnPage = nextSeqn;
        }
    }
}