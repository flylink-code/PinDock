namespace PinDock;

public readonly record struct RecentRoots(string Roaming, string Local);

public interface IRecentProvider
{
    IEnumerable<AppRecentItem> Collect(ResolvedAppTarget target, RecentRoots roots);
}
