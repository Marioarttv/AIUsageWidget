"""Package the generated PNG into the multi-size Windows icon; requires Pillow."""
from pathlib import Path
from PIL import Image

repo = Path(__file__).resolve().parents[1]
source = repo / "assets" / "widget.png"
target = repo / "assets" / "widget.ico"
sizes = [(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)]
with Image.open(source) as image:
    assert image.width == image.height and image.width >= 256, "Expected a square PNG master"
    image.convert("RGBA").save(target, format="ICO", sizes=sizes)
with Image.open(target) as icon:
    assert icon.ico.sizes() == set(sizes), "Windows icon is missing required size variants"
print(f"Packaged {target.name} with {len(sizes)} sizes (16–256 pixels).")
