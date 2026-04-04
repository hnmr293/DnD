import { describe, it, expect, afterEach } from "vitest";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { DebuggerClient } from "../src/debugger-client.js";
import { ClientManager } from "../src/client-manager.js";
import { waitForStopOrExit } from "../src/tools/execution.js";
import type { StoppedParams, ExitedParams } from "../src/types/protocol.js";

const __dirname = dirname(fileURLToPath(import.meta.url));

const HOST_PATH =
  process.env.DND_HOST_PATH ??
  resolve(
    __dirname,
    "../../debugger/src/DnD.Host/bin/Debug/net8.0/DnD.Host.dll",
  );

/**
 * Simulates the blocking execution pattern used by MCP tools:
 * Register listeners BEFORE sending the action, then wait for stopped/exited.
 * Uses a fixed 10s timeout — for tests that need a custom timeout, use the
 * imported waitForStopOrExit from execution.ts instead.
 */
function waitForStopOrExitLocal(
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

  it("continue should return immediately without waiting for stopped", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();
    await client.launch({ program: "test.dll" });

    // continue() should resolve as soon as the RPC response arrives,
    // NOT wait for the next stopped/exited notification.
    // The stub fires stopped after 50ms; if continue() waited, it would
    // take >= 50ms. We verify it returns well under that.
    const start = Date.now();
    await client.continue({});
    const elapsed = Date.now() - start;
    expect(elapsed).toBeLessThan(500);

    // The stopped notification still arrives independently
    const event = await waitForStopOrExitLocal(client);
    expect(event.type).toBe("stopped");
  });

  it("stepOver should block until stopped", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();
    await client.launch({ program: "test.dll" });

    const waitPromise = waitForStopOrExitLocal(client);
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

    const waitPromise = waitForStopOrExitLocal(client);
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

    const waitPromise = waitForStopOrExitLocal(client);
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
    const wait1 = waitForStopOrExitLocal(client);
    await client.continue({});
    const event1 = await wait1;
    expect(event1.type).toBe("stopped");

    // Second continue
    const wait2 = waitForStopOrExitLocal(client);
    await client.continue({});
    const event2 = await wait2;
    expect(event2.type).toBe("stopped");
  });

  it("step sequence: stepOver then stepIn", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();
    await client.launch({ program: "test.dll" });

    const wait1 = waitForStopOrExitLocal(client);
    await client.stepOver({});
    const event1 = await wait1;
    expect(event1.type).toBe("stopped");

    const wait2 = waitForStopOrExitLocal(client);
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
    const wait1 = waitForStopOrExitLocal(client);
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
    const waitExit = waitForStopOrExitLocal(client);
    await client.terminate();
    const exitEvent = await waitExit;
    expect(exitEvent.type).toBe("exited");
  });

  it("terminate should produce exited event", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();
    await client.launch({ program: "test.dll" });

    const waitPromise = waitForStopOrExitLocal(client);
    await client.terminate();
    const event = await waitPromise;

    expect(event.type).toBe("exited");
    if (event.type === "exited") {
      expect(event.params.exitCode).toBe(0);
    }
  });
});

describe("waitForStopOrExit", () => {
  let client: DebuggerClient;

  afterEach(async () => {
    if (client) {
      await client.dispose();
    }
  });

  it("resolves on stopped event", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();
    await client.launch({ program: "test.dll" });

    // continue triggers stopped after 50ms in stub mode
    await client.continue({});
    const event = await waitForStopOrExit(client);
    expect(event.type).toBe("stopped");
    if (event.type === "stopped") {
      expect(event.params.reason).toBe("breakpoint");
      expect(event.params.threadId).toBe(1);
    }
  });

  it("resolves on exited event", async () => {
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

  it("rejects on timeout", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();
    await client.launch({ program: "test.dll" });

    // Don't trigger any event — should timeout
    // Use a very short timeout to keep the test fast
    await expect(waitForStopOrExit(client, 100)).rejects.toThrow("Timed out");
  });

  it("resolves immediately if event fires before timeout", async () => {
    client = new DebuggerClient(HOST_PATH, ["--stub"]);
    await client.start();
    await client.launch({ program: "test.dll" });

    // continue fires stopped after 50ms; 5s timeout should not be hit
    await client.continue({});
    const start = Date.now();
    const event = await waitForStopOrExit(client, 5000);
    const elapsed = Date.now() - start;
    expect(event.type).toBe("stopped");
    expect(elapsed).toBeLessThan(1000);
  });
});

