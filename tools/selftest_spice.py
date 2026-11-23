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
PLAIN_PORT, TLS_PORT, PROXY_PORT, QMP_PORT, VIDEO_PORT, AUDIO_PORT = 15931, 15961, 13128, 14444, 15932, 15933


def animation_floppy(path: str) -> str:
    """Dischetto con un settore di avvio che anima un gradiente a 320x200 (VGA 13h), 25 fps, senza fine.

    Stesso schema di ``macos-spice-client/.../Fixtures/display/anim.asm``, assemblato a mano::

        cli; xor ax,ax; mov ds,ax; mov ss,ax; mov sp,0x7c00; sti
        mov ax,0x13; int 0x10; mov ax,0xa000; mov es,ax; xor bp,bp
        frame: xor di,di; xor dx,dx
        row:   xor cx,cx
        col:   mov ax,cx; add ax,dx; add ax,bp; shr ax,1; and al,0x3f; add al,0x40; stosb
               inc cx; cmp cx,320; jb col
               inc dx; cmp dx,200; jb row
               mov ah,0x86; xor cx,cx; mov dx,40000; int 0x15   ; attesa di 40 ms
               inc bp; jmp frame
    """
    code = bytes.fromhex(
        "FA31C08ED88ED0BC007CFB"      # 0: preparazione dei segmenti e della pila
        "B81300CD10B800A08EC031ED"    # 11: modo 13h, ES = A000, contatore dei fotogrammi
        "31FF31D2"                    # 23 frame
        "31C9"                        # 27 row
        "89C801D001E8D1E8243F0440AA"  # 29 col: colore = ((x + y + t) >> 1) & 63 + 64
        "4181F9400172EC"              # 42: x < 320 → col
        "4281FAC80072E3"              # 49: y < 200 → row
        "B48631C9BA409CCD15"          # 56: attesa
        "45EBD3")                     # 65: t++ → frame
    return _floppy(path, code)


def beep_floppy(path: str) -> str:
    """Dischetto con un settore di avvio che accende l'altoparlante del PC a 440 Hz e resta in attesa::

        mov al,0xb6; out 0x43,al          ; PIT canale 2, onda quadra
        mov ax,2712; out 0x42,al          ; 1193182 / 440
        mov al,ah;   out 0x42,al
        in al,0x61;  or al,3; out 0x61,al ; collega il PIT all'altoparlante
        halt: hlt; jmp halt
    """
    return _floppy(path, bytes.fromhex("B0B6E643B8980AE64288E0E642E4610C03E661F4EBFD"))


def _floppy(path: str, code: bytes) -> str:
    sector = code + bytes(510 - len(code)) + b"\x55\xAA"
    with open(path, "wb") as handle:
        handle.write(sector + bytes(1474560 - len(sector)))
    return path


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
        # Video (M5): l'animazione con streaming-video=all fa nascere un flusso (MJPEG o H.264).
        spike_env.start_qemu("wsc-video", port=VIDEO_PORT, password=password,
                             extra=["-fda", animation_floppy(os.path.join(report, "anim.img")), "-boot", "a"],
                             spice_extra=["streaming-video=all"]),
        # Audio (M7): l'altoparlante del PC suona un La e QEMU lo manda al canale playback SPICE.
        spike_env.start_qemu("wsc-audio", port=AUDIO_PORT, password=password,
                             extra=["-audiodev", "spice,id=snd0", "-machine", "pcspk-audiodev=snd0",
                                    "-fda", beep_floppy(os.path.join(report, "beep.img")), "-boot", "a"]),
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
        vv(os.path.join(report, "video.vv"),
           ["host=127.0.0.1", f"port={VIDEO_PORT}", f"password={password}", "title=video"]),
        vv(os.path.join(report, "audio.vv"),
           ["host=127.0.0.1", f"port={AUDIO_PORT}", f"password={password}", "title=audio"]),
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
