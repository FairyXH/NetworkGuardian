"""Exercise the published EXE through the official Python MCP client, using isolated data."""
import argparse
import asyncio
import json
import os
from pathlib import Path
import socket
import subprocess
import time
import uuid
import urllib.request
import httpx

from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


async def exercise(url, report, next_port):
    async with streamablehttp_client(url, timeout=120, terminate_on_close=False) as (read, write, _):
        async with ClientSession(read, write) as session:
            initialized = await session.initialize()
            tools = (await session.list_tools()).tools
            report["version"] = initialized.serverInfo.version
            report["toolCount"] = len(tools)
            assert len(tools) >= 38

            async def call(name, args=None, error=False):
                result = await session.call_tool(name, args or {})
                if error is not None:
                    assert bool(result.isError) == error, (name, result.content)
                report["calls"].append({"tool": name, "expectedError": error})
                return result.structuredContent or {}

            for name in ["guardian_status", "quality_report", "devices_list", "wifi_list", "config_get",
                         "credentials_list", "learning_status", "learning_question", "learning_options",
                         "debug_logs", "debug_bundle", "debug_model", "debug_decision"]:
                await call(name)
            await call("guardian_refresh")
            await call("guardian_pause", {"paused": True})
            await call("guardian_pause", {"paused": False})
            await call("recovery_reset")
            await call("config_patch", {"patch": {"quality": {"minimumGain": 24}}})
            config = await call("config_get", {"section": "quality"})
            assert config["quality"]["minimumGain"] == 24
            await call("config_patch", {"patch": {"quality": {"typo": True}}}, error=True)
            await call("config_patch", {"patch": {"mcp": {"port": 99999}}}, error=True)
            await call("config_patch", {"patch": {"general": None}}, error=True)
            await call("debug_logs", {"minimumLevel": "warning", "category": "MCP"})
            await call("config_reload")
            fake_ssid = "NG-MCP-QA-" + uuid.uuid4().hex[:10]
            await call("campus_set", {"ssid": fake_ssid, "enabled": True})
            await call("campus_set", {"ssid": fake_ssid, "enabled": False})
            devices = await call("devices_list")
            wlan = next((d for d in devices.get("devices", []) if d["classification"]["category"] == 2
                         and d["record"].get("netCfgInstanceId")), None)
            if wlan:
                await call("wifi_bind", {"ssid": fake_ssid, "adapter": wlan["record"]["netCfgInstanceId"]})
                await call("config_patch", {"patch": {"wifi": {"campusWifiAdapterAssignments": {
                    fake_ssid + "-dictionary": wlan["record"]["netCfgInstanceId"]}}}})
                await call("wifi_bind", {"ssid": fake_ssid + "-dictionary"})
                await call("wifi_bind", {"ssid": fake_ssid})
            scan = await call("wifi_scan", error=None)
            assert scan.get("completed") is True
            await call("connectivity_test")
            await call("wifi_profile_delete", {"profile": fake_ssid})
            await call("config_patch", {"patch": {"offlineCommands": [{"id": "qa-noop", "name": "MCP QA",
                "enabled": True, "kind": "shell", "executablePath": "exit 0", "waitForExit": True,
                "executionTimeoutSeconds": 5, "waitAfterRunSeconds": 0}]}})
            command = await call("command_run", {"id": "qa-noop"})
            assert command["started"] and command["exitCode"] == 0
            question = (await call("learning_question", {"next": True}))["question"]
            assert question and question["options"]
            await call("learning_answer", {"questionId": question["id"], "value": question["options"][0]["value"]})
            status = await call("learning_status")
            assert status["totalAnswers"] == 1
            await call("learning_export")
            await call("learning_delete_answer", {"questionId": question["id"]})
            await call("learning_reset")
            entry = await call("credentials_upsert", {"entry": {"ssid": fake_ssid, "enabled": False,
                                    "kind": 2, "security": 1}, "password": ""})
            await call("credentials_list", {"includeSecrets": True})
            await call("credentials_delete", {"id": entry["id"]})
            await call("credentials_apply", {"id": "qa-missing-entry"}, error=True)
            await call("device_restart", {"deviceId": "qa-missing-device"}, error=True)
            await call("device_set_state", {"deviceId": "qa-missing-device", "state": "kept_disabled"}, error=True)
            await call("command_run", {"id": "qa-missing-command"}, error=True)
            await call("learning_choose", {"key": "qa-missing-option", "remember": True}, error=True)
            await call("debug_clear_logs")
            rebound = await call("config_patch", {"patch": {"mcp": {"port": next_port}}})
            assert rebound["mcp"]["url"] == f"http://127.0.0.1:{next_port}/mcp"
            report["selfRebindResponded"] = True


