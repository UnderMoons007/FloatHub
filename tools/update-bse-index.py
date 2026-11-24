"""Refresh the bundled name directory from Tencent's BSE 920xxx quote family."""
import re
import time
import urllib.request
from pathlib import Path

root = Path(__file__).resolve().parents[1]
destination = root / "src/FloatHub.StockTracker/BseNames.txt"
names = {}
for start in range(0, 1000, 40):
    keys = ",".join(f"bj920{n:03d}" for n in range(start, start + 40))
    with urllib.request.urlopen("https://qt.gtimg.cn/q=" + keys, timeout=12) as response:
        body = response.read().decode("gbk")
    for code, name in re.findall(r'v_bj(920\d{3})="62~([^~\r\n]+)~', body):
        names[code] = name.strip()
    time.sleep(1.1)
if len(names) < 200:
    raise SystemExit("Incomplete response. Existing directory preserved.")
temporary = destination.with_suffix(".tmp")
temporary.write_text("".join(f"{code}\t{name}\n" for code, name in sorted(names.items())), encoding="utf-8")
temporary.replace(destination)
print(f"{len(names)} security names written")
