import { describe, it, expect, afterEach } from "vitest";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { DebuggerClient } from "../src/debugger-client.js";
import type { StoppedParams, ExitedParams } from "../src/types/protocol.js";

const __dirname = dirname(fileURLToPath(import.meta.url));

// Resolve DnD.Host.dll — uses DND_HOST_PATH env or default build output
const HOST_PATH = process.env.DND_HOST_PATH
  ?? resolve(__dirname, "../../debugger/src/DnD.Host/bin/Debug/net8.0-windows/DnD.Host.dll");

describe("DebuggerClient (stub mode)", () => {
  let client: DebuggerClient;

  afterEach(async () => {
    if (client) {
      await client.dispose();
    }
  });

  it("should launch and get processId", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();

    const result = await client.launch({ program: "test.dll" });
    expect(result.processId).toBe(12345);
  });

  it("should set a breakpoint", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();

    const result = await client.setBreakpoint({ file: "Program.cs", line: 10 });
    expect(result.breakpoint.verified).toBe(true);
    expect(result.breakpoint.file).toBe("Program.cs");
    expect(result.breakpoint.line).toBe(10);
  });

  it("should get stack trace", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();

    await client.launch({ program: "test.dll" });
    const result = await client.getStackTrace({});
    expect(result.stackFrames.length).toBeGreaterThan(0);
    expect(result.stackFrames[0].name).toBe("Main");
  });

  it("should evaluate expressions", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();

    await client.launch({ program: "test.dll" });
    const result = await client.evaluate({ expression: "x + 1" });
    expect(result.result).toBe("stub:x + 1");
  });

  it("should receive exited notification on terminate", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();

    await client.launch({ program: "test.dll" });

    const exitedPromise = new Promise<ExitedParams>((resolve) => {
      client.on("exited", resolve);
    });

    await client.terminate();
    const exited = await exitedPromise;
    expect(exited.exitCode).toBe(0);
  });

  it("should receive stopped notification on continue", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();

    await client.launch({ program: "test.dll" });

    const stoppedPromise = new Promise<StoppedParams>((resolve) => {
      client.on("stopped", resolve);
    });

    await client.continue({});
    const stopped = await stoppedPromise;
    expect(stopped.reason).toBe("breakpoint");
    expect(stopped.threadId).toBe(1);
  });

  it("should run a full debug session", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();

    // Launch
    const launch = await client.launch({ program: "test.dll" });
    expect(launch.processId).toBe(12345);

    // Set breakpoint
    const bp = await client.setBreakpoint({ file: "Program.cs", line: 10 });
    expect(bp.breakpoint.verified).toBe(true);

    // Evaluate
    const evalResult = await client.evaluate({ expression: "x" });
    expect(evalResult.result).toBe("stub:x");

    // Get variables
    const vars = await client.getVariables({ variablesReference: 0 });
    expect(vars.variables.length).toBeGreaterThan(0);

    // Terminate
    const exitedPromise = new Promise<ExitedParams>((resolve) => {
      client.on("exited", resolve);
    });
    await client.terminate();
    const exited = await exitedPromise;
    expect(exited.exitCode).toBe(0);
  });
});
