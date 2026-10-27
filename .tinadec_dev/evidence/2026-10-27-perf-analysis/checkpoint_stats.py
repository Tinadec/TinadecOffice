"""Read-only size breakdown of a Tinadec scope database (opens with mode=ro)."""
import sqlite3
import sys

path = sys.argv[1]
db = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
q = lambda sql: db.execute(sql).fetchall()

print("runs:", q("select count(*) from runs")[0][0])
print("event_index rows:", q("select count(*) from event_index")[0][0])
print("run_stream rows:", q("select count(*) from run_stream")[0][0])
print()
print("checkpoints: count, total MB, avg KB, max KB")
print(q("select count(*), round(sum(content_length)/1048576.0,1), round(avg(content_length)/1024.0,1), round(max(content_length)/1024.0,1) from run_checkpoints"))
print()
print("per run: checkpoints, total MB, last KB")
for row in q("""select run_id, count(*), round(sum(content_length)/1048576.0,2), round(max(content_length)/1024.0,1)
                from run_checkpoints group by run_id order by sum(content_length) desc limit 8"""):
    print(row)
print()
print("run_stream: rows by kind")
for row in q("select kind, count(*), round(sum(coalesce(delta_length,0))/1024.0,1) from run_stream group by kind order by 2 desc"):
    print(row)
print()
print("run_stream rows per run (top 5)")
for row in q("select run_id, count(*) from run_stream group by run_id order by 2 desc limit 5"):
    print(row)
