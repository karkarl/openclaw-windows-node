using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

/// <summary>
/// Represents a command that a node can handle
/// </summary>
public class NodeCommand
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = ""; // canvas, camera, screen, system, etc.
}

/// <summary>
/// Request from gateway to invoke a node command
/// </summary>
public class NodeInvokeRequest
{
    public string Id { get; set; } = "";
    public string Command { get; set; } = "";
    public JsonElement Args { get; set; }
}

public class NodeInvokeCompletedEventArgs : EventArgs
{
    public string RequestId { get; set; } = "";
    public string Command { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public TimeSpan Duration { get; set; }
    public string? NodeId { get; set; }
}

/// <summary>
/// Response to a node.invoke request
/// </summary>
public class NodeInvokeResponse
{
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
    public object? Payload { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Interface for implementing node capabilities
/// </summary>
public interface INodeCapability
{
    /// <summary>The capability category (canvas, camera, screen, system)</summary>
    string Category { get; }

    /// <summary>Commands this capability can handle</summary>
    IReadOnlyList<string> Commands { get; }

    /// <summary>Check if this capability can handle the given command</summary>
    bool CanHandle(string command);

    /// <summary>Execute a command and return the result</summary>
    Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request);

    /// <summary>
    /// Execute a command with a cancellation token. The default implementation
    /// just calls <see cref="ExecuteAsync(NodeInvokeRequest)"/>; capabilities
    /// with long-running work (screen.record, camera.clip) should override so
    /// MCP request cancellation propagates into the underlying capture
    /// pipeline rather than orphaning it.
    /// </summary>
    Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(request);
}

/// <summary>
/// Base class for node capabilities with common functionality
/// </summary>
public abstract class NodeCapabilityBase : INodeCapability
{
    public abstract string Category { get; }
    public abstract IReadOnlyList<string> Commands { get; }

    protected IOpenClawLogger Logger { get; }

    protected NodeCapabilityBase(IOpenClawLogger logger)
    {
        Logger = logger;
    }

    public virtual bool CanHandle(string command)
    {
        return Commands.Contains(command);
    }

    public abstract Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request);

    public virtual Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(request);
    
    protected NodeInvokeResponse Success(object? payload = null)
    {
        return new NodeInvokeResponse { Ok = true, Payload = payload };
    }
    
    protected NodeInvokeResponse Error(string message)
    {
        return new NodeInvokeResponse { Ok = false, Error = message };
    }
    
    protected T? GetArg<T>(JsonElement args, string name, T? defaultValue = default)
    {
        var value = args.GetObject<T>(name);
        return value is null ? defaultValue : value;
    }
    
    protected string? GetStringArg(JsonElement args, string name, string? defaultValue = null)
        => args.GetStringOrNull(name) ?? defaultValue;
    
    protected int GetIntArg(JsonElement args, string name, int defaultValue = 0)
        => args.GetInt32OrDefault(name, defaultValue);
    
    protected bool GetBoolArg(JsonElement args, string name, bool defaultValue = false)
        => args.GetBoolOrDefault(name, defaultValue);

    /// <summary>
    /// Get a string array from a JSON array property. Non-string and whitespace-only elements
    /// are ignored. Strings are trimmed to preserve the historical system.which behavior.
    /// </summary>
    protected string[] GetStringArrayArg(JsonElement args, string name)
        => args.GetStringArray(name, trimValues: true, skipEmpty: true);
}

/// <summary>
/// Node registration information
/// </summary>
public class NodeRegistration
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Platform { get; set; } = "windows";
    public string DisplayName { get; set; } = "";
    public List<string> Capabilities { get; set; } = new();
    public List<string> Commands { get; set; } = new();
    public Dictionary<string, bool> Permissions { get; set; } = new();
}
