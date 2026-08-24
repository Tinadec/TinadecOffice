---
name: terminal-hygiene
description: 防终端卡顿/反复停顿的强制操作纪律。在任何 bash/pwsh/终端调用可能长时间挂起、前台运行长驻服务、轮询健康检查、或反复起停进程的 TinadecOffice 任务上自动生效。触发词：终端卡住、卡在终端、不要在终端卡住、防卡顿、后台启动、Start-Process、轮询 health、前台命令超时、日志刷屏、进程锁 dll、long-running。
user-invocable: true
---

# terminal-hygiene — 防终端卡顿操作纪律

TinadecOffice 在 Windows/pwsh 下运行，agent 最容易卡死的四类情况及其对策。**规则排序即优先级**，先看规则 1，命中即停。

## 核心铁律

**一次 bash 调用 = 一次能确定结束的操作。** 任何不能保证在调用超时内自己退出的命令，一律不直接跑——改后台 + 带超时的短轮询，或改用纯 HTTP 探活。

## 规则 1：长驻/前台命令永不裸跑

以下命令会**一直挂着不退出**，裸跑必被工具超时强杀，浪费 2 分钟还拿不到输出：

- `npm run dev` / `dev:core` / `dev:gateway` / `dotnet run` / `bun run src/index.ts` / `vite` / 任何服务器
- 任何交互式/监听式命令

**正确做法（后台启动 + 重定向日志 + 短轮询探活）：**

```powershell
# 1) 停掉旧进程（关键：避免 dll/端口锁）
Get-Process dotnet -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -match 'TinadecCore' } | Stop-Process -Force -ErrorAction SilentlyContinue

# 2) 后台启动，日志落盘到 opencode 临时目录（不在终端占屏）
$out = "$env:TEMP\opencode\core.out.log"; $err = "$env:TEMP\opencode\core.err.log"
Remove-Item $out,$err -ErrorAction SilentlyContinue
Start-Process -FilePath "pwsh" -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","scripts/setup-dotnet-env.ps1","run","--project","TinadecCore/Api","--no-build" `
  -RedirectStandardOutput $out -RedirectStandardError $err -WindowStyle Hidden
```

启动命令本身必须**立即返回**（Start-Process 是异步的）。启动后**不要再等**。

## 规则 2：探活 = 单次调用内的短循环，绝不逐条调用

不要把 health 检查拆成一条条 bash 调用（每次等 30-60s 超时，等于反复卡）。**把轮询折叠进一次调用**，超时自断：

```powershell
# 一条调用内最多等 ~40s 探活，中途返回 true 就 break，绝不满轮
$ok = $false
foreach ($i in 1..20) {
  try { $r = Invoke-RestMethod -Uri 'http://127.0.0.1:48731/api/v1/health' -TimeoutSec 2; if ($r.status -eq 'ok') { $ok = $true; break } } catch {}
  Start-Sleep -Milliseconds 900
}
"core=$ok"
```

探到 `true` 立刻进入下一步，**不要**再加"确认它还活着"的第二次轮询。

## 规则 3：验证 = 在一条调用里一次跑完，别拆多步

E2E/功能验证把"创建 → 断言 → 清理"合并进**一条** bash 调用（用 `;` / `try-catch` / 变量传递），一次拿全部结果。绝不为每个 HTTP 请求单独开一次 bash（每次 30-45s，纯浪费）。

```powershell
# 一条调用内：POST 创建 → GET 断言 → DELETE 清理
$p = @{ driver='openai-compatible'; display_name='t'; connection_kind='api-key'; base_url='http://127.0.0.1:9/v1' } | ConvertTo-Json -Compress
$c = Invoke-RestMethod 'http://127.0.0.1:48731/api/v1/model-providers' -Method Post -ContentType 'application/json' -Body $p -TimeoutSec 15
"created=$($c.id)"
$null = Invoke-WebRequest "http://127.0.0.1:48731/api/v1/model-providers/$($c.id)" -Method Delete -TimeoutSec 10 -SkipHttpErrorCheck
"done"
```

## 规则 4：日志刷屏 / 异常被吞时，别翻完整日志

服务日志落到文件后，排查**用 grep 精筛关键词**，不要 `Get-Content` 整段（几 MB 刷屏 = 卡）。开发者异常页可能吞真实根因（内层含不可序列化字段），此时**直接跑相关单测**定位，比翻日志快。

```powershell
# 精筛，绝不整读
Get-Content "$env:TEMP\opencode\core.out.log" | Select-String -Pattern "error|fail:" | Select-Object -First 20
```

## 规则 5：构建/测试用包装脚本，保持前台但给足超时

`.NET` 的 build/test **需要等结果**（前台跑），用 `scripts/setup-dotnet-env.ps1` 包装，timeout 给足（build 60-300s、test 300-400s），一次跑完。不要在 build 后立刻开 dev（会 dll 锁），**build 完先停 dev 或等 dev 退出再起**。

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/setup-dotnet-env.ps1 build TinadecCore/TinadecCore.slnx --no-restore   # timeout ≥ 300s
```

## 本仓库速查

| 动作 | 正确命令 |
|------|---------|
| 后台起 Core | 规则 1 的 Start-Process + `scripts/setup-dotnet-env.ps1 run --project TinadecCore/Api --no-build` |
| 后台起 Gateway | `Start-Process bun run src/index.ts -WorkingDirectory TinadecGateway`（`npm` 用 `npm.cmd`） |
| 探 Core health | 规则 2 的 48731 短循环 |
| 探 Gateway health | 规则 2 的 48730 短循环 |
| build/test | 规则 5 的 setup-dotnet-env.ps1，超时给足 |
| 停 dev 释放 dll/端口 | `Get-Process dotnet,node,bun \| ? { $_.CommandLine -match 'Tinadec\|bun run' } \| Stop-Process -Force` + `Get-NetTCPConnection -LocalPort 48731,48730,5173` |

## 反模式清单（违反即卡）

- ❌ 直接 `npm run dev` / `dotnet run` 裸跑 → 必被工具超时杀，拿不到输出
- ❌ 一条条 bash 调 health → 每条 30-60s，反复卡
- ❌ 每个 HTTP 请求单独开 bash → 纯浪费
- ❌ `Get-Content` 整读几 MB 服务日志 → 刷屏卡死
- ❌ 后台 dev 没停就去 build → dll 被锁，MSB3021 复制失败
- ❌ `dotnet test` 时还挂着 dev Core 实例 → 真实子进程测试抢资源随机失败

## 一句话总结

**能后台就后台，能折叠就折叠，能精筛就精筛，能探活就短循环——永远让一条 bash 调用自己确定地结束。**