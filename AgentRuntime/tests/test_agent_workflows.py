import unittest

from timenotifier_agent.server import handle_request


class AgentWorkflowTests(unittest.TestCase):
    def test_generates_daily_report_with_fake_model_response(self):
        response = handle_request({
            "id": "1",
            "workflow": "generate_daily_report",
            "payload": {
                "mockModelResponse": "## 今日工作内容简述\n1、完成 LangGraph 编排接入，提升日报生成流程可维护性。",
                "evidence": [{
                    "capturedAt": "2026-07-02T09:00:00",
                    "title": "Agent 改造",
                    "summary": "完成流程编排",
                }],
            },
        })

        self.assertTrue(response["ok"])
        result = response["result"]
        self.assertIn("今日工作内容简述", result["content"])
        self.assertIn("LangGraph 编排", result["dailyText"])

    def test_empty_evidence_returns_no_confirmed_content(self):
        response = handle_request({
            "id": "2",
            "workflow": "generate_daily_report",
            "payload": {
                "evidence": [],
            },
        })

        self.assertTrue(response["ok"])
        self.assertEqual("暂无可确认内容", response["result"]["dailyText"])

    def test_invalid_model_config_returns_structured_error(self):
        response = handle_request({
            "id": "3",
            "workflow": "chat",
            "payload": {
                "message": "帮我生成日报",
                "modelSettings": {
                    "analysis": {
                        "baseUrl": "",
                        "apiKey": "",
                        "model": "",
                    },
                },
            },
        })

        self.assertFalse(response["ok"])
        self.assertEqual("ValueError", response["error"]["type"])
        self.assertIn("API", response["error"]["message"])


if __name__ == "__main__":
    unittest.main()
