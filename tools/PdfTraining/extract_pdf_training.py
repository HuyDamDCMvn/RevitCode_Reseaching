import argparse
import json
import math
import os
import re
from pathlib import Path

import fitz  # PyMuPDF


def parse_args():
    parser = argparse.ArgumentParser(description="Extract training samples from PDF drawings")
    parser.add_argument("--input", type=str, required=True, help="PDF folder")
    parser.add_argument("--output", type=str, required=True, help="Annotated output folder")
    parser.add_argument("--pattern-output", type=str, required=True, help="Pattern JSON output path")
    parser.add_argument("--rule-output", type=str, required=True, help="Rule JSON output path")
    parser.add_argument("--min-text-len", type=int, default=3, help="Minimum text length to keep")
    parser.add_argument("--fast", action="store_true", help="Skip vector drawings for speed")
    parser.add_argument("--max_pdfs", type=int, default=0, help="Limit number of PDFs (0 = all)")
    parser.add_argument("--max_pages", type=int, default=0, help="Limit pages per PDF (0 = all)")
    parser.add_argument("--clear_output", action="store_true", help="Clear existing pdf_*.json outputs")
    return parser.parse_args()


def distance_point_to_segment(px, py, x1, y1, x2, y2):
    vx = x2 - x1
    vy = y2 - y1
    wx = px - x1
    wy = py - y1
    c1 = vx * wx + vy * wy
    if c1 <= 0:
        return math.hypot(px - x1, py - y1), (x1, y1)
    c2 = vx * vx + vy * vy
    if c2 <= c1:
        return math.hypot(px - x2, py - y2), (x2, y2)
    b = c1 / c2
    bx = x1 + b * vx
    by = y1 + b * vy
    return math.hypot(px - bx, py - by), (bx, by)


def guess_category_system(tag_text):
    text = (tag_text or "").upper()
    if re.search(r"\bRLT\b", text) or re.search(r"\bZUL\b|\bABL\b|\bAUL\b|\bFOL\b|\bUML\b", text) or text.startswith("L_"):
        return "OST_DuctCurves", "SupplyAir"
    if re.search(r"\bDN\d+", text) or re.search(r"\bSW\b|\bTW\b|\bKW\b", text):
        return "OST_PipeCurves", "Sanitary"
    if "KLT" in text:
        return "OST_PipeCurves", "Refrigeration"
    if "HZG" in text:
        return "OST_PipeCurves", "Heating"
    if "ELEA" in text or "ELTR" in text or "E-TR" in text:
        return "OST_ElectricalEquipment", ""
    return "OST_GenericModel", ""


def infer_position_from_offset(dx, dy, tol=1e-3):
    ax = abs(dx)
    ay = abs(dy)
    if ax < tol and ay < tol:
        return "Center"
    if ax < tol:
        return "TopCenter" if dy >= 0 else "BottomCenter"
    if ay < tol:
        return "Right" if dx >= 0 else "Left"
    if dx >= 0 and dy >= 0:
        return "TopRight"
    if dx < 0 and dy >= 0:
        return "TopLeft"
    if dx >= 0 and dy < 0:
        return "BottomRight"
    return "BottomLeft"


def build_row_col_groups(points, tol):
    rows = []
    cols = []
    for idx, (x, y) in enumerate(points):
        added = False
        for row in rows:
            if abs(row["y"] - y) <= tol:
                row["ids"].append(idx)
                row["y"] = (row["y"] * (len(row["ids"]) - 1) + y) / len(row["ids"])
                added = True
                break
        if not added:
            rows.append({"y": y, "ids": [idx]})

        added = False
        for col in cols:
            if abs(col["x"] - x) <= tol:
                col["ids"].append(idx)
                col["x"] = (col["x"] * (len(col["ids"]) - 1) + x) / len(col["ids"])
                added = True
                break
        if not added:
            cols.append({"x": x, "ids": [idx]})

    row_id = {}
    for i, row in enumerate(rows):
        for idx in row["ids"]:
            row_id[idx] = f"row_{i:03d}"
    col_id = {}
    for i, col in enumerate(cols):
        for idx in col["ids"]:
            col_id[idx] = f"col_{i:03d}"
    return row_id, col_id


def find_anchor_for_tag(centers, idx, radius):
    cx, cy = centers[idx]
    neighbors = []
    nearest = None
    nearest_dist = 1e9
    for j, (x, y) in enumerate(centers):
        if j == idx:
            continue
        dist = math.hypot(cx - x, cy - y)
        if dist < nearest_dist:
            nearest_dist = dist
            nearest = (x, y)
        if dist <= radius:
            neighbors.append((x, y))
    if neighbors:
        ax = sum(p[0] for p in neighbors) / len(neighbors)
        ay = sum(p[1] for p in neighbors) / len(neighbors)
        return ax, ay, True
    if nearest:
        return (cx + nearest[0]) / 2.0, (cy + nearest[1]) / 2.0, True
    return cx, cy, False


