"""Generates the football parity fixtures of Castor.AI.Worker.Tests (Football/Fixtures).

Each fixture is a 640x360 BGR frame (raw bytes, the worker's preferred video size) plus what the
Python detector (castostudio_ai_football.detector.BallDetector) finds on it, so the C# port can be
checked against the real ultralytics pipeline.

Also writes, when a samples folder is given, the demo media of profiles/football.json: two 6 s clips
(left: ball then empty, right: the opposite) and the same frames as PNG for runs without ffmpeg.

Run inside the Castor-AI environment:
  cd ../Castor-AI
  uv run --no-sync python -I ../Castor-AI-Worker/scripts/generate_football_fixtures.py ../Castor-AI-Worker/Castor.AI.Worker.Tests/Football/Fixtures [../Castor-AI-Worker/samples]
"""

import json
import sys
from pathlib import Path

import cv2
import numpy as np
import torch
from ultralytics import ASSETS, YOLO

from castostudio_ai_football.constants import DETECTION_IMAGE_SIZE, MODEL_PATH, PREDICTION_CONFIDENCE

WIDTH, HEIGHT = 640, 360


def pitch(seed: int) -> np.ndarray:
    rng = np.random.default_rng(seed)
    img = np.zeros((HEIGHT, WIDTH, 3), np.float32)
    img[:] = (40, 130, 50)
    img += rng.normal(0, 12, img.shape)
    for x in range(0, WIDTH, 90):
        img[:, x : x + 45] *= 1.12
    cv2.line(img, (0, 120), (WIDTH, 140), (230, 230, 230), 3)
    return img


def ball(img: np.ndarray, r: int, cx: int, cy: int) -> np.ndarray:
    """Shaded ball with dark patches: COCO 'sports ball' around 0.5-0.7 with this checkpoint."""
    shadow = np.zeros((HEIGHT, WIDTH), np.float32)
    cv2.ellipse(shadow, (cx + r // 3, cy + r), (r, r // 3), 0, 0, 360, 1, -1)
    img = img * (1 - 0.5 * shadow[..., None])
    yy, xx = np.mgrid[0:HEIGHT, 0:WIDTH]
    mask = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2) <= r
    light = np.clip(1.1 - ((xx - cx + r * 0.4) ** 2 + (yy - cy + r * 0.4) ** 2) / (2.2 * r * r), 0.35, 1.1)
    surface = np.ones_like(img) * 235 * light[..., None]
    patches = np.zeros((HEIGHT, WIDTH), np.uint8)
    for a in range(5):
        t = a * 2 * np.pi / 5 + 0.3
        cv2.circle(patches, (int(cx + 0.62 * r * np.cos(t)), int(cy + 0.62 * r * np.sin(t))), max(1, int(r * 0.22)), 1, -1)
    cv2.circle(patches, (cx, cy), max(1, int(r * 0.25)), 1, -1)
    surface[patches > 0] = 30
    img[mask] = surface[mask]
    return img


def detections(frame: np.ndarray, device, half: bool, rgb_first: bool) -> list:
    """detector.py: resize to 416, BGR->RGB, predict. ultralytics then flips numpy input again."""
    # A fresh model per call: ultralytics keeps the device and precision of its first predict.
    model = YOLO(str(MODEL_PATH))
    img = cv2.resize(frame, (DETECTION_IMAGE_SIZE, DETECTION_IMAGE_SIZE))
    if rgb_first:
        img = cv2.cvtColor(img, cv2.COLOR_BGR2RGB)
    result = model.predict(img, conf=PREDICTION_CONFIDENCE, imgsz=DETECTION_IMAGE_SIZE, device=device, half=half, verbose=False)[0]
    return [{"class": int(b.cls), "confidence": round(float(b.conf), 4)} for b in result.boxes]


def write_samples(samples: Path, ball_frame: np.ndarray, empty_frame: np.ndarray) -> None:
    samples.mkdir(parents=True, exist_ok=True)
    ball_frame = np.clip(ball_frame, 0, 255).astype(np.uint8)
    empty_frame = np.clip(empty_frame, 0, 255).astype(np.uint8)
    cv2.imwrite(str(samples / "football_ball.png"), ball_frame)
    cv2.imwrite(str(samples / "football_empty.png"), empty_frame)

    fps, seconds = 15, 3
    for name, first, second in (("left", ball_frame, empty_frame), ("right", empty_frame, ball_frame)):
        path = samples / f"football_{name}.mp4"
        writer = cv2.VideoWriter(str(path), cv2.VideoWriter_fourcc(*"mp4v"), fps, (WIDTH, HEIGHT))
        for frame in (first, second):
            for _ in range(fps * seconds):
                writer.write(frame)
        writer.release()

        # The codec changes the pixels: the ball must still be found in the clip.
        capture = cv2.VideoCapture(str(path))
        found = []
        for index in (fps, fps * (seconds + 1)):
            capture.set(cv2.CAP_PROP_POS_FRAMES, index)
            ok, frame = capture.read()
            found.append(max((b["confidence"] for b in detections(frame, "cpu", False, True) if b["class"] == 32), default=0.0))
        capture.release()
        print(f"{path.name}: ball confidence first half {found[0]:.3f}, second half {found[1]:.3f}")


def main() -> None:
    out = Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)

    frames = {
        "pitch_ball": ball(pitch(1), 13, 160, 220),
        "pitch_empty": pitch(1),
        "zidane": cv2.resize(cv2.imread(str(ASSETS / "zidane.jpg")), (WIDTH, HEIGHT), interpolation=cv2.INTER_AREA),
    }

    expected = {}
    for name, frame in frames.items():
        frame = np.clip(frame, 0, 255).astype(np.uint8)
        (out / f"{name}.bgr").write_bytes(frame.tobytes())
        expected[name] = {
            # Exactly detector.py (CUDA, half precision).
            "python": detections(frame, 0, True, True) if torch.cuda.is_available() else None,
            # Same input, float32 on CPU: the reference for the float32 ONNX model.
            "python_fp32": detections(frame, "cpu", False, True),
            # Model fed RGB (swap_red_blue=true).
            "rgb_fp32": detections(frame, "cpu", False, False),
        }

    (out / "expected.json").write_text(json.dumps({"width": WIDTH, "height": HEIGHT, "frames": expected}, indent=2))
    print(json.dumps(expected, indent=2))

    if len(sys.argv) > 2:
        write_samples(Path(sys.argv[2]), frames["pitch_ball"], frames["pitch_empty"])


if __name__ == "__main__":
    main()
