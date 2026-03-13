namespace DnD.Protocol;

using System.Text.Json.Serialization;

public record LaunchResponse(
    [property: JsonPropertyName("processId")] int ProcessId
);

public record AttachResponse(
    [property: JsonPropertyName("processId")] int ProcessId
);

public record SetBreakpointResponse(
    [property: JsonPropertyName("breakpoint")] Breakpoint Breakpoint
);

public record GetBreakpointsResponse(
    [property: JsonPropertyName("breakpoints")] Breakpoint[] Breakpoints
);

public record GetStackTraceResponse(
    [property: JsonPropertyName("stackFrames")] StackFrame[] StackFrames
);

public record GetVariablesResponse(
    [property: JsonPropertyName("variables")] Variable[] Variables
);

public record GetThreadsResponse(
    [property: JsonPropertyName("threads")] ThreadInfo[] Threads
);

public record GetExceptionResponse(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("stackTrace")] string? StackTrace = null,
    [property: JsonPropertyName("innerException")] ExceptionInfo? InnerException = null
);

public record EvaluateResponse(
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("variablesReference")] int VariablesReference,
    [property: JsonPropertyName("type")] string? Type = null
);
