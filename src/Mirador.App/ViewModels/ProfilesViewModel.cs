using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.Build;
using Mirador.Core.Elevation;
using Mirador.Core.Profiles;
using Mirador.Core.Session;
using Mirador.Pimax;

namespace Mirador.App.ViewModels;

public sealed record HeadsetItem(string? Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record DirectModeItem(DirectModeKind Kind, string Name)
{
    public override string ToString() => Name;
}

public sealed record EyeVariantItem(string? Id, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class EnvRow : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Value { get; set; } = "";
}

public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly AppHost host;
    private Profile? editing;
    private bool loading;

    private bool? lighthouse;

    public ProfilesViewModel(AppHost host)
    {
        this.host = host;
        Build = new BuildViewModel(host, App.Current.Ui);
        Build.PropertyChanged += OnBuildStateChanged;
        Verify = new VerifyViewModel(host, App.Current.Ui);
        Verify.Changed += RefreshApproval;
        MiradorMonado = new MiradorMonadoViewModel(host);
        Headsets.Add(new HeadsetItem(null, "None"));
        foreach (var module in host.Modules.Values)
        {
            Headsets.Insert(Headsets.Count - 1, new HeadsetItem(module.Id, module.DisplayName));
        }

        EyeVariants.Add(new EyeVariantItem(null, "Automatic"));
        EyeVariants.Add(new EyeVariantItem(EyeVariantChoice.None, "None"));
        foreach (var variant in EyeBlobVariants.All)
        {
            EyeVariants.Add(new EyeVariantItem(variant, variant));
        }

        DirectModes.Add(new DirectModeItem(DirectModeKind.None, "None"));
        foreach (var layer in host.DirectModes.Values)
        {
            DirectModes.Add(new DirectModeItem(layer.Kind, layer.DisplayName));
        }

        Extra.CollectionChanged += (_, e) =>
        {
            foreach (EnvRow row in e.NewItems ?? Array.Empty<EnvRow>())
            {
                row.PropertyChanged += OnRowChanged;
            }

            foreach (EnvRow row in e.OldItems ?? Array.Empty<EnvRow>())
            {
                row.PropertyChanged -= OnRowChanged;
            }

            Changed();
        };
    }

    public ObservableCollection<ProfileItem> Profiles { get; } = [];

    public static readonly ProfileItem AddItem = new("", "Add a profile…");

    public string PickerDescription => IsDirty ? "Save or revert to pick another." : "";

    public bool CanDelete =>
        !IsDirty
        && Selected is { } selected && selected != AddItem
        && Profiles.Count(p => p != AddItem) > 1
        && host.Session.CurrentProfile?.Profile.Id != selected.Id
        && !IsBuilding(selected.Id);

    private bool IsBuilding(string profileId) => host.Builds.RunOf(profileId) is { Outcome: null, Cancelled: false };

    public BuildViewModel Build { get; }

    public VerifyViewModel Verify { get; }

    public MiradorMonadoViewModel MiradorMonado { get; }

    public ObservableCollection<HeadsetItem> Headsets { get; } = [];

    public ObservableCollection<DirectModeItem> DirectModes { get; } = [];

    public ObservableCollection<EyeVariantItem> EyeVariants { get; } = [];

    public ObservableCollection<EnvRow> Extra { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    public partial ProfileItem? Selected { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEyeVariant))]
    public partial HeadsetItem? Headset { get; set; }

    [ObservableProperty]
    public partial EyeVariantItem? EyeVariant { get; set; }

    public bool ShowsEyeVariant => Headset?.Id == PimaxModule.ModuleId;

    [ObservableProperty]
    public partial DirectModeItem? DirectMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonadoFromFolder), nameof(MonadoFromLink), nameof(ShowBranchCard), nameof(RunAsAdministratorDescription))]
    public partial int MonadoSource { get; set; }

    [ObservableProperty]
    public partial string MonadoLink { get; set; } = "";

    [ObservableProperty]
    public partial string MonadoBranch { get; set; } = "";

    public ObservableCollection<string> Branches { get; } = [];

    [ObservableProperty]
    public partial string? SelectedBranch { get; set; }

    partial void OnSelectedBranchChanged(string? value)
    {
        if (value is not null)
        {
            MonadoBranch = value;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBranchCard))]
    public partial bool ShowBranches { get; set; }

    public bool ShowBranchCard => ShowBranches && MonadoFromLink;

    [ObservableProperty]
    public partial bool IsListingBranches { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBranchListProblem))]
    public partial string? BranchListProblem { get; set; }

    public bool HasBranchListProblem => BranchListProblem is not null;

    private CancellationTokenSource? listing;
    private string? listingUrl;
    private BranchOffer? branchOffer;

    private BranchOffer Offer => branchOffer ??= new BranchOffer(() => host.Tools.Git, new CommandRunner());

    [ObservableProperty]
    public partial bool RunAsAdministrator { get; set; }

    public bool CanRunAsAdministrator { get; } = !SafeIsStandardAccount();

    public string RunAsAdministratorDescription
    {
        get
        {
            if (ApprovalBlocked is { } blocked)
            {
                return blocked;
            }

            if (BuildChanged)
            {
                return "This build changed since it was approved. Press Start to approve it.";
            }

            var text = MonadoSource == MiradorSource
                ? "Gives Monado realtime GPU priority."
                : "Only for a branch you trust.";
            return CanRunAsAdministrator ? text : $"{text} Needs an administrator account.";
        }
    }

    private static bool SafeIsStandardAccount()
    {
        try
        {
            return ElevatedMonado.IsStandardAccount();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    [ObservableProperty]
    public partial bool IsApproved { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunAsAdministratorDescription))]
    public partial bool BuildChanged { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunAsAdministratorDescription))]
    public partial string? ApprovalBlocked { get; set; }

    [ObservableProperty]
    public partial string MonadoOptions { get; set; } = "";

    [ObservableProperty]
    public partial string MonadoFolder { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitch), nameof(CanDelete), nameof(PickerDescription))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool HasProblems { get; set; }

    public string ProblemsText => string.Join(Environment.NewLine, Problems);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    public bool MonadoFromFolder => MonadoSource == FolderSource;

    public bool MonadoFromLink => MonadoSource == LinkSource;

    private const int MiradorSource = 0;
    private const int UpstreamSource = 1;
    private const int LinkSource = 2;
    private const int FolderSource = 3;

    public bool UsesBaseStations
    {
        get => lighthouse ?? Edited().UsesBaseStations(HeadsetUsesLighthouse());
        set
        {
            if (value == UsesBaseStations)
            {
                return;
            }

            lighthouse = value;
            Changed();
        }
    }

    public bool CanSwitch => !IsDirty;

    public bool HasMessage => Message is not null;

    public void DismissMessage() => Message = null;

    public void Attach()
    {
        host.Session.Changed += OnSessionChanged;
        host.Builds.Changed += OnBuildsChanged;
        Build.Attach();
        Verify.Attach();
        IsRunning = host.Session.State != SessionState.Stopped;
        Build.MonadoRuns = IsRunning;
        Verify.MonadoRuns = IsRunning;
        MiradorMonado.MonadoRuns = IsRunning;
        if (!IsDirty)
        {
            LoadList(keep: Selected?.Id);
        }
    }

    public void Detach()
    {
        CancelListing();
        host.Session.Changed -= OnSessionChanged;
        host.Builds.Changed -= OnBuildsChanged;
        Build.Detach();
        Verify.Detach();
    }

    private void OnBuildsChanged(string profileId) => App.Current.Ui.TryEnqueue(() => OnPropertyChanged(nameof(CanDelete)));

    private void OnBuildStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BuildViewModel.IsBuilding) or nameof(BuildViewModel.IsApproving))
        {
            RefreshApproval();
        }
    }

    public string FreeName(string wanted)
    {
        var names = Profiles.Where(p => p != AddItem).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(wanted))
        {
            return wanted;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{wanted} {i}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    public async Task DeleteAsync()
    {
        var id = Selected!.Id;
        if (IsBuilding(id))
        {
            Message = "Its Monado is being built: cancel the build, then delete the profile.";
            return;
        }

        if (Change(set => set.Delete(id), keep: null))
        {
            Message = await TakeBackUnneededAsync();

            _ = Task.Run(() => RemoveLeftovers(id));
        }
    }

    private void RemoveLeftovers(string profileId)
    {
        try
        {
            if (host.Locator.BuildsRoot is not null && host.Locator.BranchDir(profileId) is var dir && Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(dir, recursive: true);
            }

            File.Delete(host.VerifyResultFile(profileId));
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Write($"Couldn't remove what was left of a deleted profile's build ({profileId})", e);
        }
    }

    private async Task AddFromPickerAsync()
    {
        var before = editing?.Id;
        if (!await AddAsync())
        {
            LoadList(before);
        }
    }

    private async Task<bool> AddAsync()
    {
        string? module = null;
        try
        {
            var present = await Task.Run(() => host.Usb.Present());
            module = host.Modules.Values.FirstOrDefault(m => m.IsPresent(present))?.Id;
        }
        catch (Exception e)
        {
            AppLog.Write("Couldn't list the USB devices for a new profile", e);
        }

        var profile = new Profile(Profile.NewId(), FreeName("New profile"), module, BuildSource.Mirador, [], new Dictionary<string, string>());
        return Change(set => set.Add(profile), profile.Id);
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is not { } selected || selected == AddItem)
        {
            return;
        }

        string? added = null;
        Change(
            set =>
            {
                var next = set.Duplicate(selected.Id, FreeName($"{selected.Name} copy"));
                added = next.Profiles[^1].Id;
                return next;
            },
            keep: null);
        LoadList(added);
    }

    [RelayCommand]
    private void AddVariable() => Extra.Add(new EnvRow());

    [RelayCommand]
    private void RemoveVariable(EnvRow row) => Extra.Remove(row);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (editing is null)
        {
            return;
        }

        var profile = Edited();
        if (!Validate(profile))
        {
            return;
        }

        string? problem = null;
        var needsConsent = profile.RunAsAdministrator && !host.ElevatedMonadoAllowed() && host.IsInstalledCopy(askHelper: false);

        if (ElevatedChoice.ApprovesOnSave(editing, profile) && Approvable(profile) && !host.FullyApproved(profile)
            && host.ApprovalTarget(profile, out _, out _) is null)
        {
            var refusal = await host.ApproveBuildAsync(profile, alsoAllow: needsConsent);
            if (refusal == AppHost.ApprovalCancelled)
            {
                if (ElevatedChoice.KeepsSwitchWhenDeclined(editing))
                {
                    problem = "Saved. Not approved: the prompt was cancelled, so it runs as you.";
                }
                else
                {
                    profile = profile with { RunAsAdministrator = false };
                    problem = "Saved to run as you: the prompt was cancelled.";
                }
            }
            else if (refusal is not null)
            {
                problem = $"Saved. Not approved: {refusal}";
            }
        }
        else if (profile.RunAsAdministrator && ElevatedChoice.ApprovalProblem(profile) is { } blocked)
        {
            problem = $"Saved, but it runs as you: {blocked}";
        }
        else if (needsConsent)
        {
            var code = await ElevatedMonado.AllowAsync();
            if (code == 0)
            {
                host.SteamVrAllowedAgain();
            }
            else
            {
                profile = profile with { RunAsAdministrator = false };
                problem = code == ElevatedRuntime.PromptCancelled
                    ? "Saved to run as you: the prompt was cancelled."
                    : "Saved to run as you: Monado couldn't be allowed to run as administrator. mirador.log says why.";
            }
        }

        var saved = Change(set => set.Replace(profile), profile.Id);
        var takeBack = await TakeBackUnneededAsync();
        if (saved)
        {
            Message = problem ?? takeBack;
        }
    }

    private async Task<string?> TakeBackUnneededAsync()
    {
        var consent = await host.TakeBackUnneededConsentAsync();
        var approvals = await host.ForgetUnneededApprovalsAsync();
        return consent is null ? approvals : approvals is null ? consent : $"{consent} {approvals}";
    }

    private bool CanSave() => IsDirty && !HasProblems;

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Revert() => Show(editing);

    partial void OnSelectedChanged(ProfileItem? value)
    {
        if (loading)
        {
            return;
        }

        if (value == AddItem)
        {
            App.Current.Ui.TryEnqueue(() => _ = AddFromPickerAsync());
            return;
        }

        try
        {
            Show(host.Profiles.Load().Profiles.FirstOrDefault(p => p.Id == value?.Id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Mirador couldn't read its profiles ({e.Message.Trim().TrimEnd('.')}).";
        }
    }

    partial void OnNameChanged(string value) => Changed();

    partial void OnHeadsetChanged(HeadsetItem? value)
    {
        if (!loading)
        {
            DirectMode = ItemFor(DefaultDirectMode(value));
        }

        Changed();
    }

    partial void OnDirectModeChanged(DirectModeItem? value) => Changed();

    partial void OnEyeVariantChanged(EyeVariantItem? value) => Changed();

    private string AutomaticVariantName()
    {
        try
        {
            var decision = BlobChoice.Resolve(host.DreamAirState.Load(), null);
            return decision.NeedsOwner ? "Automatic" : $"Automatic ({decision.Variant})";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "Automatic";
        }
    }

    private DirectModeKind DefaultDirectMode(HeadsetItem? headset) =>
        headset?.Id is { } id && host.Modules.TryGetValue(id, out var module) ? module.DefaultDirectMode : DirectModeKind.None;

    private DirectModeItem ItemFor(DirectModeKind kind) => DirectModes.FirstOrDefault(d => d.Kind == kind) ?? DirectModes[0];

    partial void OnMonadoSourceChanged(int value)
    {
        MiradorMonado.Show(value == MiradorSource);
        Changed();
    }

    partial void OnMonadoLinkChanged(string value)
    {
        if (!loading)
        {
            try
            {
                MonadoBranch = BranchLink.Parse(value).Ref;
            }
            catch (MiradorException)
            {
            }

            ClearBranches();
        }

        Changed();
    }

    public async Task ListBranchesAsync(bool keepCurrent = false)
    {
        if (MonadoSource != LinkSource || BranchLink.NamesItsBranch(MonadoLink) || MonadoBranch.StartsWith("refs/", StringComparison.Ordinal))
        {
            ClearBranches();
            return;
        }

        string url;
        try
        {
            url = BranchLink.Parse(MonadoLink).Url;
        }
        catch (MiradorException)
        {
            ClearBranches();
            return;
        }

        if (listing is not null && url == listingUrl)
        {
            return;
        }

        listing?.Cancel();
        listing?.Dispose();
        var cts = listing = new CancellationTokenSource();
        listingUrl = url;
        IsListingBranches = true;
        BranchListProblem = null;
        try
        {
            if (await Offer.ListAsync(url, cts.Token) is not { } branches || cts.IsCancellationRequested)
            {
                return;
            }

            var savedLink = editing?.Monado.Kind == SourceKind.Branch && string.Equals(editing.Monado.Url, url, StringComparison.OrdinalIgnoreCase);
            var (items, pick) = BranchOffer.Offer(branches, MonadoBranch, keepCurrent || savedLink);
            Branches.Clear();
            foreach (var branch in items)
            {
                Branches.Add(branch);
            }

            SelectedBranch = pick;
            MonadoBranch = pick;
            ShowBranches = Branches.Count >= 2;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (GitRemoteException e)
        {
            BranchListProblem = e.Message;
        }
        finally
        {
            if (ReferenceEquals(listing, cts))
            {
                IsListingBranches = false;
                listing = null;
                listingUrl = null;
                cts.Dispose();
            }
        }
    }

    private void CancelListing()
    {
        listing?.Cancel();
        listing = null;
        listingUrl = null;
        IsListingBranches = false;
    }

    private void ClearBranches()
    {
        CancelListing();
        branchOffer?.Forget();
        SelectedBranch = null;
        Branches.Clear();
        ShowBranches = false;
        IsListingBranches = false;
        BranchListProblem = null;
    }

    partial void OnMonadoBranchChanged(string value) => Changed();

    partial void OnRunAsAdministratorChanged(bool value) => Changed();

    private static bool Approvable(Profile profile) =>
        profile.Monado.Kind is SourceKind.Branch or SourceKind.Folder
        || (profile.Monado.Kind == SourceKind.Mirador && profile.ExtraEnvironment.Count > 0);

    private void RefreshApproval()
    {
        var approvable = editing is not null && Approvable(editing);
        IsApproved = approvable && host.FullyApproved(editing!);
        ApprovalBlocked = approvable && editing!.RunAsAdministrator ? ElevatedChoice.ApprovalProblem(editing!) : null;
        BuildChanged = !IsApproved && approvable && editing!.RunAsAdministrator && editing.Monado.Kind is SourceKind.Branch or SourceKind.Folder
            && host.HasApproval(editing) && !host.ApprovedBuildCurrent(editing);
    }

    partial void OnMonadoOptionsChanged(string value) => Changed();

    partial void OnMonadoFolderChanged(string value) => Changed();

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Changed();

    private bool Change(Func<ProfileSet, ProfileSet> change, string? keep)
    {
        Message = null;
        try
        {
            var set = change(host.Profiles.Load());
            host.Profiles.Save(set);
            LoadList(keep ?? Selected?.Id);
            host.NotifyProfilesChanged();
            return true;
        }
        catch (MiradorException e)
        {
            Message = e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Mirador couldn't save its profiles ({e.Message.Trim().TrimEnd('.')}).";
        }

        return false;
    }

    private void LoadList(string? keep)
    {
        ProfileSet set;
        try
        {
            set = host.Profiles.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Mirador couldn't read its profiles ({e.Message.Trim().TrimEnd('.')}).";
            return;
        }

        loading = true;
        try
        {
            Profiles.Clear();
            foreach (var p in set.Profiles)
            {
                Profiles.Add(new ProfileItem(p.Id, p.Name));
            }

            Profiles.Add(AddItem);
            Selected = Profiles.FirstOrDefault(p => p != AddItem && p.Id == keep) ?? Profiles.FirstOrDefault(p => p != AddItem && p.Id == set.SelectedId);
        }
        finally
        {
            loading = false;
        }

        OnPropertyChanged(nameof(CanDelete));

        Show(set.Profiles.FirstOrDefault(p => p.Id == Selected?.Id));
    }

    private void Show(Profile? p)
    {
        editing = p;
        lighthouse = p?.Lighthouse;
        loading = true;
        try
        {
            Name = p?.Name ?? "";
            Headset = Headsets.FirstOrDefault(h => h.Id == p?.ModuleId) ?? Headsets[^1];
            DirectMode = ItemFor(p?.DirectMode ?? DefaultDirectMode(Headset));
            if (AutomaticVariantName() is var automatic && EyeVariants[0].Name != automatic)
            {
                EyeVariants[0] = new EyeVariantItem(null, automatic);
            }

            var eyeVariant = p is null ? null : EyeVariantChoice.Of(p);
            EyeVariant = EyeVariants.First(v => v.Id == eyeVariant);
            MonadoSource = p?.Monado switch
            {
                { Kind: SourceKind.Folder } => FolderSource,
                { IsUpstreamMain: true } => UpstreamSource,
                { Kind: SourceKind.Branch } => LinkSource,
                _ => MiradorSource,
            };
            MonadoFolder = p?.Monado.Folder ?? "";
            RunAsAdministrator = p?.RunAsAdministrator ?? false;
            MonadoLink = p?.Monado.Kind == SourceKind.Branch && !p.Monado.IsUpstreamMain ? p.Monado.Url ?? "" : "";
            MonadoBranch = MonadoLink.Length > 0 ? p!.Monado.Ref ?? "" : "";
            MonadoOptions = MonadoLink.Length > 0 ? string.Join(Environment.NewLine, p!.Monado.Options) : "";
            ClearBranches();
            Extra.Clear();
            foreach (var v in p?.ExtraEnvironment ?? [])
            {
                Extra.Add(new EnvRow { Name = v.Name, Value = v.Value });
            }
        }
        finally
        {
            loading = false;
        }

        IsDirty = false;
        Verify.Show(p);
        Verify.Unsaved = false;
        RefreshApproval();
        if (MonadoFromLink)
        {
            _ = ListBranchesAsync(keepCurrent: true);
        }

        MiradorMonado.Show(MonadoSource == MiradorSource);
        Build.Show(p);
        Build.Unsaved = false;
        Validate(Edited());
        OnPropertyChanged(nameof(UsesBaseStations));
    }

    private Profile Edited()
    {
        IReadOnlyDictionary<string, string> options = new Dictionary<string, string>(editing?.ModuleOptions ?? new Dictionary<string, string>());
        if (ShowsEyeVariant)
        {
            options = EyeVariantChoice.With(options, EyeVariant?.Id);
        }

        var directMode = DirectMode?.Kind ?? DirectModeKind.None;
        return new Profile(
            editing?.Id ?? "",
            Name.Trim(),
            Headset?.Id,
            EditedSource(),
            [.. Extra.Select(r => new EnvVar(r.Name.Trim(), r.Value))],
            options,
            lighthouse,
            RunAsAdministrator && CanRunAsAdministrator,
            editing?.DirectMode is null && directMode == DefaultDirectMode(Headset) ? null : directMode);
    }

    private BuildSource EditedSource() => MonadoSource switch
    {
        FolderSource => new BuildSource(SourceKind.Folder, MonadoFolder.Trim()),
        UpstreamSource => BuildSource.UpstreamMain,
        LinkSource => BuildSource.FromBranch(
            LinkUrl() ?? MonadoLink.Trim(), MonadoBranch.Trim(), BranchLink.SplitOptions(MonadoOptions)),
        _ => BuildSource.Mirador,
    };

    private string? LinkUrl()
    {
        try
        {
            return BranchLink.Parse(MonadoLink).Url;
        }
        catch (MiradorException)
        {
            return null;
        }
    }

    private string? LinkProblem()
    {
        if (MonadoSource != LinkSource)
        {
            return null;
        }

        try
        {
            BranchLink.Parse(MonadoLink);
            return null;
        }
        catch (MiradorException e)
        {
            return e.Message;
        }
    }

    private bool HeadsetUsesLighthouse() =>
        Headset?.Id is { } id && host.Modules.TryGetValue(id, out var module) && module.UsesSteamVrLighthouse;

    private void Changed()
    {
        if (loading || editing is null)
        {
            return;
        }

        IsDirty = true;
        Build.Unsaved = true;
        Verify.Unsaved = true;
        Validate(Edited());

        OnPropertyChanged(nameof(UsesBaseStations));
    }

    private bool Validate(Profile p)
    {
        var problems = ProfileValidation.Problems(p, host.Locator, _ => null).ToList();
        if (LinkProblem() is { } linkProblem)
        {
            problems.RemoveAll(x => x.Contains(MonadoLink.Trim(), StringComparison.Ordinal) && MonadoLink.Trim().Length > 0);
            problems.Insert(0, linkProblem);
        }

        Problems.Clear();
        foreach (var problem in problems)
        {
            Problems.Add(problem);
        }

        HasProblems = problems.Count > 0;
        OnPropertyChanged(nameof(ProblemsText));
        return !HasProblems;
    }

    private void OnSessionChanged() =>
        App.Current.Ui.TryEnqueue(() => MiradorMonado.MonadoRuns = Build.MonadoRuns = Verify.MonadoRuns = IsRunning = host.Session.State != SessionState.Stopped);
}
