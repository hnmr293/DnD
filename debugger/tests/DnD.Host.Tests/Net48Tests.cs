namespace DnD.Host.Tests;

/// <summary>
/// .NET Framework 4.8 variants of the debug session tests.
/// Each class inherits all [Fact] methods from its base class;
/// the only change is TargetFramework → "net48", which makes
/// FindFixture() resolve the net48 EXE instead of the net10.0 DLL.
/// </summary>

[Collection("DebugSession")]
[Trait("Category", "Net48")]
public class Net48DebugSessionTests : DebugSessionTests
{
    protected override string TargetFramework => "net48";
}

[Collection("DebugSession")]
[Trait("Category", "Net48")]
public class Net48EvalTests : EvalTests
{
    protected override string TargetFramework => "net48";
}

[Collection("DebugSession")]
[Trait("Category", "Net48")]
public class Net48ExceptionTests : ExceptionTests
{
    protected override string TargetFramework => "net48";
}

[Collection("DebugSession")]
[Trait("Category", "Net48")]
public class Net48ExitCodeTests : ExitCodeTests
{
    protected override string TargetFramework => "net48";
}

[Collection("DebugSession")]
[Trait("Category", "Net48")]
public class Net48StopAtEntryTests : StopAtEntryTests
{
    protected override string TargetFramework => "net48";
}
