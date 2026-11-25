using Mirador.Core.Monado;

namespace Mirador.Core.Profiles;

public static class ProfileValidation
{
    public static IReadOnlyList<string> Problems(Profile p, MonadoLocator locator, Func<Profile, string?> moduleProblem)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(p.Name))
        {
            problems.Add("A profile needs a name.");
        }

        if (p.Monado.Kind == SourceKind.Folder && p.Monado.Folder is { } folder && !string.IsNullOrWhiteSpace(folder)
            && !Path.IsPathFullyQualified(folder.Trim()))
        {
            problems.Add($"The Monado build folder \"{folder.Trim()}\" isn't a full path. Pick the folder again in the profile.");
        }
        else if (p.Monado.Kind == SourceKind.Folder)
        {
            try
            {
                locator.Locate(p);
            }
            catch (MiradorException e)
            {
                problems.Add(e.Message);
            }
        }
        else if (p.Monado.Kind == SourceKind.Branch && BranchLink.Problem(p.Monado) is { } branchProblem)
        {
            problems.Add(branchProblem);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in p.ExtraEnvironment)
        {
            var name = variable.Name.Trim();
            if (name.Length == 0)
            {
                problems.Add("Every extra variable needs a name.");
            }
            else if (name.Contains('='))
            {
                problems.Add($"A variable's name can't contain \"=\": {name}.");
            }
            else if (!seen.Add(name))
            {
                problems.Add($"{name} is set twice. Keep one.");
            }
        }

        if (moduleProblem(p) is { } problem)
        {
            problems.Add(problem);
        }

        return problems.Distinct().ToList();
    }
}
