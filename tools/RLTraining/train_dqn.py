"""
Deep Q-Network (DQN) Training for SmartTag Placement

This script trains a DQN agent to learn optimal tag placement strategies
from training data and user feedback.

Usage:
    python train_dqn.py --data ../src/SmartTag/Data/Training/annotated
    python train_dqn.py --feedback ../src/SmartTag/Data/Training/feedback --finetune model.onnx

Requirements:
    pip install torch numpy onnx scikit-learn tqdm
"""

import argparse
import json
import os
import random
from collections import deque, namedtuple
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn
import torch.optim as optim
from torch.utils.data import DataLoader, Dataset

# Category mapping (match FeatureExtractor.cs)
CATEGORY_INDEX = {
    "OST_PipeCurves": 0,
    "OST_DuctCurves": 1,
    "OST_CableTray": 2,
    "OST_Conduit": 3,
    "OST_MechanicalEquipment": 4,
    "OST_ElectricalEquipment": 5,
    "OST_PlumbingFixtures": 6,
    "Other": 7
}

# Feature normalization (match FeatureExtractor.cs)
MAX_LENGTH = 100.0
MAX_WIDTH = 20.0
MAX_HEIGHT = 20.0
MAX_DIAMETER = 5.0
MAX_DISTANCE = 50.0

# Action mapping (positions + alignment + leader)
POSITION_ACTIONS = {
    "TopRight": 0,
    "TopLeft": 1,
    "TopCenter": 2,
    "BottomRight": 3,
    "BottomLeft": 4,
    "BottomCenter": 5,
    "Right": 6,
    "Left": 7,
    "Center": 8
}

ACTION_NAMES = [
    "TopRight",
    "TopLeft",
    "TopCenter",
    "BottomRight",
    "BottomLeft",
    "BottomCenter",
    "Right",
    "Left",
    "Center",
    "AlignRow",
    "AlignColumn",
    "ToggleLeader"
]

# Experience tuple for replay buffer
Experience = namedtuple('Experience', ['state', 'action', 'reward', 'next_state', 'done'])


class DQN(nn.Module):
    """Deep Q-Network for tag placement policy."""
    
    def __init__(self, state_dim=50, action_dim=12, hidden_dims=[128, 256, 256]):
        super().__init__()
        
        layers = []
        prev_dim = state_dim
        
        for hidden_dim in hidden_dims:
            layers.extend([
                nn.Linear(prev_dim, hidden_dim),
                nn.ReLU(),
                nn.Dropout(0.1)
            ])
            prev_dim = hidden_dim
        
        layers.append(nn.Linear(prev_dim, action_dim))
        
        self.network = nn.Sequential(*layers)
        
    def forward(self, x):
        return self.network(x)


class ReplayBuffer:
    """Experience replay buffer for DQN training."""
    
    def __init__(self, capacity=10000):
        self.buffer = deque(maxlen=capacity)
    
    def push(self, experience):
        self.buffer.append(experience)
    
    def sample(self, batch_size):
        return random.sample(self.buffer, min(batch_size, len(self.buffer)))
    
    def __len__(self):
        return len(self.buffer)


