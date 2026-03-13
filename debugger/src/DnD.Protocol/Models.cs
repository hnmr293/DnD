namespace DnD.Protocol;

using System.Text.Json.Serialization;

public record Breakpoint(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("condition")] string? Condition = null,
    [property: JsonPropertyName("hitCount")] int? HitCount = null
);

public record StackFrame(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("file")] string? File = null,
    [property: JsonPropertyName("line")] int? Line = null,
    [property: JsonPropertyName("column")] int? Column = null,
    [property: JsonPropertyName("moduleId")] string? ModuleId = null
);

public record Variable(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("variablesReference")] int VariablesReference,
    [property: JsonPropertyName("type")] string? Type = null
);

public record ThreadInfo(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("current")] bool Current = false
);

public record ExceptionInfo(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message")] string? Message = null
);

public enum StopReason { Breakpoint, Step, Pause, Exception, Entry }

public enum OutputCategory { Stdout, Stderr, Console }
