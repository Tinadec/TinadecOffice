"""Which tool turns dominate the largest checkpoint of a run (read-only)."""
import json
import os
import sqlite3
import sys

db_path, run_id = sys.argv[1], sys.argv[2]
data_root = os.path.dirname(db_path)
db = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
ref = db.execute("select content_reference from run_checkpoints where run_id=? order by revision desc limit 1", (run_id,)).fetchone()[0]
path = next(p for p in (ref, os.path.join(data_root, ref), os.path.join(data_root, "content", ref)) if os.path.isfile(p))
doc = json.load(open(path, "rb"))
turns = [(t, task.get("taskKey")) for task in doc["tasks"] for t in task.get("toolTurns", [])]
print("turns:", len(turns))
sizes = sorted(((len(t.get("resultJson") or ""), len(t.get("argumentsJson") or ""), t.get("toolId"), key) for t, key in turns), reverse=True)
for result, args, tool, key in sizes[:8]:
    print(f"result {result/1024:9.1f} KB  args {args/1024:7.1f} KB  {tool:20} task={key}")
print("sum result KB:", round(sum(s[0] for s in sizes) / 1024, 1))
big = max(turns, key=lambda pair: len(pair[0].get("resultJson") or ""))[0]
print("largest result head:", (big.get("resultJson") or "")[:300].replace("\n", " "))
