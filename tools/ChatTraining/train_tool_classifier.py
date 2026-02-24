"""
MLP Tool Classifier for RevitChat

Trains a small MLP that maps Ollama embeddings (768-dim) to tool probabilities.
Used as an additional signal alongside the LLM for tool selection.

Usage:
    python train_tool_classifier.py --interactions ../../HD.extension/lib/net8/Data/Feedback/interactions_mrhuy.jsonl
    python train_tool_classifier.py --learned ../../HD.extension/lib/net8/Data/Feedback/learned_examples_mrhuy.json
    python train_tool_classifier.py --builtin  (uses built-in training prompts)

Requirements:
    pip install torch numpy requests tqdm
    Ollama running at http://localhost:11434 with nomic-embed-text model
"""

import argparse
import json
import os
import random
import sys
from pathlib import Path

import numpy as np
import requests
import torch
import torch.nn as nn
import torch.optim as optim
from torch.utils.data import DataLoader, Dataset
from tqdm import tqdm

OLLAMA_URL = "http://localhost:11434"
EMBED_MODEL = "nomic-embed-text"
EMBED_DIM = 768

BUILTIN_TRAINING_DATA = [
    ("how many ducts in the model", "count_elements"),
    ("count all pipes", "count_elements"),
    ("bao nhieu ong trong mo hinh", "count_elements"),
    ("dem so luong ong gio", "count_elements"),
    ("list all walls", "get_elements"),
    ("show me all ducts", "get_elements"),
    ("liet ke tat ca ong gio", "get_elements"),
    ("tim ong nuoc tren tang 1", "get_elements"),
    ("find pipes on level 1", "get_elements"),
    ("get all cable trays", "get_elements"),
    ("summary of the model", "get_model_statistics"),
    ("model overview", "get_model_statistics"),
    ("tong quan mo hinh", "get_model_statistics"),
    ("tom tat cac element", "get_model_statistics"),
    ("thong ke mo hinh", "get_model_statistics"),
    ("what categories are in the model", "get_categories"),
    ("export to csv", "export_to_csv"),
    ("xuat file csv", "export_to_csv"),
    ("export to json", "export_to_json"),
    ("export pdf", "export_pdf"),
    ("create BOQ for duct", "mep_quantity_takeoff"),
    ("boc khoi luong ong gio", "mep_quantity_takeoff"),
    ("thong ke khoi luong", "mep_quantity_takeoff"),
    ("quantity takeoff for pipes", "mep_quantity_takeoff"),
    ("check warnings", "get_model_warnings"),
    ("kiem tra canh bao", "get_model_warnings"),
    ("model health check", "get_model_statistics"),
    ("check clashes", "check_clashes"),
    ("kiem tra va cham", "check_clashes"),
    ("find disconnected elements", "check_disconnected_elements"),
    ("check missing parameters", "check_missing_parameters"),
    ("hide elements", "hide_elements"),
    ("isolate selection", "isolate_elements"),
    ("change color of ducts", "override_element_color"),
    ("zoom to element", "zoom_to_elements"),
    ("create section view", "create_section_view"),
    ("create 3d view", "create_3d_view"),
    ("tag all in view", "tag_all_in_view"),
    ("tag elements", "tag_elements"),
    ("add dimension", "create_dimension"),
    ("get system connectivity", "get_system_connectivity"),
    ("duct system summary", "get_duct_summary"),
    ("pipe system summary", "get_pipe_summary"),
    ("get mep systems", "get_mep_systems"),
    ("set parameter value", "set_parameter_value"),
    ("delete selected elements", "delete_elements"),
    ("select elements by parameter", "select_by_parameter_value"),
    ("move elements", "move_elements"),
    ("copy elements", "copy_elements"),
    ("rename elements", "rename_elements"),
    ("create schedule", "create_schedule"),
    ("get shared parameters", "get_shared_parameters"),
    ("get view filters", "get_view_filters"),
    ("apply view template", "apply_view_template"),
    ("resize duct", "resize_mep_elements"),
    ("change pipe size", "resize_mep_elements"),
    ("split pipe", "split_mep_elements"),
    ("set pipe slope", "set_pipe_slope"),
    ("auto size ducts", "auto_size_mep"),
    ("route pipe between points", "route_mep_between"),
    ("get linked models", "get_linked_models"),
    ("get revisions", "get_revisions"),
    ("check insulation coverage", "check_insulation_coverage"),
    ("check velocity", "check_velocity"),
    ("measure distance to slab", "measure_distance_to_slab"),
    ("get panel schedules", "get_panel_schedules"),
    ("check circuit loads", "get_circuit_loads"),
    ("get mep spaces", "get_mep_spaces"),
    ("check space airflow", "check_space_airflow"),
    ("find unused families", "find_unused_families"),
    ("find imported cad", "find_imported_cad"),
    ("purge unused", "purge_unused_elements"),
    ("get groups", "get_groups"),
    ("place family instance", "place_family_instance"),
    ("load family", "load_family"),
    ("get family types", "get_family_types"),
    ("create elbow", "create_elbow"),
    ("create opening", "create_openings"),
    ("detect intersections", "detect_mep_intersections"),
    ("get element geometry", "get_element_geometry"),
    ("find elements near", "find_elements_near"),
    ("get wall layers", "get_wall_layers"),
    ("compare views", "compare_views"),
    ("screenshot current view", "screenshot_view"),
    ("export ifc", "export_ifc"),
    ("add insulation to pipes", "add_change_insulation"),
    ("flip duct direction", "flip_mep_elements"),
    ("connect mep elements", "connect_mep_elements"),
    ("create 3d view by system", "create_3d_view_by_system"),
    ("override color by system", "override_color_by_system"),
    ("isolate category", "isolate_category"),
    ("show hidden elements", "get_hidden_elements"),
    ("reset view", "reset_view_isolation"),
    ("get structural model", "get_structural_model"),
    ("check rebar coverage", "check_rebar_coverage"),
    ("tao mat cat", "create_section_view"),
    ("tao schedule", "create_schedule"),
    ("tao 3d view", "create_3d_view"),
    ("xuat ifc", "export_ifc"),
    ("an element", "hide_elements"),
    ("hien element", "unhide_elements"),
    ("doi mau ong gio", "override_element_color"),
    ("kiem tra ket noi", "get_system_connectivity"),
    ("kiem tra slope ong nuoc", "check_pipe_slope"),
    ("dat tag cho tat ca", "tag_all_in_view"),
    ("do khoang cach den san", "measure_distance_to_slab"),
    ("tim element gan", "find_elements_near"),
    ("lay thong tin connector", "get_connector_info"),
    ("kiem tra ap luc", "analyze_pressure_loss"),
    ("phan phoi luu luong", "get_flow_distribution"),
]


