namespace SGG.UnityMCP.Editor.Commands
{
    internal static class ProfilerFrameReadPolicy
    {
        internal const string Selected = "selected";
        internal const string Authoritative = "authoritative";
        internal const string Invalid = "invalid";
        internal const string Unsettled = "unsettled";
        internal const string Unavailable = "unavailable";

        internal static string SelectFrame(
            bool apiAvailable,
            bool rangeAvailable,
            int firstFrameIndex,
            int lastFrameIndex,
            int requestedFrameIndex,
            out int selectedFrameIndex)
        {
            selectedFrameIndex = -1;
            if (!apiAvailable || !rangeAvailable) return Unavailable;
            selectedFrameIndex = requestedFrameIndex >= 0 ? requestedFrameIndex : lastFrameIndex;
            if (firstFrameIndex < 0 || lastFrameIndex < firstFrameIndex) return Unsettled;
            if (selectedFrameIndex < firstFrameIndex || selectedFrameIndex > lastFrameIndex) return Invalid;
            return Selected;
        }

        internal static string ClassifyData(string selectionState, bool threadCountAvailable, int threadCount)
        {
            if (selectionState != Selected) return selectionState;
            if (!threadCountAvailable) return Unavailable;
            return threadCount > 0 ? Authoritative : Unsettled;
        }

        internal static string ResultState(string selectionState, bool truncated, int matchedMarkers)
        {
            if (selectionState != Authoritative) return selectionState;
            if (matchedMarkers > 0) return "authoritative_nonempty";
            return truncated ? "authoritative_bounded_empty" : "authoritative_empty";
        }

        internal static bool TraversalLimitReached(int attempts, int maxAttempts, bool traversalCompleted, bool traversalFailed)
        {
            return !traversalCompleted && !traversalFailed && maxAttempts > 0 && attempts >= maxAttempts;
        }

        internal static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        internal static bool IsAuthoritativeResultEvidence(string selectionState, string resultState)
        {
            return selectionState == Authoritative
                && (resultState == "authoritative_empty"
                    || resultState == "authoritative_bounded_empty"
                    || resultState == "authoritative_nonempty");
        }
    }
}
