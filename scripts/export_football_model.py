"""Exports the Castor-AI football YOLO checkpoint to ONNX for the C# worker.

Run by scripts/export-football-model.ps1 inside the Castor-AI environment (ultralytics).
Usage: python export_football_model.py <checkpoint.pt> <output.onnx> <imgsz>
"""

import shutil
import sys
import tempfile
from pathlib import Path

from ultralytics import YOLO


def main() -> None:
    checkpoint, output, imgsz = Path(sys.argv[1]), Path(sys.argv[2]), int(sys.argv[3])

    # ultralytics writes next to the checkpoint: export from a copy to leave Castor-AI untouched.
    with tempfile.TemporaryDirectory() as work:
        copy = Path(work) / checkpoint.name
        shutil.copyfile(checkpoint, copy)
        model = YOLO(str(copy))
        if model.names.get(32) != "sports ball":
            raise SystemExit(f"class 32 is {model.names.get(32)!r}, expected 'sports ball'")

        exported = model.export(format="onnx", imgsz=imgsz, opset=17, dynamic=False, simplify=True, half=False)
        output.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(exported, output)

    print(f"exported {output}")


if __name__ == "__main__":
    main()