class ToolClassifierMLP(nn.Module):
    def __init__(self, input_dim=768, hidden1=256, hidden2=128, num_tools=1):
        super().__init__()
        self.network = nn.Sequential(
            nn.Linear(input_dim, hidden1),
            nn.ReLU(),
            nn.Dropout(0.2),
            nn.Linear(hidden1, hidden2),
            nn.ReLU(),
            nn.Dropout(0.1),
            nn.Linear(hidden2, num_tools),
        )

    def forward(self, x):
        return self.network(x)


class EmbeddingDataset(Dataset):
    def __init__(self, embeddings, labels):
        self.embeddings = torch.FloatTensor(np.array(embeddings))
        self.labels = torch.LongTensor(labels)

    def __len__(self):
        return len(self.labels)

    def __getitem__(self, idx):
        return self.embeddings[idx], self.labels[idx]


def get_embedding(text, timeout=30):
    try:
        resp = requests.post(
            f"{OLLAMA_URL}/api/embeddings",
            json={"model": EMBED_MODEL, "prompt": text},
            timeout=timeout,
        )
        resp.raise_for_status()
        return resp.json().get("embedding")
    except Exception as e:
        print(f"  Embedding failed for '{text[:40]}...': {e}")
        return None


def check_ollama():
    try:
        resp = requests.get(f"{OLLAMA_URL}/api/tags", timeout=5)
        models = [m["name"] for m in resp.json().get("models", [])]
        if not any(EMBED_MODEL in m for m in models):
            print(f"WARNING: {EMBED_MODEL} not found. Available: {models}")
            print(f"Run: ollama pull {EMBED_MODEL}")
            return False
        return True
    except Exception:
        print(f"ERROR: Ollama not running at {OLLAMA_URL}")
        return False


