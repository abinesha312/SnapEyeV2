#!/usr/bin/env python
"""Quick test to verify Tesseract OCR availability."""
import sys

print("Checking Tesseract OCR installation...\n")

# 1. Check if pytesseract package is installed
try:
    import pytesseract
    print("[OK] pytesseract Python package is installed (version: " + pytesseract.__version__ + ")")
except ImportError:
    print("[FAIL] pytesseract package NOT installed. Run: pip install pytesseract")
    sys.exit(1)

# 2. Check if Tesseract OCR binary is available
try:
    version = pytesseract.get_tesseract_version()
    print(f"[OK] Tesseract OCR binary is installed (version: {version})")
    print("\nTesseract is ready to use!")
except Exception as e:
    print(f"[FAIL] Tesseract OCR binary NOT found: {e}")
    print("\nTo fix:")
    print("1. Download Tesseract installer from: https://github.com/UB-Mannheim/tesseract/wiki")
    print("2. Run the installer (recommended path: C:\\Program Files\\Tesseract-OCR\\)")
    print("3. Add to PATH or set pytesseract.pytesseract.tesseract_cmd = r'C:\\Program Files\\Tesseract-OCR\\tesseract.exe'")
    sys.exit(1)
