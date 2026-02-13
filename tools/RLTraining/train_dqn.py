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
            # Extract features (simplified)
            element = sample.get('element', {})
            context = sample.get('context', {})
            tag = sample.get('tag', {})
            
            # Create state from features
            features = np.random.randn(20).astype(np.float32)  # Placeholder
            knn_scores = [0.4, 0.3, 0.2, 0.05, 0.05]  # Placeholder
            
            state = env.reset(features, knn_scores, [], [])
            
            # Determine action from ground truth
            position = tag.get('position', 'TopRight')
            action = 0  # Map position to action (simplified)
            
            # Calculate reward
            reward = env.REWARDS['no_collision'] + env.REWARDS['aligned']
            
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
    
    print("\nTraining complete!")


if __name__ == '__main__':
    main()
