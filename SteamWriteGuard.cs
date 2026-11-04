namespace SteamAchievementUnlocker;

public static class SteamWriteGuard
{
    // Check identity at each mutation boundary, including after server confirmation.
    public static bool Run(IEnumerable<string> names, Func<bool> sameSession, Func<string, bool> set, Func<bool> store)
    {
        if (!sameSession()) return false;
        bool allSet = true;
        foreach (string name in names)
        {
            if (!sameSession()) return false;
            if (!set(name)) allSet = false;
        }
        if (!sameSession()) return false;
        bool saved = store();
        return sameSession() && allSet && saved;
    }
}
