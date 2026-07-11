import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

interface ToolLayerResponse {
  call_id: number;
  success: boolean;
  result?: unknown;
  error?: string;
}

interface PendingCall {
  resolve: (value: unknown) => void;
  reject: (reason: Error) => void;
  timer: NodeJS.Timeout;
}

const instances = new Map<string, ToolLayerProcess>();
let nextCallId = 1;

class ToolLayerProcess {
  private readonly child: ChildProcessWithoutNullStreams;
  private readonly pending = new Map<number, PendingCall>();
  private stdoutBuffer = '';
  private stderrBuffer = '';

  constructor(readonly workspace: string) {
    const launch = resolveToolLayerLaunch();
    this.child = spawn(launch.command, launch.args, {
      cwd: workspace,
      env: process.env,
      shell: false,
      windowsHide: true
    });
    this.child.stdout.setEncoding('utf8');
    this.child.stderr.setEncoding('utf8');
    this.child.stdout.on('data', (chunk: string) => this.consumeStdout(chunk));
    this.child.stderr.on('data', (chunk: string) => {
      this.stderrBuffer = (this.stderrBuffer + chunk).slice(-8_192);
    });
    this.child.on('error', (error) => this.failAll(error));
    this.child.on('exit', (code, signal) => {
      instances.delete(workspace);
      this.failAll(new Error(
        `TinadecTools exited (${signal ? `signal ${signal}` : `code ${code ?? 'unknown'}`}).${this.stderrSuffix()}`
      ));
    });
  }

  call(toolId: string, params: Record<string, unknown>, approved: boolean, sessionId: string): Promise<unknown> {
    if (this.child.exitCode !== null || this.child.killed) {
      return Promise.reject(new Error('TinadecTools process is not running.'));
    }

    const callId = nextCallId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(callId);
        reject(new Error(`TinadecTools call '${toolId}' timed out.${this.stderrSuffix()}`));
      }, 30_000);
      this.pending.set(callId, { resolve, reject, timer });
      const request = JSON.stringify({
        tool_id: toolId,
        session_id: sessionId,
        toolcall_id: callId,
        approved,
        params
      });
      this.child.stdin.write(`${request}\n`, (error) => {
        if (!error) return;
        const pending = this.pending.get(callId);
        if (!pending) return;
        clearTimeout(pending.timer);
        this.pending.delete(callId);
        pending.reject(error);
      });
    });
  }

  dispose(): Promise<void> {
    if (this.child.exitCode !== null) return Promise.resolve();
    const exited = new Promise<void>((resolve) => this.child.once('exit', () => resolve()));
    this.child.stdin.end();
    this.child.kill();
    return exited;
  }

  terminate(): void {
    this.child.kill();
  }

  private consumeStdout(chunk: string): void {
    this.stdoutBuffer += chunk;
    for (;;) {
      const newline = this.stdoutBuffer.indexOf('\n');
      if (newline < 0) return;
      const line = this.stdoutBuffer.slice(0, newline).trim();
      this.stdoutBuffer = this.stdoutBuffer.slice(newline + 1);
      if (!line) continue;
      this.handleResponseLine(line);
    }
  }

  private handleResponseLine(line: string): void {
    let response: ToolLayerResponse;
    try {
      response = JSON.parse(line) as ToolLayerResponse;
    } catch {
      this.failAll(new Error(`TinadecTools emitted invalid JSON: ${line.slice(0, 200)}`));
      return;
    }

    const pending = this.pending.get(response.call_id);
    if (!pending) return;
    clearTimeout(pending.timer);
    this.pending.delete(response.call_id);
    if (response.success) {
      pending.resolve(response.result);
    } else {
      pending.reject(new Error(response.error ?? String(response.result ?? 'TinadecTools call failed.')));
    }
  }

  private failAll(error: Error): void {
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.pending.clear();
  }

  private stderrSuffix(): string {
    const stderr = this.stderrBuffer.trim();
    return stderr ? ` stderr: ${stderr}` : '';
  }
}

export async function callToolLayer(
  workspace: string,
  toolId: string,
  params: Record<string, unknown>,
  options: { approved?: boolean; sessionId?: string | null } = {}
): Promise<unknown> {
  const resolvedWorkspace = path.resolve(workspace);
  let instance = instances.get(resolvedWorkspace);
  if (!instance) {
    instance = new ToolLayerProcess(resolvedWorkspace);
    instances.set(resolvedWorkspace, instance);
  }
  return instance.call(toolId, params, options.approved === true, options.sessionId ?? 'gateway');
}

export async function disposeToolLayerWorkspace(workspace: string): Promise<void> {
  const resolvedWorkspace = path.resolve(workspace);
  const instance = instances.get(resolvedWorkspace);
  if (!instance) return;
  instances.delete(resolvedWorkspace);
  await instance.dispose();
}

export async function disposeToolLayerProcesses(): Promise<void> {
  const active = [...instances.values()];
  instances.clear();
  await Promise.all(active.map((instance) => instance.dispose()));
}

function resolveToolLayerLaunch(): { command: string; args: string[] } {
  const configured = process.env['TINADEC_TOOLS_BIN']?.trim();
  if (configured) {
    return { command: configured, args: parseConfiguredArgs() };
  }

  const gatewayDirectory = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const repositoryRoot = path.resolve(gatewayDirectory, '..');
  const executable = process.platform === 'win32' ? 'TinadecTools.exe' : 'TinadecTools';
  for (const configuration of ['Debug', 'Release']) {
    const candidate = path.join(repositoryRoot, 'TinadecTools', 'bin', configuration, 'net10.0', executable);
    if (existsSync(candidate)) return { command: candidate, args: [] };
  }

  const dll = path.join(repositoryRoot, 'TinadecTools', 'bin', 'Debug', 'net10.0', 'TinadecTools.dll');
  if (existsSync(dll)) return { command: 'dotnet', args: [dll] };
  throw new Error('TinadecTools executable was not found. Set TINADEC_TOOLS_BIN or build TinadecTools first.');
}

function parseConfiguredArgs(): string[] {
  const value = process.env['TINADEC_TOOLS_ARGS']?.trim();
  if (!value) return [];
  const parsed: unknown = JSON.parse(value);
  if (!Array.isArray(parsed) || !parsed.every((item) => typeof item === 'string')) {
    throw new Error('TINADEC_TOOLS_ARGS must be a JSON string array.');
  }
  return parsed;
}

process.once('exit', () => {
  for (const instance of instances.values()) instance.terminate();
});