def load_interactions_jsonl(path):
    samples = []
    if not os.path.exists(path):
        return samples
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                record = json.loads(line)
                prompt = record.get("prompt", "")
                tool_calls = record.get("tool_calls", [])
                success = record.get("success", False)
                if success and prompt and tool_calls:
                    for tc in tool_calls:
                        name = tc.get("name", "")
                        if name:
                            samples.append((prompt, name))
            except json.JSONDecodeError:
                continue
    return samples


def load_learned_examples(path):
    samples = []
    if not os.path.exists(path):
        return samples
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        if isinstance(data, list):
            for ex in data:
                prompt = ex.get("prompt", "")
                tool = ex.get("tool", "")
                if prompt and tool:
                    samples.append((prompt, tool))
    except Exception:
        pass
    return samples


def augment_prompt(prompt):
    augmented = [prompt]

    words = prompt.split()
    if len(words) > 3:
        idx = random.randint(0, len(words) - 1)
        shuffled = list(words)
        shuffled[idx], shuffled[min(idx + 1, len(words) - 1)] = (
            shuffled[min(idx + 1, len(words) - 1)],
            shuffled[idx],
        )
        augmented.append(" ".join(shuffled))

    typo_map = {"a": "e", "e": "a", "i": "y", "o": "u", "t": "r"}
    chars = list(prompt.lower())
    if len(chars) > 5:
        pos = random.randint(2, len(chars) - 2)
        if chars[pos] in typo_map:
            chars[pos] = typo_map[chars[pos]]
        augmented.append("".join(chars))

    return augmented


