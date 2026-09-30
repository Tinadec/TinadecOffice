import json,glob,os,re,collections
meta=json.load(open("meta.json",encoding="utf8"))
ROLES={r[0]:set(r[3]) for r in meta["roles"]}
ORDER=[r[0] for r in meta["roles"]]
ALLTOOLS=set().union(*ROLES.values())
# expected: list of acceptable role-sets per call, in order
EXP={1:[{"search"}],2:[{"global_engineering"}],3:[{"doc_writer"}],4:[{"reviewer"}],5:[{"git_steward"}],
     6:[{"search"}],7:[{"global_engineering"}],8:[{"search","git_steward"}],
     9:[{"search"},{"global_engineering"}],10:[{"global_engineering"},{"git_steward"}],11:[{"reviewer"}],12:[{"doc_writer"}]}
def select_worker(req):
    req=set(t.strip() for t in req if t and t.strip())
    unknown=req-ALLTOOLS
    if unknown: return None,"unknown_tool:"+",".join(sorted(unknown))
    cands=[r for r in ORDER if req<=ROLES[r]]
    if not cands: return None,"worker_unavailable"
    if not req: cands.sort(key=lambda r:(-len(ROLES[r]),ORDER.index(r)))
    else: cands.sort(key=lambda r:(len(ROLES[r])-len(req),ORDER.index(r)))
    return cands[0],"ok"
def score(path):
    cond,model=re.match(r"out_(\w)_(\w+)\.json",os.path.basename(path)).groups()
    inv={v:k for k,v in meta["names"].get(cond,{}).items()}
    try: data=json.load(open(path,encoding="utf8"))
    except json.JSONDecodeError as ex: return cond,model,0,0,14,{"output_json_invalid":str(ex)[:40]},None,[]
    ans={a["scenario"]:a.get("calls",[]) for a in data["answers"]}
    len_ok_counter=[]; calls_ok=calls_total=0; scen_ok=0; errors=collections.Counter(); detail=[]
    for s,exp in EXP.items():
        calls=[c for c in ans.get(s,[]) if c.get("tool")=="task_dispatch"]
        routed=[]
        for c in calls:
            a=c.get("args",{})
            if cond=="A":
                r,why=select_worker(a.get("required_tools") or [])
                if why!="ok": errors[why.split(":")[0]]+=1
                if not (a.get("required_tools")): errors["empty_required_tools"]+=1
            else:
                r=inv.get(a.get("agent"))
                if r is None: errors["invalid_agent_id"]+=1
            routed.append(r)
        ok=len(routed)==len(exp) and all(r in e for r,e in zip(routed,exp))
        # parallel scenario: order-insensitive
        if s==9 and len(routed)==2: ok=sorted(map(str,routed))==sorted(["global_engineering","search"])
        # lenient: expected roles appear in order; extras only read-only prep (search/reviewer)
        it=iter(routed); sub=all(any(r in e for r in it) for e in exp)
        extras=[r for r in routed if r not in set().union(*exp)]
        lenient=ok or (sub and all(r in ("search","reviewer") for r in extras))
        len_ok_counter.append(lenient)
        scen_ok+=ok
        for i,e in enumerate(exp):
            calls_total+=1
            if i<len(routed) and routed[i] in e: calls_ok+=1
        if not ok: detail.append((s,routed))
    fu=None
    if cond!="A":
        c13=ans.get(13,[])
        target=meta["names"][cond]["search"]+"#1"
        fu=any(c.get("tool")=="task_followup" and c.get("args",{}).get("handle")==target for c in c13)
    errors["lenient_scen_ok"]=sum(len_ok_counter)
    return cond,model,scen_ok,calls_ok,calls_total,dict(errors),fu,detail
rows=[score(p) for p in sorted(glob.glob("outputs/out_*.json"))]
print(f"{'cond':4} {'model':7} {'scen':>7} {'calls':>7}  followup  errors")
for cond,model,so,co,ct,err,fu,det in rows:
    print(f"{cond:4} {model:7} {so:>3}/12 {co:>3}/{ct}  {str(fu):8}  {err}")
    for d in det: print("      miss", d)
