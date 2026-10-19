"""Contract tests against the real pinned engine, using only a loopback fake upstream.

The tiny WASM below is a test-only solver accepted by our fake upstream. It cannot
solve real challenges and is never bundled or selected by the desktop application.
No credentials or model requests are sent to DeepSeek by this test.
"""
import argparse
import base64
import hashlib
import hmac
import http.client
import http.server
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import tempfile
import threading
import time
import tomllib
import urllib.error
import urllib.request


def leb(n):
    out = bytearray()
    while True:
        b = n & 127
        n >>= 7
        out.append(b | (128 if n else 0))
        if not n:
            return bytes(out)


def name(s):
    b = s.encode()
    return leb(len(b)) + b


def section(i, data):
    return bytes([i]) + leb(len(data)) + data


def fake_wasm():
    types = b"\x03\x60\x01\x7f\x01\x7f\x60\x02\x7f\x7f\x01\x7f\x60\x06" + b"\x7f" * 5 + b"\x7c\x00"
    exports = b"\x04" + name("memory") + b"\x02\x00"
    for i, s in enumerate(["__wbindgen_add_to_stack_pointer", "__wbindgen_malloc", "wasm_solve"]):
        exports += name(s) + b"\x00" + leb(i)
    bodies = [b"\x00\x41\x80\x08\x0b", b"\x00\x41\x80\x10\x0b",
              b"\x00\x20\x00\x41\x01\x36\x02\x00\x20\x00\x44" + b"\x00" * 8 + b"\x39\x03\x08\x0b"]
    return b"\0asm\x01\0\0\0" + section(1, types) + section(3, b"\x03\x00\x01\x02") + section(5, b"\x01\x00\x01") + section(7, exports) + section(10, b"\x03" + b"".join(leb(len(b)) + b for b in bodies))


