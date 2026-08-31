from __future__ import annotations

import json
import re
import urllib.error
import urllib.request
from typing import Any, Literal, TypedDict

from langgraph.graph import END, START, StateGraph


class AgentState(TypedDict, total=False):
    workflow: str
    payload: dict[str, Any]
    events: list[str]
    result: dict[str, Any]
    error: str


def run_workflow(workflow: str, payload: dict[str, Any]) -> dict[str, Any]:
    graph = _build_graph()
    state = graph.invoke({
        "workflow": workflow,
        "payload": payload,
        "events": [],
    })
    if state.get("error"):
        raise ValueError(state["error"])
    return state.get("result", {})


def _build_graph():
    builder = StateGraph(AgentState)
    builder.add_node("validate", _validate)
    builder.add_node("chat", _chat)
    builder.add_node("generate_daily_report", _generate_daily_report)
    builder.add_node("prepare_daily_submission", _prepare_daily_submission)
    builder.add_edge(START, "validate")
    builder.add_conditional_edges(
        "validate",
        _route,
        {
            "chat": "chat",
            "generate_daily_report": "generate_daily_report",
            "prepare_daily_submission": "prepare_daily_submission",
            "error": END,
        },
    )
    builder.add_edge("chat", END)
    builder.add_edge("generate_daily_report", END)
    builder.add_edge("prepare_daily_submission", END)
    return builder.compile()


def _validate(state: AgentState) -> dict[str, Any]:
    workflow = state.get("workflow", "")
    if workflow not in {"chat", "generate_daily_report", "prepare_daily_submission"}:
        return {"error": f"Unknown workflow: {workflow}"}

    payload = state.get("payload") or {}
    if not isinstance(payload, dict):
        return {"error": "Payload must be an object."}

    if not payload.get("mockModelResponse") and workflow == "chat":
        _require_model_settings(payload)

    if not payload.get("mockModelResponse") and workflow in {"generate_daily_report", "prepare_daily_submission"}:
        evidence = payload.get("evidence") or []
        if evidence:
            _require_model_settings(payload)

    return {"events": [*_events(state), f"workflow:{workflow}:validated"]}


def _route(state: AgentState) -> Literal["chat", "generate_daily_report", "prepare_daily_submission", "error"]:
    if state.get("error"):
        return "error"
    workflow = state.get("workflow", "")
    if workflow == "chat":
        return "chat"
    if workflow == "generate_daily_report":
        return "generate_daily_report"
    if workflow == "prepare_daily_submission":
        return "prepare_daily_submission"
    return "error"


def _chat(state: AgentState) -> dict[str, Any]:
    payload = state["payload"]
    message = str(payload.get("message") or "").strip()
    if not message:
        return {"error": "Message cannot be empty."}

    reply = payload.get("mockModelResponse")
    if not reply:
        prompt = f"""
你是 Timenotifier 的工作助手。请用中文简洁回复用户，并且只围绕工作记录、日报、自动化提交、证据整理和配置排查提供帮助。

用户消息：
{message}
""".strip()
        reply = _call_model(payload, prompt)

    return {
        "events": [*_events(state), "workflow:chat:completed"],
        "result": {
            "reply": str(reply).strip(),
        },
    }


def _generate_daily_report(state: AgentState) -> dict[str, Any]:
    result = _create_daily_report_result(state["payload"])
    return {
        "events": [*_events(state), "workflow:generate_daily_report:completed"],
        "result": result,
    }


def _prepare_daily_submission(state: AgentState) -> dict[str, Any]:
    result = _create_daily_report_result(state["payload"])
    result["statusMessage"] = "日报已由 LangGraph 生成，准备填写到页面并等待确认提交。"
    return {
        "events": [*_events(state), "workflow:prepare_daily_submission:completed"],
        "result": result,
    }


def _create_daily_report_result(payload: dict[str, Any]) -> dict[str, Any]:
    evidence = payload.get("evidence") or []
    if not evidence:
        content = "## 今日工作内容简述\n暂无可确认内容"
        return {
            "content": content,
            "dailySummary": "暂无可确认内容",
            "dailyText": "暂无可确认内容",
        }

    content = payload.get("mockModelResponse")
    if not content:
        prompt = str(payload.get("prompt") or _build_daily_prompt(payload, evidence))
        content = _call_model(payload, prompt)

    content = _normalize_daily_markdown(str(content))
    summary = _extract_daily_summary(content)
    return {
        "content": content,
        "dailySummary": summary,
        "dailyText": _to_plain_text(summary),
    }


