using CommunityToolkit.Mvvm.ComponentModel;
using Mirador.App.Services;
using Mirador.Pimax;

namespace Mirador.App.ViewModels;

/// <summary>An eye geometry variant in the list; the tested one says so.</summary>
public sealed record VariantItem(string Id)
{
    public override string ToString() => Id == EyeBlobVariants.Tested ? $"{Id} (tested)" : Id;
}

/// <summary>The Eye tracking page (Dream Air profiles only): the eye geometry variant Mirador uploads.</summary>
public sealed partial class EyeTrackingViewModel : ObservableObject
{
    private readonly AppHost host;

    public EyeTrackingViewModel(AppHost host)
    {
        this.host = host;
        Refresh();
    }

    /// <summary>The variant card's description: where the variant came from, and why it matters.</summary>
    public string VariantDescription =>
        $"{VariantSource.TrimEnd('.')}. The eye tracker's geometry differs between variants; Mirador uploads the one for yours at every Start.";

    public IReadOnlyList<VariantItem> Variants { get; } = [.. EyeBlobVariants.All.Select(v => new VariantItem(v))];

    [ObservableProperty]
    public partial VariantItem? SelectedVariant { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VariantDescription))]
    public partial string VariantSource { get; set; } = "";

    /// <summary>What went wrong saving the variant; null when nothing did.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    public bool HasMessage => Message is not null;

    public void DismissMessage() => Message = null;

    /// <summary>The owner picked a variant: it is theirs from now on, until Pimax's software says otherwise.</summary>
    partial void OnSelectedVariantChanged(VariantItem? value)
    {
        if (loadingVariant || value is null)
        {
            return;
        }

        try
        {
            host.DreamAirState.Update(s => BlobChoice.Chosen(s, value.Id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Mirador couldn't save the variant ({e.Message.Trim().TrimEnd('.')}).";
        }

        ShowVariant(host.DreamAirState.Load());
    }

    private bool loadingVariant;

    private void ShowVariant(DreamAirState state)
    {
        var decision = BlobChoice.Resolve(state, null);
        loadingVariant = true;
        try
        {
            // Nothing picked while unknown: the owner can then pick the tested one too.
            SelectedVariant = decision.NeedsOwner ? null : Variants.FirstOrDefault(v => v.Id == decision.Variant);
        }
        finally
        {
            loadingVariant = false;
        }

        VariantSource = decision.NeedsOwner
            ? "Not known yet: pick yours, or start Pimax Play once and Mirador reads it."
            : state.BlobSource == BlobChoice.FromPimax ? "Read from Pimax Play" : "Chosen by you";
    }

    /// <summary>Reads what Mirador remembers (the variant) again.</summary>
    public void Refresh()
    {
        DreamAirState state;
        try
        {
            state = host.DreamAirState.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            state = new DreamAirState();
        }

        ShowVariant(state);
    }
}
