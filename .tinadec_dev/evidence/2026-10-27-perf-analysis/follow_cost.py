"""Order-of-magnitude cost of the SSE follow/replay queries on a real scope (read-only).

Python's sqlite3 is a thin C binding, so these numbers are a lower bound for the EF Core path.
"""
import json
import os
import sqlite3
import sys
import time

db_path = sys.argv[1]
data_root = os.path.dirname(db_path)
db = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)

def timed(label, fn, repeat=20):
    best = float("inf")
    result = None
    for _ in range(repeat):
        start = time.perf_counter()
        result = fn()
        best = min(best, time.perf_counter() - start)
    print(f"{label:62} {best*1000:8.2f} ms  rows={len(result)}")
    return result

slim = "select run_id, sequence, timestamp, byte_offset, byte_length from event_index"
timed("follow tick, unfiltered feed (whole event_index)", lambda: db.execute(slim).fetchall())

top_session = db.execute(
    "select session_id, count(*) from event_index group by session_id order by 2 desc limit 1").fetchone()
print(f"largest session: {top_session[0]} with {top_session[1]} events")
timed("follow tick, largest session (all its rows)",
      lambda: db.execute(slim + " where session_id=?", (top_session[0],)).fetchall())

rows = db.execute(
    "select run_id, byte_offset, byte_length from event_index where session_id=? order by timestamp, sequence",
    (top_session[0],)).fetchall()

def materialize_per_row_open():
    out = []
    for run_id, offset, length in rows:
        path = os.path.join(data_root, "events", f"{run_id.lower()}.events.jsonl")
        if not os.path.exists(path):
            path = os.path.join(data_root, "events", f"{run_id}.events.jsonl")
        with open(path, "rb") as f:
            f.seek(offset)
            out.append(json.loads(f.read(length)))
    return out

timed("initial replay of largest session (one open per row)", materialize_per_row_open, repeat=3)
print()
print("follow ticks per second per subscriber: 5 (200 ms poll)")
