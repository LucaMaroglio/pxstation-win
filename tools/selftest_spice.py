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
PLAIN_PORT, TLS_PORT, PROXY_PORT, QMP_PORT, VIDEO_PORT, AUDIO_PORT, USB_PORT = 15931, 15961, 13128, 14444, 15932, 15933, 15934


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


def boot_iso(path: str) -> str:
    """ISO 9660 minima con avvio El Torito "senza emulazione" (per il CD-ROM USB emulato, M8).

    Il settore di avvio scrive "SPICE ISO OK" sullo schermo (int 10h) e sulla console di debug di
    QEMU (porta 0xE9), poi si ferma. Indipendente dal segmento di caricamento::

        push cs; pop ds; call here; here: pop si; add si, msg-here
        next: lodsb; or al,al; jz done; out 0xe9,al; mov ah,0x0e; mov bx,7; int 0x10; jmp next
        done: hlt; jmp done
        msg: db "SPICE ISO OK", 13, 10, 0
    """
    sector = 2048
    code = bytes.fromhex("0E1FE800005E83C617AC08C0740BE6E9B40EBB0700CD10EBF0F4EBFD") + b"SPICE ISO OK\r\n\x00"
    image = bytearray(sector * 24)

    def both16(value: int) -> bytes:
        return value.to_bytes(2, "little") + value.to_bytes(2, "big")

    def both32(value: int) -> bytes:
        return value.to_bytes(4, "little") + value.to_bytes(4, "big")

    def directory_record(extent: int, name: bytes) -> bytes:
        record = bytes([0, 0]) + both32(extent) + both32(sector) + bytes(7) + bytes([2, 0, 0]) + both16(1) + bytes([len(name)]) + name
        record = record + bytes(len(record) % 2)
        return bytes([len(record)]) + record[1:]

    # Descrittore del volume primario (settore 16), root nel settore 21.
    pvd = bytearray(sector)
    pvd[0:8] = b"\x01CD001\x01\x00"
    pvd[40:72] = b"SPICETEST".ljust(32)
    pvd[80:88] = both32(len(image) // sector)
    pvd[120:124] = both16(1)
    pvd[124:128] = both16(1)
    pvd[128:132] = both16(sector)
    root = directory_record(21, b"\x00")
    pvd[156:156 + len(root)] = root
    pvd[881] = 1
    image[16 * sector:17 * sector] = pvd
    # Boot record El Torito (settore 17): catalogo nel settore 19.
    boot = bytearray(sector)
    boot[0:7] = b"\x00CD001\x01"
    boot[7:39] = b"EL TORITO SPECIFICATION".ljust(32, b"\x00")
    boot[0x47:0x4B] = (19).to_bytes(4, "little")
    image[17 * sector:18 * sector] = boot
    # Terminatore dei descrittori (settore 18).
    image[18 * sector:18 * sector + 7] = b"\xFFCD001\x01"
    # Catalogo di avvio (settore 19): voce di convalida + voce iniziale (senza emulazione, 4 settori virtuali dal 20).
    validation = bytearray(32)
    validation[0] = 1
    validation[4:28] = b"SPICE".ljust(24, b"\x00")
    validation[30:32] = b"\x55\xAA"
    checksum = (-sum(int.from_bytes(validation[i:i + 2], "little") for i in range(0, 32, 2))) & 0xFFFF
    validation[28:30] = checksum.to_bytes(2, "little")
    initial = bytearray(32)
    initial[0] = 0x88
    initial[6:8] = (4).to_bytes(2, "little")
    initial[8:12] = (20).to_bytes(4, "little")
    image[19 * sector:19 * sector + 64] = validation + initial
    image[20 * sector:20 * sector + len(code)] = code
    image[20 * sector + 510:20 * sector + 512] = b"\x55\xAA"
    # Directory radice (settore 21): "." e "..".
    entries = directory_record(21, b"\x00") + directory_record(21, b"\x01")
    image[21 * sector:21 * sector + len(entries)] = entries
    with open(path, "wb") as handle:
        handle.write(image)
    return path


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

    debugcon = os.path.join(report, "debugcon.txt")
    iso = boot_iso(os.path.join(report, "avvio.iso"))
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
        # USB e smartcard (M8): slot usb-redir su xHCI, lettore usb-ccid con ccid-card-passthru sul canale
        # smartcard; senza disco il BIOS si riavvia ogni 2 s finché non trova il CD-ROM USB dell'app.
        spike_env.start_qemu("wsc-usb", port=USB_PORT, password=password,
                             extra=["-device", "qemu-xhci,id=xhci",
                                    "-chardev", "spicevmc,id=ur0,name=usbredir", "-device", "usb-redir,chardev=ur0,bus=xhci.0",
                                    "-device", "usb-ccid,bus=xhci.0", "-chardev", "spicevmc,id=ccid,name=smartcard",
                                    "-device", "ccid-card-passthru,chardev=ccid",
                                    "-debugcon", f"file:{debugcon}", "-boot", "reboot-timeout=2000"]),
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
        vv(os.path.join(report, "usb.vv"),
           ["host=127.0.0.1", f"port={USB_PORT}", f"password={password}", "title=usb"]),
        vv(os.path.join(report, "ticket-errato.vv"),
           ["host=127.0.0.1", f"port={PLAIN_PORT}", "password=sbagliato", "title=ticket errato"]),
    ]
    try:
        time.sleep(2)  # QEMU in ascolto
        env = dict(os.environ)
        env["WSC_DATA_DIR"] = os.path.join(report, "data")
        env["WSC_SELFTEST_VV"] = ";".join(files)
        env["WSC_SELFTEST_ISO"] = iso
        code = subprocess.run([exe, "--self-test", report], env=env, timeout=240).returncode
        print("richieste al proxy:", proxy.requests)
        if not any(r.startswith("CONNECT pvespiceproxy:") and r.endswith(f":{TLS_PORT} HTTP/1.1") for r in proxy.requests):
            print("ERRORE: il proxy non ha ricevuto il CONNECT atteso", file=sys.stderr)
            return 3
        print("eventi QMP della VM in chiaro:", qmp.events)
        with open(debugcon, encoding="latin-1") as handle:
            console = handle.read()
        print("console di debug della VM usb:", repr(console[-80:]))
        if "SPICE ISO OK" not in console:
            print("ERRORE: la VM usb non si è avviata dalla ISO montata dall'app", file=sys.stderr)
            return 5
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
