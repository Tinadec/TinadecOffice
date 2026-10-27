"""Compare per-row file opens against one open per run file for the same replay (read-only)."""
import itertools
import json
import os
import sqlite3
import sys
import time

db_path = sys.argv[1]
data_root = os.path.dirname(db_path)
db = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)

def event_file(run_id):
    path = os.path.join(data_root, "events", f"{run_id.lower()}.events.jsonl")
    return path if os.path.exists(path) else os.path.join(data_root, "events", f"{run_id}.events.jsonl")

def load(where, args=()):
    return db.execute(
        "select run_id, byte_offset, byte_length from event_index " + where + " order by timestamp, sequence", args).fetchall()

def per_row(rows):
    out = []
    for run_id, offset, length in rows:
        with open(event_file(run_id), "rb") as f:
            f.seek(offset)
            out.append(json.loads(f.read(length)))
    return out

def per_run(rows):
    out = []
    ordered = sorted(rows, key=lambda r: r[0])
    for run_id, group in itertools.groupby(ordered, key=lambda r: r[0]):
        with open(event_file(run_id), "rb") as f:
            for _, offset, length in group:
                f.seek(offset)
                out.append(json.loads(f.read(length)))
    return out

top = db.execute("select session_id from event_index group by session_id order by count(*) desc limit 1").fetchone()[0]
for label, rows in (("largest session", load("where session_id=?", (top,))), ("unfiltered feed", load(""))):
    for name, fn in (("one open per row", per_row), ("one open per run", per_run)):
        start = time.perf_counter()
        fn(rows)
        print(f"{label:16} {name:18} rows={len(rows):5}  {(time.perf_counter()-start)*1000:8.1f} ms")
