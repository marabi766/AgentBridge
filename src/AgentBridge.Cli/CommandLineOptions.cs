using AgentBridge.Abstractions.Models;

namespace AgentBridge.Cli;

/// <summary>
/// What one invocation of the headless host was asked to do.
///
/// Every setting is optional and null means "leave the stored value alone", so
/// that a run with no arguments repeats the last one exactly. An option that
/// defaulted to something instead would silently overwrite a stored choice the
/// operator made on purpose.
/// </summary>
public sealed record CommandLineOptions
{
    public string? ProjectPath { get; init; }

    public string? SettingsFilePath { get; init; }

    public BridgeStartPoint? StartPoint { get; init; }

    public int? MaximumIterations { get; init; }

    public bool? DryRun { get; init; }

    public string? ClaudeArguments { get; init; }

    public string? CodexArguments { get; init; }

    public int? ClaudeTimeoutSeconds { get; init; }

    public int? CodexTimeoutSeconds { get; init; }

    public string? ClaudeReportFileName { get; init; }

    public string? CodexPromptFileName { get; init; }

    /// <summary>Print the resolved setup and both agents' readiness, then exit.</summary>
    public bool StatusOnly { get; init; }

    /// <summary>Discard persisted state before starting, rather than resuming it.</summary>
    public bool ResetState { get; init; }

    public bool ShowHelp { get; init; }

    public string? ParseError { get; init; }

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            // Reads the value that belongs to the flag at position i, so a flag
            // written without one fails here rather than swallowing the next flag.
            // The pass-through options are exempt from that guard: what they carry
            // is another program's command line, and it legitimately starts with a
            // dash — refusing it would make the one option most likely to be set
            // impossible to set.
            string? Value(bool valueMayLookLikeAFlag = false)
            {
                if (i + 1 >= args.Length
                    || (!valueMayLookLikeAFlag && args[i + 1].StartsWith('-')))
                {
                    options = options with { ParseError = $"'{argument}' needs a value." };
                    return null;
                }

                return args[++i];
            }

            switch (argument)
            {
                case "-h" or "--help" or "/?":
                    return options with { ShowHelp = true };

                case "--project":
                    if (Value() is { } project) options = options with { ProjectPath = Path.GetFullPath(project) };
                    break;

                case "--settings":
                    if (Value() is { } settings) options = options with { SettingsFilePath = Path.GetFullPath(settings) };
                    break;

                case "--start-with":
                    if (Value() is { } startWith)
                    {
                        options = startWith.ToLowerInvariant() switch
                        {
                            "claude" => options with { StartPoint = BridgeStartPoint.WaitForClaudeReport },
                            "codex" => options with { StartPoint = BridgeStartPoint.WaitForCodexPrompt },
                            _ => options with { ParseError = $"--start-with expects 'claude' or 'codex', not '{startWith}'." },
                        };
                    }

                    break;

                case "--max-iterations":
                    if (Value() is { } iterations)
                    {
                        options = int.TryParse(iterations, out var parsedIterations) && parsedIterations > 0
                            ? options with { MaximumIterations = parsedIterations }
                            : options with { ParseError = $"--max-iterations expects a positive number, not '{iterations}'." };
                    }

                    break;

                case "--live":
                    options = options with { DryRun = false };
                    break;

                case "--dry-run":
                    options = options with { DryRun = true };
                    break;

                case "--claude-args":
                    if (Value(valueMayLookLikeAFlag: true) is { } claudeArgs) options = options with { ClaudeArguments = claudeArgs };
                    break;

                case "--codex-args":
                    if (Value(valueMayLookLikeAFlag: true) is { } codexArgs) options = options with { CodexArguments = codexArgs };
                    break;

                case "--claude-timeout":
                    if (Value() is { } claudeTimeout)
                    {
                        options = int.TryParse(claudeTimeout, out var parsedClaudeTimeout) && parsedClaudeTimeout > 0
                            ? options with { ClaudeTimeoutSeconds = parsedClaudeTimeout }
                            : options with { ParseError = $"--claude-timeout expects a positive number of seconds, not '{claudeTimeout}'." };
                    }

                    break;

                case "--codex-timeout":
                    if (Value() is { } codexTimeout)
                    {
                        options = int.TryParse(codexTimeout, out var parsedCodexTimeout) && parsedCodexTimeout > 0
                            ? options with { CodexTimeoutSeconds = parsedCodexTimeout }
                            : options with { ParseError = $"--codex-timeout expects a positive number of seconds, not '{codexTimeout}'." };
                    }

                    break;

                case "--report-file":
                    if (Value() is { } reportFile) options = options with { ClaudeReportFileName = reportFile };
                    break;

                case "--prompt-file":
                    if (Value() is { } promptFile) options = options with { CodexPromptFileName = promptFile };
                    break;

                case "--status":
                    options = options with { StatusOnly = true };
                    break;

                case "--reset-state":
                    options = options with { ResetState = true };
                    break;

                default:
                    options = options with { ParseError = $"Unrecognised argument '{argument}'." };
                    break;
            }

