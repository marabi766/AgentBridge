using AgentBridge.Abstractions.Models;

namespace AgentBridge.Infrastructure.Notifications;

public enum TelegramLanguage
{
    English,
    Persian,
}

/// <summary>
/// The bot's own fixed replies — help, status labels, command confirmations —
/// in both languages <see cref="BridgeConfiguration.TelegramLanguage"/> can
/// select.
///
/// Deliberately does not touch <c>LastError</c> or <c>LastAction</c>: those
/// strings are matched elsewhere by their English prefix (see
/// <c>MainWindowViewModel.CanRetryCodex</c> and the <c>CanContinueWaitingFor*</c>
/// properties), so translating them would silently break button enablement.
/// Only the surrounding labels this class owns outright are bilingual; agent
/// names, command words, and recorded error/action text stay exactly as
/// written everywhere else.
/// </summary>
public static class TelegramText
{
    public static TelegramLanguage ParseLanguage(string? value) =>
        string.Equals(value, "fa", StringComparison.OrdinalIgnoreCase) ? TelegramLanguage.Persian : TelegramLanguage.English;

    public static string HelpText(TelegramLanguage language) => language == TelegramLanguage.Persian
        ? string.Join('\n',
        [
            "دستورها:",
            "/run — شروع یا ادامه از آخرین نقطه‌ی ذخیره‌شده",
            "/stop — توقف اجرای فعلی",
            "/pause, /resume — مکث و ادامه",
            "/status — وضعیت فعلی",
            "/retry_claude, /retry_codex — ارسال دوباره‌ی دستور فعلی",
            "/continue_claude, /continue_codex — درخواست ادامه‌ی کار از عامل",
            "/recover_claude, /recover_codex — برای استفاده بعد از خاموشی یا کرش",
        ])
        : string.Join('\n',
        [
            "Commands:",
            "/run — start or resume from the last checkpoint",
            "/stop — stop the current run",
            "/pause, /resume",
            "/status — current state",
            "/retry_claude, /retry_codex — resend the current instruction",
            "/continue_claude, /continue_codex — ask an agent to carry on",
            "/recover_claude, /recover_codex — use after a shutdown or crash",
        ]);

    public static string StatusOnlyRestriction(TelegramLanguage language) => language == TelegramLanguage.Persian
        ? "این چت فقط اجازه‌ی ارسال دستور /status را دارد."
        : "This chat can only send /status.";

    public static string Started(TelegramLanguage language) =>
        language == TelegramLanguage.Persian ? "شروع شد." : "Started.";

    public static string Stopped(TelegramLanguage language) =>
        language == TelegramLanguage.Persian ? "متوقف شد." : "Stopped.";

    public static string Paused(TelegramLanguage language) =>
        language == TelegramLanguage.Persian ? "در حالت مکث قرار گرفت." : "Paused.";

    public static string Resumed(TelegramLanguage language) =>
        language == TelegramLanguage.Persian ? "ادامه یافت." : "Resumed.";

    public static string RetryingClaude(TelegramLanguage language) =>
        language == TelegramLanguage.Persian ? "در حال ارسال دوباره‌ی دستور به Claude." : "Retrying Claude.";

    public static string RetryingCodex(TelegramLanguage language) =>
        language == TelegramLanguage.Persian ? "در حال ارسال دوباره‌ی دستور به Codex." : "Retrying Codex.";

    public static string ContinuingClaude(TelegramLanguage language) =>
        language == TelegramLanguage.Persian ? "از Claude خواسته شد ادامه دهد." : "Asking Claude to continue.";

    public static string ContinuingCodex(TelegramLanguage language) =>
        language == TelegramLanguage.Persian ? "از Codex خواسته شد ادامه دهد." : "Asking Codex to continue.";

    public static string RecoveringClaude(TelegramLanguage language) => language == TelegramLanguage.Persian
        ? "به Claude اطلاع داده شد که اجرای قبلی‌اش ناقص متوقف شده بود."
        : "Telling Claude its run was cut short.";

    public static string RecoveringCodex(TelegramLanguage language) => language == TelegramLanguage.Persian
        ? "به Codex اطلاع داده شد که اجرای قبلی‌اش ناقص متوقف شده بود."
        : "Telling Codex its run was cut short.";

    public static string CouldNotDoThat(TelegramLanguage language, string reason) =>
        language == TelegramLanguage.Persian ? $"انجام نشد: {reason}" : $"Could not do that: {reason}";

    public static string IterationLabel(TelegramLanguage language, int current, int max) =>
        language == TelegramLanguage.Persian ? $"دور اجرا {current}/{max}" : $"Iteration {current}/{max}";

    public static string BranchLabel(TelegramLanguage language, string branch) =>
        language == TelegramLanguage.Persian ? $"شاخه: {branch}" : $"Branch: {branch}";

    public static string LastErrorLabel(TelegramLanguage language, string error) =>
        language == TelegramLanguage.Persian ? $"آخرین خطا: {error}" : $"Last error: {error}";

    public static string ModeLabel(TelegramLanguage language, bool dryRun) => language == TelegramLanguage.Persian
        ? (dryRun ? "اجرای آزمایشی" : "اجرای واقعی")
        : (dryRun ? "Dry Run" : "LIVE");

    /// <summary>
    /// Translates the fixed set of strings <c>BridgeStateDescriptions.Describe</c>
    /// can produce. Anything not in the table (there is no twelfth state) passes
    /// through unchanged rather than showing nothing.
    /// </summary>
    public static string StateLabel(TelegramLanguage language, string englishStateText)
    {
        if (language != TelegramLanguage.Persian)
        {
            return englishStateText;
        }

        return englishStateText switch
        {
            "Idle" => "بی‌کار",
            "Waiting for Claude" => "در انتظار Claude",
            "Claude report detected" => "گزارش Claude شناسایی شد",
            "Waiting for Codex" => "در انتظار Codex",
            "Codex processing" => "Codex در حال پردازش",
            "Codex prompt detected" => "دستور Codex شناسایی شد",
            "Claude processing" => "Claude در حال پردازش",
            "Paused" => "متوقف‌شده (مکث)",
            "Stopped" => "متوقف‌شده",
            "Error" => "خطا",
            _ => englishStateText,
        };
    }
}
