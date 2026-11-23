"""Autoverifica contro il finto Proxmox della prova (../windows-spike).

Avvia il finto Proxmox (API HTTPS + QEMU con SPICE per le VM accese), lancia l'app con
``--self-test`` e una configurazione isolata che usa quel server (impronta del certificato
fissata), poi stampa ``self-test.txt``. Usato da ``tools/build.ps1 -Proxmox``.

    python tools/selftest_proxmox.py <WindowsSpiceClient.exe> <cartella-rapporto>
"""

from __future__ import annotations

import hashlib
import os
import ssl
import subprocess
import sys
import threading

SPIKE = os.environ.get("WSC_SPIKE_DIR") or os.path.join(os.path.dirname(__file__), "..", "..", "windows-spike")


def main() -> int:
    exe, report = sys.argv[1], os.path.abspath(sys.argv[2])
    sys.path.insert(0, os.path.abspath(SPIKE))
    os.chdir(SPIKE)
    import spike_env  # noqa: E402  (ambiente della prova: QEMU, PKI, finto Proxmox)

    state: dict = {}
    ready_event = threading.Event()

    def ready(server, cert):
        state["server"] = server
        with open(cert, encoding="ascii") as handle:
            state["sha"] = hashlib.sha256(ssl.PEM_cert_to_DER_cert(handle.read())).hexdigest()
        ready_event.set()

    threading.Thread(target=spike_env.run_fake_proxmox,
                     kwargs={"port": 18006, "spice_base": 15900, "ready": ready}, daemon=True).start()
    if not ready_event.wait(60):
        print("finto Proxmox non avviato", file=sys.stderr)
        return 2

    fake = sys.modules["fake_proxmox"]
    env = dict(os.environ)
    env["WSC_DATA_DIR"] = os.path.join(report, "data")
    env["WSC_SELFTEST_SERVER"] = f"127.0.0.1:18006|{fake.TOKEN_ID}|{fake.SECRET}|{state['sha']}"
    try:
        code = subprocess.run([exe, "--self-test", report], env=env, timeout=180).returncode
    finally:
        state["server"].shutdown()
    return code


if __name__ == "__main__":
    sys.exit(main())
