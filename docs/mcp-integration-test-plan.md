# MCP Server Integration Test Plan

## Step 1: Environment Setup

### 1.1 Build

```bash
# C# debugger engine + host
dotnet build debugger/DnD.slnx

# Test fixtures (debuggee programs)
dotnet build debugger/tests/fixtures/HelloWorld
dotnet build debugger/tests/fixtures/BreakpointTest
dotnet build debugger/tests/fixtures/VariablesTest
dotnet build debugger/tests/fixtures/EvalTest
dotnet build debugger/tests/fixtures/ExceptionTest
dotnet build debugger/tests/fixtures/ExitCodeTest

# TypeScript MCP server
cd mcp && npm run build
```

### 1.2 MCP Server Registration

Create `.mcp.json` in the project root:

```json
{
  "mcpServers": {
    "dnd-debugger": {
      "type": "stdio",
      "command": "node",
      "args": ["E:/dnd/mcp/dist/index.js"],
      "env": {
        "DND_HOST_PATH": "E:/dnd/debugger/src/DnD.Host/bin/Debug/net8.0-windows/DnD.Host.dll"
      }
    }
  }
}
```

### 1.3 Connection Verification

Restart Claude Code and verify the MCP server is recognized:

```
/mcp
```

Confirm that `dnd-debugger` appears with 20 tools listed.

---

## Step 2: Test Scenarios

Each scenario issues instructions in the Claude Code chat and verifies MCP tool call results.

**Important**: Scenarios 2.10 and 2.11 test the `not-started` state and must be run **first** after a fresh MCP server connection (restart Claude Code). Once any `launch` has been called, the state transitions to `exited` after termination, not back to `not-started`.

### 2.1 Basic: Process Launch and Termination

**Purpose**: Verify launch / terminate / output file behavior

**Steps**:
1. `launch` the HelloWorld fixture
   - Expected: `Process exited with code 0` + `Output file: {path}`
   - Expected: Output file path is within the OS temp directory
2. Read the output file using the Read tool
   - Expected: Contains `Hello, World!`

**Verification Points**:
- Output file path is included in the response
- Output file contains stdout content

### 2.2 Breakpoints + Stopping

**Purpose**: Verify setBreakpoint -> launch -> stop -> display format + removeBreakpoint

**Steps**:
1. `setBreakpoint` at BreakpointTest Program.cs:7
   - Expected: `Breakpoint 1 set at ...Program.cs:7 (pending — will activate when code is loaded)`
2. `getBreakpoints` to check BP list
   - Expected: 1 entry with `(pending)` display
3. `launch` BreakpointTest -> `continue`
   - Expected: `Stopped: breakpoint #1` + module name `[BreakpointTest.dll]`
4. `getStackTrace`
   - Expected: Stack frame shows module name `[BreakpointTest.dll]`
5. `getVariables` should include `a`, `b`
6. `removeBreakpoint` with breakpointId=1 (while stopped)
   - Expected: `Breakpoint 1 removed`
7. `getBreakpoints` to verify removal
   - Expected: `No breakpoints set`

**Note**: `removeBreakpoint` must be called while the debugger connection is active (before `terminate`). Calling it after `terminate` will return an error.

### 2.3 Stepping

**Purpose**: Verify stepOver / stepIn / stepOut behavior

**Steps (stepOver)**:
1. Launch VariablesTest -> stops at Debugger.Break()
   - Expected: `Stopped: pause`
2. Execute `stepOver` several times
   - Expected: Each time `Stopped: step` + line number advances
3. Stack trace line numbers should be monotonically increasing
4. `terminate` to exit

**Steps (stepIn)**:
5. `setBreakpoint` at BreakpointTest Program.cs:12 (`var result = Add(1, 2);`)
6. `launch` BreakpointTest -> stops at breakpoint
   - Expected: `Stopped: breakpoint #1` at Program.cs:12
