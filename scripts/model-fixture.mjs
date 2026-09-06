#!/usr/bin/env node
/**
 * TinadecOffice local model fixture — zero-dependency OpenAI-compatible server.
 *
 * Start (from repo root):
 *   node scripts/model-fixture.mjs
 *   FIXTURE_PORT=48735 node scripts/model-fixture.mjs        # custom port
 *
 * Endpoints:
 *   GET  /v1/models                 → deterministic model list
 *   POST /v1/chat/completions       → deterministic completion (stream or not)
 *   GET  /healthz                   → fixture liveness (used by e2e scripts)
 *
 * Behavior (e2e-local-loop contract):
 *   - Any Authorization header value is accepted; a missing header returns 401.
 *   - Keyword routing over the raw request text (case-insensitive):
 *       "planner"    → valid task-graph JSON array (PlannedTask-compatible)
 *       "executor"   → task completion narrative
 *       "supervisor" → supervision verdict JSON {"decision":"pass",...}
 *       "meeting"    → meeting summary
 *       (none)       → generic acknowledgement
 *   - stream:true → SSE chunks (OpenAI-compatible deltas, id increments, final `data: [DONE]`)
 *   - otherwise   → standard completion JSON
 *
 * Deterministic: identical request bodies yield identical responses (no clock,
 * no randomness beyond an id counter that callers must not depend on).
 */

import http from "node:http";

const FIXTURE_PORT = Number(process.env.FIXTURE_PORT || 48735);
const HOST = "127.0.0.1";

const MODEL = "tinadec-fixture-1";

const PLANNER_RESPONSE = JSON.stringify([
  {
    task_key: "fixture-task-1",
    title: "整理会议纪要并生成 executor 汇报",
    description: "Fixture planner response: one deterministic task for the e2e local loop.",
    success_criteria: ["executor 输出任务完成叙述", "supervisor 给出 pass 判定"],
    dependencies: [],
    required_capabilities: [],
    required_tools: [],
    priority: 1,
    risk: "low"
  }
]);

const EXECUTOR_RESPONSE =
  "executor：任务 fixture-task-1 已完成。已核对成功标准：(1) 任务完成叙述已生成；(2) 证据摘要已就绪，等待 supervisor 判定。";

const SUPERVISOR_RESPONSE = JSON.stringify({
  decision: "pass",
  reasons: ["fixture：执行证据覆盖全部 success_criteria", "无未处理风险"],
  revise_task_indexes: [],
  criteria_verdicts: [
    { task_key: "fixture-task-1", criterion: "executor 输出任务完成叙述", satisfied: true, evidence: "fixture executor 叙述已返回" },
    { task_key: "fixture-task-1", criterion: "supervisor 给出 pass 判定", satisfied: true, evidence: "本判定即证据" }
  ]
});

const MEETING_RESPONSE =
  "meeting 总结：本次会议围绕本地 e2e 闭环展开。planner 给出了单一确定性任务，executor 按时完成并输出叙述，supervisor 评审为 pass。结论：闭环通过，无需返工。";

const GENERIC_RESPONSE = "fixture：收到请求，这是确定性默认响应。";

/**
 * Deterministic role routing.
 *
 * Base rule (documented contract): keyword over the raw request text —
 * planner → executor → supervisor → meeting.
 *
 * Refinement for the Core run engine: every engine call embeds the full agent
 * roster, so all four keywords appear in every request. The engine's per-role
 * instruction markers are therefore checked first so each pipeline stage gets its
 * own deterministic response (the supervisor verdict MUST parse or the run
 * escalates instead of finishing). The generic keyword rule remains as fallback
 * for direct/manual fixture use.
 */
function pickResponse(text) {
  const lower = text.toLowerCase();
  if (lower.includes("监督智能体") || lower.includes("pass/revise/escalate")) return SUPERVISOR_RESPONSE;
  if (lower.includes("任务规划智能体") || lower.includes("仅输出 JSON 数组")) return PLANNER_RESPONSE;
  if (lower.includes("执行层 agent") || lower.includes("只输出完成摘要")) return EXECUTOR_RESPONSE;
  if (lower.includes("interaction kind:") || lower.includes("user-facing response")) return MEETING_RESPONSE;
  if (lower.includes("planner")) return PLANNER_RESPONSE;
  if (lower.includes("executor")) return EXECUTOR_RESPONSE;
  if (lower.includes("supervisor")) return SUPERVISOR_RESPONSE;
  if (lower.includes("meeting")) return MEETING_RESPONSE;
  return GENERIC_RESPONSE;
}

let nextId = 0;

function completionPayload(body, requestId) {
  const promptText = JSON.stringify(body);
  const content = pickResponse(promptText);
  const model = (body && typeof body.model === "string" && body.model) || MODEL;
  const promptTokens = Math.max(1, Math.ceil(promptText.length / 4));
  const completionTokens = Math.max(1, Math.ceil(content.length / 4));
  const created = 1700000000; // deterministic, not wall-clock
  return {
    id: `chatcmpl-fixture-${requestId}`,
    object: "chat.completion",
    created,
    model,
    choices: [
      {
        index: 0,
        message: { role: "assistant", content },
        finish_reason: "stop",
        logprobs: null
      }
    ],
    usage: {
      prompt_tokens: promptTokens,
      completion_tokens: completionTokens,
      total_tokens: promptTokens + completionTokens
    }
  };
}

