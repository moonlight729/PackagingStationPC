import fcntl
import json
import os
import select
import struct
import termios
import time

TIOCMSET = 0x5402
TIOCM_DTR = 0x002
TIOCM_RTS = 0x004
MAGIC = b"\x53\x59"
PROTOCOL_VERSION = 0x03
MSG_UPDATE_VERSION = 0xA4
FLAG_REQUEST = 0x00
PORT_CANDIDATES = ("/dev/cam0_acm1", "/dev/ttyACM1")


def crc16(data):
    crc = 0xFFFF
    for byte in data:
        crc ^= byte
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc


def build_query(sequence):
    header = (
        MAGIC
        + bytes([PROTOCOL_VERSION, MSG_UPDATE_VERSION, FLAG_REQUEST])
        + sequence.to_bytes(2, "little")
        + (0).to_bytes(2, "little")
        + b"\x00"
    )
    return header + crc16(header).to_bytes(2, "little")


def find_response(buffer):
    offset = 0
    while True:
        offset = buffer.find(MAGIC, offset)
        if offset < 0 or len(buffer) - offset < 12:
            return None
        frame = buffer[offset:]
        payload_length = int.from_bytes(frame[7:9], "little")
        frame_length = 12 + payload_length
        if len(frame) < frame_length:
            return None
        if (
            frame[2] == PROTOCOL_VERSION
            and frame[3] == MSG_UPDATE_VERSION
            and frame[9] == 0
            and crc16(frame[: 10 + payload_length])
            == int.from_bytes(frame[10 + payload_length : frame_length], "little")
        ):
            return frame[10 : 10 + payload_length].decode("utf-8", "replace")
        offset += 1


def direct_query(port):
    descriptor = os.open(port, os.O_RDWR | os.O_NOCTTY | os.O_NONBLOCK)
    try:
        try:
            fcntl.flock(descriptor, fcntl.LOCK_SH | fcntl.LOCK_NB)
        except OSError:
            pass

        attributes = termios.tcgetattr(descriptor)
        attributes[0] = attributes[1] = attributes[3] = 0
        attributes[2] = termios.CS8 | termios.CLOCAL | termios.CREAD
        attributes[4] = attributes[5] = termios.B921600
        attributes[6][termios.VMIN] = attributes[6][termios.VTIME] = 0
        termios.tcsetattr(descriptor, termios.TCSANOW, attributes)
        fcntl.ioctl(descriptor, TIOCMSET, struct.pack("I", TIOCM_DTR | TIOCM_RTS))
        termios.tcflush(descriptor, termios.TCIFLUSH)

        for attempt in range(8):
            os.write(descriptor, build_query(attempt + 1))
            deadline = time.time() + 1.5
            buffer = b""
            while time.time() < deadline:
                ready, _, _ = select.select([descriptor], [], [], 0.2)
                if descriptor in ready:
                    try:
                        buffer += os.read(descriptor, 4096)
                    except BlockingIOError:
                        continue
                payload = find_response(buffer)
                if payload:
                    return payload
        return None
    finally:
        os.close(descriptor)


def log_fallback():
    path = "/userdata/datatrue/logs/dora-camera10.log"
    marker = "glasses UPDATE_VERSION response: "
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as stream:
            matches = [line.strip() for line in stream if marker in line]
        if not matches:
            return None, None
        line = matches[-1]
        return line.split(marker, 1)[1].strip(), line[:23]
    except OSError:
        return None, None


def decode_versions(payload):
    value = json.loads(payload)
    if not isinstance(value, dict):
        raise ValueError("version response is not a JSON object")
    return {str(key): str(item) for key, item in value.items()}


def main():
    port = next((item for item in PORT_CANDIDATES if os.path.exists(item)), None)
    if not port:
        print(json.dumps({"success": False, "error": "glasses control port not found"}))
        return

    direct_error = "no response after 8 attempts"
    try:
        payload = direct_query(port)
        if payload:
            print(
                json.dumps(
                    {
                        "success": True,
                        "source": "direct_uart",
                        "port": port,
                        "versions": decode_versions(payload),
                    },
                    ensure_ascii=False,
                )
            )
            return
    except Exception as error:
        direct_error = str(error)

    payload, timestamp = log_fallback()
    if payload:
        try:
            print(
                json.dumps(
                    {
                        "success": True,
                        "warning": True,
                        "source": "camera10_log",
                        "port": port,
                        "timestamp": timestamp,
                        "versions": decode_versions(payload),
                        "message": direct_error,
                    },
                    ensure_ascii=False,
                )
            )
            return
        except Exception as error:
            direct_error += "; log fallback parse failed: " + str(error)

    print(
        json.dumps(
            {"success": False, "port": port, "error": direct_error},
            ensure_ascii=False,
        )
    )


main()