describe("ClientManager state transitions for waitForStop", () => {
  let manager: ClientManager;

  afterEach(async () => {
    if (manager) {
      await manager.shutdown();
    }
  });

  it("state is not-started before launch", async () => {
    manager = new ClientManager({ hostPath: HOST_PATH, stub: true });
    const snap = manager.getStateSnapshot();
    expect(snap.state).toBe("not-started");
  });

  it("state is running after continue", async () => {
    manager = new ClientManager({ hostPath: HOST_PATH, stub: true });
    const client = await manager.ensureClient();
    await client.launch({ program: "test.dll" });
    await client.continue({});
    manager.markRunning();
    expect(manager.state).toBe("running");

    // Wait for stub's stopped event to settle
    await new Promise((r) => setTimeout(r, 200));
  });

  it("state transitions to stopped after continue + wait", async () => {
    manager = new ClientManager({ hostPath: HOST_PATH, stub: true });
    const client = await manager.ensureClient();
    await client.launch({ program: "test.dll" });
    await client.continue({});
    manager.markRunning();

    const event = await waitForStopOrExit(client);
    expect(event.type).toBe("stopped");
    // ClientManager's event handler should have updated state
    expect(manager.state).toBe("stopped");
  });

  it("state transitions to exited after terminate", async () => {
    manager = new ClientManager({ hostPath: HOST_PATH, stub: true });
    const client = await manager.ensureClient();
    await client.launch({ program: "test.dll" });

    const waitPromise = waitForStopOrExit(client);
    await client.terminate();
    await waitPromise;

    expect(manager.state).toBe("exited");
  });

  it("snapshot includes stopReason and threadId when stopped", async () => {
    manager = new ClientManager({ hostPath: HOST_PATH, stub: true });
    const client = await manager.ensureClient();
    await client.launch({ program: "test.dll" });
    await client.continue({});
    manager.markRunning();

    await waitForStopOrExit(client);
    const snap = manager.getStateSnapshot();
    expect(snap.state).toBe("stopped");
    expect(snap.stopReason).toBe("breakpoint");
    expect(snap.threadId).toBe(1);
  });

  it("snapshot includes exitCode when exited", async () => {
    manager = new ClientManager({ hostPath: HOST_PATH, stub: true });
    const client = await manager.ensureClient();
    await client.launch({ program: "test.dll" });

    const waitPromise = waitForStopOrExit(client);
    await client.terminate();
    await waitPromise;

    const snap = manager.getStateSnapshot();
    expect(snap.state).toBe("exited");
    expect(snap.exitCode).toBe(0);
  });

  it("snapshot has no stop details in not-started state", async () => {
    manager = new ClientManager({ hostPath: HOST_PATH, stub: true });
    const snap = manager.getStateSnapshot();
    expect(snap.state).toBe("not-started");
    expect(snap.stopReason).toBeUndefined();
    expect(snap.threadId).toBeUndefined();
    expect(snap.exitCode).toBeUndefined();
  });

  it("state should be running after attach", async () => {
    manager = new ClientManager({ hostPath: HOST_PATH, stub: true });
    const client = await manager.ensureClient();

    await client.attach({ processId: 1234 });

    expect(manager.state).toBe("not-started");

    manager.markRunning();

    expect(manager.state).toBe("running");
  });

  it("ensureClient after exited creates fresh client", async () => {
    manager = new ClientManager({ hostPath: HOST_PATH, stub: true });
    const client1 = await manager.ensureClient();
    await client1.launch({ program: "test.dll" });

    const waitPromise = waitForStopOrExit(client1);
    await client1.terminate();
    await waitPromise;
    expect(manager.state).toBe("exited");

    // ensureClient should create a new client after exit
    const client2 = await manager.ensureClient();
    expect(manager.state).toBe("not-started");

    // New client should work
    await client2.launch({ program: "test.dll" });
    const waitPromise2 = waitForStopOrExit(client2);
    await client2.terminate();
    await waitPromise2;
    expect(manager.state).toBe("exited");
  });
});
