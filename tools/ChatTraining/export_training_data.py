"""
Export and merge training data from various RevitChat feedback sources.

Reads interaction logs (JSONL), learned examples (JSON), and approved feedback (JSON),
then generates a unified training dataset for train_tool_classifier.py.

Usage:
    python export_training_data.py --feedback-dir ../../HD.extension/lib/net8/Data/Feedback
    python export_training_data.py --feedback-dir ../../HD.extension/lib/net8/Data/Feedback --output training_export.json
"""

import argparse
import json
import os
from pathlib import Path


def load_interactions(feedback_dir):
    """Load successful tool calls from interactions JSONL files."""
    samples = []
    for jsonl_file in Path(feedback_dir).glob("interactions_*.jsonl"):
        with open(jsonl_file, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    rec = json.loads(line)
                    if not rec.get("success") or rec.get("retry"):
                        continue
                    prompt = rec.get("prompt", "")
                    if not prompt:
                        continue
                    for tc in rec.get("tool_calls", []):
                        name = tc.get("name", "")
                        if name and tc.get("result_ok", False):
                            samples.append(
                                {
                                    "prompt": prompt,
                                    "tool": name,
                                    "intent": rec.get("intent", ""),
                                    "category": rec.get("category", ""),
                                    "source": "interactions",
                                }
                            )
                except json.JSONDecodeError:
                    continue
    return samples


def load_learned_examples(feedback_dir):
    """Load learned examples from DynamicFewShotSelector exports."""
    samples = []
    for json_file in Path(feedback_dir).glob("learned_examples_*.json"):
        try:
            with open(json_file, "r", encoding="utf-8") as f:
                data = json.load(f)
            if isinstance(data, list):
                for ex in data:
                    prompt = ex.get("prompt", "")
                    tool = ex.get("tool", "")
                    if prompt and tool:
                        samples.append(
                            {
                                "prompt": prompt,
                                "tool": tool,
                                "intent": ex.get("intent", ""),
                                "category": ex.get("category", ""),
                                "source": "learned_examples",
                            }
                        )
        except Exception:
            continue
    return samples


def load_approved_feedback(feedback_dir):
    """Load approved feedback from ChatFeedbackService."""
    samples = []
    for json_file in Path(feedback_dir).glob("chat_feedback_*.json"):
        try:
            with open(json_file, "r", encoding="utf-8") as f:
                data = json.load(f)
            approved_list = data if isinstance(data, list) else data.get("approved", [])
            for entry in approved_list:
                prompt = entry.get("prompt", "")
                tools = entry.get("tools", [])
                if not prompt or not tools:
                    continue
                for tool_info in tools:
                    name = (
                        tool_info.get("name", "")
                        if isinstance(tool_info, dict)
                        else str(tool_info)
                    )
                    if name:
                        samples.append(
                            {
                                "prompt": prompt,
                                "tool": name,
                                "intent": "",
                                "category": "",
                                "source": "approved_feedback",
                            }
                        )
        except Exception:
            continue
    return samples


def deduplicate(samples):
    """Remove exact duplicates based on (prompt, tool) pair."""
    seen = set()
    unique = []
    for s in samples:
        key = (s["prompt"].lower().strip(), s["tool"].lower())
        if key not in seen:
            seen.add(key)
            unique.append(s)
    return unique


def main():
    parser = argparse.ArgumentParser(description="Export training data from feedback")
    parser.add_argument(
        "--feedback-dir",
        type=str,
        default="../../HD.extension/lib/net8/Data/Feedback",
    )
    parser.add_argument("--output", type=str, default="training_export.json")
    args = parser.parse_args()

    feedback_dir = args.feedback_dir
    if not os.path.isdir(feedback_dir):
        print(f"Feedback directory not found: {feedback_dir}")
        return

    print(f"Scanning {feedback_dir}...")

    interactions = load_interactions(feedback_dir)
    print(f"  Interactions: {len(interactions)} samples")

    learned = load_learned_examples(feedback_dir)
    print(f"  Learned examples: {len(learned)} samples")

    approved = load_approved_feedback(feedback_dir)
    print(f"  Approved feedback: {len(approved)} samples")

    all_samples = interactions + learned + approved
    print(f"  Total raw: {len(all_samples)}")

    unique = deduplicate(all_samples)
    print(f"  After dedup: {len(unique)}")

    tools = sorted(set(s["tool"] for s in unique))
    print(f"  Unique tools: {len(tools)}")

    with open(args.output, "w", encoding="utf-8") as f:
        json.dump(unique, f, indent=2, ensure_ascii=False)

    print(f"\nExported to: {args.output}")
    print(f"\nTo retrain the classifier:")
    print(f"  python train_tool_classifier.py --learned {args.output} --epochs 50")


if __name__ == "__main__":
    main()
