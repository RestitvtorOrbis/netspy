#!/usr/bin/env python3
# Copyright (C) 2026 netSpy contributors
# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of dnSpy.
#
# dnSpy is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# dnSpy is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.

"""Export the netSpy geometry to a deterministic 32-bit PNG-backed ICO."""

from pathlib import Path
import struct
import zlib

SCALE = 4
SIZES = (16, 24, 32, 48, 64, 256)
BG = (0x11, 0x18, 0x27, 0xFF)
CYAN = (0x22, 0xD3, 0xEE, 0xFF)
VIOLET = (0xA7, 0x8B, 0xFA, 0xFF)
WHITE = (0xF8, 0xFA, 0xFC, 0xFF)
POINTS = ((14, 45), (14, 25), (32, 14), (50, 25), (50, 45))
NODES = ((14, 45), (32, 14), (50, 45))

def rect(x, y):
    if 12 <= x <= 52 or 12 <= y <= 52:
        return True
    cx = 12 if x < 12 else 52
    cy = 12 if y < 12 else 52
    return (x - cx) ** 2 + (y - cy) ** 2 <= 144

def segment(px, py, a, b):
    dx, dy = b[0] - a[0], b[1] - a[1]
    length = dx * dx + dy * dy
    t = ((px - a[0]) * dx + (py - a[1]) * dy) / length
    t = min(max(t, 0), 1)
    qx, qy = a[0] + t * dx, a[1] + t * dy
    return (px - qx) ** 2 + (py - qy) ** 2 <= 9

def circle(x, y, cx, cy, radius):
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius * radius

def png(size):
    rows = []
    for y in range(size):
        for x in range(size):
            samples = []
            for sy in range(SCALE):
                for sx in range(SCALE):
                    px = (x + (sx + .5) / SCALE) * 64 / size
                    py = (y + (sy + .5) / SCALE) * 64 / size
                    value = BG if rect(px, py) else (0, 0, 0, 0)
                    if any(segment(px, py, a, b) for a, b in zip(POINTS, POINTS[1:])):
                        value = CYAN
                    for cx, cy in NODES:
                        if circle(px, py, cx, cy, 5):
                            value = WHITE
                        if circle(px, py, cx, cy, 4):
                            value = VIOLET
                    samples.append(value)
            rows.append(tuple(round(sum(p[c] for p in samples) / len(samples)) for c in range(4)))
    raw = b''.join(b'\0' + bytes(row) for row in rows)
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xFFFFFFFF)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)) +
            chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))

def main():
    frames = [png(size) for size in SIZES]
    offset = 6 + len(frames) * 16
    entries = []
    for size, frame in zip(SIZES, frames):
        dimension = 0 if size == 256 else size
        entries.append(struct.pack('<BBBBHHII', dimension, dimension, 0, 0, 1, 32, len(frame), offset))
        offset += len(frame)
    output = Path(__file__).parents[1] / 'Images' / 'netSpy.ico'
    output.write_bytes(struct.pack('<HHH', 0, 1, len(frames)) + b''.join(entries) + b''.join(frames))

if __name__ == '__main__':
    main()
