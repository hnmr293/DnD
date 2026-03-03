namespace DnD.Protocol;

using System.Text.Json.Serialization;

public record StoppedNotification(
    [property: JsonPropertyName("reason")] StopReason Reason,
    [property: JsonPropertyName("threadId")] int ThreadId,
    [property: JsonPropertyName("description")] string? Description = null
);

public record ExitedNotification(
    [property: JsonPropertyName("exitCode")] int ExitCode
);

public record OutputNotification(
    [property: JsonPropertyName("category")] OutputCategory Category,
    [property: JsonPropertyName("output")] string Output
);