def main():
    parser = argparse.ArgumentParser(description="Train MLP tool classifier")
    parser.add_argument("--interactions", type=str, default=None)
    parser.add_argument("--learned", type=str, default=None)
    parser.add_argument("--builtin", action="store_true", default=True)
    parser.add_argument("--output", type=str, default="../../HD.extension/lib/net8/Data/Models")
    parser.add_argument("--epochs", type=int, default=50)
    parser.add_argument("--batch_size", type=int, default=32)
    parser.add_argument("--lr", type=float, default=1e-3)
    parser.add_argument("--augment", action="store_true", default=True)
    args = parser.parse_args()

    if not check_ollama():
        print("Ollama is required for embedding generation. Exiting.")
        sys.exit(1)

    all_samples = []

    if args.builtin:
        all_samples.extend(BUILTIN_TRAINING_DATA)
        print(f"Built-in: {len(BUILTIN_TRAINING_DATA)} samples")

    if args.interactions:
        interactions = load_interactions_jsonl(args.interactions)
        all_samples.extend(interactions)
        print(f"Interactions: {len(interactions)} samples")

    if args.learned:
        learned = load_learned_examples(args.learned)
        all_samples.extend(learned)
        print(f"Learned: {len(learned)} samples")

    if not all_samples:
        print("No training data. Exiting.")
        sys.exit(1)

    if args.augment:
        augmented = []
        for prompt, tool in all_samples:
            for aug_prompt in augment_prompt(prompt):
                augmented.append((aug_prompt, tool))
        all_samples = augmented
        print(f"After augmentation: {len(all_samples)} samples")

    tool_names = sorted(set(t for _, t in all_samples))
    tool_to_idx = {name: idx for idx, name in enumerate(tool_names)}
    num_tools = len(tool_names)
    print(f"Tools: {num_tools} unique tools")

    print("\nGenerating embeddings...")
    embeddings = []
    labels = []
    for prompt, tool in tqdm(all_samples, desc="Embedding"):
        emb = get_embedding(prompt)
        if emb and len(emb) == EMBED_DIM:
            embeddings.append(emb)
            labels.append(tool_to_idx[tool])

    print(f"Valid samples: {len(embeddings)}/{len(all_samples)}")
    if len(embeddings) < 10:
        print("Too few valid samples. Exiting.")
        sys.exit(1)

    split = int(len(embeddings) * 0.9)
    indices = list(range(len(embeddings)))
    random.shuffle(indices)

    train_emb = [embeddings[i] for i in indices[:split]]
    train_lbl = [labels[i] for i in indices[:split]]
    val_emb = [embeddings[i] for i in indices[split:]]
    val_lbl = [labels[i] for i in indices[split:]]

    train_dataset = EmbeddingDataset(train_emb, train_lbl)
    val_dataset = EmbeddingDataset(val_emb, val_lbl)
    train_loader = DataLoader(train_dataset, batch_size=args.batch_size, shuffle=True)
    val_loader = DataLoader(val_dataset, batch_size=args.batch_size)

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Device: {device}")

    model = ToolClassifierMLP(EMBED_DIM, 256, 128, num_tools).to(device)
    optimizer = optim.Adam(model.parameters(), lr=args.lr)
    criterion = nn.CrossEntropyLoss()
    scheduler = optim.lr_scheduler.StepLR(optimizer, step_size=20, gamma=0.5)

    print(f"\nTraining for {args.epochs} epochs...")
    best_val_acc = 0.0

    for epoch in range(args.epochs):
        model.train()
        total_loss = 0
        correct = 0
        total = 0

        for emb_batch, lbl_batch in train_loader:
            emb_batch, lbl_batch = emb_batch.to(device), lbl_batch.to(device)
            optimizer.zero_grad()
            output = model(emb_batch)
            loss = criterion(output, lbl_batch)
            loss.backward()
            optimizer.step()

            total_loss += loss.item()
            _, predicted = output.max(1)
            correct += predicted.eq(lbl_batch).sum().item()
            total += lbl_batch.size(0)

        scheduler.step()
        train_acc = correct / max(total, 1) * 100

        model.eval()
        val_correct = 0
        val_total = 0
        with torch.no_grad():
            for emb_batch, lbl_batch in val_loader:
                emb_batch, lbl_batch = emb_batch.to(device), lbl_batch.to(device)
                output = model(emb_batch)
                _, predicted = output.max(1)
                val_correct += predicted.eq(lbl_batch).sum().item()
                val_total += lbl_batch.size(0)

        val_acc = val_correct / max(val_total, 1) * 100

        if (epoch + 1) % 5 == 0 or epoch == 0:
            print(
                f"Epoch {epoch+1}/{args.epochs}, "
                f"Loss: {total_loss/len(train_loader):.4f}, "
                f"Train: {train_acc:.1f}%, Val: {val_acc:.1f}%"
            )

        if val_acc > best_val_acc:
            best_val_acc = val_acc

    print(f"\nBest val accuracy: {best_val_acc:.1f}%")

    os.makedirs(args.output, exist_ok=True)

    # Save model
    model_path = os.path.join(args.output, "tool_classifier.pt")
    torch.save(
        {
            "model_state": model.state_dict(),
            "tool_names": tool_names,
            "tool_to_idx": tool_to_idx,
            "embed_dim": EMBED_DIM,
            "num_tools": num_tools,
        },
        model_path,
    )
    print(f"Model saved: {model_path}")

    # Save tool index mapping
    index_path = os.path.join(args.output, "tool_classifier_index.json")
    with open(index_path, "w", encoding="utf-8") as f:
        json.dump(
            {"tool_names": tool_names, "embed_dim": EMBED_DIM, "num_tools": num_tools},
            f,
            indent=2,
        )
    print(f"Index saved: {index_path}")

    # Export ONNX
    model.eval()
    model.cpu()
    dummy = torch.randn(1, EMBED_DIM)
    onnx_path = os.path.join(args.output, "tool_classifier.onnx")
    try:
        torch.onnx.export(
            model,
            dummy,
            onnx_path,
            export_params=True,
            opset_version=18,
            input_names=["embedding"],
            output_names=["logits"],
            dynamic_axes={
                "embedding": {0: "batch"},
                "logits": {0: "batch"},
            },
            dynamo=False,
        )
        print(f"ONNX exported: {onnx_path} ({os.path.getsize(onnx_path)} bytes)")
    except Exception as e:
        print(f"ONNX export failed: {e}")
        print("Model .pt file is still available for manual conversion.")

    print("\nDone!")


if __name__ == "__main__":
    main()