class TagPlacementEnvironment:
    """
    Simulated environment for tag placement.
    
    State: 50-dimensional vector
    - [0-19]  Element features
    - [20-24] KNN candidate scores
    - [25-34] Nearest existing tag positions (relative)
    - [35-44] Collision map (10 directions)
    - [45-49] Alignment opportunities
    
    Actions: 12 discrete actions
    - 0-4: Select KNN candidate 0-4
    - 5-8: Shift (left, right, up, down)
    - 9-10: Align (row, column)
    - 11: Toggle leader
    
    Rewards:
    - +10: No collision
    - +5: Aligned with existing tag
    - +3: Short leader
    - -20: Collision with tag
    - -15: Collision with element
    - -10: Leader crosses element
    - +50/-50: User approved/rejected
    """
    
    STATE_DIM = 50
    ACTION_DIM = 12
    
    # Action definitions
    ACTIONS = {
        0: 'select_candidate_0',
        1: 'select_candidate_1',
        2: 'select_candidate_2',
        3: 'select_candidate_3',
        4: 'select_candidate_4',
        5: 'shift_left',
        6: 'shift_right',
        7: 'shift_up',
        8: 'shift_down',
        9: 'align_row',
        10: 'align_column',
        11: 'toggle_leader'
    }
    
    # Reward values
    REWARDS = {
        'no_collision': 10.0,
        'aligned': 5.0,
        'short_leader': 3.0,
        'collision_tag': -20.0,
        'collision_element': -15.0,
        'leader_crosses': -10.0,
        'user_approved': 50.0,
        'user_rejected': -50.0,
        'user_adjusted': -10.0
    }
    
    def __init__(self):
        self.state = None
        self.existing_tags = []
        self.element_bounds = []
        
    def reset(self, element_features, knn_scores, existing_tags, element_bounds):
        """Reset environment with new element context."""
        self.existing_tags = existing_tags
        self.element_bounds = element_bounds
        
        # Build state vector
        state = np.zeros(self.STATE_DIM, dtype=np.float32)
        
        # Element features [0-19]
        state[:len(element_features)] = element_features[:20]
        
        # KNN candidate scores [20-24]
        for i, score in enumerate(knn_scores[:5]):
            state[20 + i] = score
        
        # Existing tags [25-34] - relative positions of 5 nearest
        # (simplified - would need actual implementation)
        
        # Collision map [35-44]
        # (simplified - would need actual implementation)
        
        # Alignment opportunities [45-49]
        # (simplified - would need actual implementation)
        
        self.state = state
        return state
    
    def step(self, action):
        """Execute action and return (next_state, reward, done)."""
        reward = 0.0
        done = False
        
        # Simulate action effects
        if action <= 4:
            # Select candidate - check collisions
            reward += self.REWARDS['no_collision']  # Simplified
        elif action <= 8:
            # Shift - small penalty for adjustments
            reward -= 1.0
        elif action <= 10:
            # Align - bonus if successful
            reward += self.REWARDS['aligned']
        else:
            # Toggle leader
            reward += 0.0
        
        # Update state (simplified)
        next_state = self.state.copy()
        
        return next_state, reward, done
    
    def get_reward_from_feedback(self, feedback_type, adjustment_distance=0):
        """Get reward from user feedback."""
        if feedback_type == 'approved':
            return self.REWARDS['user_approved']
        elif feedback_type == 'rejected':
            return self.REWARDS['user_rejected']
        elif feedback_type == 'adjusted':
            # Penalty proportional to adjustment distance
            return self.REWARDS['user_adjusted'] * (1 + adjustment_distance)
        return 0.0