def make_training_file(samples, pdf_name):
    return {
        "version": "1.0",
        "source": {
            "project": "SampleDrawing PDF",
            "discipline": "MEP",
            "drawings": [pdf_name],
            "viewScale": 100,
            "annotatedBy": "PDF auto-extract",
            "annotatedDate": ""
        },
        "samples": samples
    }


def aggregate_patterns(samples):
    by_category = {}
    for s in samples:
        cat = s["element"]["category"]
        by_category.setdefault(cat, {"pos": {}, "leader": 0, "total": 0})
        by_category[cat]["total"] += 1
        pos = s["tag"]["position"]
        by_category[cat]["pos"][pos] = by_category[cat]["pos"].get(pos, 0) + 1
        if s["tag"]["hasLeader"]:
            by_category[cat]["leader"] += 1
    return by_category


def frequency_label(count, total):
    if total <= 0:
        return "Sometimes"
    ratio = count / total
    if ratio >= 0.7:
        return "Always"
    if ratio >= 0.45:
        return "Usually"
    if ratio >= 0.2:
        return "Sometimes"
    return "Rare"


def write_pattern_file(pattern_path, by_category):
    observations = []
    for cat, stats in by_category.items():
        total = stats["total"]
        pos_sorted = sorted(stats["pos"].items(), key=lambda kv: kv[1], reverse=True)
        has_leader = stats["leader"] >= (total / 2) if total > 0 else False
        for pos, count in pos_sorted[:4]:
            observations.append({
                "elementType": "PDF",
                "category": cat,
                "tagPosition": pos,
                "hasLeader": has_leader,
                "context": "Auto-extracted from PDF layout",
                "frequency": frequency_label(count, total)
            })

    doc = {
        "source": {
            "projectName": "SampleDrawing PDF",
            "drawingNumber": "Batch",
            "viewType": "FloorPlan",
            "discipline": "MEP",
            "scale": "1:100",
            "company": "",
            "date": ""
        },
        "observations": observations,
        "generalNotes": [
            "Auto-generated from PDF text blocks. Use as hint patterns only."
        ],
        "imageReferences": []
    }

    Path(pattern_path).parent.mkdir(parents=True, exist_ok=True)
    with open(pattern_path, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=2)


def write_rule_file(rule_path, by_category):
    categories = sorted(by_category.keys())
    preferred = []
    leader = False
    if categories:
        top = by_category[categories[0]]["pos"]
        preferred = [k for k, _ in sorted(top.items(), key=lambda kv: kv[1], reverse=True)[:3]]
        leader = by_category[categories[0]]["leader"] >= (by_category[categories[0]]["total"] / 2)

    rule = {
        "$schema": "../Schema/TaggingRule.schema.json",
        "id": "pdf_inferred_generic",
        "name": "PDF Inferred Generic Rule",
        "version": "1.0.0",
        "description": "Low priority rule inferred from PDF samples",
        "enabled": True,
        "priority": 5,
        "conditions": {
            "categories": categories,
            "familyNamePatterns": [".*"],
            "viewTypes": ["FloorPlan", "CeilingPlan", "Section"]
        },
        "actions": {
            "preferredPositions": preferred or ["TopRight"],
            "addLeader": bool(leader),
            "leaderStyle": "Straight",
            "avoidCollisionWith": [],
            "alignToGrid": False,
            "groupAlignment": "None"
        },
        "scoring": {
            "collisionPenalty": -65,
            "preferenceBonus": 40,
            "alignmentBonus": 20,
            "leaderLengthPenalty": 0,
            "nearEdgeBonus": 5
        }
    }

    Path(rule_path).parent.mkdir(parents=True, exist_ok=True)
    with open(rule_path, "w", encoding="utf-8") as f:
        json.dump(rule, f, indent=2)