def _build_daily_prompt(payload: dict[str, Any], evidence: list[dict[str, Any]]) -> str:
    date = str(payload.get("date") or "")
    lines = []
    for index, item in enumerate(evidence, start=1):
        captured_at = item.get("capturedAt") or ""
        title = item.get("title") or ""
        summary = item.get("summary") or ""
        kind = item.get("displayKind") or item.get("kind") or ""
        lines.append(f"[E{index}] {captured_at} {kind} | {title} | {summary}")

    return f"""
你是一个严谨的工作报告助手。请只根据下面的证据生成日报草稿，风格为职场简洁，可直接复制提交。

日期：{date}

要求：
1. 不要编造证据中没有的信息。
2. 只输出一个二级标题：今日工作内容简述。
3. 标题下输出 3-5 条最高价值编号内容，编号格式使用“1、”。
4. 无证据或信息不足时写“暂无可确认内容”。

证据：
{chr(10).join(lines)}
""".strip()


def _call_model(payload: dict[str, Any], prompt: str) -> str:
    provider = ((payload.get("modelSettings") or {}).get("analysis") or {})
    base_url = str(provider.get("baseUrl") or "").strip().rstrip("/")
    api_key = str(provider.get("apiKey") or "").strip()
    model = str(provider.get("model") or "").strip()
    if not base_url or not api_key or not model:
        raise ValueError("请先配置 API 请求地址、API Key 和模型名称。")

    responses_url = _join_endpoint(base_url, "responses")
    responses_body = {
        "model": model,
        "max_output_tokens": 900,
        "input": [{
            "role": "user",
            "content": [{"type": "input_text", "text": prompt}],
        }],
    }
    try:
        return _extract_responses_text(_post_json(responses_url, api_key, responses_body))
    except RuntimeError as responses_error:
        chat_url = _join_endpoint(base_url, "chat/completions")
        chat_body = {
            "model": model,
            "max_tokens": 900,
            "messages": [{
                "role": "user",
                "content": prompt,
            }],
        }
        try:
            return _extract_chat_text(_post_json(chat_url, api_key, chat_body))
        except RuntimeError as chat_error:
            raise RuntimeError(f"Responses 请求失败：{responses_error}；Chat Completions 兜底也失败：{chat_error}") from chat_error


def _post_json(url: str, api_key: str, body: dict[str, Any]) -> dict[str, Any]:
    data = json.dumps(body, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=data,
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
            "Accept": "application/json",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"{exc.code} {exc.reason}: {detail}") from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(str(exc.reason)) from exc


def _join_endpoint(base_url: str, endpoint: str) -> str:
    if base_url.endswith(f"/{endpoint}") or base_url.endswith(f"/v1/{endpoint}"):
        return base_url
    if base_url.endswith("/responses") or base_url.endswith("/chat/completions"):
        base_url = base_url.rsplit("/", 1)[0]
    if not base_url.endswith("/v1"):
        base_url = base_url + "/v1"
    return f"{base_url}/{endpoint}"


def _extract_responses_text(response: dict[str, Any]) -> str:
    output_text = response.get("output_text")
    if isinstance(output_text, str):
        return output_text

    texts: list[str] = []
    for output in response.get("output") or []:
        for content in output.get("content") or []:
            text = content.get("text")
            if isinstance(text, str):
                texts.append(text)
    if texts:
        return "\n".join(texts)
    return json.dumps(response, ensure_ascii=False)


def _extract_chat_text(response: dict[str, Any]) -> str:
    for choice in response.get("choices") or []:
        message = choice.get("message") or {}
        content = message.get("content")
        if isinstance(content, str):
            return content
    return json.dumps(response, ensure_ascii=False)


def _normalize_daily_markdown(content: str) -> str:
    text = content.strip()
    if not text:
        text = "暂无可确认内容"
    if "今日工作内容简述" not in text:
        text = f"## 今日工作内容简述\n{text}"
    if not text.lstrip().startswith("##"):
        text = re.sub(r"^.*?(今日工作内容简述)", r"## \1", text, count=1, flags=re.S)
    return text.strip()


def _extract_daily_summary(content: str) -> str:
    match = re.search(r"##\s*今日工作内容简述\s*(.*)", content, flags=re.S)
    summary = match.group(1) if match else content
    summary = summary.strip()
    return summary or "暂无可确认内容"


def _to_plain_text(markdown: str) -> str:
    text = re.sub(r"^#{1,6}\s*", "", markdown, flags=re.M)
    text = re.sub(r"\*\*(.*?)\*\*", r"\1", text)
    text = re.sub(r"`([^`]*)`", r"\1", text)
    text = re.sub(r"\n{3,}", "\n\n", text)
    return text.strip()


def _require_model_settings(payload: dict[str, Any]) -> None:
    provider = ((payload.get("modelSettings") or {}).get("analysis") or {})
    if not str(provider.get("baseUrl") or "").strip():
        raise ValueError("请先配置 API 请求地址。")
    if not str(provider.get("apiKey") or "").strip():
        raise ValueError("请先配置 API Key。")
    if not str(provider.get("model") or "").strip():
        raise ValueError("请先配置模型名称。")


def _events(state: AgentState) -> list[str]:
    return list(state.get("events") or [])