class DQNTrainer:
    """DQN training loop."""
    
    def __init__(
        self,
        state_dim=50,
        action_dim=12,
        lr=1e-4,
        gamma=0.99,
        epsilon_start=1.0,
        epsilon_end=0.1,
        epsilon_decay=0.995,
        batch_size=64,
        target_update=100
    ):
        self.device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
        print(f"Using device: {self.device}")
        
        # Networks
        self.policy_net = DQN(state_dim, action_dim).to(self.device)
        self.target_net = DQN(state_dim, action_dim).to(self.device)
        self.target_net.load_state_dict(self.policy_net.state_dict())
        self.target_net.eval()
        
        # Training components
        self.optimizer = optim.Adam(self.policy_net.parameters(), lr=lr)
        self.criterion = nn.SmoothL1Loss()
        self.replay_buffer = ReplayBuffer()
        
        # Hyperparameters
        self.gamma = gamma
        self.epsilon = epsilon_start
        self.epsilon_end = epsilon_end
        self.epsilon_decay = epsilon_decay
        self.batch_size = batch_size
        self.target_update = target_update
        self.steps = 0
        
    def select_action(self, state, explore=True):
        """Select action using epsilon-greedy policy."""
        if explore and random.random() < self.epsilon:
            return random.randrange(TagPlacementEnvironment.ACTION_DIM)
        
        with torch.no_grad():
            state_tensor = torch.FloatTensor(state).unsqueeze(0).to(self.device)
            q_values = self.policy_net(state_tensor)
            return q_values.argmax().item()
    
    def train_step(self):
        """Perform one training step."""
        if len(self.replay_buffer) < self.batch_size:
            return None
        
        # Sample batch
        experiences = self.replay_buffer.sample(self.batch_size)
        batch = Experience(*zip(*experiences))
        
        # Convert to tensors
        states = torch.FloatTensor(np.array(batch.state)).to(self.device)
        actions = torch.LongTensor(batch.action).to(self.device)
        rewards = torch.FloatTensor(batch.reward).to(self.device)
        next_states = torch.FloatTensor(np.array(batch.next_state)).to(self.device)
        dones = torch.FloatTensor(batch.done).to(self.device)
        
        # Current Q values
        current_q = self.policy_net(states).gather(1, actions.unsqueeze(1))
        
        # Target Q values
        with torch.no_grad():
            next_q = self.target_net(next_states).max(1)[0]
            target_q = rewards + self.gamma * next_q * (1 - dones)
        
        # Loss and backprop
        loss = self.criterion(current_q.squeeze(), target_q)
        self.optimizer.zero_grad()
        loss.backward()
        torch.nn.utils.clip_grad_norm_(self.policy_net.parameters(), 1.0)
        self.optimizer.step()
        
        # Update target network
        self.steps += 1
        if self.steps % self.target_update == 0:
            self.target_net.load_state_dict(self.policy_net.state_dict())
        
        # Decay epsilon
        self.epsilon = max(self.epsilon_end, self.epsilon * self.epsilon_decay)
        
        return loss.item()
    
    def save_model(self, path):
        """Save model checkpoint."""
        torch.save({
            'policy_net': self.policy_net.state_dict(),
            'optimizer': self.optimizer.state_dict(),
            'epsilon': self.epsilon,
            'steps': self.steps
        }, path)
        print(f"Model saved to {path}")
    
    def load_model(self, path):
        """Load model checkpoint."""
        checkpoint = torch.load(path, map_location=self.device)
        self.policy_net.load_state_dict(checkpoint['policy_net'])
        self.target_net.load_state_dict(checkpoint['policy_net'])
        self.optimizer.load_state_dict(checkpoint['optimizer'])
        self.epsilon = checkpoint.get('epsilon', 0.1)
        self.steps = checkpoint.get('steps', 0)
        print(f"Model loaded from {path}")
    
    def export_onnx(self, path, state_dim=50):
        """Export model to ONNX format."""
        self.policy_net.eval()
        dummy_input = torch.randn(1, state_dim).to(self.device)
        
        torch.onnx.export(
            self.policy_net,
            dummy_input,
            path,
            export_params=True,
            opset_version=14,
            input_names=['state'],
            output_names=['q_values'],
            dynamic_axes={
                'state': {0: 'batch_size'},
                'q_values': {0: 'batch_size'}
            }
        )
        print(f"ONNX model exported to {path}")


def load_training_data(data_path):
    """Load training data from JSON files."""
    samples = []
    
    for json_file in Path(data_path).glob('**/*.json'):
        try:
            with open(json_file, 'r', encoding='utf-8') as f:
                data = json.load(f)
                if 'samples' in data:
                    samples.extend(data['samples'])
        except Exception as e:
            print(f"Error loading {json_file}: {e}")
    
    print(f"Loaded {len(samples)} training samples")
    return samples


def load_feedback_data(feedback_path):
    """Load user feedback data."""
    feedback = []
    
    for json_file in Path(feedback_path).glob('**/*.json'):
        try:
            with open(json_file, 'r', encoding='utf-8') as f:
                data = json.load(f)
                if isinstance(data, list):
                    feedback.extend(data)
        except Exception as e:
            print(f"Error loading {json_file}: {e}")
    
    print(f"Loaded {len(feedback)} feedback records")
    return feedback


def normalize(value, max_value):
    if max_value <= 0:
        return 0.0
    return max(0.0, min(1.0, float(value) / max_value))


def normalize_angle(degrees):
    # Normalize to [0, 1] using radians in [0, pi]
    radians = (degrees or 0.0) * np.pi / 180.0
    radians = abs(radians)
    while radians > np.pi:
        radians -= np.pi
    return radians / np.pi


def encode_density(density):
    if not density:
        return 0.5
    d = density.strip().lower()
    if d == "low":
        return 0.0
    if d == "high":
        return 1.0
    return 0.5


def get_category_index(category):
    if category in CATEGORY_INDEX:
        return CATEGORY_INDEX[category]
    return CATEGORY_INDEX["Other"]


