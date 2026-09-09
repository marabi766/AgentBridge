using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentBridge.Cli;

/// <summary>
/// Turns bridge status into the two things a headless operator can actually
/// read: one line per change while a run is going, and a full setup report on
/// demand. There is no window to look at, so anything the dashboard would have
/// shown has to be printable.
/// </summary>
public static class StatusReport
{
    public static string Format(BridgeStatusView status)
    {
        var parts = new List<string>
        {
            $"[{status.CurrentState}]",
            $"iteration {status.CurrentIteration}/{status.MaximumIterations}",
            $"claude={status.ClaudeStatus}",
            $"codex={status.CodexStatus}",
        };

        if (!string.IsNullOrWhiteSpace(status.LastAction))
        {
            parts.Add($"— {status.LastAction}");
        }

        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            parts.Add($"!! {status.LastError}");
        }

        return string.Join(' ', parts);
    }

    public static async Task WriteAsync(
        BridgeConfiguration configuration,
        string settingsFilePath,
        string stateFilePath,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("=== Agent Bridge (headless) ===");
        Console.WriteLine($"Settings file    : {settingsFilePath}");
        Console.WriteLine($"State file       : {stateFilePath}");
        Console.WriteLine($"Logs             : {HeadlessPaths.LogsDirectory}");
        Console.WriteLine($"Project          : {configuration.ProjectPath}");
        Console.WriteLine($"Mode             : {(configuration.DryRun ? "Dry run" : "Live")}");
        Console.WriteLine($"Max iterations   : {configuration.MaximumIterations}");
        Console.WriteLine($"Claude report    : {configuration.ClaudeReportFileName}");
        Console.WriteLine($"Codex prompt     : {configuration.CodexPromptFileName}");
        Console.WriteLine();

        var projectService = services.GetRequiredService<IProjectService>();
        var validation = await projectService.ValidateProjectAsync(configuration, cancellationToken);
        Console.WriteLine($"Project valid    : {(validation.IsValid ? "yes" : "NO")}");
        Console.WriteLine($"Git repository   : {(validation.IsGitRepository ? "yes" : "no")}");
        Console.WriteLine($"{configuration.ClaudeReportFileName} present: {(validation.ClaudeReportFileExists ? "yes" : "no")}");
        Console.WriteLine($"{configuration.CodexPromptFileName} present: {(validation.CodexPromptFileExists ? "yes" : "no")}");
        foreach (var error in validation.Errors)
        {
            Console.WriteLine($"  error   : {error}");
        }

        foreach (var warning in validation.Warnings)
        {
            Console.WriteLine($"  warning : {warning}");
        }

        Console.WriteLine();

        var adapters = services.GetRequiredService<IAgentAdapterProvider>();
        foreach (var role in new[] { AgentRole.Claude, AgentRole.Codex })
        {
            var adapter = adapters.GetAdapter(role);
            Console.WriteLine(await adapter.GetDiagnosticsAsync(cancellationToken));
        }
    }
}
