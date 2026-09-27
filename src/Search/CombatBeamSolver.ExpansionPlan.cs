using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private readonly record struct ExpansionPlan(SearchNode Parent, bool PrepareChoiceMetadata);

    private IEnumerable<(PreparedCardAction Action, PredictedCard Card)> EnumeratePlannedCardActions(
        ExpansionPlan plan)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SearchNode node = plan.Parent;
        SimulationSnapshot snapshot = node.Snapshot;
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        if (snapshot.PlayerDead || snapshot.AllEnemiesDead)
            yield break;

        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        IReadOnlyList<PredictedCard> hand = playerState.Hand.Cards;
        HandFingerprintBuffer seenCards = default;
        int seenCardCount = 0;
        for (int handIndex = 0; handIndex < hand.Count; handIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PredictedCard card = hand[handIndex];
            string cardId = card.Preview.Id.Entry;
            int occurrence = 0;
            for (int priorIndex = 0; priorIndex < handIndex; priorIndex++)
            {
                if (string.Equals(hand[priorIndex].Preview.Id.Entry, cardId, StringComparison.Ordinal))
                    occurrence++;
            }
            if (!simulatedCombat.CanPlayCard(simulator, card))
                continue;
            StateFingerprint playableKey = BuildPlayableCardKey(card);
            bool duplicate = false;
            for (int seenIndex = 0; seenIndex < seenCardCount; seenIndex++)
            {
                if (seenCards[seenIndex] == playableKey)
                {
                    duplicate = true;
                    break;
                }
            }
            if (duplicate)
            {
                _run.DuplicateCardBranchesPruned++;
                continue;
            }
            seenCards[seenCardCount++] = playableKey;
            string cardStateKey = CardChoiceSupport.ChoiceCardKey(card);
            bool requiresUnsupportedExistingChoice = plan.PrepareChoiceMetadata
                && CardChoiceSupport.RequiresUnsupportedExistingChoice(card.Preview);
            PlanCardChoice? requiredEmptyChoice = plan.PrepareChoiceMetadata
                ? CardChoiceSupport.BuildRequiredEmptyChoice(card.Preview)
                : null;
            int cardStateOccurrence = 0;
            for (int priorIndex = 0; priorIndex < handIndex; priorIndex++)
            {
                if (string.Equals(
                        CardChoiceSupport.ChoiceCardKey(hand[priorIndex]),
                        cardStateKey,
                        StringComparison.Ordinal))
                {
                    cardStateOccurrence++;
                }
            }
            foreach ((int targetIndex, Creature? target) in TargetsFor(card, simulator))
            {
                if (node.ActionCount == 0 && !card.Original.CanPlayTargeting(target))
                    continue;
                string cardTitle;
                string targetName;
                if (plan.PrepareChoiceMetadata)
                {
                    cardTitle = displayNames.Card(card.Preview);
                    targetName = displayNames.Creature(target, simulatedCombat.KnownEnemies);
                }
                else
                {
                    targetName = displayNames.Creature(target, simulatedCombat.KnownEnemies);
                    cardTitle = displayNames.Card(card.Preview);
                }
                PlanAction action = new(
                    PlanActionKind.PlayCard,
                    node.Turn,
                    card.Preview.Id.Entry,
                    occurrence,
                    targetIndex,
                    target?.CombatId,
                    cardTitle,
                    targetName,
                    ReplayCount: Math.Max(0, card.Preview.GetEnchantedReplayCount()),
                    CardStateKey: cardStateKey,
                    CardStateOccurrence: cardStateOccurrence,
                    CardEnchantmentId: card.Preview.Enchantment?.Id.Entry ?? "",
                    CardUpgradeLevel: card.Preview.CurrentUpgradeLevel);
                yield return (new PreparedCardAction(
                    action,
                    card.Preview.Type,
                    target?.CombatId,
                    requiresUnsupportedExistingChoice,
                    requiredEmptyChoice), card);
            }
        }
    }

    private IEnumerable<PreparedPotionAction> EnumeratePlannedPotionActions(ExpansionPlan plan)
    {
        SearchNode node = plan.Parent;
        SimulationSnapshot snapshot = node.Snapshot;
        if (snapshot.PlayerDead || snapshot.AllEnemiesDead
            || _earliestPotionTurn is { } earliestTurn && node.Turn < earliestTurn
            || _maximumPotionUses != null
                && ExplicitPotionUseCount(node) >= _maximumPotionUses.Value)
        {
            yield break;
        }

        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        for (int potionSlot = 0; potionSlot < root.PotionSlotCount; potionSlot++)
        {
            PotionModel? potion = simulatedCombat.GetPotionAtSlot(_player, potionSlot);
            if (potion == null
                || !simulatedCombat.IsPotionAvailable(_player, potionSlot)
                || !PotionOnUseSupport.CanSearch(potion)
                || !AllowsPotionUse(potionSlot, potion.Id.Entry))
            {
                continue;
            }

            foreach ((int targetIndex, Creature? target) in TargetsForPotion(potion, simulator))
            {
                PlanAction baseAction = new(
                    PlanActionKind.UsePotion,
                    node.Turn,
                    TargetIndex: targetIndex,
                    TargetCombatId: target?.CombatId,
                    TargetName: displayNames.Creature(target,
                        ((SimulatedCombatState)simulator.State.CombatState).KnownEnemies),
                    PotionSlot: potionSlot,
                    PotionId: potion.Id.Entry,
                    PotionTitle: displayNames.Potion(potion));
                yield return new PreparedPotionAction(baseAction, potion);
            }
        }
    }
}