def extract_features_from_sample(sample):
    element = sample.get("element", {})
    context = sample.get("context", {})

    features = np.zeros(20, dtype=np.float32)
    idx = 0

    # Category one-hot (0-7)
    cat_idx = get_category_index(element.get("category", "Other"))
    for i in range(8):
        features[idx] = 1.0 if i == cat_idx else 0.0
        idx += 1

    # Geometry (8-12)
    features[idx] = normalize_angle(element.get("orientation", 0.0)); idx += 1
    features[idx] = normalize(element.get("length", 0.0), MAX_LENGTH); idx += 1
    features[idx] = normalize(element.get("width", 0.0), MAX_WIDTH); idx += 1
    features[idx] = normalize(element.get("height", 0.0), MAX_HEIGHT); idx += 1
    features[idx] = 1.0 if element.get("isLinear", False) else 0.0; idx += 1

    # Context (13-19)
    features[idx] = encode_density(context.get("density", "medium")); idx += 1
    features[idx] = 1.0 if context.get("hasNeighborAbove", False) else 0.0; idx += 1
    features[idx] = 1.0 if context.get("hasNeighborBelow", False) else 0.0; idx += 1
    features[idx] = 1.0 if context.get("hasNeighborLeft", False) else 0.0; idx += 1
    features[idx] = 1.0 if context.get("hasNeighborRight", False) else 0.0; idx += 1
    features[idx] = normalize(context.get("distanceToWall", 0.0), MAX_DISTANCE); idx += 1
    features[idx] = normalize(context.get("parallelElementsCount", 0.0), 10.0); idx += 1

    return features


def build_policy_key(sample):
    element = sample.get("element", {})
    context = sample.get("context", {})

    category = (element.get("category") or "").strip()
    system_type = (element.get("systemType") or "").strip()
    density = (context.get("density") or "medium").strip().lower()

    parallel = context.get("parallelElementsCount", 0) or 0
    if parallel <= 0:
        parallel_bucket = "0"
    elif parallel <= 2:
        parallel_bucket = "1-2"
    else:
        parallel_bucket = "3+"

    orientation = abs((element.get("orientation") or 0.0) % 180.0)
    if orientation <= 15 or orientation >= 165:
        orientation_bucket = "H"
    elif abs(orientation - 90) <= 15:
        orientation_bucket = "V"
    else:
        orientation_bucket = "D"

    is_linear = element.get("isLinear", False)
    return f"{category}|{system_type}|{density}|{parallel_bucket}|{orientation_bucket}|{is_linear}"


def export_policy_json(trainer, samples, output_path):
    if not samples:
        return

    policy_accum = {}
    policy_count = {}

    trainer.policy_net.eval()

    with torch.no_grad():
        for sample in samples:
            key = build_policy_key(sample)
            features = extract_features_from_sample(sample)
            knn_scores = [0.0] * 5

            state = np.zeros(TagPlacementEnvironment.STATE_DIM, dtype=np.float32)
            state[:20] = features[:20]
            state[20:25] = knn_scores[:5]

            state_tensor = torch.FloatTensor(state).unsqueeze(0).to(trainer.device)
            q_values = trainer.policy_net(state_tensor).cpu().numpy().flatten()

            if key not in policy_accum:
                policy_accum[key] = np.zeros_like(q_values)
                policy_count[key] = 0

            policy_accum[key] += q_values
            policy_count[key] += 1

    policies = []
    for key, scores in policy_accum.items():
        count = policy_count.get(key, 1)
        avg_scores = (scores / max(count, 1)).tolist()
        action_scores = {ACTION_NAMES[i]: float(avg_scores[i]) for i in range(len(ACTION_NAMES))}

        # Split key for metadata
        parts = key.split("|")
        if len(parts) < 6:
            continue

        policies.append({
            "key": key,
            "category": parts[0],
            "systemType": parts[1],
            "density": parts[2],
            "parallelBucket": parts[3],
            "orientationBucket": parts[4],
            "isLinear": parts[5] == "True",
            "sampleCount": count,
            "actionScores": action_scores
        })

    policy_file = {
        "version": "1.0",
        "updatedAt": "",
        "policies": policies
    }

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(policy_file, f, indent=2)

    print(f"RL policy exported to {output_path}")