7. Execute `stepIn`
   - Expected: `Stopped: step` at Program.Add (Program.cs:6 or :7) — entered the function
8. `terminate` to exit

### 2.4 Variable Inspection

**Purpose**: Verify getVariables expands local variables + empty scope message

**Steps**:
1. Launch VariablesTest -> stops at Debugger.Break()
2. `getVariables` (variablesReference: 0) to get local variables
   - Expected: `x: int = 42`, `name: string = "hello"`, `pi: double = 3.14`, `flag: bool = true`
3. Array `arr` should have variablesReference > 0
4. `getVariables` with that variablesReference to expand
   - Expected: `[0]: int = 1`, `[1]: int = 2`, `[2]: int = 3`
5. Specify a nonexistent variablesReference (e.g., 9999)
   - Expected: `No variables in the current scope`

### 2.5 Expression Evaluation

**Purpose**: Verify evaluate with various expressions + object type variablesReference

**Steps** (using EvalTest fixture):
1. Launch -> stops at Debugger.Break()
2. Evaluate the following expressions in order:

| Expression | Expected Result |
|---|---|
| `number` | `42 (int)` — no ref |
| `greeting` | `"Hello, World!" (string)` |
| `greeting.Length` | `13 (int)` |
| `obj.ToString()` | `"TestClass(test, 100)" (string)` |
| `obj.Add(10)` | `110 (int)` |
| `number + 1` | `43 (int)` |
| `number > 40` | `true (bool)` |
| `list.Count` | `3 (int)` |
| `"hello"` | `"hello" (string)` |
| `null` | `null (null)` |
| `obj` | `{TestClass} (ref: N)` — N > 0  |
| `list` | `{List<int>} (ref: N)` — N > 0  |
| `nonexistent` | Error (variable not found) |

### 2.6 Error Handling

**Purpose**: Verify error messages for invalid operations

**Steps**:
1. `continue` before launch -> error message
2. `getStackTrace` before launch -> error message
3. `evaluate` before launch -> error message
4. `launch` a nonexistent file -> error message
5. Access a nonexistent member -> error message

**Verification Points**:
- Errors are returned as MCP errors and Claude can recover
- Claude does not crash

### 2.7 Pause (Async Break)

**Purpose**: Verify pause tool behavior

**Steps**:
1. ExitCodeTest terminates immediately via `launch`, so use VariablesTest instead
2. Launch VariablesTest -> stops at Debugger.Break()
3. `continue` to resume execution -> process either exits or stops
4. Alternative: Launch BreakpointTest without BPs -> exits without stopping, so pause cannot be tested

**Alternative Steps (Recommended)**: Use VariablesTest
1. Launch VariablesTest -> stops at Debugger.Break() (reason: pause)
2. `getState` to check state -> `State: stopped (pause)`
3. Call `pause` (already stopped)
   - Expected: `Stopped: pause — Already stopped` + stack position display

**Verification Points**:
- Calling pause while already stopped does not produce an error
- Stop response contains `pause` reason

### 2.8 getThreads (Thread List)

**Purpose**: Verify getThreads tool behavior

**Steps**:
1. Launch VariablesTest -> stops at Debugger.Break()
2. Call `getThreads`
   - Expected: `Threads (* = current):` + at least 1 thread displayed
   - Expected: Current thread has `*` marker
   - Expected: Thread ID is a positive integer

**Verification Points**:
- Current thread is identified by `*`
- Thread ID matches the threadId from the stopped notification

### 2.9 getException (Exception Information Retrieval)

**Purpose**: Verify getException tool behavior

**Steps** (using ExceptionTest fixture):
1. Launch ExceptionTest with no arguments (default: null-ref)
   - Expected: `Stopped: exception` + exception description
2. Call `getException`
   - Expected: `NullReferenceException` type name
   - Expected: Message is included
3. `terminate` to exit

