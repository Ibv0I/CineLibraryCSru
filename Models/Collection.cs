namespace CineМедиатекаCS.Models;

public class Collection
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int MovieCount { get; set; }
}

public class GenreFacet
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

public class МедиатекаStats
{
    public int TotalФильмы { get; set; }
    public int TotalMissing { get; set; }
    public long TotalПродолжительность { get; set; }
    public double? AvgРейтинг { get; set; }
    public int TotalДиски { get; set; }

    public string TotalПродолжительностьText
    {
        get
        {
            if (TotalПродолжительность == 0) return "—";
            var h = TotalПродолжительность / 60;
            if (h < 24) return $"{h}h";
            var d = h / 24;
            return $"{d}d {h % 24}h";
        }
    }

    public string AvgРейтингText => AvgРейтинг.HasValue ? $"★ {AvgРейтинг:F1}" : "—";
}

/// <summary>v2.8.2 — TV stats for the Статистика page.</summary>
public class TvStats
{
    public int TotalShows { get; set; }
    public int TotalЭпизоды { get; set; }
    public int ПросмотреноЭпизоды { get; set; }
    public long TotalПродолжительность { get; set; }   // minutes, across all episodes
    public double? AvgРейтинг { get; set; }

    public int WatchPercent => TotalЭпизоды > 0
        ? (int)Math.Round(100.0 * ПросмотреноЭпизоды / TotalЭпизоды) : 0;
    public string AvgРейтингText => AvgРейтинг.HasValue ? $"★ {AvgРейтинг:F1}" : "—";
}
