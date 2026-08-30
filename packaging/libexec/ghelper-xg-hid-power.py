#!/usr/bin/python3
"""Send the upstream Windows G-Helper XG Mobile sleep/wake HID report."""

from __future__ import annotations

import fcntl
import os
import stat
import sys
from pathlib import Path


REPORT_LENGTH = 300
REPORT_ID = 0x5E
SUPPORTED_HID_IDS = {
    "0003:00000B05:00001970",
}
EXPECTED_PRODUCT = "ROG Flow X13 GV301QH_GV301QH"


def hid_iocsfeature(length: int) -> int:
    # linux/uapi/linux/hidraw.h: HIDIOCSFEATURE(len)
    return (3 << 30) | (length << 16) | (ord("H") << 8) | 0x06


def find_device() -> Path:
    matches: list[Path] = []
    for node in sorted(Path("/sys/class/hidraw").glob("hidraw*")):
        try:
            properties = (node / "device" / "uevent").read_text(encoding="ascii")
        except OSError:
            continue
        hid_ids = {
            line.removeprefix("HID_ID=").strip().upper()
            for line in properties.splitlines()
            if line.startswith("HID_ID=")
        }
        if hid_ids & SUPPORTED_HID_IDS:
            matches.append(Path("/dev") / node.name)
    if len(matches) != 1:
        raise RuntimeError(f"expected exactly one supported XG HID device, found {matches}")
    return matches[0]


def require_safe_machine() -> None:
    if os.geteuid() != 0:
        raise RuntimeError("must run as root")
    product = Path("/sys/class/dmi/id/product_name").read_text().strip()
    if product != EXPECTED_PRODUCT:
        raise RuntimeError(f"unsupported product: {product!r}")
    enabled = Path(
        "/sys/bus/platform/devices/asus-nb-wmi/egpu_enable"
    ).read_text().strip()
    if enabled != "1":
        raise RuntimeError(f"XG Mobile is not logically enabled: {enabled!r}")


def main() -> int:
    if len(sys.argv) != 2 or sys.argv[1] not in {"sleep", "wake"}:
        print(f"usage: {Path(sys.argv[0]).name} sleep|wake", file=sys.stderr)
        return 64

    require_safe_machine()
    device = find_device()
    action = sys.argv[1]
    commands = (
        [bytes([REPORT_ID, 0xE4, 0x01])]
        if action == "sleep"
        else [b"^ASUS Tech.Inc.", bytes([REPORT_ID, 0xE4, 0x02])]
    )

    flags = os.O_RDWR | os.O_CLOEXEC | os.O_NOFOLLOW
    descriptor = os.open(device, flags)
    try:
        info = os.fstat(descriptor)
        if not stat.S_ISCHR(info.st_mode):
            raise RuntimeError(f"not a character device: {device}")
        for command in commands:
            report = bytearray(REPORT_LENGTH)
            report[: len(command)] = command
            fcntl.ioctl(descriptor, hid_iocsfeature(REPORT_LENGTH), report, True)
            print(f"XG HID {action}: {command.hex('-').upper()} via {device}")
    finally:
        os.close(descriptor)

    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"XG HID power command refused: {error}", file=sys.stderr)
        raise SystemExit(1)
