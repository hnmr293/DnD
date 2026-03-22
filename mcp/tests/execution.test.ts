import { describe, it, expect, afterEach } from "vitest";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { DebuggerClient } from "../src/debugger-client.js";
import type { StoppedParams, ExitedParams } from "../src/types/protocol.js";

const __dirname = dirname(fileURLToPath(import.meta.url));

const HOST_PATH =
  process.env.DND_HOST_PATH ??
  resolve(
    __dirname,
    "../../debugger/src/DnD.Host/bin/Debug/net8.0-windows/DnD.Host.dll",
  );

/**
 * Simulates the blocking execution pattern used by MCP tools:
 * Register listeners BEFORE sending the action, then wait for stopped/exited.
 */
function waitForStopOrExit(
  client: DebuggerClient,
): Promise<
  | { type: "stopped"; params: StoppedParams }
  | { type: "exited"; params: ExitedParams }
> {
  return new Promise((resolve, reject) => {
    let resolved = false;
    const cleanup = () => {
      resolved = true;
      client.removeListener("stopped", onStopped);
      client.removeListener("exited", onExited);
    };
    const onStopped = (params: StoppedParams) => {
      if (resolved) return;
      cleanup();
      resolve({ type: "stopped", params });
    };
    const onExited = (params: ExitedParams) => {
      if (resolved) return;
      cleanup();
      resolve({ type: "exited", params });
    };
    client.on("stopped", onStopped);
    client.on("exited", onExited);
    setTimeout(() => {
      if (resolved) return;
      cleanup();
      reject(new Error("Timed out waiting for stop/exit"));
    }, 10_000);
  });
}

describe("Blocking execution pattern (stub mode)", () => {
  let client: DebuggerClient;

  afterEach(async () => {
    if (client) {
      await client.dispose();
    }
  });

  it("continue should block until stopped", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();
    await client.launch({ program: "test.dll" });

    const waitPromise = waitForStopOrExit(client);
    await client.continue({});
    const event = await waitPromise;

    expect(event.type).toBe("stopped");
    if (event.type === "stopped") {
      expect(event.params.reason).toBe("breakpoint");
      expect(event.params.threadId).toBe(1);

      // After stopped, fetch context
      const stack = await client.getStackTrace({
        threadId: event.params.threadId,
      });
      expect(stack.stackFrames.length).toBeGreaterThan(0);

      const vars = await client.getVariables({ variablesReference: 0 });
      expect(vars.variables.length).toBeGreaterThan(0);
    }
  });

  it("stepOver should block until stopped", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();
    await client.launch({ program: "test.dll" });

    const waitPromise = waitForStopOrExit(client);
    await client.stepOver({});
    const event = await waitPromise;

    expect(event.type).toBe("stopped");
    if (event.type === "stopped") {
      expect(event.params.reason).toBe("step");
    }
  });

  it("stepIn should block until stopped", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();
    await client.launch({ program: "test.dll" });

    const waitPromise = waitForStopOrExit(client);
    await client.stepIn({});
    const event = await waitPromise;

    expect(event.type).toBe("stopped");
    if (event.type === "stopped") {
      expect(event.params.reason).toBe("step");
    }
  });

  it("stepOut should block until stopped", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();
    await client.launch({ program: "test.dll" });

    const waitPromise = waitForStopOrExit(client);
    await client.stepOut({});
    const event = await waitPromise;

    expect(event.type).toBe("stopped");
    if (event.type === "stopped") {
      expect(event.params.reason).toBe("step");
    }
  });

  it("continue after stopped should produce another stopped", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();
    await client.launch({ program: "test.dll" });

    // First continue
    const wait1 = waitForStopOrExit(client);
    await client.continue({});
    const event1 = await wait1;
    expect(event1.type).toBe("stopped");

    // Second continue
    const wait2 = waitForStopOrExit(client);
    await client.continue({});
    const event2 = await wait2;
    expect(event2.type).toBe("stopped");
  });

  it("step sequence: stepOver then stepIn", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();
    await client.launch({ program: "test.dll" });

    const wait1 = waitForStopOrExit(client);
    await client.stepOver({});
    const event1 = await wait1;
    expect(event1.type).toBe("stopped");

    const wait2 = waitForStopOrExit(client);
    await client.stepIn({});
    const event2 = await wait2;
    expect(event2.type).toBe("stopped");
  });

  it("full session: launch, step, inspect, terminate", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();
    await client.launch({ program: "test.dll" });

    // Set breakpoint
    const bp = await client.setBreakpoint({ file: "Program.cs", line: 5 });
    expect(bp.breakpoint.verified).toBe(true);

    // Continue until stopped
    const wait1 = waitForStopOrExit(client);
    await client.continue({});
    const event1 = await wait1;
    expect(event1.type).toBe("stopped");

    if (event1.type === "stopped") {
      // Inspect state
      const stack = await client.getStackTrace({
        threadId: event1.params.threadId,
      });
      expect(stack.stackFrames.length).toBeGreaterThan(0);

      const vars = await client.getVariables({ variablesReference: 0 });
      expect(vars.variables.length).toBeGreaterThan(0);

      const evalResult = await client.evaluate({ expression: "x + 1" });
      expect(evalResult.result).toBeDefined();
    }

    // Terminate
    const waitExit = waitForStopOrExit(client);
    await client.terminate();
    const exitEvent = await waitExit;
    expect(exitEvent.type).toBe("exited");
  });

  it("terminate should produce exited event", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();
    await client.launch({ program: "test.dll" });

    const waitPromise = waitForStopOrExit(client);
    await client.terminate();
    const event = await waitPromise;

    expect(event.type).toBe("exited");
    if (event.type === "exited") {
      expect(event.params.exitCode).toBe(0);
    }
  });
});
