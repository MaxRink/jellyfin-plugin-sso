namespace SSO_Auth.Tests;

/// <summary>
/// Groups the tests that replace the static <c>SSOPlugin.Instance</c>, so xUnit runs them one at a
/// time instead of letting them overwrite each other's configuration.
/// </summary>
[CollectionDefinition("PluginInstance", DisableParallelization = true)]
public class PluginInstanceCollection
{
}
