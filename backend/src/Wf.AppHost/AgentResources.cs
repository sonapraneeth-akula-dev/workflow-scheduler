using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Wf.AppHost;

public static class AgentResources
{
    public static string[] NormalizeArguments(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var normalized = new List<string>();
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument == "--lan") normalized.Add("--Local:Lan=true");
            else if (argument.StartsWith("--lan=", StringComparison.Ordinal))
                normalized.Add("--Local:Lan=" + argument["--lan=".Length..]);
            else if (argument == "--linux-agent") normalized.Add("--Local:LinuxAgent=true");
            else if (argument.StartsWith("--linux-agent=", StringComparison.Ordinal))
                normalized.Add("--Local:LinuxAgent=" + argument["--linux-agent=".Length..]);
            else if (argument == "--agents")
            {
                if (++index == arguments.Length) throw new ArgumentException("--agents requires a positive integer.");
                normalized.Add("--Local:Agents=" + arguments[index]);
            }
            else if (argument.StartsWith("--agents=", StringComparison.Ordinal))
                normalized.Add("--Local:Agents=" + argument["--agents=".Length..]);
            else normalized.Add(argument);
        }
        return normalized.ToArray();
    }

    internal static IReadOnlyList<LocalAgent> ReadIdentities(IConfiguration configuration, string root)
    {
        var text = configuration["Local:Agents"] ?? "1";
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
            throw new InvalidOperationException("Local:Agents (--agents) must be a positive integer.");
        var linux = configuration.GetValue("Local:LinuxAgent", false);
        var agents = new List<LocalAgent>();
        for (var index = 0; index < count; index++)
            Read(index == 0 ? "wf-agent" : "wf-agent-" + (index + 1).ToString(CultureInfo.InvariantCulture),
                index == 0 ? "local" : "local-" + (index + 1).ToString(CultureInfo.InvariantCulture), false);
        if (linux) Read("wf-agent-linux", "linux", true);
        return agents;

        void Read(string resourceName, string directory, bool container)
        {
            var agentRoot = Path.Combine(root, "agents", directory);
            Directory.CreateDirectory(agentRoot);
            var path = Path.Combine(agentRoot, "id");
            if (!File.Exists(path)) File.WriteAllText(path, Guid.NewGuid().ToString("D"));
            var value = File.ReadAllText(path);
            if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || value != id.ToString("D"))
                throw new InvalidDataException($"The {resourceName} identity file must contain a non-empty canonical UUID.");
            if (agents.Any(agent => agent.Id == id))
                throw new InvalidDataException("Local agents must have distinct persistent identities.");
            agents.Add(new(resourceName, agentRoot, id, container));
        }
    }

    internal static void Add(IDistributedApplicationBuilder builder, IResourceBuilder<ProjectResource> host,
        string root, IReadOnlyList<LocalAgent> agents, int apiPort)
    {
        foreach (var descriptor in agents)
        {
            IResourceBuilder<IResourceWithEnvironment> agent;
            string work;
            string secrets;
            if (descriptor.Linux)
            {
                var backend = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
                agent = builder.AddDockerfile(descriptor.ResourceName, backend,
                        Path.Combine(backend, "src", "Wf.Agent", "Dockerfile"))
                    .WithContainerName("kardia-local-agent-linux-" + apiPort.ToString(CultureInfo.InvariantCulture))
                    .WithVolume(name: "wf-local-agent-" + descriptor.Id.ToString("N"), target: "/var/lib/wf-agent")
                    .WithEnvironment("Agent__AllowInsecureTransport", "true")
                    .WaitFor(host);
                work = "/var/lib/wf-agent";
                secrets = work + "/secrets";
            }
            else
            {
                agent = builder.AddProject<Projects.Wf_Agent>(descriptor.ResourceName, launchProfileName: null).WaitFor(host);
                work = descriptor.Directory;
                secrets = Path.Combine(root, "secrets");
            }
            agent.WithEnvironment("Agent__Id", descriptor.Id.ToString("D"))
                .WithEnvironment("Agent__Name", "local-" + descriptor.Id.ToString("N"))
                .WithEnvironment("Agent__Pool", "default")
                .WithEnvironment("Agent__Labels__environment", "local")
                .WithEnvironment("Agent__Labels__instance", descriptor.ResourceName)
                .WithEnvironment("Agent__Labels__runtime", descriptor.Linux ? "container" : "native")
                .WithEnvironment("Agent__Engines__0", host.GetEndpoint("engine"))
                .WithEnvironment("Agent__ResultJournalDirectory", Child("results"))
                .WithEnvironment("Agent__LogSpoolDirectory", Child("logs"))
                .WithEnvironment("Agent__PackageCache", Child("packages"))
                .WithEnvironment("WF_DATASET_ROOT", descriptor.Linux ? Child("datasets") : Path.Combine(root, "datasets"))
                .WithEnvironment("Secrets__Provider", "Local")
                .WithEnvironment("Secrets__LocalDirectory", secrets);

            string Child(string name) => descriptor.Linux ? work + "/" + name : Path.Combine(work, name);
        }
    }

    internal sealed record LocalAgent(string ResourceName, string Directory, Guid Id, bool Linux);
}