4. Launch ExceptionTest with arguments `["custom"]`
   - Expected: `Stopped: exception`
5. Call `getException`
   - Expected: `InvalidOperationException: Something went wrong`
6. `terminate` to exit

**Verification Points**:
- Exception type name is correct
- Message matches the original exception message
- Calling getException when no exception is present produces an error (not in exception-stopped state)

### 2.10 getState (Debugger State Query)

**Purpose**: Verify getState tool behavior

**Prerequisite**: This scenario must be run **first** after a fresh MCP server connection to test the `not-started` state. Once any `launch` has been called, the state after `terminate` is `exited`, not `not-started`.

**Steps**:
1. Call `getState` before any launch (fresh MCP server session)
   - Expected: `State: not-started`
2. Launch VariablesTest -> stops at Debugger.Break()
3. Call `getState`
   - Expected: `State: stopped (pause)` + threadId + output file path
4. `continue` to resume -> wait for process exit
5. Call `getState`
   - Expected: `State: exited (exit code 0)`

**Verification Points**:
- Each state (not-started / stopped / exited) transitions correctly
- Stop state includes stop reason and thread ID
- Exit state includes exit code
- Output file path is displayed

### 2.11 waitForStop (Blocking Wait for Stop/Exit)

**Purpose**: Verify waitForStop tool behavior across all states

**Prerequisite**: Step 1 must be run **before any `launch`** in the MCP server session (fresh connection). After a process has exited, `waitForStop` returns exit info instead of an error.

**Steps**:
1. Call `waitForStop` before any launch (fresh MCP server session)
   - Expected: Error — `No process running. Call launch or attach first.`
2. Launch VariablesTest -> stops at Debugger.Break()
3. Call `waitForStop` (already stopped)
   - Expected: `Stopped: pause — Debugger.Break()` + stack position (immediate return, no wait)
4. `continue` to resume
5. Call `waitForStop` with timeout=5000
   - Expected: `Process exited with code 0` (process already exited, immediate return)
6. Call `getState`
   - Expected: `State: exited (exit code 0)`

**Additional Scenario — Timeout**:
1. Launch BreakpointTest with BP at Program.cs:7
2. `continue` -> stops at breakpoint
3. Call `waitForStop` (already stopped)
   - Expected: Immediate return with `Stopped: breakpoint #1`
4. `continue` -> process exits
5. New session: Launch BreakpointTest with BP at Program.cs:7
6. `continue` -> stops at breakpoint
7. Do NOT continue. Call `waitForStop` again.
   - Expected: Immediate return (already stopped)

**Verification Points**:
- Returns error when no process is running (not-started state)
- Returns immediately when already stopped (does not block)
- Returns immediately when already exited (does not block)
- Returns correct stop reason and stack position when stopped
- Returns correct exit code when exited

### 2.12 Output File

**Purpose**: Verify program output is written to file

**Steps**:
1. Launch VariablesTest -> stops at Debugger.Break()
2. `getState` to check output file path
3. `continue` to let process exit
4. Read the output file using the Read tool
   - Expected: Contains `42 hello 3.14 true` (Console.WriteLine output from VariablesTest)

**Verification Points**:
- Output file path is included in the launch response
- File contains stdout content
- File is readable during debugging (while stopped) (FILE_SHARE_READ)

### 2.13 Display Format Verification

**Purpose**: Batch verification of text display improvements

**Steps**: Verify the following during execution of the above scenarios (no separate execution needed)

| # | Item | Verification Method | Expected Display |
|---|------|---------------------|-----------------|
| 1 | Step description | Tool list (`/mcp`) | stepIn: "If the current line has no function call, behaves like stepOver" |
| 2 | getVariables description | Tool list (`/mcp`) | "pass the (ref: N) number shown in the output as variablesReference" |
| 3 | Unverified BP display | Scenario 2.2 step 1 | `(pending — will activate when code is loaded)` |
| 4 | Empty scope display | Scenario 2.4 step 5 | `No variables in the current scope` |
| 5 | Module name display | Scenario 2.2 steps 3-4 | `[BreakpointTest.dll]` |
| 6 | Evaluate ref display | Scenario 2.5 (obj, list) | `(ref: N)` — N > 0 |
| 7 | breakpointId display | Scenario 2.2 step 3 | `breakpoint #1` |