            if (options.ParseError is not null)
            {
                return options;
            }
        }

        return options;
    }

    /// <summary>
    /// Folds this invocation onto the stored configuration. Both CLI transports
    /// are turned on unconditionally: this host registers no desktop adapters,
    /// so leaving the flags off would describe a setup that cannot occur here.
    /// </summary>
    public BridgeConfiguration ApplyTo(BridgeConfiguration configuration) => configuration with
    {
        ProjectPath = ProjectPath ?? configuration.ProjectPath,
        ClaudeReportFileName = ClaudeReportFileName ?? configuration.ClaudeReportFileName,
        CodexPromptFileName = CodexPromptFileName ?? configuration.CodexPromptFileName,
        MaximumIterations = MaximumIterations ?? configuration.MaximumIterations,
        DryRun = DryRun ?? configuration.DryRun,
        UseClaudeCli = true,
        UseCodexCli = true,
        ClaudeCliArguments = ClaudeArguments ?? configuration.ClaudeCliArguments,
        CodexCliArguments = CodexArguments ?? configuration.CodexCliArguments,
        ClaudeCliTimeoutSeconds = ClaudeTimeoutSeconds ?? configuration.ClaudeCliTimeoutSeconds,
        CodexCliTimeoutSeconds = CodexTimeoutSeconds ?? configuration.CodexCliTimeoutSeconds,
        // Nothing here has a window to auto-launch, and the desktop identifiers
        // are not consulted by the command line adapters.
        AutoLaunchClaude = false,
        AutoLaunchChatGpt = false,
    };

    public const string HelpText = """
        agent-bridge — runs the Claude/Codex loop with no window, using both CLIs.

        Usage:
          agent-bridge --project <path> [options]

        Every option is remembered, so a later run with no options repeats the last one.

        Options:
          --project <path>        Repository the two agents work in.
          --start-with <agent>    Which protocol file to wait for first: claude | codex.
                                  'codex' means an existing CodexPrompt.md starts the loop.
          --live                  Really run the agents. The default is a dry run that
                                  logs what it would send and touches nothing.
          --dry-run               Force a dry run.
          --max-iterations <n>    Stop after n Claude reports.
          --report-file <name>    Claude's report filename. Default ClaudeResultReport.md
          --prompt-file <name>    Codex's prompt filename. Default CodexPrompt.md
          --claude-args "<args>"  Arguments for one claude run.
                                  Default: --print --permission-mode acceptEdits
          --codex-args "<args>"   Arguments for one codex run.
                                  Default: exec --sandbox workspace-write --skip-git-repo-check -
          --claude-timeout <sec>  How long one claude run may take. Default 3600.
          --codex-timeout <sec>   How long one codex run may take. Default 1800.
          --settings <path>       Settings file to use.
          --status                Print the resolved setup and both CLIs' readiness, then exit.
          --reset-state           Discard persisted state instead of resuming it.
          -h, --help              This text.

        Stop a run with Ctrl+C. The loop keeps going while the Windows session is
        locked; both agents are processes, not windows.
        """;
}
