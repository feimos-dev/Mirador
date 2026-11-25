namespace Mirador.Pimax;

public sealed record BlobDecision(string Variant, bool NeedsOwner, DreamAirState State);

public static class BlobChoice
{
    public static BlobDecision Resolve(DreamAirState s, string? detected)
    {
        if (detected is not null && EyeBlobVariants.All.Contains(detected))
        {
            return new BlobDecision(detected, false, s with { BlobVariant = detected, BlobTrackerSerial = null });
        }

        if (s.BlobVariant is { } remembered && EyeBlobVariants.All.Contains(remembered))
        {
            return new BlobDecision(remembered, false, s);
        }

        return new BlobDecision(EyeBlobVariants.Fallback, true, s);
    }

    public static DreamAirState AfterUpload(DreamAirState s, string? trackerSerial)
    {
        if (string.IsNullOrEmpty(trackerSerial) || s.BlobTrackerSerial == trackerSerial)
        {
            return s;
        }

        return s.BlobTrackerSerial is null
            ? s with { BlobTrackerSerial = trackerSerial }
            : s with { BlobTrackerSerial = trackerSerial, BlobVariant = null };
    }

    public static bool Cleared(DreamAirState before, DreamAirState after) => before.BlobVariant is not null && after.BlobVariant is null;
}
