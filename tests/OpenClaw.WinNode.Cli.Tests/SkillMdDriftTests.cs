using System.Reflection;
using System.Text.RegularExpressions;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.Mcp;

namespace OpenClaw.WinNode.Cli.Tests;

/// <summary>
/// F-11: skill.md duplicates the tray-side capability surface for agent
/// readability. This test compares the set of <c>### &lt;command&gt;</c>
/// headings in skill.md and <see cref="McpToolBridge.KnownCommands"/>
/// against the commands exposed by capabilities registered by NodeService,
/// so additions or renames in the tray fail loudly here instead of silently
/// shipping drifted documentation.
///
/// The test compares command identifiers only — descriptions, examples,
/// and prose can be tweaked freely without breaking the test.
/// </summary>
public class SkillMdDriftTests
{
    [Fact]
    public void SkillMd_command_set_matches_capability_registry()
    {
        var skillMdPath = LocateSkillMd();
        var content = File.ReadAllText(skillMdPath);

        var documented = ParseCommandHeadings(content);
        var described = new HashSet<string>(McpToolBridge.KnownCommands, StringComparer.Ordinal);
        var registered = GetRegisteredCapabilityCommands();

        var missingFromDescriptions = registered.Except(described).OrderBy(s => s).ToList();
        var missingFromDoc = described.Except(documented).OrderBy(s => s).ToList();
        var missingRegisteredFromDoc = registered.Except(documented).OrderBy(s => s).ToList();
        var extrasInDoc = documented.Except(described).OrderBy(s => s).ToList();

        if (missingFromDescriptions.Count > 0
            || missingFromDoc.Count > 0
            || missingRegisteredFromDoc.Count > 0
            || extrasInDoc.Count > 0)
        {
            var msg = "skill.md drifted from the capability registry " +
                      "(McpToolBridge.CommandDescriptions). Update " +
                      "src/OpenClaw.WinNode.Cli/skill.md and " +
                      "src/OpenClaw.Shared/Mcp/McpToolBridge.cs.\n  " +
                      "Registered commands missing from CommandDescriptions: " +
                      $"[{string.Join(", ", missingFromDescriptions)}]\n  " +
                      "CommandDescriptions missing from doc: " +
                      $"[{string.Join(", ", missingFromDoc)}]\n  " +
                      "Registered commands missing from doc: " +
                      $"[{string.Join(", ", missingRegisteredFromDoc)}]\n  Extras in doc: " +
                      $"[{string.Join(", ", extrasInDoc)}]";
            Assert.Fail(msg);
        }
    }

    /// <summary>
    /// skill.md lists each command under its own H3 heading like
    /// <c>### system.notify</c>. Anything matching <c>### &lt;dotted.name&gt;</c>
    /// counts as a documented command. We deliberately ignore other H3s
    /// (e.g. "### Message kinds", "### ComponentDef") which don't have a
    /// dotted-name shape.
    /// </summary>
    private static HashSet<string> ParseCommandHeadings(string md)
    {
        // Match ### followed by a single dotted token (lowercase, dots, dots+lowercase
        // segments only) to the end of the line. canvas.a2ui.pushJSONL has a
        // mixed-case suffix, so allow camelCase tail segments too.
        var rx = new Regex(@"^###\s+([a-z][a-zA-Z0-9]*(?:\.[a-zA-Z0-9]+)+)\s*$",
                           RegexOptions.Multiline);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in rx.Matches(md))
        {
            set.Add(m.Groups[1].Value);
        }
        return set;
    }

    /// <summary>
    /// skill.md ships next to winnode.exe. From the test's working directory
    /// (the test bin folder), walk up to the repo root and resolve the source
    /// copy — that's the canonical input the build copies to output. Falls
    /// back to the test bin's own copy if the source can't be located.
    /// </summary>
    private static string LocateSkillMd()
        => LocateRepoFile(Path.Combine("src", "OpenClaw.WinNode.Cli", "skill.md"));

    private static HashSet<string> GetRegisteredCapabilityCommands()
    {
        var commands = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in GetRegisteredCapabilityTypes())
        {
            var capability = CreateCapability(type);
            foreach (var command in capability.Commands)
            {
                commands.Add(command);
            }
        }

        return commands;
    }

    private static IEnumerable<Type> GetRegisteredCapabilityTypes()
    {
        var nodeServicePath = LocateRepoFile(Path.Combine("src", "OpenClaw.Tray.WinUI", "Services", "NodeService.cs"));
        var content = File.ReadAllText(nodeServicePath);
        var names = Regex.Matches(content, @"new\s+([A-Za-z][A-Za-z0-9_]*Capability)\s*\(")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

        var assembly = typeof(INodeCapability).Assembly;
        foreach (var name in names)
        {
            var type = assembly.GetTypes()
                .SingleOrDefault(t => t.Name == name
                    && !t.IsAbstract
                    && typeof(INodeCapability).IsAssignableFrom(t));

            if (type == null)
            {
                Assert.Fail($"NodeService registers {name}, but no matching INodeCapability type was found.");
            }

            yield return type;
        }
    }

    private static INodeCapability CreateCapability(Type type)
    {
        foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                     .OrderBy(c => c.GetParameters().Length))
        {
            var parameters = ctor.GetParameters();
            var args = new object?[parameters.Length];
            var supported = true;

            for (var i = 0; i < parameters.Length; i++)
            {
                if (!TryCreateConstructorArg(parameters[i], out args[i]))
                {
                    supported = false;
                    break;
                }
            }

            if (supported)
            {
                return (INodeCapability)ctor.Invoke(args);
            }
        }

        Assert.Fail($"Could not construct registered capability {type.FullName} for command drift testing.");
    }

    private static bool TryCreateConstructorArg(ParameterInfo parameter, out object? value)
    {
        var type = parameter.ParameterType;
        if (type == typeof(IOpenClawLogger))
        {
            value = NullLogger.Instance;
            return true;
        }

        if (type == typeof(IDeviceStatusProvider))
        {
            value = new FakeDeviceStatusProvider();
            return true;
        }

        if (type == typeof(string))
        {
            value = parameter.Name?.Contains("gatewayUrl", StringComparison.OrdinalIgnoreCase) == true
                ? "http://127.0.0.1:8765"
                : "";
            return true;
        }

        if (parameter.HasDefaultValue)
        {
            value = parameter.DefaultValue;
            return true;
        }

        value = null;
        return Nullable.GetUnderlyingType(type) != null || !type.IsValueType;
    }

    private static string LocateRepoFile(string relativePath)
    {
        var repoRoot = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            var envCandidate = Path.Combine(repoRoot, relativePath);
            if (File.Exists(envCandidate)) return envCandidate;
        }

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        if (Path.GetFileName(relativePath).Equals("skill.md", StringComparison.Ordinal))
        {
            var nextTo = Path.Combine(AppContext.BaseDirectory, "skill.md");
            if (File.Exists(nextTo)) return nextTo;
        }

        throw new FileNotFoundException($"Could not locate {relativePath} from the test working directory.");
    }

    private sealed class FakeDeviceStatusProvider : IDeviceStatusProvider
    {
        // Drift testing only needs to instantiate DeviceCapability and read Commands.
        public object GetOsInfo() => new { };
        public Task<object> GetCpuInfoAsync() => Task.FromResult<object>(new { });
        public object GetMemoryInfo() => new { };
        public object GetDiskInfo() => new { };
        public object GetBatteryInfo() => new { };
        public void Dispose() { }
    }
}
