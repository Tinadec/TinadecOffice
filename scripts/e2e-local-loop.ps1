# TinadecOffice 端到端本地闭环验收（plan §8 阶段 3 / 阶段 0 验收记录）
#
# 流程：fixture → Core → Gateway → readiness receipt → 模型 provider/路由/探针 →
#       Agent Pack 安装 → 项目/会话 → 交互提交（receipt 断言）→
#       run stream 重放（seq 单调、恰好一个 done）→ 幂等重放 → 最终 projection。
# 全部 API 调用默认经 Gateway（Desktop 唯一入口）走通，验证代理链路的 SSE 与错误映射。
#
# 用法（仓库根目录）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/e2e-local-loop.ps1
# 可选参数：
#   -CoreUrl http://127.0.0.1:48731    Core 地址（默认本机 48731）
#   -GatewayUrl http://127.0.0.1:48730 Gateway 地址（默认本机 48730）
#   -FixturePort 48735                 本地模型 fixture 端口
#   -SkipBuild                         跳过 dotnet 构建（已 build 过时）
#   -DirectCore                        调试用：绕过 Gateway 直连 Core
#
# 验收记录写入 tmp/e2e-local-loop/<时间戳>/acceptance.json。

param(
    [string]$CoreUrl = "http://127.0.0.1:48731",
    [string]$GatewayUrl = "http://127.0.0.1:48730",
    [string]$FixturePort = "48735",
    [switch]$SkipBuild,
    [switch]$DirectCore
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$work = Join-Path $root "tmp\e2e-local-loop\$stamp"
New-Item -ItemType Directory -Force -Path $work | Out-Null

$script:record = [ordered]@{
    started_at   = (Get-Date).ToUniversalTime().ToString("o")
    core_url     = $CoreUrl
    gateway_url  = $GatewayUrl
    via          = if ($DirectCore) { "core-direct (debug)" } else { "gateway" }
    fixture_port = $FixturePort
    work_dir     = $work
    steps        = New-Object System.Collections.ArrayList
}
$script:failed = $false

function Add-Step {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    $entry = [ordered]@{ step = $Name; ok = $Ok; detail = $Detail; at = (Get-Date).ToUniversalTime().ToString("o") }
    [void]$script:record.steps.Add($entry)
    $mark = "PASS"; if (-not $Ok) { $mark = "FAIL"; $script:failed = $true }
    Write-Host "[$mark] $Name :: $Detail"
}

function Wait-HttpOk {
    param([string]$Url, [int]$TimeoutSeconds, [string]$Label)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-RestMethod -Uri $Url -Method Get -TimeoutSec 3 | Out-Null
            return $true
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    Add-Step "等待 $Label" $false "$Url 在 ${TimeoutSeconds}s 内未就绪"
    return $false
}

function Save-Record {
    $record.finished_at = (Get-Date).ToUniversalTime().ToString("o")
    $record.ok = (-not $script:failed)
    $path = Join-Path $work "acceptance.json"
    $record | ConvertTo-Json -Depth 12 | Out-File -FilePath $path -Encoding utf8
    Write-Host "验收记录: $path"
}

# ── 1. 启动本地模型 fixture ────────────────────────────────────────────────
$env:FIXTURE_PORT = $FixturePort
$env:FIXTURE_STEP_DELAY_MS = "300"
$fixtureOut = Join-Path $work "fixture.log"
$fixture = Start-Process -FilePath "node" -ArgumentList "scripts/model-fixture.mjs" `
    -WorkingDirectory $root -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput $fixtureOut -RedirectStandardError (Join-Path $work "fixture.err.log")
try {
    $fixtureReady = Wait-HttpOk -Url "http://127.0.0.1:$FixturePort/healthz" -TimeoutSeconds 20 -Label "模型 fixture"
    if (-not $fixtureReady) { Save-Record; exit 1 }
    Add-Step "模型 fixture 就绪" $true "http://127.0.0.1:$FixturePort/v1"

    # ── 2. 启动 Core（命令行参数指定临时 SQLite 数据目录，不污染开发库） ────
    $coreDll = Join-Path $root "TinadecCore\Api\bin\Debug\net10.0\TinadecCore.Api.dll"
    if (-not (Test-Path $coreDll)) { Add-Step "Core 构建产物" $false "$coreDll 不存在；先运行 npm run build 或 dotnet build"; Save-Record; exit 1 }
    # NOTE: Start-Process joins -ArgumentList without quoting, so every path that
    # may contain a space (repo root, work dir) must be quoted explicitly.
    $coreArgs = @(
        ('"' + $coreDll + '"'),
        "--urls", $CoreUrl,
        ('--TinadecPersistence:Sqlite:DatabasePath="' + (Join-Path $work 'tinadec.db') + '"'),
        ('--TinadecPersistence:DataRoot="' + (Join-Path $work 'data') + '"')
    )
    $core = Start-Process -FilePath "dotnet" -ArgumentList $coreArgs `
        -WorkingDirectory (Join-Path $root "TinadecCore\Api") -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $work "core.log") -RedirectStandardError (Join-Path $work "core.err.log")
    try {
        $coreReady = Wait-HttpOk -Url "$CoreUrl/api/v1/health" -TimeoutSeconds 240 -Label "Core"
        if (-not $coreReady) { Save-Record; exit 1 }
        Add-Step "Core 就绪" $true "$BaseUrl/api/v1/health"

        # ── 2b. 启动 Gateway（Desktop 唯一入口；未配置独立工具运行时 → Core 权威） ──
        $BaseUrl = if ($DirectCore) { $CoreUrl } else { $GatewayUrl }
        $gateway = $null
        if (-not $DirectCore) {
            # Resolve a real Bun executable: npm's bun.ps1/bun.cmd shims are not
            # valid Start-Process targets, while a native install exposes bun.exe.
            $bunExe = "bun"
            $nativeBun = Join-Path $env:USERPROFILE ".bun\bin\bun.exe"
            $npmBun = Join-Path $env:APPDATA "npm\node_modules\bun\bin\bun.exe"
            if (Test-Path $nativeBun) { $bunExe = $nativeBun }
            elseif (Test-Path $npmBun) { $bunExe = $npmBun }
            $gateway = Start-Process -FilePath $bunExe -ArgumentList "src/index.ts" `
                -WorkingDirectory (Join-Path $root "TinadecGateway") -PassThru -WindowStyle Hidden `
                -RedirectStandardOutput (Join-Path $work "gateway.log") -RedirectStandardError (Join-Path $work "gateway.err.log")
            $gatewayReady = Wait-HttpOk -Url "$GatewayUrl/api/v1/health" -TimeoutSeconds 40 -Label "Gateway"
            if (-not $gatewayReady) { Save-Record; exit 1 }
            Add-Step "Gateway 就绪" $true "$GatewayUrl/api/v1/health（经 Gateway 验证代理链路）"
        }
        # ── 3. 统一 readiness receipt ───────────────────────────────────────
        $readiness = Invoke-RestMethod -Uri "$BaseUrl/api/v1/readiness" -Method Get
        $readiness | ConvertTo-Json -Depth 12 | Out-File -FilePath (Join-Path $work "readiness.json") -Encoding utf8
        $ids = ($readiness.items | ForEach-Object { $_.id }) -join ","
        Add-Step "readiness receipt" $true "status=$($readiness.status); items=$ids"

        # ── 4. 模型 provider + chat 路由 + 连通性探针 ────────────────────────
        $providerBody = @{
            driver          = "openai"
            display_name    = "e2e-fixture-provider"
            connection_kind = "api-key"
            base_url        = "http://127.0.0.1:$FixturePort/v1"
            model           = "tinadec-fixture-1"
            api_key         = "fixture-local-secret"
        } | ConvertTo-Json
        $provider = Invoke-RestMethod -Uri "$BaseUrl/api/v1/model-providers" -Method Post `
            -ContentType "application/json; charset=utf-8" -Body $providerBody
        Add-Step "创建模型 provider" $true "id=$($provider.id)"

        $routes = Invoke-RestMethod -Uri "$BaseUrl/api/v1/model-routes" -Method Get
        $chat = $routes | Where-Object { $_.purpose -eq "chat" } | Select-Object -First 1
        $routeHeaders = @{}
        if ($null -ne $chat) { $routeHeaders["If-Match"] = "`"$($chat.revision)`"" }
        $routeBody = @{
            candidates = @(@{ provider_instance_id = $provider.id; model = "tinadec-fixture-1" })
        } | ConvertTo-Json -Depth 6
        Invoke-RestMethod -Uri "$BaseUrl/api/v1/model-routes/chat" -Method Put `
            -ContentType "application/json; charset=utf-8" -Body $routeBody -Headers $routeHeaders | Out-Null
        Add-Step "绑定 chat 路由" $true "provider=$($provider.id) model=tinadec-fixture-1"

        $probe = Invoke-RestMethod -Uri "$BaseUrl/api/v1/model-probe?force=true" -Method Post
        Add-Step "模型连通性探针" ($probe.status -eq "ready") "status=$($probe.status) reason=$($probe.reason)"

        # ── 5. Agent Pack 安装（manifest → 规范化 sha256 → preview → apply） ──
        function Agent([string]$key, [string]$layer, [string]$role, [string[]]$caps, [string[]]$tools, [string]$prompt) {
            return @{ resource_key = $key; slug = $key; display_name = $key; description = $key;
                layer = $layer; role = $role; capabilities = $caps;
                model_strategy = @{ kind = "inherit" }; tool_scope = $tools;
                system_prompt = $prompt; enabled = $true;
                base_prompt_pipeline_ref = "prompt:baseline-prompt" }
        }
        function ModeNode([string]$key, [string]$agent, [string]$layer) {
            return @{ node_key = $key; agent_ref = "agent:$agent"; layer = $layer; label = $agent; config = @{}; position = $null }
        }
        $packId = "tinadec.e2e.local-loop-pack"
        $manifest = [ordered]@{
            api_version   = "tinadec.io/agent-pack/v1alpha1"
            kind          = "AgentPack"
            metadata      = [ordered]@{ pack_id = $packId; owner = "tinadec.e2e"; product_id = "tinadec.e2e";
                name = "E2E Local Loop Pack"; version = "0.1.0" }
            compatibility = [ordered]@{ minimum_core_version = "0.1.0"; required_core_capabilities = @() }
            resources     = [ordered]@{
                agents          = @(
                    (Agent "meeting" "operation" "session_coordinator" @("task.dispatch", "user.respond") @("*") "meeting-system"),
                    (Agent "context_compressor" "operation" "context_maintenance" @("context.patch") @("*") "context-system"),
                    (Agent "skill_recommender" "operation" "capability_advisor" @("tool.search") @("*") "skill-system"),
                    (Agent "supervisor" "operation" "quality_controller" @("supervision.review") @("*") "supervisor-system"),
                    (Agent "evolution" "operation" "experience_curator" @("agent.candidate") @("*") "evolution-system"),
                    (Agent "git_steward" "operation" "git_steward" @("git.review") @() "git-steward-system"),
                    (Agent "task_planner" "execution" "execution_coordinator" @("agent.create_temporary", "task.plan") @("*") "planner-system"),
                    (Agent "worker.general" "execution" "task_executor" @("task.execute") @("*") "worker-system")
                )
                prompt_pipelines = @(
                    @{ resource_key = "baseline-prompt"; slug = "baseline-prompt"; display_name = "Baseline Prompt";
                        description = "E2E prompt."; graph = @{
                            nodes = @(
                                @{ id = "template"; type = "template"; config = @{ content = "e2e-template" } },
                                @{ id = "assemble"; type = "assemble"; config = @{} }
                            )
                            edges = @(@{ source = "template"; target = "assemble" })
                        } }
                )
                modes           = @(
                    @{ resource_key = "default-mode"; slug = "default-mode"; display_name = "Default Mode";
                        description = "E2E mode."
                        nodes = @(
                            (ModeNode "executor-1" "task_planner" "execution"),
                            (ModeNode "executor-2" "worker.general" "execution"),
                            (ModeNode "meeting-1" "meeting" "operation"),
                            (ModeNode "meeting-2" "context_compressor" "operation"),
                            (ModeNode "meeting-3" "skill_recommender" "operation"),
                            (ModeNode "meeting-4" "supervisor" "operation"),
                            (ModeNode "meeting-5" "evolution" "operation"),
                            (ModeNode "meeting-6" "git_steward" "operation")
                        )
                        edges = @(); canvas_layout = @{} }
                )
            }
            activation    = [ordered]@{ workspace_defaults = [ordered]@{
                agent_ref = "agent:meeting"; mode_ref = "mode:default-mode"; prompt_pipeline_ref = "prompt:baseline-prompt" } }
        }
        $manifestPath = Join-Path $work "agent-pack.manifest.json"
        $manifest | ConvertTo-Json -Depth 20 | Out-File -FilePath $manifestPath -Encoding utf8
        # Core 以“键名排序、UTF-8、无空白”的规范形计算 sha256（见 scripts/canonical-digest.mjs）。
        $digest = @(& node (Join-Path $PSScriptRoot "canonical-digest.mjs") $manifestPath 2>$null)[0]
        if ([string]::IsNullOrWhiteSpace($digest) -or $digest.Length -ne 64) {
            Add-Step "Agent Pack 摘要" $false "canonical-digest.mjs 未返回 64 位 sha256（输出: '$digest'）"
            Save-Record; exit 1
        }
        # envelope 需要两种形态：JSON 字符串（preview 请求体）与嵌套对象（apply 请求体）。
        $envelopeObj = @{ manifest = $manifest; integrity = @{ algorithm = "sha256"; digest = $digest } }
        $envelope = $envelopeObj | ConvertTo-Json -Depth 20
        $preview = Invoke-RestMethod -Uri "$BaseUrl/api/v1/agent-packs/install-preview" -Method Post `
            -ContentType "application/json; charset=utf-8" -Body $envelope
        $applyHeaders = @{ "Idempotency-Key" = "e2e-local-loop-pack-install" }
        $applyBody = @{ preview_id = $preview.preview_id; envelope = $envelopeObj } | ConvertTo-Json -Depth 20
        $applied = Invoke-RestMethod -Uri "$BaseUrl/api/v1/agent-packs/$packId" -Method Put `
            -ContentType "application/json; charset=utf-8" -Body $applyBody -Headers $applyHeaders
        Add-Step "Agent Pack 安装" ($applied.status -eq "installed") "pack=$packId status=$($applied.status)"

        # ── 6. 项目 + 会话（工作区目录必须真实存在，admission 会校验） ────────
        $workspaceDir = Join-Path $work "workspace"
        New-Item -ItemType Directory -Force -Path $workspaceDir | Out-Null
        $project = Invoke-RestMethod -Uri "$BaseUrl/api/v1/projects" -Method Post `
            -ContentType "application/json; charset=utf-8" `
            -Body (@{ name = "e2e-local-loop"; path = $workspaceDir } | ConvertTo-Json)
        $session = Invoke-RestMethod -Uri "$BaseUrl/api/v1/sessions" -Method Post `
            -ContentType "application/json; charset=utf-8" `
            -Body (@{ project_id = $project.id; title = "e2e session" } | ConvertTo-Json)
        Add-Step "项目/会话创建" $true "project=$($project.id) session=$($session.id)"

        # ── 7. 交互提交 + receipt 断言 ──────────────────────────────────────
        $clientMessageId = "e2e-$stamp"
        # dispatch_mode 显式携带：Gateway 对 interactions 做前置校验（Core 侧缺省为 queued）。
        $interactionBody = @{ content = "请用 fixture 完成一次本地闭环。"; client_message_id = $clientMessageId; dispatch_mode = "queued" } | ConvertTo-Json
        $interaction = Invoke-RestMethod -Uri "$BaseUrl/api/v1/sessions/$($session.id)/interactions" -Method Post `
            -ContentType "application/json; charset=utf-8" -Body $interactionBody
        $receiptOk = ($null -ne $interaction.interaction_id) -and ($null -ne $interaction.run_id) -and
            ($null -ne $interaction.client_message_id) -and ($null -ne $interaction.context_revision) -and
            ($null -ne $interaction.stream_cursor) -and ($interaction.correlation_id -eq $clientMessageId)
        Add-Step "交互 receipt" $receiptOk "run=$($interaction.run_id) cursor=$($interaction.stream_cursor) rev=$($interaction.context_revision)"
        if (-not $receiptOk) { Save-Record; exit 1 }
        $runId = $interaction.run_id

        # ── 8. 等待运行终态 ─────────────────────────────────────────────────
        $deadline = (Get-Date).AddSeconds(180)
        $runStatus = $null
        while ((Get-Date) -lt $deadline) {
            $orch = Invoke-RestMethod -Uri "$BaseUrl/api/v1/runs/$runId/orchestration" -Method Get
            $runStatus = $orch.run.status
            if ($runStatus -in @("completed", "failed", "cancelled")) { break }
            Start-Sleep -Milliseconds 500
        }
        Add-Step "运行终态" ($runStatus -eq "completed") "status=$runStatus (timeout 180s)"

        # ── 9. run stream 全量重放：seq 单调、恰好一个 done ──────────────────
        $streamPath = Join-Path $work "run-stream.txt"
        & curl.exe -s -N --max-time 60 "$BaseUrl/api/v1/runs/$runId/stream?after_seq=0" | Out-File -FilePath $streamPath -Encoding utf8
        $seqs = @()
        $kinds = @()
        Get-Content $streamPath | ForEach-Object {
            if ($_ -match '^id:\s*(\d+)') { $seqs += [long]$Matches[1] }
            elseif ($_ -match '"kind":"([a-z_]+)"') { $kinds += $Matches[1] }
        }
        $monotonic = $true
        for ($i = 1; $i -lt $seqs.Count; $i++) { if ($seqs[$i] -le $seqs[$i - 1]) { $monotonic = $false; break } }
        $doneCount = ($kinds | Where-Object { $_ -eq "done" }).Count
        Add-Step "run stream 重放" ($monotonic -and $doneCount -eq 1 -and $seqs.Count -gt 0) `
            "events=$($seqs.Count) kinds=$(($kinds | Select-Object -Unique) -join ',') done=$doneCount monotonic=$monotonic"

        # ── 10. 幂等重放：同一 client_message_id 返回同一 run ────────────────
        $replay = Invoke-RestMethod -Uri "$BaseUrl/api/v1/sessions/$($session.id)/interactions" -Method Post `
            -ContentType "application/json; charset=utf-8" -Body $interactionBody
        Add-Step "client_message_id 幂等" ($replay.run_id -eq $runId) "replay run=$($replay.run_id)"

        # ── 11. 最终 projection：一条用户消息 + 一条会议输出 ──────────────────
        $messages = Invoke-RestMethod -Uri "$BaseUrl/api/v1/sessions/$($session.id)/messages" -Method Get
        if ($messages -isnot [array] -and $null -ne $messages.messages) { $messages = $messages.messages }
        $userCount = @($messages | Where-Object { $_.role -eq "user" }).Count
        $assistantCount = @($messages | Where-Object { $_.role -eq "assistant" }).Count
        Add-Step "最终 projection" ($userCount -ge 1 -and $assistantCount -ge 1) "user=$userCount assistant=$assistantCount"
        # ── 12. 写操作 E2E：write_file + 审批 + 恰好一次副作用 ───────────────
        $writeContent = "e2e write_file 副作用验证 $stamp"
        $writeParams = @{ filepath = "e2e-write.txt"; content = $writeContent }
        $actionCreate = @{ project_id = $project.id; tool_id = "write_file"; params = $writeParams; idempotency_key = "e2e-write-$stamp" } | ConvertTo-Json -Depth 6
        $action = Invoke-RestMethod -Uri "$BaseUrl/api/v1/user/tool-actions" -Method Post `
            -ContentType "application/json; charset=utf-8" -Body $actionCreate
        Add-Step "创建 write_file 动作" ($null -ne $action.id) "action=$($action.id) status=$($action.status) requires_approval=$($action.requires_approval)"

        $deadline = (Get-Date).AddSeconds(60)
        $actionStatus = $action.status
        while ((Get-Date) -lt $deadline) {
            if ($actionStatus -in @("completed", "failed", "blocked", "outcome_unknown")) { break }
            # 等待中的人工决策：对 permission 或 action approval 给出 approved。
            if ($actionStatus -in @("awaiting_user", "awaiting_delegate", "awaiting_approval")) {
                $approvalId = if ($actionStatus -eq "awaiting_approval" -and $action.action_approval_id) { $action.action_approval_id } else { $action.permission_request_id }
                if ($approvalId) {
                    try {
                        Invoke-RestMethod -Uri "$BaseUrl/api/v1/approvals/$approvalId/decision" -Method Post `
                            -ContentType "application/json; charset=utf-8" `
                            -Body (@{ decision = "approved"; reason = "e2e local loop" } | ConvertTo-Json) | Out-Null
                    } catch { }
                }
            }
            $action = Invoke-RestMethod -Uri "$BaseUrl/api/v1/user/tool-actions/$($action.id)/resume" -Method Post
            $actionStatus = $action.status
            if ($actionStatus -notin @("completed", "failed", "blocked", "outcome_unknown")) { Start-Sleep -Milliseconds 300 }
        }
        Add-Step "write_file 审批执行" ($actionStatus -eq "completed") "status=$actionStatus"

        $writePath = Join-Path $workspaceDir "e2e-write.txt"
        $fileOk = (Test-Path $writePath) -and ((Get-Content $writePath -Raw -Encoding UTF8) -match [regex]::Escape($writeContent))
        Add-Step "写副作用恰好一次" $fileOk "file=$writePath exists=$(Test-Path $writePath)"

        # 幂等：同 idempotency_key 重放返回同一 action，不产生第二次写。
        $actionReplay = Invoke-RestMethod -Uri "$BaseUrl/api/v1/user/tool-actions" -Method Post `
            -ContentType "application/json; charset=utf-8" -Body $actionCreate
        Add-Step "write 动作幂等" ($actionReplay.id -eq $action.id) "replay action=$($actionReplay.id)"

        # ── 13. 运行中重启恢复：kill Core → 重启 → 引擎续跑至完成 ────────────
        $restartMessageId = "e2e-restart-$stamp"
        $restartBody = @{ content = "重启恢复验证。"; client_message_id = $restartMessageId; dispatch_mode = "queued" } | ConvertTo-Json
        $restartInteraction = Invoke-RestMethod -Uri "$BaseUrl/api/v1/sessions/$($session.id)/interactions" -Method Post `
            -ContentType "application/json; charset=utf-8" -Body $restartBody
        $restartRunId = $restartInteraction.run_id

        # 等待运行进入执行期（非 queued/admission），立即杀掉 Core。
        $killDeadline = (Get-Date).AddSeconds(60)
        $killed = $false
        while ((Get-Date) -lt $killDeadline) {
            $orchNow = Invoke-RestMethod -Uri "$BaseUrl/api/v1/runs/$restartRunId/orchestration" -Method Get
            $statusNow = $orchNow.run.status
            if ($statusNow -in @("understanding", "executing", "replanning", "reviewing")) {
                if ($null -ne $core -and -not $core.HasExited) { Stop-Process -Id $core.Id -Force }
                $killed = $true
                break
            }
            if ($statusNow -in @("completed", "failed", "cancelled")) { break }
            Start-Sleep -Milliseconds 100
        }
        Add-Step "运行中终止 Core" $killed "killed=$killed status_at_kill=$statusNow"
        Start-Sleep -Seconds 3

        # 重启 Core（同一临时库），Gateway 无需重启（按请求代理）。
        $core = Start-Process -FilePath "dotnet" -ArgumentList $coreArgs `
            -WorkingDirectory (Join-Path $root "TinadecCore\Api") -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $work "core-restart.log") -RedirectStandardError (Join-Path $work "core-restart.err.log")
        $restartReady = Wait-HttpOk -Url "$CoreUrl/api/v1/health" -TimeoutSeconds 60 -Label "Core 重启"
        if (-not $restartReady) { Save-Record; exit 1 }
        Add-Step "Core 重启" $true "lease 过期后引擎续跑（awaiting_* 保护与 30s grace 不变）"

        $resumeDeadline = (Get-Date).AddSeconds(300)
        $resumeStatus = $null
        while ((Get-Date) -lt $resumeDeadline) {
            try {
                $orchAfter = Invoke-RestMethod -Uri "$BaseUrl/api/v1/runs/$restartRunId/orchestration" -Method Get
                $resumeStatus = $orchAfter.run.status
                if ($resumeStatus -in @("completed", "failed", "cancelled")) { break }
            } catch { Start-Sleep -Milliseconds 500 }
            Start-Sleep -Milliseconds 500
        }
        Add-Step "重启后续跑完成" ($resumeStatus -eq "completed") "status=$resumeStatus (timeout 300s)"

        # 重放重启 run 的流：恰好一个 done（续跑不重放旧事件、不重复终态）。
        $restartStreamPath = Join-Path $work "restart-stream.txt"
        & curl.exe -s -N --max-time 90 "$BaseUrl/api/v1/runs/$restartRunId/stream?after_seq=0" | Out-File -FilePath $restartStreamPath -Encoding utf8
        $restartKinds = @()
        Get-Content $restartStreamPath | ForEach-Object {
            if ($_ -match '"kind":"([a-z_]+)"') { $restartKinds += $Matches[1] }
        }
        $restartDoneCount = ($restartKinds | Where-Object { $_ -eq "done" }).Count
        Add-Step "重启 run 流一致性" ($restartDoneCount -eq 1) "kinds=$(($restartKinds | Select-Object -Unique) -join ',') done=$restartDoneCount"

        # 消息投影：两次交互各恰好一条用户消息 + 一条会议输出。
        $messages2 = Invoke-RestMethod -Uri "$BaseUrl/api/v1/sessions/$($session.id)/messages" -Method Get
        if ($messages2 -isnot [array] -and $null -ne $messages2.messages) { $messages2 = $messages2.messages }
        $userCount2 = @($messages2 | Where-Object { $_.role -eq "user" }).Count
        $assistantCount2 = @($messages2 | Where-Object { $_.role -eq "assistant" }).Count
        Add-Step "重启后 projection 无重复" ($userCount2 -eq 2 -and $assistantCount2 -eq 2) "user=$userCount2 assistant=$assistantCount2"
    }
    finally {
        if ($null -ne $gateway -and -not $gateway.HasExited) { try { Stop-Process -Id $gateway.Id -Force } catch {} }
        if ($null -ne $core -and -not $core.HasExited) { try { Stop-Process -Id $core.Id -Force } catch {} }
    }
}
finally {
    if ($null -ne $fixture -and -not $fixture.HasExited) { try { Stop-Process -Id $fixture.Id -Force } catch {} }
    Remove-Item Env:FIXTURE_PORT -ErrorAction SilentlyContinue
    Remove-Item Env:FIXTURE_STEP_DELAY_MS -ErrorAction SilentlyContinue
    Remove-Item Env:TINADEC_PERSISTENCE__SQLITE__DATABASEPATH -ErrorAction SilentlyContinue
    Remove-Item Env:TINADEC_PERSISTENCE__DATAROOT -ErrorAction SilentlyContinue
}

Save-Record
if ($script:failed) { exit 1 }
Write-Host "`nE2E 本地闭环验收：全部通过。"
exit 0