def main():
    parser = argparse.ArgumentParser(description='Train DQN for SmartTag placement')
    parser.add_argument('--data', type=str, default='../src/SmartTag/Data/Training/annotated',
                        help='Path to training data')
    parser.add_argument('--feedback', type=str, default=None,
                        help='Path to user feedback data')
    parser.add_argument('--finetune', type=str, default=None,
                        help='Path to existing model for fine-tuning')
    parser.add_argument('--output', type=str, default='../src/SmartTag/Data/Models',
                        help='Output directory for models')
    parser.add_argument('--epochs', type=int, default=100,
                        help='Number of training epochs')
    parser.add_argument('--batch_size', type=int, default=64,
                        help='Batch size')
    parser.add_argument('--lr', type=float, default=1e-4,
                        help='Learning rate')
    parser.add_argument('--max_samples', type=int, default=0,
                        help='Limit number of training samples (0 = all)')
    parser.add_argument('--sample_seed', type=int, default=42,
                        help='Random seed for sample limit')
    
    args = parser.parse_args()
    
    # Create output directory
    os.makedirs(args.output, exist_ok=True)
    
    # Initialize trainer
    trainer = DQNTrainer(
        batch_size=args.batch_size,
        lr=args.lr
    )
    
    # Load existing model for fine-tuning
    if args.finetune:
        trainer.load_model(args.finetune)
    
    # Load training data
    training_samples = load_training_data(args.data)
    if args.max_samples and args.max_samples > 0 and len(training_samples) > args.max_samples:
        random.seed(args.sample_seed)
        training_samples = random.sample(training_samples, args.max_samples)
        print(f"Using {len(training_samples)} sampled training records")
    
    # Load feedback if provided
    feedback_samples = []
    if args.feedback:
        feedback_samples = load_feedback_data(args.feedback)
    
    # Training loop
    env = TagPlacementEnvironment()
    
    print(f"\nStarting training for {args.epochs} epochs...")
    
    for epoch in range(args.epochs):
        epoch_loss = 0
        num_steps = 0
        
        # Train on annotated data
        for sample in training_samples:
            element = sample.get('element', {})
            context = sample.get('context', {})
            tag = sample.get('tag', {})

            # Create state from features
            features = extract_features_from_sample(sample)
            knn_scores = [0.0, 0.0, 0.0, 0.0, 0.0]
            state = env.reset(features, knn_scores, [], [])

            # Determine action from ground truth position
            position = tag.get('position', 'TopRight')
            action = POSITION_ACTIONS.get(position, 0)

            # Calculate reward
            reward = env.REWARDS['no_collision']
            if tag.get('alignedWithRow'):
                reward += env.REWARDS['aligned']
            if tag.get('alignedWithColumn'):
                reward += env.REWARDS['aligned']
            if tag.get('hasLeader'):
                leader_len = float(tag.get('leaderLength', 0.0) or 0.0)
                reward += max(env.REWARDS['short_leader'] - leader_len, 0.0)
            
            # Store experience
            next_state = state.copy()
            done = True
            
            trainer.replay_buffer.push(
                Experience(state, action, reward, next_state, done)
            )
            
            # Train
            loss = trainer.train_step()
            if loss is not None:
                epoch_loss += loss
                num_steps += 1
        
        # Report progress
        avg_loss = epoch_loss / max(num_steps, 1)
        print(f"Epoch {epoch + 1}/{args.epochs}, Loss: {avg_loss:.4f}, Epsilon: {trainer.epsilon:.4f}")
        
        # Save checkpoint periodically
        if (epoch + 1) % 10 == 0:
            trainer.save_model(os.path.join(args.output, f'checkpoint_epoch_{epoch + 1}.pt'))
    
    # Save final model
    trainer.save_model(os.path.join(args.output, 'placement_policy.pt'))
    
    # Export to ONNX
    trainer.export_onnx(os.path.join(args.output, 'placement_policy.onnx'))

    # Export policy JSON (for C# runtime)
    policy_output = os.path.join(args.output, 'rl_policy.json')
    export_policy_json(trainer, training_samples, policy_output)
    
    # Also place policy next to training data if possible
    try:
        data_dir = Path(args.data).resolve().parent
        if data_dir.name.lower() == "training":
            export_policy_json(trainer, training_samples, str(data_dir / "rl_policy.json"))
    except Exception:
        pass
    
    print("\nTraining complete!")


if __name__ == '__main__':
    main()
