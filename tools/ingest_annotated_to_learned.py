#!/usr/bin/env python3
"""
Ingest all training JSON in annotated folder and write learned_overrides.json.
Matches ExportIngestionService logic so internal training stays in sync.
Usage: python ingest_annotated_to_learned.py <annotated_dir> <output_path>
Example: python ingest_annotated_to_learned.py ../src/SmartTag/Data/Training/annotated ../src/SmartTag/Data/Training/learned_overrides.json
"""
import json
import os
import sys
from collections import defaultdict
from datetime import datetime
from pathlib import Path


def get_str(obj, *keys):
    """Get string from dict with case-insensitive key fallback."""
    for k in keys:
        for key in (k, k[0].lower() + k[1:] if len(k) > 1 else k):
            if key in obj and obj[key] is not None:
                v = obj[key]
                return (v or "").strip() if isinstance(v, str) else str(v)
    return ""


def get_bool(obj, key, default=False):
    for k in (key, key[0].lower() + key[1:] if len(key) > 1 else key):
        if k in obj:
            return bool(obj[k])
    return default


def get_num(obj, key, default=None):
    for k in (key, key[0].lower() + key[1:] if len(key) > 1 else key):
        if k in obj and obj[k] is not None:
            try:
                return float(obj[k])
            except (TypeError, ValueError):
                pass
    return default


def aggregate_group(samples):
    """Aggregate a list of samples into one LearnedOverride (preferred positions, addLeader, offset)."""
    if not samples:
        return None
    positions = []
    has_leader_count = 0
    offset_x_list, offset_y_list = [], []
    for s in samples:
        if not s:
            continue
        tag = s.get("Tag") or s.get("tag") or {}
        pos = get_str(tag, "Position")
        if pos:
            positions.append(pos)
        if get_bool(tag, "HasLeader"):
            has_leader_count += 1
        ox, oy = get_num(tag, "OffsetX"), get_num(tag, "OffsetY")
        if ox is not None:
            offset_x_list.append(abs(ox))
        if oy is not None:
            offset_y_list.append(abs(oy))
    # Preferred positions: most frequent first
    position_counts = defaultdict(int)
    for p in positions:
        position_counts[p] += 1
    preferred = sorted(position_counts.keys(), key=lambda x: -position_counts[x])
    if not preferred:
        preferred = ["TopRight"]
    add_leader = has_leader_count > (len(samples) / 2) if samples else False
    align_row_count = sum(1 for s in samples if get_bool(s.get("Tag") or s.get("tag") or {}, "AlignedWithRow"))
    align_col_count = sum(1 for s in samples if get_bool(s.get("Tag") or s.get("tag") or {}, "AlignedWithColumn"))
    prefer_align_row = len(samples) > 0 and align_row_count > (len(samples) / 2)
    prefer_align_column = len(samples) > 0 and align_col_count > (len(samples) / 2)
    avg_offset = None
    if offset_x_list or offset_y_list:
        ax = sum(offset_x_list) / len(offset_x_list) if offset_x_list else 0
        ay = sum(offset_y_list) / len(offset_y_list) if offset_y_list else 0
        avg_offset = (ax + ay) / 2.0
        if avg_offset < 0.01:
            avg_offset = None
    return {
        "preferredPositions": preferred,
        "addLeader": add_leader,
        "offsetDistance": avg_offset,
        "preferAlignRow": prefer_align_row,
        "preferAlignColumn": prefer_align_column,
        "sampleCount": len(samples),
    }


def aggregate_by_category(samples):
    """Group by element.category -> LearnedOverride."""
    groups = defaultdict(list)
    for s in samples:
        if not s:
            continue
        el = s.get("Element") or s.get("element") or {}
        cat = get_str(el, "Category")
        if not cat:
            continue
        groups[cat].append(s)
    return {k: aggregate_group(v) for k, v in groups.items() if aggregate_group(v) and aggregate_group(v)["sampleCount"] > 0}


def aggregate_by_category_and_system(samples):
    """Group by category|systemType (skip if no system)."""
    groups = defaultdict(list)
    for s in samples:
        if not s:
            continue
        el = s.get("Element") or s.get("element") or {}
        cat = get_str(el, "Category")
        if not cat:
            continue
        sys_type = get_str(el, "SystemType") or ""
        if not sys_type.strip():
            continue
        key = f"{cat}|{sys_type}"
        groups[key].append(s)
    return {k: aggregate_group(v) for k, v in groups.items() if aggregate_group(v) and aggregate_group(v)["sampleCount"] > 0}


def merge_overrides(target, source):
    """Merge source into target; prefer entry with larger sampleCount."""
    for k, v in source.items():
        if not k or not v:
            continue
        if k in target:
            if v.get("sampleCount", 0) >= target[k].get("sampleCount", 0):
                target[k] = v
        else:
            target[k] = v


def main():
    if len(sys.argv) != 3:
        print("Usage: python ingest_annotated_to_learned.py <annotated_dir> <output_path>")
        sys.exit(1)
    annotated_dir = Path(sys.argv[1]).resolve()
    output_path = Path(sys.argv[2]).resolve()
    if not annotated_dir.is_dir():
        print(f"Error: not a directory: {annotated_dir}")
        sys.exit(1)
    all_by_cat = {}
    all_by_cat_sys = {}
    total_samples = 0
    files_ok = 0
    for f in sorted(annotated_dir.glob("*.json")):
        if f.name.startswith("_"):
            continue
        try:
            with open(f, "r", encoding="utf-8") as fp:
                data = json.load(fp)
        except Exception as e:
            print(f"Skip {f.name}: {e}")
            continue
        samples = data.get("Samples") or data.get("samples") or []
        if not samples:
            continue
        by_cat = aggregate_by_category(samples)
        by_cat_sys = aggregate_by_category_and_system(samples)
        merge_overrides(all_by_cat, by_cat)
        merge_overrides(all_by_cat_sys, by_cat_sys)
        total_samples += len(samples)
        files_ok += 1
        print(f"  {f.name}: {len(samples)} samples")
    out = {
        "version": 1,
        "updatedAt": datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%S.000Z"),
        "byCategory": all_by_cat,
        "byCategoryAndSystem": all_by_cat_sys,
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as fp:
        json.dump(out, fp, indent=2, ensure_ascii=False)
    print(f"Ingested {total_samples} samples from {files_ok} files -> {output_path}")
    print(f"  byCategory: {len(all_by_cat)} entries, byCategoryAndSystem: {len(all_by_cat_sys)} entries")


if __name__ == "__main__":
    main()
