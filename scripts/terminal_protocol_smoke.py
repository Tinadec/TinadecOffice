"""Smoke test for the TinadTools shell tool / terminal session protocol.

Drives the built TinadecTools host over its line-delimited JSON protocol to verify:
- `shell` one-shot execution returns a session id and captured output
- output streams as `terminal.stdout` wire events before the response
- `long_lived` sessions keep streaming after the call returns (broadcast events)
- `#terminal` status / kill control work
"""

import json
import queue
import subprocess
import sys
import threading
import time
from pathlib import Path

EXE = Path(__file__).resolve().parents[1] / "TinadecTools/bin/Debug/net10.0/TinadecTools.exe"


def main() -> int:
    proc = subprocess.Popen(
        [str(EXE)],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        encoding="utf-8",
        bufsize=1,
    )
    failures: list[str] = []

    # Background collector: keeps every wire event and routes responses by call id.
    events: list[dict] = []
    responses: "queue.Queue[dict]" = queue.Queue()

    def reader() -> None:
        for raw in proc.stdout:
            raw = raw.strip()
            if not raw:
                continue
            try:
                parsed = json.loads(raw)
            except json.JSONDecodeError:
                continue
            if parsed.get("kind") == "event":
                events.append(parsed)
            else:
                responses.put(parsed)

    threading.Thread(target=reader, daemon=True).start()

    def send(tool_id: str, call_id: int, params=None, approved: bool = True) -> None:
        payload = {"tool_id": tool_id, "session_id": "smoke", "toolcall_id": call_id, "approved": approved}
        if params is not None:
            payload["params"] = params
        proc.stdin.write(json.dumps(payload) + "\n")
        proc.stdin.flush()

    def read_until_call(call_id: int, timeout: float = 30.0):
        """Wait for the response for call_id; unrelated responses stay queued."""
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                parsed = responses.get(timeout=0.5)
            except queue.Empty:
                continue
            if parsed.get("call_id") == call_id:
                return parsed, list(events)
        return None, list(events)

    def wait_for_event(name: str, session_id: str, timeout: float = 15.0) -> bool:
        deadline = time.time() + timeout
        while time.time() < deadline:
            if any(
                e["event"] == name and e["payload"].get("terminal_session_id") == session_id
                for e in events
            ):
                return True
            time.sleep(0.2)
        return False

    # 1. one-shot shell
    send("shell", 1, {"command": "echo smoke-hello", "timeout_ms": 20000})
    response, events = read_until_call(1)
    if response is None:
        failures.append("one-shot shell produced no response")
    else:
        result = response.get("result") or {}
        if result.get("Status") != "completed":
            failures.append(f"expected completed, got {result.get('Status')}")
        if "smoke-hello" not in result.get("Stdout", ""):
            failures.append(f"stdout missing command output: {result.get('Stdout')!r}")
        if not result.get("TerminalSessionId"):
            failures.append("response missing terminal_session_id")
        stdout_events = [e for e in events if e["event"] == "terminal.stdout"]
        if not stdout_events:
            failures.append("no terminal.stdout event before the response")
        elif "smoke-hello" not in stdout_events[0]["payload"]["data"]:
            failures.append("terminal.stdout event does not carry the output")
        print(f"one-shot: status={result.get('Status')} exit={result.get('ExitCode')} "
              f"stdout_events={len(stdout_events)}")

    # 2. long-lived session
    send("shell", 2, {"command": "ping -n 6 127.0.0.1", "long_lived": True, "timeout_ms": 60000})
    response, events = read_until_call(2, timeout=40)
    session_id = None
    if response is None:
        failures.append("long-lived shell produced no response")
    else:
        result = response.get("result") or {}
        session_id = result.get("TerminalSessionId")
        if result.get("Status") != "long_lived":
            failures.append(f"expected long_lived, got {result.get('Status')}")
        print(f"long-lived: status={result.get('Status')} sid={session_id}")

    # 3. control: status then kill
    if session_id:
        send("#terminal", 3, {"action": "status"}, approved=False)
        response, _ = read_until_call(3)
        if response is None or response.get("error"):
            failures.append(f"status failed: {response}")
        else:
            sessions = (response.get("result") or {}).get("Sessions") or []
            if not any(s.get("TerminalSessionId") == session_id for s in sessions):
                failures.append("status did not list the long-lived session")
            print(f"status: sessions={len(sessions)}")

        send("#terminal", 4, {"action": "kill", "terminal_session_id": session_id}, approved=False)
        response, _ = read_until_call(4)
        if response is None or response.get("error"):
            failures.append(f"kill failed: {response}")
        elif not (response.get("result") or {}).get("Success"):
            failures.append("kill reported failure")
        else:
            print("kill: ok")

        # The session must end with a terminal.exit broadcast (either from the
        # kill or because the command finished on its own first).
        if not wait_for_event("terminal.exit", session_id):
            failures.append("no terminal.exit broadcast for the long-lived session")
        else:
            print("exit event: ok")

    proc.stdin.close()
    try:
        proc.wait(timeout=10)
    except subprocess.TimeoutExpired:
        proc.kill()

    if failures:
        print("\nFAILURES:", file=sys.stderr)
        for failure in failures:
            print(f"  - {failure}", file=sys.stderr)
        return 1

    print("\nAll terminal protocol smoke checks passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
