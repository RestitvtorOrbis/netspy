# netSpy mark

This is the original netSpy application mark. It was created for netSpy and is not copied from dnSpy or dnSpyEx artwork. The mark and its source are distributed under the repository's GPL-3.0-or-later license.

The SVG and WPF mark use the same geometry: an opaque midnight `#FF111827` rounded square (12 DIP radius), a cyan `#FF22D3EE` six-DIP network polyline, violet `#FFA78BFA` nodes, and near-white `#FFF8FAFC` two-DIP node outlines.

## ICO provenance

`Images/netSpy.ico` contains deterministic 32-bit RGBA PNG frames at 16, 24, 32, 48, 64, and 256 pixels. It was exported locally on 2026-09-04 with Python 3.12.3 and only the standard library; no build-time dependency is required. The exact export command is:

```sh
python3 --version
python3 dnSpy/dnSpy/Branding/export-netspy-ico.py
```

The exporter is a deterministic supersampling renderer for the geometry above and emits the ICO directory in ascending frame-size order. Re-running the command produces the checked-in icon from the same source geometry.