### 2.14 Full Debug Session (E2E)

**Purpose**: Execute a complete debugging workflow end-to-end

**Steps**: Give Claude the following instruction:

> Debug the EvalTest fixture.
> Set breakpoints, inspect variables, and evaluate expressions.

Verify that Claude autonomously performs:
1. Launch process with launch
2. Set BP at appropriate location with setBreakpoint
3. Run to BP with continue
4. Check stack with getStackTrace
5. Inspect local variables with getVariables
6. Evaluate expressions with evaluate
7. Terminate process with terminate

### 2.15 $exception Pseudo-Variable

**Purpose**: Access exceptions via the `$exception` pseudo-variable when stopped on exception

**Steps** (using ExceptionTest fixture, arguments `["custom"]`):
1. `launch` -> stops on exception
2. `getVariables(0)` to get local variables
   - Expected: `$exception` is included in the variable list (type: `System.InvalidOperationException`)
3. Call `evaluate("$exception")`
   - Expected: `{System.InvalidOperationException} (System.InvalidOperationException) (ref: N)`
4. Call `evaluate("$exception.Message")`
   - Expected: `"Something went wrong" (string)`
5. Call `evaluate("$exception.StackTrace")`
   - Expected: Stack trace string (including file name and line numbers)
6. `terminate` to exit

**Verification Points**:
- `$exception` appears in getVariables results
- `$exception.Message` can access base class (System.Exception) properties
- `$exception.StackTrace` works via func-eval property access

### 2.16 Exception Debug Session (E2E)

**Purpose**: Exception debugging workflow

**Steps**: Give Claude the following instruction:

> Launch the ExceptionTest fixture with argument "custom" and investigate the cause of the exception.

Verify that Claude autonomously performs:
1. Launch ExceptionTest with launch
2. Stops on exception -> stop reason includes exception information
3. Get exception details with getException
4. Check stack with getStackTrace
5. Inspect state with getVariables / evaluate
6. Terminate process with terminate

### 2.17 Conditional Breakpoints

**Purpose**: Verify conditional BP behavior with the condition parameter

**Steps** (using EvalTest fixture):
1. `setBreakpoint` at Program.cs:14 with condition `number > 40`
   - Expected: `[condition: number > 40]` display
2. `launch` -> stops at Debugger.Break()
3. `continue` to run
   - Expected: `Stopped: breakpoint #1` — number=42 so condition is true, stops
4. `terminate` to exit
5. New session: Set BP at same location with condition `number > 100`
6. `launch` -> `continue`
   - Expected: Condition is false -> auto-continue -> process exits normally

### 2.18 Hit Count Breakpoints

**Purpose**: Verify hit count BP behavior with the hitCount parameter

**Steps** (using EvalTest fixture):
1. `setBreakpoint` at Program.cs:14 with hitCount=1
   - Expected: `[hitCount: 1]` display
2. `launch` -> `continue` -> stops on first hit
3. `terminate` to exit
4. New session: Set BP at same location with hitCount=2
5. `launch` -> `continue`
   - Expected: Only hit once so auto-continue -> process exits normally

### 2.19 Exception Breakpoints

**Purpose**: Verify exception filtering with setExceptionBreakpoints

**Steps** (using ExceptionTest fixture, arguments `["custom"]`):
1. `setExceptionBreakpoints` with thrown=true, uncaught=true, types=["ArgumentException"]
   - Expected: `Type filter: ArgumentException` display
2. `launch` ExceptionTest
   - Expected: InvalidOperationException doesn't match the filter, so it's skipped -> process exits
