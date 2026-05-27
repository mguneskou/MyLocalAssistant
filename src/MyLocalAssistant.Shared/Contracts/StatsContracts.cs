namespace MyLocalAssistant.Shared.Contracts;

/// <summary>Summary statistics for the usage dashboard.</summary>
public sealed record StatsDto(
    /// <summary>Total chat requests in the selected window.</summary>
    int TotalChats,
    /// <summary>Number of distinct active users.</summary>
    int ActiveUsers,
    /// <summary>Chat error rate (0–1).</summary>
    double ErrorRate,
    /// <summary>Per-agent chat counts, descending.</summary>
    System.Collections.Generic.List<AgentStatDto> ByAgent,
    /// <summary>Chats per day over the last 30 days.</summary>
    System.Collections.Generic.List<DayStat> DailyChats);

public sealed record AgentStatDto(string AgentId, int Count, int Errors);
public sealed record DayStat(System.DateOnly Day, int Count);