def main():
    args = parse_args()
    input_dir = Path(args.input)
    output_dir = Path(args.output)
    output_dir.mkdir(parents=True, exist_ok=True)

    if args.clear_output:
        for old_file in output_dir.glob("pdf_*.json"):
            try:
                old_file.unlink()
            except Exception:
                pass

    all_samples = []

    processed_pdfs = 0
    for pdf_path in sorted(input_dir.glob("*.pdf")):
        if args.max_pdfs and processed_pdfs >= args.max_pdfs:
            break
        print(f"Processing {pdf_path.name} ...")
        doc = fitz.open(pdf_path)
        pdf_samples = []

        for page_index, page in enumerate(doc):
            if args.max_pages and page_index >= args.max_pages:
                break
            blocks = page.get_text("blocks")
            text_blocks = []
            heights = []
            for b in blocks:
                x0, y0, x1, y1, text, *_ = b
                cleaned = " ".join((text or "").split())
                if len(cleaned) < args.min_text_len:
                    continue
                text_blocks.append((x0, y0, x1, y1, cleaned))
                heights.append(abs(y1 - y0))

            if not text_blocks:
                continue

            tol = max(5.0, (sum(heights) / len(heights)) * 0.6)
            centers = [((x0 + x1) / 2, (y0 + y1) / 2) for x0, y0, x1, y1, _ in text_blocks]
            row_id, col_id = build_row_col_groups(centers, tol)
            anchor_radius = max(200.0, tol * 6.0)

            segments = []
            if not args.fast:
                drawings = page.get_drawings()
                for d in drawings:
                    for item in d.get("items", []):
                        if item[0] == "l":
                            (x1, y1) = item[1]
                            (x2, y2) = item[2]
                            segments.append((x1, y1, x2, y2))

            for idx, (x0, y0, x1, y1, text) in enumerate(text_blocks):
                cx = (x0 + x1) / 2
                cy = (y0 + y1) / 2
                best_seg = None
                best_dist = 1e9
                best_point = (cx, cy)

                for (sx1, sy1, sx2, sy2) in segments:
                    dist, pt = distance_point_to_segment(cx, cy, sx1, sy1, sx2, sy2)
                    if dist < best_dist:
                        best_dist = dist
                        best_seg = (sx1, sy1, sx2, sy2)
                        best_point = pt

                is_linear = best_seg is not None and best_dist < 60
                if is_linear:
                    sx1, sy1, sx2, sy2 = best_seg
                    angle = math.degrees(math.atan2(sy2 - sy1, sx2 - sx1))
                    length = math.hypot(sx2 - sx1, sy2 - sy1)
                    ex, ey = best_point
                else:
                    angle = 0.0
                    length = 0.0
                    ex, ey, _ = find_anchor_for_tag(centers, idx, anchor_radius)

                category, system = guess_category_system(text)
                dx = cx - ex
                dy = cy - ey
                position = infer_position_from_offset(dx, dy, tol=1e-3)

                leader_len = math.hypot(dx, dy)
                has_leader = bool(is_linear) or leader_len > (tol * 0.5)

                sample = {
                    "id": f"{pdf_path.stem}_p{page_index:02d}_{idx:04d}",
                    "element": {
                        "category": category,
                        "familyName": "PDF",
                        "typeName": "PDF",
                        "orientation": angle,
                        "isLinear": bool(is_linear),
                        "length": length,
                        "width": 0.0,
                        "height": 0.0,
                        "diameter": 0.0,
                        "systemType": system,
                        "centerX": ex,
                        "centerY": ey
                    },
                    "context": {
                        "density": "medium",
                        "neighborCount": 0,
                        "hasNeighborAbove": False,
                        "hasNeighborBelow": False,
                        "hasNeighborLeft": False,
                        "hasNeighborRight": False,
                        "distanceToNearestAbove": 0.0,
                        "distanceToNearestBelow": 0.0,
                        "distanceToNearestLeft": 0.0,
                        "distanceToNearestRight": 0.0,
                        "distanceToWall": 0.0,
                        "parallelElementsCount": 0,
                        "isInGroup": False
                    },
                    "tag": {
                        "position": position,
                        "offsetX": dx,
                        "offsetY": dy,
                        "hasLeader": has_leader,
                        "leaderLength": leader_len,
                        "rotation": "Horizontal",
                        "alignedWithRow": row_id.get(idx, None) is not None,
                        "alignedWithColumn": col_id.get(idx, None) is not None,
                        "rowId": row_id.get(idx, None),
                        "columnId": col_id.get(idx, None),
                        "tagText": text,
                        "tagWidth": abs(x1 - x0),
                        "tagHeight": abs(y1 - y0)
                    }
                }

                pdf_samples.append(sample)

        doc.close()

        if pdf_samples:
            out_path = output_dir / f"pdf_{pdf_path.stem}.json"
            with open(out_path, "w", encoding="utf-8") as f:
                json.dump(make_training_file(pdf_samples, pdf_path.name), f, indent=2)
            all_samples.extend(pdf_samples)
        processed_pdfs += 1

    if all_samples:
        by_category = aggregate_patterns(all_samples)
        write_pattern_file(args.pattern_output, by_category)
        write_rule_file(args.rule_output, by_category)

    print(f"Extracted {len(all_samples)} samples from PDF(s).")


if __name__ == "__main__":
    main()