3. New session: Set filter with types=["InvalidOperation"]
4. `launch` ExceptionTest
   - Expected: `Stopped: exception — System.InvalidOperationException: Something went wrong`
5. `terminate` to exit

---

## Step 3: Verification Checklist

| # | Category | Verification Item |
|---|----------|------------------|
| 1 | Connection | `dnd-debugger` appears in `/mcp` |
| 2 | Connection | 20 tools are recognized |
| 3 | Launch | launch returns output file path |
| 4 | Terminate | terminate completes successfully |
| 5 | BP | setBreakpoint -> `(pending — will activate when code is loaded)` |
| 6 | BP | launch + continue -> stops at `breakpoint #N` |
| 7 | BP | getBreakpoints shows `(pending/verified)` |
| 8 | BP | removeBreakpoint deletes BP |
| 9 | Stepping | stepOver advances the line |
| 10 | Stepping | stepIn enters a function |
| 11 | Variables | getVariables displays local variables |
| 12 | Variables | variablesReference expands objects |
| 13 | Variables | Empty scope shows `No variables in the current scope` |
| 14 | Evaluation | Variable name evaluation |
| 15 | Evaluation | Property access (greeting.Length) |
| 16 | Evaluation | Method call (obj.ToString()) |
| 17 | Evaluation | Arithmetic (number + 1) |
| 18 | Evaluation | Literals ("hello", 42, true, null) |
| 19 | Evaluation | Generic types (list.Count) |
| 20 | Evaluation | Object evaluate shows `(ref: N)` |
| 21 | Display | Stop response shows module name `[*.dll]` |
| 22 | Display | getStackTrace shows module name `[*.dll]` |
| 23 | Pause | pause while stopped -> no error, reason: pause |
| 24 | Threads | getThreads shows thread list + `*` marker |
| 25 | Exception | getException retrieves exception type and message |
| 26 | Exception | NullReferenceException type name is correct |
| 27 | Exception | InvalidOperationException + message is correct |
| 28 | State | Before launch: `State: not-started` |
| 29 | State | While stopped: `State: stopped` + reason + threadId |
| 30 | State | After exit: `State: exited` + exit code |
| 31 | waitForStop | Before launch -> error |
| 32 | waitForStop | While stopped -> immediate return with stop info |
| 33 | waitForStop | After exit -> immediate return with exit code |
| 34 | Output | Output file contains stdout content |
| 35 | Output | Output file is readable via Read while stopped |
| 36 | Error | Operations before launch -> error |
| 37 | Error | Nonexistent variable -> error |
| 38 | $exception | $exception appears in getVariables |
| 39 | $exception | evaluate("$exception.Message") retrieves message |
| 40 | $exception | evaluate("$exception.StackTrace") retrieves stack trace |
| 41 | Conditional BP | condition true -> stops |
| 42 | Conditional BP | condition false -> auto-continue -> normal exit |
| 43 | Hit Count | hitCount=1, 1 hit -> stops |
| 44 | Hit Count | hitCount=2, only 1 hit -> auto-continue |
| 45 | Exception BP | thrown+uncaught, no filter -> stops |
| 46 | Exception BP | types=["ArgumentException"], InvalidOp -> skipped |
| 47 | Exception BP | types=["InvalidOperation"], InvalidOp -> stops |
| 48 | E2E | Claude autonomously completes a debug session |
| 49 | E2E | Claude autonomously completes an exception debug session |

---

## Notes

- Test fixtures are built targeting **net10.0**. DnD.Host is built targeting **net8.0-windows**. Be aware of runtime differences.
- The `DND_HOST_PATH` environment variable must specify the absolute path to the host DLL (relative paths would resolve from `mcp/dist/`).
- Windows only (ICorDebug COM API).
- `Launch_NonexistentProgram` takes ~30 seconds due to DbgShim timeout.
