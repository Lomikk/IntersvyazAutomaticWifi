"""Synthetic checks only: no external network, WLAN or real Windows state."""
import importlib.util
import socket
import ssl
import struct
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("campus_diag", Path(__file__).with_name("campus-offline-diagnostics.py"))
diag = importlib.util.module_from_spec(spec)
spec.loader.exec_module(diag)


class FakeSocket:
    def __init__(self, response):
        self.response = response
        self.sent = []
        self.closed = False

    def settimeout(self, value):
        pass

    def connect(self, endpoint):
        self.endpoint = endpoint

    def sendall(self, data):
        self.sent.append(data)

    def recv(self, size):
        data, self.response = self.response[:size], self.response[size:]
        return data

    def close(self):
        self.closed = True


class Contracts(unittest.TestCase):
    def test_authorization_requires_explicit_confirmation(self):
        with patch.object(diag, "confirm_word", return_value=False), patch.object(subprocess, "run") as run:
            from unittest.mock import Mock
            report = Mock()
            diag.invoke_authorization(report, Path("IS74Wifi.exe"))
        run.assert_not_called()
        report.save.assert_called_once_with("authorization_skipped", reason="not explicitly confirmed")

    def test_authorization_invokes_installed_client_once_without_shell_or_retry(self):
        from unittest.mock import Mock
        with tempfile.TemporaryDirectory() as folder:
            executable = Path(folder) / "IS74Wifi.exe"
            executable.write_bytes(b"synthetic executable; never run")
            report = Mock()
            with patch.dict(diag.os.environ, {"LOCALAPPDATA": folder}), patch.object(diag, "confirm_word", return_value=True), patch.object(subprocess, "run", return_value=Mock(returncode=1)) as run, patch("builtins.print"):
                diag.invoke_authorization(report, executable)
            run.assert_called_once_with([str(executable), "connect"], check=False)
            end = next(call for call in report.save.call_args_list if call.args[0] == "authorization_end")
            self.assertEqual(end.kwargs["exit_code"], 1)

    def test_api_root_200_is_not_classified_as_captive(self):
        sock = FakeSocket(b"HTTP/1.1 200 OK\r\n\r\n")
        with patch.object(diag, "bound_socket", return_value=sock), patch.object(ssl, "create_default_context") as context:
            context.return_value.wrap_socket.return_value = sock
            result = diag.request("api.is74.ru", "192.0.2.2", {}, 2)
        self.assertEqual(result["outcome"], "OtherHTTP")

    def test_adapter_selection_reprompts_without_logging_phone(self):
        with patch("builtins.input", side_effect=["+70000000000", "", "2", "1"]), patch("builtins.print") as output:
            self.assertEqual(diag.choose_adapter([{"id": "wifi"}]), "wifi")
        self.assertNotIn("+70000000000", str(output.call_args_list))

    def test_ip_probe_preserves_host_without_dns_or_redirect(self):
        sock = FakeSocket(b"HTTP/1.1 302 Found\r\nLocation: https://online.susu.ru/\r\n\r\n")
        with patch.object(diag, "bound_socket", return_value=sock), patch.object(socket, "getaddrinfo", side_effect=AssertionError("DNS forbidden")), patch.object(diag, "direct_dns", side_effect=AssertionError("DNS forbidden")):
            result = diag.request("online.susu.ru", "192.0.2.1", {}, 2)
        self.assertEqual(result["outcome"], "Internet")
        self.assertEqual(sock.endpoint, ("192.0.2.1", 80))
        self.assertEqual(len(sock.sent), 1)
        self.assertIn(b"GET / HTTP/1.1\r\nHost: online.susu.ru\r\n", sock.sent[0])
        self.assertTrue(sock.closed)

    def test_captive_and_redirect_tokens_are_not_in_report(self):
        sock = FakeSocket(b"HTTP/1.1 200 OK\r\nLocation: http://w.is74.ru/stepOne?secret=123\r\nSet-Cookie: secret=456\r\n\r\nprivate body")
        with patch.object(diag, "bound_socket", return_value=sock):
            result = diag.request("online.susu.ru", "192.0.2.1", {}, 2)
        self.assertEqual(result["outcome"], "HTTP200_candidate_captive")
        self.assertNotIn("secret", str(result))
        self.assertNotIn("private body", str(result))

    def test_tls_uses_original_name_and_rejects_bad_certificate(self):
        sock = FakeSocket(b"")
        with patch.object(diag, "bound_socket", return_value=sock), patch.object(ssl, "create_default_context") as create:
            create.return_value.wrap_socket.side_effect = ssl.SSLCertVerificationError("untrusted")
            result = diag.request("api.is74.ru", "192.0.2.2", {}, 2)
        create.return_value.wrap_socket.assert_called_once_with(sock, server_hostname="api.is74.ru")
        self.assertEqual(result["failed_phase"], "tls")
        self.assertEqual(sock.sent, [])
        self.assertTrue(sock.closed)

    def test_dns_failure_does_not_fall_back_to_system_route(self):
        with patch.object(diag, "direct_dns", side_effect=TimeoutError("DNS timed out")), patch.object(diag, "bound_socket") as dial, patch.object(diag, "system_dns", side_effect=AssertionError("fallback forbidden")):
            result = diag.request("online.susu.ru", "192.0.2.1", {}, .3, True)
        self.assertEqual(result["failed_phase"], "dns")
        dial.assert_not_called()

    def test_dns_checks_transaction_and_rejects_truncation(self):
        question = b"\x01a\0\0\x01\0\x01"
        packet = struct.pack("!6H", 42, 0x8180, 1, 1, 0, 0) + question
        packet += b"\xc0\x0c" + struct.pack("!HHIH", 1, 1, 60, 4) + socket.inet_aton("192.0.2.1")
        self.assertEqual(diag.parse_dns(packet, 42, question), ["192.0.2.1"])
        with self.assertRaises(ValueError):
            diag.parse_dns(packet, 43, question)
        with self.assertRaises(ValueError):
            diag.parse_dns(packet[:-1], 42, question)


if __name__ == "__main__":
    unittest.main()
