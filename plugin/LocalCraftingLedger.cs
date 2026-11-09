using System;
using System.Collections.Generic;
using System.Linq;

internal interface ICraftingLedger
{
    long DustBalance { get; }
    IReadOnlyDictionary<string, int> OwnedCounts();
    int OwnedCount(string cardId);
    bool PackRevealPending { get; }
    CraftPlan CommitBatch(string cardId, CraftDirection direction, int count, int expectedOwnedCount);
    CraftPlan PlanMassDisenchant();
    CraftPlan CommitMassDisenchant();
}

internal static class CraftingLedgerRegistry
{
    internal static ICraftingLedger Current = null;
}

internal sealed class LocalCraftingLedger : ICraftingLedger
{
    private readonly LocalProfile profile;

    internal LocalCraftingLedger(LocalProfile profile) => this.profile = profile ?? throw new ArgumentNullException(nameof(profile));

    public long DustBalance => profile.DustBalance;
    public bool PackRevealPending => profile.PackRevealPending;
    public IReadOnlyDictionary<string, int> OwnedCounts() => profile.Collection;
    public int OwnedCount(string cardId) => OwnedCounts().TryGetValue(cardId, out int count) ? count : 0;

    private static CraftLedgerView View(long dust, IReadOnlyDictionary<string, int> counts,
        IReadOnlyDictionary<string, int> acquired, bool pending) => new CraftLedgerView {
            Dust = dust, Counts = counts, Acquired = acquired, PackRevealPending = pending
        };

    public CraftPlan CommitBatch(string cardId, CraftDirection direction, int count, int expectedOwnedCount) =>
        Commit(view => LocalCrafting.PlanBatch(view, cardId, direction, count, expectedOwnedCount),
            direction == CraftDirection.Craft ? LocalCraftOp.Craft : LocalCraftOp.Disenchant);

    public CraftPlan PlanMassDisenchant() => LocalCrafting.PlanMassDisenchant(
        View(profile.DustBalance, profile.Collection, profile.Acquired, profile.PackRevealPending));

    public CraftPlan CommitMassDisenchant() => Commit(LocalCrafting.PlanMassDisenchant, LocalCraftOp.MassDisenchant);

    private CraftPlan Commit(Func<CraftLedgerView, CraftPlan> plan, LocalCraftOp op)
    {
        try
        {
            var preview = plan(View(profile.DustBalance, profile.Collection, profile.Acquired, profile.PackRevealPending));
            if (!preview.Ok) return preview;
            return profile.CommitChange(next => {
                var committed = plan(View(profile.InfiniteDust ? ProfileCatalog.UnlimitedDust : next.DustBalance,
                    next.Collection.ToDictionary(card => card.Id, card => card.Count),
                    next.Collection.ToDictionary(card => card.Id, card => card.Acquired), next.PackRevealPending));
                if (!committed.Ok) throw new InvalidOperationException(committed.Error);
                var cardsById = next.Collection.ToDictionary(card => card.Id, StringComparer.Ordinal);
                foreach (var delta in committed.Cards)
                {
                    if (!cardsById.TryGetValue(delta.CardId, out var card))
                    {
                        card = new LocalCardCount { Id = delta.CardId };
                        next.Collection.Add(card);
                        cardsById.Add(delta.CardId, card);
                    }
                    card.Count = checked(card.Count + delta.CountDelta);
                    card.Acquired = delta.AcquiredAfter;
                    // The player crafted it in the card view: native adds it seen (seenBefore), so the save does too.
                    LocalProfile.SeeOnAcquire(next, delta.CardId, 0, delta.CountDelta);
                }
                if (!profile.InfiniteDust || committed.DustDelta > 0)
                    next.DustBalance = checked(next.DustBalance + committed.DustDelta);
                next.CraftSequence = checked(next.CraftSequence + 1);
                next.LastCraft = new LocalCraftReceipt {
                    Sequence = next.CraftSequence, Op = op, DustDelta = committed.DustDelta, Undoable = false, TokenDelta = 0,
                    Cards = committed.Cards.Select(delta => new LocalCraftCard {
                        CardId = delta.CardId, CountDelta = delta.CountDelta, AcquiredBefore = delta.AcquiredBefore
                    }).ToList()
                };
                if (op == LocalCraftOp.Craft) {
                    LocalProfile.TrackCraftedAchievementCards(next, committed.Cards.Where(card => card.CountDelta > 0).Select(card => card.CardId));
                    LocalProfile.ReconcileCollectionAchievement(next);
                }
                return committed;
            });
        }
        catch (Exception error) { return new CraftPlan { Error = error.Message }; }
    }
}
