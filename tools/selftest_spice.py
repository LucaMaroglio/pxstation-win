"""Autoverifica SPICE contro QEMU per Windows (ambiente di ../windows-spike).

Avvia due QEMU con SPICE e ticket:

* ``plain``: porta in chiaro su 127.0.0.1;
* ``proxmox``: solo TLS (CA e soggetto stile Proxmox), raggiunto attraverso un proxy HTTP
  CONNECT come lo spiceproxy di Proxmox, con host opaco ``pvespiceproxy:…``;

poi scrive tre file ``.vv`` (i due sopra e ``ticket-errato``) e lancia l'app con
``--self-test`` e ``WSC_SELFTEST_VV``. Usato da ``tools/build.ps1 -Spice``.

Input (M4): la VM in chiaro ha un monitor QMP; l'app le invia Ctrl+Alt+Canc e il BIOS la deve
riavviare (evento ``RESET``). Entrambe le VM restano in modalità server del puntatore: il server
offre quella client solo con l'agente o una tavoletta attivata dal sistema ospite, e il BIOS non
la attiva.

    python tools/selftest_spice.py <WindowsSpiceClient.exe> <cartella-rapporto>
"""

from __future__ import annotations

import json
import os
import secrets
import socket
import subprocess
import sys
import threading
import time

SPIKE = os.environ.get("WSC_SPIKE_DIR") or os.path.join(os.path.dirname(__file__), "..", "..", "windows-spike")
PLAIN_PORT, TLS_PORT, PROXY_PORT, QMP_PORT = 15931, 15961, 13128, 14444


class QmpEvents:
    """Raccoglie gli eventi QMP di una VM (solo lettura: nessun comando oltre a qmp_capabilities)."""

    def __init__(self, port: int) -> None:
        self.events: list[str] = []
        deadline = time.time() + 10
        while True:
            try:
                self._socket = socket.create_connection(("127.0.0.1", port), timeout=2)
                break
            except OSError:
                if time.time() > deadline:
                    raise
                time.sleep(0.2)
        self._file = self._socket.makefile("rwb")
        self._file.readline()  # saluto
        self._file.write(b'{"execute": "qmp_capabilities"}\n')
        self._file.flush()
        self._socket.settimeout(None)
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self) -> None:
        try:
            for line in self._file:
                message = json.loads(line)
                if "event" in message:
                    self.events.append(message["event"])
        except (OSError, ValueError):
            pass

    def close(self) -> None:
        try:
            self._socket.close()
        except OSError:
            pass


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
        spike_env.start_qemu("wsc-plain", port=PLAIN_PORT, password=password,
                             extra=["-qmp", f"tcp:127.0.0.1:{QMP_PORT},server=on,wait=off"]),
        spike_env.start_qemu("wsc-proxmox", tls_port=TLS_PORT, x509_dir=os.path.dirname(pki["ca-cert.pem"]),
                             password=password),
    ]
    proxy = spike_env.ConnectProxy(PROXY_PORT)
    qmp = QmpEvents(QMP_PORT)
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
        print("eventi QMP della VM in chiaro:", qmp.events)
        if "RESET" not in qmp.events:
            print("ERRORE: Ctrl+Alt+Canc non ha riavviato la VM in chiaro", file=sys.stderr)
            return 4
        return code
    finally:
        qmp.close()
        proxy.close()
        for process in processes:
            process.kill()
        for path in files:
            os.remove(path)  # contengono il ticket


if __name__ == "__main__":
    sys.exit(main())
