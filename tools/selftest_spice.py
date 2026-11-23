"""Autoverifica SPICE contro QEMU per Windows (ambiente di ../windows-spike).

Avvia due QEMU con SPICE e ticket:

* ``plain``: porta in chiaro su 127.0.0.1;
* ``proxmox``: solo TLS (CA e soggetto stile Proxmox), raggiunto attraverso un proxy HTTP
  CONNECT come lo spiceproxy di Proxmox, con host opaco ``pvespiceproxy:…``;

poi scrive tre file ``.vv`` (i due sopra e ``ticket-errato``) e lancia l'app con
``--self-test`` e ``WSC_SELFTEST_VV``. Usato da ``tools/build.ps1 -Spice``.

    python tools/selftest_spice.py <WindowsSpiceClient.exe> <cartella-rapporto>
"""

from __future__ import annotations

import os
import secrets
import subprocess
import sys
import time

SPIKE = os.environ.get("WSC_SPIKE_DIR") or os.path.join(os.path.dirname(__file__), "..", "..", "windows-spike")
PLAIN_PORT, TLS_PORT, PROXY_PORT = 15931, 15961, 13128


def vv(path: str, lines: list[str]) -> str:
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("[virt-viewer]\ntype=spice\n" + "\n".join(lines) + "\n")
    return path


def main() -> int:
    exe, report = sys.argv[1], os.path.abspath(sys.argv[2])
    os.makedirs(report, exist_ok=True)
    sys.path.insert(0, os.path.abspath(SPIKE))
    import spike_env  # noqa: E402  (QEMU, PKI, proxy CONNECT della prova)

    if not os.path.exists(spike_env.QEMU):
        print(f"QEMU non trovato: {spike_env.QEMU}", file=sys.stderr)
        return 2

    # Ticket casuali per questa prova, mai salvati altrove che nei .vv temporanei.
    password = secrets.token_hex(12)
    pki = spike_env.make_pki(os.path.join(report, "pki"))
    with open(pki["ca-cert.pem"], encoding="ascii") as handle:
        ca = handle.read().strip().replace("\n", "\\n")

    processes = [
        spike_env.start_qemu("wsc-plain", port=PLAIN_PORT, password=password),
        spike_env.start_qemu("wsc-proxmox", tls_port=TLS_PORT, x509_dir=os.path.dirname(pki["ca-cert.pem"]),
                             password=password),
    ]
    proxy = spike_env.ConnectProxy(PROXY_PORT)
    files = [
        vv(os.path.join(report, "in-chiaro.vv"),
           ["host=127.0.0.1", f"port={PLAIN_PORT}", f"password={password}", "title=in chiaro"]),
        vv(os.path.join(report, "proxmox-tls.vv"),
           [f"host=pvespiceproxy:{secrets.token_hex(4)}:101:pve1::{secrets.token_hex(8)}", f"tls-port={TLS_PORT}",
            f"proxy=http://127.0.0.1:{PROXY_PORT}", f"password={password}", f"ca={ca}",
            f"host-subject={spike_env.SERVER_SUBJECT}", "secure-channels=main;display;inputs;cursor",
            "title=Proxmox TLS"]),
        vv(os.path.join(report, "ticket-errato.vv"),
           ["host=127.0.0.1", f"port={PLAIN_PORT}", "password=sbagliato", "title=ticket errato"]),
    ]
    try:
        time.sleep(2)  # QEMU in ascolto
        env = dict(os.environ)
        env["WSC_DATA_DIR"] = os.path.join(report, "data")
        env["WSC_SELFTEST_VV"] = ";".join(files)
        code = subprocess.run([exe, "--self-test", report], env=env, timeout=240).returncode
        print("richieste al proxy:", proxy.requests)
        if not any(r.startswith("CONNECT pvespiceproxy:") and r.endswith(f":{TLS_PORT} HTTP/1.1") for r in proxy.requests):
            print("ERRORE: il proxy non ha ricevuto il CONNECT atteso", file=sys.stderr)
            return 3
        return code
    finally:
        proxy.close()
        for process in processes:
            process.kill()
        for path in files:
            os.remove(path)  # contengono il ticket


if __name__ == "__main__":
    sys.exit(main())