async def finish_disable(url, report):
    try:
        async with streamablehttp_client(url, timeout=30, terminate_on_close=False) as (read, write, _):
            async with ClientSession(read, write) as session:
                await session.initialize()
                await session.list_tools()
                result = await session.call_tool("config_patch", {"patch": {"mcp": {"enabled": False}}})
                assert not result.isError
                report["selfDisableResponded"] = True
                report["calls"].append({"tool": "config_patch", "reboundPort": True})
    except* httpx.ConnectError:
        # The optional SDK GET/SSE poll may race with an explicitly stopped listener.
        # Only accept this after the POST tool result was actually received.
        if not report.get("selfDisableResponded"):
            raise


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    parser.add_argument("--root", required=True)
    args = parser.parse_args()
    root = Path(args.root).resolve()
    root.mkdir(parents=True, exist_ok=True)
    port = free_port()
    config = {
        "version": 15,
        "general": {"automaticRecovery": False, "manageInterfaceMetrics": False,
                    "ensureRadioOnAtStartup": False, "autoEnableWifiRadio": False,
                    "autoEnableWifiDevices": False, "radioWatchdogEnabled": False},
        "startup": {"startMinimized": True, "closeToTray": False, "runAtLogon": False},
        "quality": {"enabled": False, "learningEnabled": True},
        "mcp": {"enabled": True, "port": port},
    }
    (root / "config.json").write_text(json.dumps(config), encoding="utf-8")
    env = dict(os.environ, NETWORKGUARDIAN_CONFIG_ROOT=str(root),
               NETWORKGUARDIAN_INSTANCE_SUFFIX=".qa-mcp-" + uuid.uuid4().hex)
    report = {"calls": [], "port": port, "bindAddress": "127.0.0.1", "authentication": False}
    with (root / "process.log").open("wb") as log:
        process = subprocess.Popen([str(Path(args.exe).resolve()), "--minimized"], env=env,
                                   stdout=log, stderr=log, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            deadline = time.monotonic() + 90
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError(f"Application exited: {process.returncode}")
                try:
                    with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=1) as response:
                        if response.status == 200:
                            break
                except OSError:
                    time.sleep(0.4)
            else:
                raise TimeoutError("MCP listener did not become available")
            next_port = free_port()
            asyncio.run(exercise(f"http://127.0.0.1:{port}/mcp", report, next_port))
            with socket.socket() as sock:
                assert sock.connect_ex(("127.0.0.1", port)) != 0, "Old listener still active after rebind"
            asyncio.run(finish_disable(f"http://127.0.0.1:{next_port}/mcp", report))
            with socket.socket() as sock:
                assert sock.connect_ex(("127.0.0.1", next_port)) != 0, "Listener still active after disable"
            report["passed"] = True
            (root / "mcp-report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
            print(json.dumps({"passed": True, "toolCount": report["toolCount"], "calls": len(report["calls"]),
                              "version": report["version"], "report": str(root / "mcp-report.json")}, indent=2))
        finally:
            process.terminate()
            process.wait(timeout=15)


if __name__ == "__main__":
    main()
