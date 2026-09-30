你在扮演一个双层智能体系统里的【上层协调者】模型。你不亲自执行，只能用工具把子任务派给下层执行者。
请对下面每个用户请求，给出你会发出的工具调用（可以一次发多个；有先后依赖时按顺序列出）。
不要使用你自己环境里的任何工具，也不要读取其它文件；只根据本文件内容作答。
输出：把一个 JSON 对象写入文件 {OUT}（用 Write 工具），格式为
{"answers":[{"scenario":1,"calls":[{"tool":"task_dispatch","args":{...}}]}, ...]}
args 必须严格符合下方工具 schema。写完后只回复 "done"。


## 工具
[
 {
  "name": "task_dispatch",
  "description": "Dispatch a sub-task to another agent and keep working yourself. Use it when work can be split in parallel or needs a capability you do not hold. Returns as soon as the sub-task is queued; its result appears in the run's evidence, not in this call's return value.",
  "parameters": {
   "type": "object",
   "properties": {
    "title": {
     "type": "string",
     "description": "One sentence describing the sub-task to hand off."
    },
    "description": {
     "type": "string",
     "description": "What the sub-agent must do, with any paths or context it needs."
    },
    "success_criteria": {
     "type": "array",
     "items": {
      "type": "string"
     },
     "description": "Externally verifiable criteria (an observable file, command output or state)."
    },
    "required_tools": {
     "type": "array",
     "items": {
      "type": "string"
     },
     "description": "Tool ids the sub-task needs, verbatim from the frozen roster. Leave empty only when you do not know which tool is needed."
    },
    "required_capabilities": {
     "type": "array",
     "items": {
      "type": "string"
     },
     "description": "Capabilities the sub-task needs, from the frozen roster."
    }
   },
   "required": [
    "title"
   ],
   "additionalProperties": false
  }
 }
]

## Frozen specialist roster (authoritative for this run):
[{"slug": "search", "role": "task_executor", "capabilities": ["task.dispatch"], "allowed_tools": ["file_search", "git_diff", "git_log", "git_status", "ls", "read_file", "stat", "web_fetch"]}, {"slug": "global_engineering", "role": "task_executor", "capabilities": ["task.dispatch"], "allowed_tools": ["file_search", "ls", "read_file", "shell", "stat", "write_file"]}, {"slug": "git_steward", "role": "task_executor", "capabilities": ["task.dispatch"], "allowed_tools": ["git_branch_list", "git_commit", "git_diff", "git_log", "git_push", "git_status"]}, {"slug": "reviewer", "role": "task_executor", "capabilities": ["task.dispatch"], "allowed_tools": ["file_search", "git_diff", "read_file"]}, {"slug": "doc_writer", "role": "task_executor", "capabilities": ["task.dispatch"], "allowed_tools": ["file_search", "read_file", "write_file"]}]

## 用户请求
1. 在仓库里找出所有引用 useUiePage 的地方，并说明各自用途。
2. 把 apps/desktop/src/lib/uiEngine.ts 里 ensureProductionUie 的英文注释翻译成中文。
3. 为 TinadecUI 新增的 showPage 写一段使用说明，保存为 docs/uie-show-page.md。
4. 审阅当前工作区未提交的改动，列出潜在风险。
5. 把当前改动提交，并推送到远程 main 分支。
6. 读取 https://vuejs.org/guide/extras/vapor.html ，总结 Vapor 模式的限制。
7. 运行 desktop 的单元测试，修复失败的用例。
8. 查一下最近 5 次提交分别改了什么。
9. 两件事可以并行：(a) 统计 apps/desktop/src/styles.css 里还剩多少处 !important；(b) 把 README.md 里的端口号 48730 改成 48740。
10. 补齐 i18n 中缺失的 key，然后把这次修改提交。
11. 对当前分支相对 main 的 diff 做一次安全审查。
12. 更新 CHANGELOG.md，记录这次 UIE 重构。