class FakeDeepSeek(http.server.BaseHTTPRequestHandler):
    prompts = []
    mode = "ok"
    logins = 0
    def log_message(self, *_):
        pass
    def send(self, value, status=200, mime="application/json"):
        data = value if isinstance(value, bytes) else json.dumps(value, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header("Content-Type", mime)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)
    def envelope(self, data):
        self.send({"code": 0, "msg": "", "data": {"biz_code": 0, "biz_msg": "", "biz_data": data}})
    def do_GET(self):
        if self.path == "/fixture.wasm":
            self.send(fake_wasm(), mime="application/wasm")
        elif "fetch_page" in self.path:
            self.envelope({"chat_sessions": [], "has_more": False})
        else:
            self.send({"unexpected": self.path}, 404)
    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))) or b"{}")
        if self.path.endswith("/users/login"):
            assert body["password"] == 'test-quote"\\\n密码🐳', "TOML password did not round-trip"
            assert body["device_id"] == "fixture-device-only-not-real"
            type(self).logins += 1
            self.envelope({"code": 0, "msg": "", "user": {"id": "fixture-user", "token": "fixture-upstream-token", "email": "fixture@example.test", "chat": {"is_muted": 0}}})
        elif self.path.endswith("/check_device"):
            self.envelope({"rotate": None})
        elif self.path.endswith("/chat_session/create"):
            self.envelope({"chat_session": {"id": "fixture-session"}})
        elif self.path.endswith("/chat/create_pow_challenge"):
            self.envelope({"challenge": {"algorithm": "DeepSeekHashV1", "challenge": "fake", "salt": "fake", "signature": "fake", "difficulty": 1, "expire_after": 60, "expire_at": int(time.time()) + 60, "target_path": "/api/v0/chat/completion"}})
        elif self.path.endswith("/chat/completion"):
            type(self).prompts.append(body)
            if type(self).mode == "error":
                self.send({"code": 40301, "msg": "fixture rejected request"})
                return
            frames = [
                'event: ready\ndata: {"request_message_id":1,"response_message_id":2}',
                'event: update_session\ndata: {}',
                'data: ' + json.dumps({"v": {"response": {"status": "WIP", "fragments": [{"type": "THINK", "content": "测试思考"}, {"type": "RESPONSE", "content": "连接"}]}}}, ensure_ascii=False),
                'data: ' + json.dumps({"p": "response/fragments/-1/content", "o": "APPEND", "v": "成功🐳"}, ensure_ascii=False),
                'data: {"p":"response/accumulated_token_usage","o":"SET","v":12}',
                'data: {"p":"response/status","o":"SET","v":"FINISHED"}',
                'event: close\ndata: {}',
            ]
            self.send(("\n\n".join(frames) + "\n\n").encode(), mime="text/event-stream")
        elif self.path.endswith(("/chat_session/delete", "/chat/stop_stream")):
            self.envelope(None)
        else:
            self.send({"unexpected": self.path}, 404)


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--engine", required=True)
    parser.add_argument("--config", required=True)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="deepseek-contract-") as tmp:
        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), FakeDeepSeek)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        port = free_port()
        config = Path(args.config).read_text(encoding="utf-8")
        parsed = tomllib.loads(config)
        assert parsed["ds_core"]["accounts"][0]["password"] == 'test-quote"\\\n密码🐳'
        config = config.replace(f'port = {parsed["server"]["port"]}', f"port = {port}")
        # Override addresses only within this isolated test configuration.
        config = config.replace("[ds_core]\n", f'[ds_core]\napi_base = "http://127.0.0.1:{server.server_port}/api/v0"\nwasm_url = "http://127.0.0.1:{server.server_port}/fixture.wasm"\nhif_enabled = false\n')
        path = Path(tmp) / "config.toml"
        path.write_text(config, encoding="utf-8")
        env = os.environ.copy()
        env.update({"DS_DATA_DIR": tmp, "RUST_LOG": "info", "NO_PROXY": "127.0.0.1,localhost", "no_proxy": "127.0.0.1,localhost"})
        # The test engine only talks to the loopback fake upstream; no policy is bypassed.
        engine = subprocess.Popen([str(Path(args.engine).resolve()), "-c", str(path)], cwd=tmp, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        output = []
        reader = threading.Thread(target=lambda: output.extend(iter(engine.stdout.readline, b"")), daemon=True)
        reader.start()
        count = 0
        def check(value, message):
            nonlocal count
            count += 1
            assert value, message
        def request(route, body=None, key="sk-local-fixture-only-01234567890123456789"):
            connection = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
            headers = {"Content-Type": "application/json"}
            if key is not None:
                headers["Authorization"] = "Bearer " + key
            connection.request("GET" if body is None else "POST", route, None if body is None else json.dumps(body, ensure_ascii=False).encode(), headers)
            response = connection.getresponse()
            result = (response.status, response.read().decode(), dict(response.getheaders()))
            connection.close()
            return result
        try:
            for _ in range(100):
                if engine.poll() is not None:
                    raise RuntimeError("Engine exited:\n" + b"".join(output).decode(errors="replace"))
                try:
                    if request("/health")[0] == 200:
                        break
                except OSError:
                    pass
                time.sleep(.1)
            else:
                raise RuntimeError("Engine readiness timeout")
            status, raw, _ = request("/v1/models")
            models = json.loads(raw)["data"]
            check(status == 200 and any(m["id"] == "deepseek-default" for m in models), "Real model endpoint must advertise default")
            check(FakeDeepSeek.logins == 1 and len(FakeDeepSeek.prompts) == 1, "Pinned engine startup sends exactly one health-check completion")
            before = len(FakeDeepSeek.prompts)
            request("/v1/models")
            check(len(FakeDeepSeek.prompts) == before, "Local model probe must not invoke upstream completions")
            check(request("/v1/models", key=None)[0] == 401, "Missing API key must be rejected")
            check(request("/v1/models", key="wrong")[0] == 401, "Wrong API key must be rejected")
            check(request("/admin/api/status")[0] == 401, "Client API key must not grant admin access")
            check(request("/admin/api/setup", {"password": "not-allowed"})[0] >= 400, "Web setup must already be disabled")
            check(request("/admin/api/login", {"password": "!desktop-managed-disabled"})[0] >= 400, "Sentinel hash must not allow web login")
            def b64(data):
                return base64.urlsafe_b64encode(data).rstrip(b"=").decode()
            header = b64(b'{"alg":"HS256","typ":"JWT"}')
            payload = b64(json.dumps({"sub": "admin", "iat": int(time.time()), "exp": int(time.time()) + 3600}).encode())
            message = header + "." + payload
            token = message + "." + b64(hmac.new(b"fixture-admin-secret", message.encode(), hashlib.sha256).digest())
            check(request("/admin/api/status", key=token)[0] == 200, "Native admin JWT must authenticate")
            chat = {"model": "deepseek-default", "messages": [{"role": "system", "content": "你是助手"}, {"role": "user", "content": "你好"}, {"role": "assistant", "content": "你好呀"}, {"role": "user", "content": "测试第二轮"}], "reasoning_effort": "none"}
            status, raw, _ = request("/v1/chat/completions", chat)
            data = json.loads(raw)
            check(status == 200 and data["choices"][0]["message"]["content"] == "连接成功🐳", "Non-streaming chat conversion must preserve Unicode")
            check("测试第二轮" in FakeDeepSeek.prompts[-1]["prompt"] and "你好呀" in FakeDeepSeek.prompts[-1]["prompt"], "Full history must reach the upstream adapter")
            check(not FakeDeepSeek.prompts[-1]["search_enabled"], "Search must remain disabled when not requested")
            chat["stream"] = True
            status, raw, headers = request("/v1/chat/completions", chat)
            chunks = [json.loads(line[6:]) for line in raw.splitlines() if line.startswith("data: ") and line != "data: [DONE]"]
            text = "".join(x["choices"][0]["delta"].get("content", "") for x in chunks if x.get("choices"))
            check(status == 200 and "text/event-stream" in headers.get("content-type", "") and text == "连接成功🐳" and "data: [DONE]" in raw, "SSE chunks must reconstruct text and terminate")
            response_body = {"model": "deepseek-default", "input": "测试 Responses", "stream": False}
            status, raw, _ = request("/v1/responses", response_body)
            result = json.loads(raw)
            check(status == 200 and result.get("object") == "response" and "连接成功" in raw, "Responses API must be usable")
            status, raw, _ = request("/anthropic/v1/messages", {"model": "deepseek-default", "max_tokens": 100, "messages": [{"role": "user", "content": "测试 Anthropic"}]})
            check(status == 200 and json.loads(raw).get("type") == "message" and "连接成功" in raw, "Anthropic compatibility must work")
            stats = json.loads(request("/admin/api/stats", key=token)[1])
            check(stats["total_requests"] >= 4 and stats["success_requests"] >= 4, "Native dashboard statistics must reflect real requests")
            status, raw, _ = request("/v1/chat/completions", {"model": "does-not-exist", "messages": [{"role": "user", "content": "hi"}]})
            check(status >= 400 and "error" in json.loads(raw), "Unknown models must return structured errors")
            FakeDeepSeek.mode = "error"
            chat["stream"] = False
            status, raw, _ = request("/v1/chat/completions", chat)
            check(status >= 400 and "error" in json.loads(raw), "Upstream rejection must not become a successful empty reply")
            print(f"PASS: {count} real-engine contract checks (loopback fake upstream; no real DeepSeek account)")
        finally:
            if engine.poll() is None:
                engine.send_signal(signal.SIGINT if os.name != "nt" else signal.SIGTERM)
                try:
                    engine.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    engine.kill()
                    engine.wait()
            server.shutdown()
            reader.join(timeout=2)
            if count < 18:
                print(b"".join(output).decode(errors="replace")[-10000:])


if __name__ == "__main__":
    main()
