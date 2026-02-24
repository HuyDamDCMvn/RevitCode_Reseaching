using System;
using System.Collections.Generic;
using SmartTag.Models;

namespace SmartTag.ML
{
    /// <summary>
    /// Calculates reward signals for RL-based tag placement.
    /// Immediate rewards are computed automatically; deferred rewards come from user feedback.
    /// </summary>
    public static class RewardCalculator
    {
        // Immediate rewards (auto-evaluated)
        public const double NoCollision = 10.0;
        public const double AlignedWithExisting = 5.0;
        public const double ShortLeader = 3.0;
        public const double CollisionWithTag = -20.0;
        public const double CollisionWithElement = -15.0;
        public const double LeaderCrossesElement = -10.0;

        // Deferred rewards (from user feedback)
        public const double UserApproved = 50.0;
        public const double UserRejected = -50.0;
        public const double UserAdjustedBase = -10.0;

        /// <summary>
        /// Calculate immediate reward for a placement against existing context.
        /// </summary>
        public static double CalculateImmediate(
            TagPlacement placement,
            List<TagPlacement> existingPlacements,
            List<BoundingBox2D> elementBounds)
        {
            if (placement == null)
                return -30.0;

            double reward = 0.0;

            bool hasTagCollision = false;
            bool hasElementCollision = false;

            if (existingPlacements != null)
            {
                foreach (var existing in existingPlacements)
                {
                    if (placement.EstimatedTagBounds.Intersects(existing.EstimatedTagBounds))
                    {
                        hasTagCollision = true;
                        break;
                    }
                }
            }

            if (elementBounds != null && placement.HasLeader)
            {
                var leaderBounds = new BoundingBox2D(
                    Math.Min(placement.TagLocation.X, placement.LeaderEnd.X),
                    Math.Min(placement.TagLocation.Y, placement.LeaderEnd.Y),
                    Math.Max(placement.TagLocation.X, placement.LeaderEnd.X),
                    Math.Max(placement.TagLocation.Y, placement.LeaderEnd.Y));

                foreach (var eBounds in elementBounds)
                {
                    if (leaderBounds.Intersects(eBounds))
                    {
                        hasElementCollision = true;
                        break;
                    }
                }
            }

            reward += hasTagCollision ? CollisionWithTag : NoCollision;

            if (hasElementCollision)
                reward += LeaderCrossesElement;

            if (existingPlacements != null && existingPlacements.Count > 0)
            {
                bool aligned = IsAlignedWithAny(placement, existingPlacements, tolerance: 0.5);
                if (aligned)
                    reward += AlignedWithExisting;
            }

            if (placement.HasLeader)
            {
                var leaderLen = placement.TagLocation.DistanceTo(placement.LeaderEnd);
                if (leaderLen < 3.0)
                    reward += ShortLeader;
            }

            return reward;
        }

        /// <summary>
        /// Calculate deferred reward from user feedback.
        /// </summary>
        public static double CalculateFromFeedback(FeedbackType type, double adjustmentDistance = 0)
        {
            return type switch
            {
                FeedbackType.Approved => UserApproved,
                FeedbackType.Rejected => UserRejected,
                FeedbackType.Adjusted => UserAdjustedBase * (1.0 + adjustmentDistance),
                _ => 0.0
            };
        }

        private static bool IsAlignedWithAny(
            TagPlacement placement,
            List<TagPlacement> existing,
            double tolerance)
        {
            foreach (var other in existing)
            {
                if (Math.Abs(placement.TagLocation.X - other.TagLocation.X) < tolerance)
                    return true;
                if (Math.Abs(placement.TagLocation.Y - other.TagLocation.Y) < tolerance)
                    return true;
            }
            return false;
        }
    }

    public enum FeedbackType
    {
        Approved,
        Rejected,
        Adjusted
    }
}
