namespace DnD.Protocol;

using System.Text.Json.Serialization;

// Process control
public record LaunchRequest(
    [property: JsonPropertyName("program")] string Program,
    [property: JsonPropertyName("args")] string[]? Args = null,
    [property: JsonPropertyName("cwd")] string? Cwd = null,
    [property: JsonPropertyName("env")] Dictionary<string, string>? Env = null,
    [property: JsonPropertyName("stopAtEntry")] bool StopAtEntry = false
);

public record AttachRequest(
    [property: JsonPropertyName("processId")] int ProcessId
);

// Execution control
public record ContinueRequest(
    [property: JsonPropertyName("threadId")] int? ThreadId = null
);

public record StepInRequest(
    [property: JsonPropertyName("threadId")] int? ThreadId = null
);

public record StepOverRequest(
    [property: JsonPropertyName("threadId")] int? ThreadId = null
);

public record StepOutRequest(
    [property: JsonPropertyName("threadId")] int? ThreadId = null
);

// Breakpoints
public record SetBreakpointRequest(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("line")] int Line
);

public record RemoveBreakpointRequest(
    [property: JsonPropertyName("breakpointId")] int BreakpointId
);

// Inspection
public record GetExceptionRequest(
    [property: JsonPropertyName("threadId")] int? ThreadId = null
);

public record GetStackTraceRequest(
    [property: JsonPropertyName("threadId")] int? ThreadId = null
);

public record GetVariablesRequest(
    [property: JsonPropertyName("variablesReference")] int VariablesReference
);

public record EvaluateRequest(
    [property: JsonPropertyName("expression")] string Expression,
    [property: JsonPropertyName("frameId")] int? FrameId = null
);
