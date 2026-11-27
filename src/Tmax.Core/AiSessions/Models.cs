namespace Tmax.Core.AiSessions;

// Mirrors src/shared/copilot-types.ts (serialized camelCase, nulls omitted).

public static class SessionStatus
{
    public const string Idle = "idle";
    public const string Thinking = "thinking";
    public const string ExecutingTool = "executingTool";
    public const string AwaitingApproval = "awaitingApproval";
    public const string WaitingForUser = "waitingForUser";

    public static bool NeedsAttention(string status) => status is AwaitingApproval or WaitingForUser;
}

public sealed record SessionSummary
{
    public required string Id { get; init; }
    public required string Provider { get; init; } // "copilot" | "claude-code"
    public string Status { get; init; } = SessionStatus.Idle;
    public string Cwd { get; init; } = "";
    public string Branch { get; init; } = "";
    public string Repository { get; init; } = "";
    public string Summary { get; init; } = "";
    public string? Slug { get; init; }
    public string? FirstPrompt { get; init; }
    public string? LatestPrompt { get; init; }
    public double? LatestPromptTime { get; init; }
    public int MessageCount { get; init; }
    public int ToolCallCount { get; init; }
    public double LastActivityTime { get; init; }
    public double? CreatedAt { get; init; }
    public string? Model { get; init; }
    public bool? Wsl { get; init; }
    public string? WslDistro { get; init; }
}

public sealed record SessionList(List<SessionSummary> Sessions, int TotalEligible, bool? SqliteActive = null);

public sealed record TranscriptEntry(string Role, string Text, double Time);

public sealed record PromptWithTime(string Text, double Time);

public sealed record CopilotWorkspace
{
    public string Cwd { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool? UserNamed { get; set; }
    public double? CreatedAt { get; set; }
}

/// <summary>Full Copilot session (copilot:getSession returns this shape, not the summary).</summary>
public sealed record CopilotSession
{
    public required string Id { get; init; }
    public string Status { get; init; } = SessionStatus.Idle;
    public required CopilotWorkspace Workspace { get; init; }
    public int MessageCount { get; init; }
    public int ToolCallCount { get; init; }
    public double LastActivityTime { get; init; }
    public int PendingToolCalls { get; init; }
    public long TotalTokens { get; init; }
    public string? FirstPrompt { get; init; }
    public string? LatestPrompt { get; init; }
    public double? LatestPromptTime { get; init; }
}

public interface ISessionMonitorEvents
{
    event Action<SessionSummary>? SessionAdded;
    event Action<SessionSummary>? SessionUpdated;
    event Action<string>? SessionRemoved;
}
