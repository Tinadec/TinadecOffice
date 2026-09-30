import json
ROLES = [
  # key, duty, boundary, tools
  ("search", "在工作区与公开网页检索、取证，产出带出处的证据与事实。", "只读：不能写文件、不能执行命令、不能提交。", ["ls","stat","read_file","file_search","git_status","git_diff","git_log","web_fetch"]),
  ("global_engineering", "在工作区执行端到端工程修改：改代码/配置、运行命令与测试并自验。", "可写文件、可执行 shell；不负责提交与推送。", ["ls","stat","read_file","file_search","write_file","shell"]),
  ("git_steward", "负责版本控制操作：提交、推送、分支管理。", "只做 git 操作，不改业务文件内容。", ["git_status","git_diff","git_log","git_commit","git_push","git_branch_list"]),
  ("reviewer", "审阅代码或改动，给出风险、缺陷与安全评估。", "只读：只出评审意见，不做修改。", ["read_file","file_search","git_diff"]),
  ("doc_writer", "撰写与更新文档（Markdown、说明、变更记录）。", "只写文档类文件，不改代码、不执行命令。", ["read_file","file_search","write_file"]),
]
NAMES = {
  "B": {r[0]: r[0] for r in ROLES},
  "C": dict(zip([r[0] for r in ROLES], ["小林","阿哲","老周","苏晴","米娅"])),
  "D": dict(zip([r[0] for r in ROLES], ["ag-3f9k","ag-q71m","ag-x0de","ag-8h2p","ag-m5ta"])),
}
SCEN = [
 "在仓库里找出所有引用 useUiePage 的地方，并说明各自用途。",
 "把 apps/desktop/src/lib/uiEngine.ts 里 ensureProductionUie 的英文注释翻译成中文。",
 "为 TinadecUI 新增的 showPage 写一段使用说明，保存为 docs/uie-show-page.md。",
 "审阅当前工作区未提交的改动，列出潜在风险。",
 "把当前改动提交，并推送到远程 main 分支。",
 "读取 https://vuejs.org/guide/extras/vapor.html ，总结 Vapor 模式的限制。",
 "运行 desktop 的单元测试，修复失败的用例。",
 "查一下最近 5 次提交分别改了什么。",
 "两件事可以并行：(a) 统计 apps/desktop/src/styles.css 里还剩多少处 !important；(b) 把 README.md 里的端口号 48730 改成 48740。",
 "补齐 i18n 中缺失的 key，然后把这次修改提交。",
 "对当前分支相对 main 的 diff 做一次安全审查。",
 "更新 CHANGELOG.md，记录这次 UIE 重构。",
]
COMMON = """你在扮演一个双层智能体系统里的【上层协调者】模型。你不亲自执行，只能用工具把子任务派给下层执行者。
请对下面每个用户请求，给出你会发出的工具调用（可以一次发多个；有先后依赖时按顺序列出）。
不要使用你自己环境里的任何工具，也不要读取其它文件；只根据本文件内容作答。
输出：把一个 JSON 对象写入文件 {OUT}（用 Write 工具），格式为
{"answers":[{"scenario":1,"calls":[{"tool":"task_dispatch","args":{...}}]}, ...]}
args 必须严格符合下方工具 schema。写完后只回复 "done"。
"""
def roster_text(cond):
    if cond == "A":
        rows = [{"slug":k,"role":"task_executor","capabilities":["task.dispatch"],"allowed_tools":sorted(t)} for k,_,_,t in ROLES]
        return "Frozen specialist roster (authoritative for this run):\n"+json.dumps(rows,ensure_ascii=False)
    lines=["可派发的执行者（id: 职责 / 边界 / 工具）:"]
    for k,duty,bound,t in ROLES:
        lines.append(f"- {NAMES[cond][k]}: {duty} / {bound} / 工具: {', '.join(t)}")
    return "\n".join(lines)
SCHEMA_A = {"name":"task_dispatch","description":"Dispatch a sub-task to another agent and keep working yourself. Use it when work can be split in parallel or needs a capability you do not hold. Returns as soon as the sub-task is queued; its result appears in the run's evidence, not in this call's return value.",
 "parameters":{"type":"object","properties":{"title":{"type":"string","description":"One sentence describing the sub-task to hand off."},"description":{"type":"string","description":"What the sub-agent must do, with any paths or context it needs."},"success_criteria":{"type":"array","items":{"type":"string"},"description":"Externally verifiable criteria (an observable file, command output or state)."},"required_tools":{"type":"array","items":{"type":"string"},"description":"Tool ids the sub-task needs, verbatim from the frozen roster. Leave empty only when you do not know which tool is needed."},"required_capabilities":{"type":"array","items":{"type":"string"},"description":"Capabilities the sub-task needs, from the frozen roster."}},"required":["title"],"additionalProperties":False}}
def schema_b(cond):
    ids=[NAMES[cond][r[0]] for r in ROLES]
    return {"name":"task_dispatch","description":"把一个子任务派给指定执行者。按职责选择 agent（见执行者列表）。返回该执行者实例的句柄（形如 <agent>#<n>），可用于 task_followup。",
     "parameters":{"type":"object","properties":{"agent":{"type":"string","enum":ids,"description":"执行者 id，取自执行者列表。"},"title":{"type":"string"},"objective":{"type":"string","description":"要完成什么、相关路径与上下文。"},"success_criteria":{"type":"array","items":{"type":"string"}},"depends_on":{"type":"array","items":{"type":"string"},"description":"本次调用中先前派发任务的 title，需要其先完成。"}},"required":["agent","title","objective","success_criteria"],"additionalProperties":False}}
FOLLOW = {"name":"task_followup","description":"对已派发的某个执行者实例追加指令（沿用它已有的上下文）。","parameters":{"type":"object","properties":{"handle":{"type":"string","description":"task_dispatch 返回的实例句柄。"},"instruction":{"type":"string"}},"required":["handle","instruction"]}}
for cond in "ABCD":
    tools=[SCHEMA_A] if cond=="A" else [schema_b(cond),FOLLOW]
    scen=list(SCEN)
    if cond!="A":
        s=NAMES[cond]["search"]; e=NAMES[cond]["global_engineering"]
        scen.append(f"（续前）你之前派发过三个任务，返回句柄分别为 {s}#1（统计 styles.css 的 !important）、{e}#1（改 README 端口）、{s}#2（查最近提交）。现在用户说：统计 !important 那个不完整，让它继续把 apps/desktop/src 下其它 .css 文件也统计进去。")
    body=COMMON+"\n\n## 工具\n"+json.dumps(tools,ensure_ascii=False,indent=1)+"\n\n## "+roster_text(cond)+"\n\n## 用户请求\n"+"\n".join(f"{i+1}. {t}" for i,t in enumerate(scen))
    open(f"prompt_{cond}.md","w",encoding="utf8").write(body)
json.dump({"roles":ROLES,"names":NAMES},open("meta.json","w",encoding="utf8"),ensure_ascii=False)
print("ok")
