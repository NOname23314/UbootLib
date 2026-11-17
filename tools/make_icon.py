# -*- coding: utf-8 -*-
"""Generate a small test icon for the sample mod (no external assets needed)."""
import os
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "icon.png")

img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.ellipse((2, 2, 61, 61), fill=(31, 58, 95, 255), outline=(46, 116, 181, 255), width=3)
d.polygon([(32, 12), (44, 44), (32, 36), (20, 44)], fill=(240, 246, 252, 255))
img.save(OUT)
print("wrote", OUT, os.path.getsize(OUT), "bytes")
