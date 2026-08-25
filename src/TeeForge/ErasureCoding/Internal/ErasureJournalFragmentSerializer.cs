namespace TeeForge.ErasureCoding.Internal;

internal static class ErasureJournalFragmentSerializer
{
    internal static bool TryRead(
        ReadOnlySpan<byte> preparePage,
        ReadOnlySpan<byte> localPayload,
        ReadOnlySpan<byte> commitPage,
        int shardSize,
        out ErasureJournalFragment? fragment,
        int journalSlotIndex = -1)
    {
        fragment = null;
        if (commitPage.Length < ErasureFormatV1.PageSize ||
            !ErasureJournalPreparePageSerializer.TryRead(
            preparePage,
            localPayload,
            shardSize,
            out ErasureJournalPreparePage prepare,
            out ErasureJournalRange[] ranges,
            out UInt128 prepareHash))
        {
            return false;
        }

        ErasureJournalCommitPage? commit = null;
        ReadOnlySpan<byte> commitPageBytes = commitPage[..ErasureFormatV1.PageSize];
        if (!ErasureFormatHash.IsAllZero(commitPageBytes))
        {
            if (!ErasureJournalCommitPageSerializer.TryRead(commitPageBytes, out ErasureJournalCommitPage candidate, out _) ||
                !ErasureJournalCommitPageSerializer.MatchesPreparePage(candidate, prepare, prepareHash))
            {
                return false;
            }

            commit = candidate;
        }

        fragment = new ErasureJournalFragment(
            prepare,
            ranges,
            localPayload.ToArray(),
            prepareHash,
            commit,
            journalSlotIndex);
        return true;
    }
}
