namespace Wire;

internal static class PolicyResolver
{
    public static ResiliencePolicy Resolve(this WireOptions options, WireCallOptions? call)
    {
        if (call?.Policy is { } p) return p;
        if (call?.PolicyName is { } name)
            return options.Policies.TryGetValue(name, out var named) ? named : throw new WireException($"Unknown Wire policy '{name}'.");
        return options.Default;
    }
}
