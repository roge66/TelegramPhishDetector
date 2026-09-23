import json
import sys
from contextlib import redirect_stdout
from pathlib import Path


def normalize_text(value):
    if value is None:
        return ""
    return " ".join(str(value).split())


def collect_from_legacy(result):
    lines = []
    confidences = []

    def walk(node):
        if node is None:
            return
        if isinstance(node, tuple) and len(node) >= 2:
            text_info = node[1]
            if isinstance(text_info, tuple) and len(text_info) >= 2:
                text = normalize_text(text_info[0])
                try:
                    confidence = float(text_info[1])
                except Exception:
                    confidence = 0.0
                if text:
                    lines.append(text)
                    confidences.append(confidence)
                return
        if isinstance(node, list):
            for item in node:
                walk(item)

    walk(result)
    return lines, confidences


def collect_from_v3(result):
    lines = []
    confidences = []

    items = result if isinstance(result, list) else [result]
    for item in items:
        data = None
        if hasattr(item, "json"):
            try:
                data = item.json
            except Exception:
                data = None
        if data is None and isinstance(item, dict):
            data = item
        if not isinstance(data, dict):
            continue

        res = data.get("res", data)
        texts = res.get("rec_texts") or res.get("texts") or []
        scores = res.get("rec_scores") or res.get("scores") or []

        for index, text in enumerate(texts):
            normalized = normalize_text(text)
            if not normalized:
                continue
            lines.append(normalized)
            try:
                confidences.append(float(scores[index]))
            except Exception:
                confidences.append(0.0)

    return lines, confidences


def main():
    if len(sys.argv) < 2:
        print(json.dumps({"text": "", "confidence": 0.0, "lines": [], "error": "Image path argument is required"}, ensure_ascii=True))
        return 2

    image_path = Path(sys.argv[1])
    if not image_path.exists():
        print(json.dumps({"text": "", "confidence": 0.0, "lines": [], "error": f"Image not found: {image_path}"}, ensure_ascii=True))
        return 2

    with redirect_stdout(sys.stderr):
        from paddleocr import PaddleOCR

        try:
            ocr = PaddleOCR(lang="ru", use_textline_orientation=True)
        except TypeError:
            ocr = PaddleOCR(lang="ru", use_angle_cls=True)

        result = ocr.predict(str(image_path))
        lines, confidences = collect_from_v3(result)

    confidence = sum(confidences) / len(confidences) if confidences else 0.0
    text = "\n".join(lines)
    print(json.dumps({"text": text, "confidence": confidence, "lines": lines}, ensure_ascii=True))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(json.dumps({"text": "", "confidence": 0.0, "lines": [], "error": str(exc)}, ensure_ascii=True))
        raise SystemExit(1)