function chunkPayload(body, requestId, index, deltaContent) {
  const model = (body && typeof body.model === "string" && body.model) || MODEL;
  return {
    id: `chatcmpl-fixture-${requestId}`,
    object: "chat.completion.chunk",
    created: 1700000000,
    model,
    choices: [
      {
        index: 0,
        delta: deltaContent === null ? {} : { role: index === 0 ? "assistant" : undefined, content: deltaContent },
        finish_reason: deltaContent === null ? "stop" : null,
        logprobs: null
      }
    ]
  };
}

function sendJson(res, status, payload) {
  const body = JSON.stringify(payload);
  res.writeHead(status, {
    "Content-Type": "application/json",
    "Content-Length": Buffer.byteLength(body)
  });
  res.end(body);
}

function handleChatCompletions(req, res, rawBody) {
  let body;
  try {
    body = rawBody.length > 0 ? JSON.parse(rawBody.toString("utf8")) : {};
  } catch {
    sendJson(res, 400, { error: { message: "invalid json body", type: "invalid_request_error" } });
    return;
  }
  const requestId = ++nextId;
  const stream = body && body.stream === true;

  if (!stream) {
    sendJson(res, 200, completionPayload(body, requestId));
    return;
  }

  // SSE mode: role chunk → content deltas → finish chunk → data: [DONE]
  // FIXTURE_STEP_DELAY_MS stretches each delta so e2e restart tests get a
  // reproducible mid-run window.
  const payload = completionPayload(body, requestId);
  const content = payload.choices[0].message.content;
  const model = (body && typeof body.model === "string" && body.model) || MODEL;
  const created = payload.created;
  res.writeHead(200, {
    "Content-Type": "text/event-stream; charset=utf-8",
    "Cache-Control": "no-cache",
    Connection: "keep-alive"
  });
  const stepDelay = Number(process.env.FIXTURE_STEP_DELAY_MS || 0);
  const chunkIds = [];
  const emit = (payloadChunk) => {
    res.write(`data: ${JSON.stringify(payloadChunk)}\n\n`);
  };
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const step = Math.max(8, Math.ceil(content.length / 4));
  for (let offset = 0; offset < content.length; offset += step) {
    chunkIds.push(content.slice(offset, offset + step));
  }
  (async () => {
    try {
      emit({
        id: `chatcmpl-fixture-${requestId}`,
        object: "chat.completion.chunk",
        created,
        model,
        choices: [{ index: 0, delta: { role: "assistant", content: "" }, finish_reason: null, logprobs: null }]
      });
      for (let index = 0; index < chunkIds.length; index++) {
        if (stepDelay > 0) await sleep(stepDelay);
        emit(chunkPayload(body, requestId, index + 1, chunkIds[index]));
      }
      if (stepDelay > 0) await sleep(stepDelay);
      emit(chunkPayload(body, requestId, chunkIds.length + 1, null));
      res.write("data: [DONE]\n\n");
      res.end();
    } catch (error) {
      try { res.destroy(); } catch { /* already gone */ }
    }
  })();
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url, `http://${HOST}:${FIXTURE_PORT}`);
  const route = `${req.method} ${url.pathname}`;

  if (route === "GET /healthz") {
    sendJson(res, 200, { status: "ok", fixture: "tinadec-model-fixture", model: MODEL });
    return;
  }

  if (route === "GET /v1/models") {
    sendJson(res, 200, {
      object: "list",
      data: [
        { id: MODEL, object: "model", created: 1700000000, owned_by: "tinadec-fixture" },
        { id: "gpt-4o-mini", object: "model", created: 1700000000, owned_by: "tinadec-fixture" }
      ]
    });
    return;
  }

  if (route === "POST /v1/chat/completions") {
    // e2e contract: Authorization header must exist; any key value is accepted.
    const authorization = req.headers.authorization;
    if (!authorization || String(authorization).trim() === "") {
      sendJson(res, 401, { error: { message: "missing Authorization header", type: "invalid_request_error" } });
      return;
    }
    const chunks = [];
    req.on("data", (piece) => chunks.push(piece));
    req.on("end", () => {
      try {
        handleChatCompletions(req, res, Buffer.concat(chunks));
      } catch (error) {
        sendJson(res, 500, { error: { message: String((error && error.message) || error), type: "fixture_error" } });
      }
    });
    req.on("error", () => {
      try { res.destroy(); } catch { /* already gone */ }
    });
    return;
  }

  sendJson(res, 404, { error: { message: `fixture has no route for ${route}`, type: "not_found" } });
});

server.listen(FIXTURE_PORT, HOST, () => {
  process.stdout.write(`[model-fixture] listening on http://${HOST}:${FIXTURE_PORT}/v1 (model: ${MODEL})\n`);
});

server.on("error", (error) => {
  process.stderr.write(`[model-fixture] failed to bind ${HOST}:${FIXTURE_PORT}: ${error.message}\n`);
  process.exit(1);
});

process.on("SIGINT", () => server.close(() => process.exit(0)));
process.on("SIGTERM", () => server.close(() => process.exit(0)));
