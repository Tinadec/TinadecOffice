"""Top-level field size breakdown of the largest checkpoint of one run (read-only)."""
import json
import os
import sqlite3
import sys

db_path, run_id = sys.argv[1], sys.argv[2]
data_root = os.path.dirname(db_path)
db = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
rows = db.execute(
    "select revision, content_reference, content_length, phase from run_checkpoints where run_id=? order by revision",
    (run_id,)).fetchall()
print("revision growth (rev, KB, phase):")
for rev, _, length, phase in rows[:: max(1, len(rows) // 12)] + [rows[-1]]:
    print(f"  {rev:4} {length/1024:8.1f}  {phase}")

ref = rows[-1][1]
candidates = [ref, os.path.join(data_root, ref), os.path.join(data_root, "content", ref)]
path = next((c for c in candidates if os.path.isfile(c)), None)
if path is None:
    print("content reference not resolvable:", ref)
    sys.exit(0)
with open(path, "rb") as f:
    doc = json.load(f)
sizes = sorted(((len(json.dumps(v, ensure_ascii=False)), k) for k, v in doc.items()), reverse=True)
print("\nlargest top-level fields of last checkpoint (KB):")
for size, key in sizes[:10]:
    print(f"  {size/1024:9.1f}  {key}")

def drill(name):
    value = doc.get(name)
    if isinstance(value, list) and value and isinstance(value[0], dict):
        agg = {}
        for item in value:
            for k, v in item.items():
                agg[k] = agg.get(k, 0) + len(json.dumps(v, ensure_ascii=False))
        print(f"\n{name}: {len(value)} items; heaviest item fields (KB):")
        for k, v in sorted(agg.items(), key=lambda x: -x[1])[:8]:
            print(f"  {v/1024:9.1f}  {k}")
    elif isinstance(value, dict):
        print(f"\n{name}: dict with {len(value)} keys; heaviest (KB):")
        for size, k in sorted(((len(json.dumps(v, ensure_ascii=False)), k) for k, v in value.items()), reverse=True)[:8]:
            print(f"  {size/1024:9.1f}  {k}")

for _, key in sizes[:3]:
    drill(key)
