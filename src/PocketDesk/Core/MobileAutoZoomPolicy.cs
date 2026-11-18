namespace PocketDesk.Core;

/// <summary>Tracks one automatic zoom step for one visit to a mobile workspace window.
/// The recovery journal, separately, owns the net total of all confirmed steps.</summary>
public sealed class MobileAutoZoomPolicy
{
    private bool? lastRequest;
    private bool ownsStep;
    private int manualIncreasesAfterStep;

    public void Reset()
    {
        lastRequest = null;
        NoteManuallyRestored();
    }

    public void NoteManuallyRestored()
    {
        ownsStep = false;
        manualIncreasesAfterStep = 0;
    }

    public int Apply(bool enabled, int currentSteps, Action<int> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (lastRequest == enabled) return 0;
        lastRequest = enabled;
        if (enabled == ownsStep || (enabled && currentSteps >= 8)) return 0;
        int delta = enabled ? 1 : -1;
        // Don't repeatedly send a shortcut after a failure; a fresh user toggle
        // or a new visit may retry. Ownership changes only after confirmed input.
        change(delta);
        ownsStep = enabled;
        manualIncreasesAfterStep = 0;
        return delta;
    }

    public void NoteManualChange(int delta)
    {
        if (!ownsStep) return;
        manualIncreasesAfterStep += delta;
        if (manualIncreasesAfterStep >= 0) return;
        // A manual minus already consumed the automatic plus. Disabling the
        // preference now must not send another minus to this application.
        ownsStep = false;
        manualIncreasesAfterStep = 0;
    }
}
