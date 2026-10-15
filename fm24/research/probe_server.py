#!/usr/bin/env python3
"""Harmless loopback endpoint for testing FM24's skin URL action and OCR."""

import argparse
import subprocess
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from read_profile import capture_window, read_identity
from rapidocr import RapidOCR


class Handler(BaseHTTPRequestHandler):
    window_id = ""
    engine = None

    def do_GET(self):
        expected_hosts = (f"127.0.0.1:{self.server.server_port}",
                          f"localhost:{self.server.server_port}")
        hosts = self.headers.get_all("Host", [])
        if len(hosts) != 1 or hosts[0].lower() not in expected_hosts:
            self.send_error(403)
            return
        if urlsplit(self.path).path != "/probe":
            self.send_error(404)
            return
        try:
            identity = read_identity(capture_window(self.window_id), self.engine)
        except (ValueError, OSError, subprocess.SubprocessError) as exc:
            print(f"FM24 probe rejected: {exc}", flush=True)
            body = f"FM Face could not identify the open player: {exc}\n".encode()
            self.send_response(422)
        else:
            print(f"FM24 probe identified: {identity}", flush=True)
            body = f"FM Face identified {identity.name} (ID: {identity.player_id}). No portrait was generated.\n".encode()
            self.send_response(200)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--window-id", required=True, help="X11 ID of the FM24 game window")
    parser.add_argument("--port", type=int, default=8768)
    args = parser.parse_args()
    Handler.window_id = args.window_id
    Handler.engine = RapidOCR()
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"FM24 probe listening on http://127.0.0.1:{args.port}", flush=True)
    server.serve_forever()
